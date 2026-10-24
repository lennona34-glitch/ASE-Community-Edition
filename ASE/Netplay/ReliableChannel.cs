/*
 *
 * The reliable, ordered channel of online play (NETPLAY.md §7.7): control messages now, the
 * session snapshot and the serial cable's bytes later.
 *
 * Messages are cut into numbered segments that travel in SEGMENT frames. The receiver puts
 * them back in order, reassembles the messages and acknowledges with the next segment it
 * expects, a 128-bit map of what arrived beyond it (a selective ACK), and the number of the
 * newest packet that brought it segments.
 *
 * Loss recovery follows QUIC (RFC 9002), which is why that packet number is there. A
 * resent segment keeps its segment number but travels in a new packet, so ordering by
 * packet tells a lost transmission from a resent one still on its way — ordering by segment
 * cannot, and a resent segment was declared lost again the moment it left, over and over.
 * A transmission is lost only on evidence: a packet three numbers later was received, or a
 * later one was and this one is older than 9/8 of the round trip. When nothing at all comes
 * back for a probe timeout (SRTT + 4·RTTVAR, 50 ms to 2 s, doubled each time it runs out in
 * a row), the two oldest outstanding segments go out as probes, nothing more: a timer that
 * resends everything it covers turns a filling router queue into a storm, since the round
 * trip grows faster than the timer and every queued segment "times out" and is sent twice.
 * The round trip is measured on the newest packet acknowledged, never on a segment that a
 * cumulative ACK covered long after it arrived.
 *
 * At most 128 segments are in flight, and they never span more than 128 numbers, so the
 * selective map always covers all of them: 2.8 MB/s at a 50 ms round trip, plenty for the
 * 2 MB snapshot of phase 4. Within that, an AIMD congestion window decides: slow start from
 * 16 segments, cut to 0.7 once per loss episode (CUBIC's factor, gentler than Reno's half on
 * the random losses of a home line), back to 2 only after three probe timeouts in a row (the
 * line is gone, not congested).
 *
 * Both halves belong to one connection and run on the network thread only.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE.Netplay
{
    /// <summary>Round-trip time of a connection (RFC 6298), fed by pings and by the newest
    /// packet each ACK covers.</summary>
    internal sealed class RttEstimator
    {
        public const long InitialRto = 300;
        public const long MinRto = 50;
        public const long MaxRto = 2000;

        double _srtt = -1;
        double _rttVar;

        public bool HasSample => _srtt >= 0;

        /// <summary>Smoothed round trip, in milliseconds (0 before the first sample).</summary>
        public double Srtt => Math.Max(_srtt, 0);

        /// <summary>The most recent sample, in milliseconds.</summary>
        public double Latest { get; private set; }

        public void Sample(double ms)
        {
            if (ms < 0)
                return;

            Latest = ms;

            if (_srtt < 0)
            {
                _srtt = ms;
                _rttVar = ms / 2;
            }
            else
            {
                _rttVar = 0.75 * _rttVar + 0.25 * Math.Abs(_srtt - ms);
                _srtt = 0.875 * _srtt + 0.125 * ms;
            }
        }

        /// <summary>The probe timeout before any backoff, in milliseconds.</summary>
        public long Rto => _srtt < 0 ? InitialRto : Math.Clamp((long)Math.Ceiling(_srtt + 4 * _rttVar), MinRto, MaxRto);
    }

    /// <summary>The sending half: segments, loss recovery and the congestion window.</summary>
    internal sealed class ReliableSender
    {
        public const int InitialWindow = 16;
        public const int MaxWindow = 128;

        /// <summary>Segments in flight never span more numbers than the selective map covers.</summary>
        public const int MaxSpan = 128;

        const int MinWindow = 2;
        const int Probes = 2;

        // What a loss episode leaves of the window
        const double Backoff = 0.7;

        // RFC 9002: lost when a packet this many numbers later was received…
        const ulong PacketThreshold = 3;
        // …or when a later one was and this one is older than this share of the round trip
        const double TimeThreshold = 9.0 / 8.0;
        // Probe timeouts in a row that mean the line is gone rather than congested
        const int PersistentCongestion = 3;

        internal sealed class Segment
        {
            public uint Seq;
            public byte Flags;
            public byte[] Data;
            public int Offset;
            public int Length;

            public int Sends;
            public long LastSent;
            public ulong LastPacket;    // the packet carrying its latest transmission (set when packed)
            public bool Acked;
            public bool Lost;           // declared lost, waiting to go out again
            public bool InFlight;       // counted in _inFlight
        }

        // Every segment not yet acknowledged, oldest first; the first _sent were sent at least once
        readonly List<Segment> _queue = new();
        int _head;
        int _sent;

        uint _nextSeq;
        int _inFlight;

        // The newest packet the other side has reported receiving with segments in it
        ulong _largestAcked;

        double _cwnd = InitialWindow;
        double _ssthresh = MaxWindow;
        long _recoveryStart = long.MinValue;    // losses of transmissions before this were already paid for

        long _lastSend;
        int _probeCount;

        /// <summary>Segments queued and not yet acknowledged.</summary>
        public int Pending => _queue.Count - _head;

        /// <summary>Total retransmissions so far (a measure of the line's losses).</summary>
        public long Retransmissions { get; private set; }

        public int CongestionWindow => (int)_cwnd;

        public void Enqueue(byte[] message)
        {
            int offset = 0;

            do
            {
                int length = Math.Min(NetWire.MaxSegmentData, message.Length - offset);
                byte flags = 0;
                if (offset == 0) flags |= NetWire.SegmentFirst;
                if (offset + length == message.Length) flags |= NetWire.SegmentLast;

                _queue.Add(new Segment { Seq = _nextSeq++, Flags = flags, Data = message, Offset = offset, Length = length });
                offset += length;
            }
            while (offset < message.Length);
        }

        /// <summary>
        /// Applies an ACK: the cumulative point, the selective map (bit i is segment
        /// next + 1 + i) and the newest packet received with segments. What it proves lost is
        /// marked for sending again. The round-trip sample is the caller's, who knows when that
        /// packet left.
        /// </summary>
        public void OnAck(uint next, ulong sackLow, ulong sackHigh, ulong largestPacket, long now, RttEstimator rtt)
        {
            // An acknowledgement of something never sent is a broken peer; ignore it
            if (SeqLess(_nextSeq, next))
                return;

            bool progress = false;

            for (int i = _head; i < _head + _sent; i++)
            {
                var s = _queue[i];
                if (s.Acked)
                    continue;

                bool isAcked = SeqLess(s.Seq, next);

                if (!isAcked)
                {
                    uint distance = s.Seq - next - 1;
                    isAcked = distance < 64 ? (sackLow & (1UL << (int)distance)) != 0
                            : distance < 128 && (sackHigh & (1UL << (int)(distance - 64))) != 0;
                }

                if (!isAcked)
                    continue;

                // Window growth: one segment per acknowledgement in slow start, one per window
                // after; nothing for transmissions from before the current loss episode began
                if (s.LastSent > _recoveryStart)
                    _cwnd = Math.Min(_cwnd + (_cwnd < _ssthresh ? 1 : 1 / _cwnd), MaxWindow);

                s.Acked = true;
                s.Lost = false;
                s.Data = null;      // the message stays alive only while a piece is unacknowledged
                if (s.InFlight)
                {
                    s.InFlight = false;
                    _inFlight--;
                }

                progress = true;
            }

            if (largestPacket > _largestAcked)
                _largestAcked = largestPacket;

            if (progress)
                _probeCount = 0;

            DetectLosses(now, rtt);

            // Drop the acknowledged run at the front
            while (_sent > 0 && _queue[_head].Acked)
            {
                _queue[_head] = null;
                _head++;
                _sent--;
            }

            if (_head > 1024 && _head * 2 > _queue.Count)
            {
                _queue.RemoveRange(0, _head);
                _head = 0;
            }
        }

        void DetectLosses(long now, RttEstimator rtt)
        {
            if (_largestAcked == 0)
                return;

            double lossDelay = Math.Max(TimeThreshold * Math.Max(rtt.Srtt, rtt.Latest), 1);

            for (int i = _head; i < _head + _sent; i++)
            {
                var s = _queue[i];

                // Not yet packed (LastPacket 0), or sent after the newest packet received
                if (s.Acked || s.Lost || s.LastPacket == 0 || s.LastPacket >= _largestAcked)
                    continue;

                if (_largestAcked - s.LastPacket >= PacketThreshold || now - s.LastSent >= lossDelay)
                {
                    s.Lost = true;
                    if (s.InFlight)
                    {
                        s.InFlight = false;
                        _inFlight--;
                    }
                    OnCongestion(s.LastSent, now);
                }
            }
        }

        // One reduction per loss episode: a loss of a transmission from before the episode
        // began belongs to it
        void OnCongestion(long sentAt, long now)
        {
            if (sentAt <= _recoveryStart)
                return;

            _recoveryStart = now;
            _ssthresh = Math.Max(_cwnd * Backoff, MinWindow);
            _cwnd = _ssthresh;
        }

        /// <summary>
        /// The segments to send now: those declared lost, then new ones, as far as the window
        /// allows; and, when nothing has come back for a probe timeout, the oldest outstanding
        /// segments as probes whatever the window says. Each is marked sent; the caller sets
        /// <see cref="Segment.LastPacket"/> as it packs them.
        /// </summary>
        public void CollectDue(long now, RttEstimator rtt, List<Segment> output)
        {
            DetectLosses(now, rtt);

            int window = Math.Min((int)_cwnd, MaxWindow);

            for (int i = _head; i < _head + _sent && _inFlight < window; i++)
            {
                var s = _queue[i];
                if (s.Lost)
                    Send(s, now, output, retransmission: true);
            }

            // New segments: within the window, and never further from the oldest outstanding
            // one than the selective map reaches
            while (_head + _sent < _queue.Count && _inFlight < window
                   && _queue[_head + _sent].Seq - _queue[_head].Seq < MaxSpan)
            {
                var s = _queue[_head + _sent];
                _sent++;
                Send(s, now, output, retransmission: false);
            }

            // Probe timeout: the line went quiet with something outstanding
            if (_sent > 0 && now - _lastSend >= rtt.Rto << Math.Min(_probeCount, 5))
            {
                int probes = 0;

                for (int i = _head; i < _head + _sent && probes < Probes; i++)
                {
                    var s = _queue[i];
                    if (s.Acked || output.Contains(s))
                        continue;

                    Send(s, now, output, retransmission: true);
                    probes++;
                }

                if (++_probeCount >= PersistentCongestion)
                {
                    _ssthresh = Math.Max(_cwnd * Backoff, MinWindow);
                    _cwnd = MinWindow;
                    _recoveryStart = now;
                }

                _lastSend = now;
            }
        }

        void Send(Segment s, long now, List<Segment> output, bool retransmission)
        {
            s.Lost = false;
            s.Sends++;
            s.LastSent = now;
            s.LastPacket = 0;
            _lastSend = now;

            if (!s.InFlight)
            {
                s.InFlight = true;
                _inFlight++;
            }

            if (retransmission)
                Retransmissions++;

            output.Add(s);
        }

        // Sequence numbers wrap at 2^32; the difference tells which comes first
        public static bool SeqLess(uint a, uint b) => (int)(a - b) < 0;
    }

    /// <summary>The receiving half: reordering, reassembly and the acknowledgement owed.</summary>
    internal sealed class ReliableReceiver
    {
        // How far ahead of the next expected segment anything is kept; the sender never spans
        // more than ReliableSender.MaxSpan, so this only bounds a misbehaving peer
        const uint MaxAhead = 4096;

        uint _next;
        readonly Dictionary<uint, (byte Flags, byte[] Data)> _ahead = new();

        readonly List<byte[]> _parts = new();
        int _partsSize;
        bool _inMessage;

        // The newest packet that brought segments: what the ACK reports, for the sender's
        // loss detection and round-trip sample
        ulong _largestPacket;

        /// <summary>Something arrived since the last ACK was written.</summary>
        public bool AckPending { get; set; }

        /// <summary>
        /// Takes a segment that came in packet <paramref name="packet"/> and appends every
        /// message it completes to <paramref name="delivered"/>. False when the peer broke the
        /// protocol (a message over the size limit, pieces out of shape); the connection is then
        /// closed.
        /// </summary>
        public bool OnSegment(uint seq, byte flags, ReadOnlySpan<byte> data, ulong packet, List<byte[]> delivered)
        {
            AckPending = true;

            if (packet > _largestPacket)
                _largestPacket = packet;

            if (ReliableSender.SeqLess(seq, _next))
                return true;                            // a resend of something already delivered

            if (seq - _next >= MaxAhead || _ahead.ContainsKey(seq))
                return true;

            _ahead[seq] = (flags, data.ToArray());

            while (_ahead.Remove(_next, out var piece))
            {
                _next++;

                if ((piece.Flags & NetWire.SegmentFirst) != 0)
                {
                    if (_inMessage)
                        return false;

                    _inMessage = true;
                    _parts.Clear();
                    _partsSize = 0;
                }
                else if (!_inMessage)
                {
                    return false;
                }

                _partsSize += piece.Data.Length;
                if (_partsSize > NetWire.MaxMessage)
                    return false;

                _parts.Add(piece.Data);

                if ((piece.Flags & NetWire.SegmentLast) != 0)
                {
                    byte[] message = new byte[_partsSize];
                    int at = 0;
                    foreach (var part in _parts)
                    {
                        part.CopyTo(message, at);
                        at += part.Length;
                    }

                    _parts.Clear();
                    _inMessage = false;
                    delivered.Add(message);
                }
            }

            return true;
        }

        /// <summary>Writes the ACK frame: the next segment expected, which of the 128 after it
        /// have arrived, and the newest packet that brought segments.</summary>
        public void WriteAck(ref WireWriter w)
        {
            ulong low = 0, high = 0;

            if (_ahead.Count > 0)
            {
                for (int i = 0; i < 64; i++)
                    if (_ahead.ContainsKey(_next + 1 + (uint)i))
                        low |= 1UL << i;

                for (int i = 0; i < 64; i++)
                    if (_ahead.ContainsKey(_next + 65 + (uint)i))
                        high |= 1UL << i;
            }

            w.U8(NetWire.FrameAck);
            w.U32(_next);
            w.U64(low);
            w.U64(high);
            w.U64(_largestPacket);

            AckPending = false;
        }
    }
}
