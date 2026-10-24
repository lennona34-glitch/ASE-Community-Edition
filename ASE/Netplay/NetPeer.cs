/*
 *
 * One connection of online play, once both ends share keys (NETPLAY.md §7.7): what it knows
 * about the other side, its two channels, the pings that measure it, and the packing of
 * everything it owes into protected datagrams.
 *
 * The other side is identified by the connection id it chose, never by its address: when a
 * NAT moves it to another port mid-game, the first packet that authenticates from the new
 * address moves the connection there.
 *
 * Network thread only.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ASE.Netplay
{
    internal sealed class NetPeer : IDisposable
    {
        public enum States
        {
            /// <summary>Keys agreed. A guest has sent JOIN and waits for the answer; a host
            /// waits for the JOIN.</summary>
            Joining,

            /// <summary>Host only: the JOIN arrived and the user has to accept or decline.</summary>
            AwaitingDecision,

            Connected,
            Closed,
        }

        const long PingInterval = 1000;
        const long BurstPingInterval = 100;
        const int BurstPings = 4;
        const int MaxQueuedDatagrams = 256;

        public readonly uint LocalId;       // what the other side puts in its packets to us
        public readonly uint RemoteId;      // what we put in ours
        public readonly bool IsHostSide;

        public IPEndPoint Address;
        public Socket Socket;
        public States State = States.Joining;

        public string Name = "";
        public string AseVersion = "";
        public string Game = "";

        public readonly long CreatedAt;
        public long LastHeard;

        // Host only: the Hello this connection answered, so a repeated Hello (the Welcome was
        // lost) gets the same Welcome instead of a second key exchange.
        public byte[] GuestNonce;
        public byte[] WelcomePacket;

        /// <summary>Made with an invitation code: its secret is in the keys, so nobody in the
        /// middle of the network can be standing in for the other side.</summary>
        public bool WithCode;

        public readonly RttEstimator Rtt = new();
        public long BytesIn, BytesOut;

        readonly PacketProtection _protection;
        readonly ReliableSender _sender = new();
        readonly ReliableReceiver _receiver = new();

        readonly Queue<(byte Channel, byte[] Data)> _datagrams = new();
        readonly Queue<ulong> _pongsOwed = new();
        readonly List<ReliableSender.Segment> _due = new();

        // When the latest packets left (microseconds), by packet number: the round trip of the
        // newest packet an ACK covers is measured from here
        const int SentRing = 1024;
        readonly ulong[] _sentNumbers = new ulong[SentRing];
        readonly long[] _sentAt = new long[SentRing];
        ulong _largestAcked;

        long _nextPing;
        int _burstLeft = BurstPings;

        byte _closeReason;
        string _closeText;
        int _closesToSend;

        public NetPeer(uint localId, uint remoteId, bool isHostSide, IPEndPoint address, Socket socket,
                       PacketProtection protection, long now)
        {
            LocalId = localId;
            RemoteId = remoteId;
            IsHostSide = isHostSide;
            Address = address;
            Socket = socket;
            _protection = protection;
            CreatedAt = now;
            LastHeard = now;
            _nextPing = now;
        }

        public long Retransmissions => _sender.Retransmissions;

        /// <summary>The CLOSE frames have gone out: the transport can forget this connection.</summary>
        public bool Finished => State == States.Closed && _closesToSend == 0;

        // ==================== Receiving ====================

        public sealed class Received
        {
            public readonly List<byte[]> Messages = new();
            public readonly List<(byte Channel, byte[] Data)> Datagrams = new();
            public bool Closed;
            public byte CloseReason;
            public string CloseText;
            public bool ProtocolError;
            public bool Moved;

            public void Clear()
            {
                Messages.Clear();
                Datagrams.Clear();
                Closed = ProtocolError = Moved = false;
                CloseText = null;
            }
        }

        /// <summary>
        /// Opens a protected datagram addressed to this connection and takes its frames. False
        /// when it does not authenticate (forged, corrupted, replayed), which changes nothing.
        /// </summary>
        public bool Receive(ReadOnlySpan<byte> datagram, IPEndPoint from, Socket socket, long now,
                            Span<byte> scratch, Received result)
        {
            if (!_protection.TryOpen(datagram, scratch, out int length, out ulong number))
                return false;

            LastHeard = now;
            BytesIn += datagram.Length;

            if (!from.Equals(Address))
            {
                Address = from;
                Socket = socket;
                result.Moved = true;
            }

            var r = new WireReader(scratch[..length]);

            while (r.Ok && r.Remaining > 0)
            {
                switch (r.U8())
                {
                    case NetWire.FramePing:
                        ulong stamp = r.U64();
                        if (r.Ok && _pongsOwed.Count < 16)
                            _pongsOwed.Enqueue(stamp);
                        break;

                    case NetWire.FramePong:
                        ulong sent = r.U64();
                        long nowUs = MicroClock();
                        if (r.Ok && sent <= (ulong)nowUs)
                            Rtt.Sample((nowUs - (long)sent) / 1000.0);
                        break;

                    case NetWire.FrameAck:
                        uint next = r.U32();
                        ulong sackLow = r.U64();
                        ulong sackHigh = r.U64();
                        ulong largest = r.U64();
                        if (!r.Ok)
                            break;

                        // The newest packet acknowledged gives the round trip (RFC 9002), never
                        // a segment a cumulative ACK covered long after it arrived
                        if (largest > _largestAcked)
                        {
                            _largestAcked = largest;
                            int slot = (int)(largest % SentRing);
                            if (_sentNumbers[slot] == largest)
                                Rtt.Sample((MicroClock() - _sentAt[slot]) / 1000.0);
                        }

                        _sender.OnAck(next, sackLow, sackHigh, largest, now, Rtt);
                        break;

                    case NetWire.FrameSegment:
                        uint seq = r.U32();
                        byte flags = r.U8();
                        var data = r.Bytes(r.U16());
                        if (r.Ok && !_receiver.OnSegment(seq, flags, data, number, result.Messages))
                        {
                            result.ProtocolError = true;
                            return true;
                        }
                        break;

                    case NetWire.FrameDatagram:
                        byte channel = r.U8();
                        var payload = r.Bytes(r.U16());
                        if (r.Ok)
                            result.Datagrams.Add((channel, payload.ToArray()));
                        break;

                    case NetWire.FrameClose:
                        byte reason = r.U8();
                        string text = r.Str();
                        if (r.Ok)
                        {
                            result.Closed = true;
                            result.CloseReason = reason;
                            result.CloseText = text;
                            State = States.Closed;
                            _closesToSend = 0;
                        }
                        return true;

                    default:
                        // An unknown frame cannot be skipped (it carries no length): the peer
                        // speaks something else, and the rest of the packet is unreadable.
                        result.ProtocolError = true;
                        return true;
                }
            }

            if (!r.Ok)
                result.ProtocolError = true;

            return true;
        }

        // ==================== Sending ====================

        public void SendMessage(byte[] message)
        {
            if (State != States.Closed)
                _sender.Enqueue(message);
        }

        public void SendDatagram(byte channel, byte[] data)
        {
            if (State == States.Closed || data.Length > NetWire.MaxDatagramData)
                return;

            // Unreliable on purpose: under congestion the oldest go, never the newest
            if (_datagrams.Count >= MaxQueuedDatagrams)
                _datagrams.Dequeue();

            _datagrams.Enqueue((channel, data));
        }

        /// <summary>Ends the connection from this side: CLOSE goes out twice (in two packets,
        /// so one loss does not hide it), then the transport forgets the connection.</summary>
        public void Close(byte reason, string text)
        {
            if (State == States.Closed)
                return;

            State = States.Closed;
            _closeReason = reason;
            _closeText = text ?? "";
            _closesToSend = 2;
        }

        /// <summary>
        /// Packs everything owed — pongs, a ping when due, the ACK, reliable segments the window
        /// allows, queued datagrams — into as few datagrams as fit, and sends them.
        /// </summary>
        public void Flush(long now, Span<byte> plain, Span<byte> datagram, Action<Socket, ReadOnlySpan<byte>, IPEndPoint> send)
        {
            if (State == States.Closed)
            {
                while (_closesToSend > 0)
                {
                    var c = new WireWriter(plain);
                    c.U8(NetWire.FrameClose);
                    c.U8(_closeReason);
                    c.Str(_closeText);
                    Emit(c.Written, datagram, send);
                    _closesToSend--;
                }
                return;
            }

            var w = new WireWriter(plain[..NetWire.MaxPlaintext]);

            while (_pongsOwed.Count > 0)
            {
                Make(ref w, NetWire.PingFrameSize, datagram, send);
                w.U8(NetWire.FramePong);
                w.U64(_pongsOwed.Dequeue());
            }

            if (now >= _nextPing)
            {
                Make(ref w, NetWire.PingFrameSize, datagram, send);
                w.U8(NetWire.FramePing);
                w.U64((ulong)MicroClock());

                _nextPing = now + (_burstLeft > 0 ? BurstPingInterval : PingInterval);
                if (_burstLeft > 0)
                    _burstLeft--;
            }

            if (_receiver.AckPending)
            {
                Make(ref w, NetWire.AckFrameSize, datagram, send);
                _receiver.WriteAck(ref w);
            }

            _due.Clear();
            _sender.CollectDue(now, Rtt, _due);

            foreach (var s in _due)
            {
                Make(ref w, NetWire.SegmentFrameHeader + s.Length, datagram, send);

                // The packet being built is the next one sealed
                s.LastPacket = _protection.NextPacketNumber;

                w.U8(NetWire.FrameSegment);
                w.U32(s.Seq);
                w.U8(s.Flags);
                w.U16((ushort)s.Length);
                w.Bytes(s.Data.AsSpan(s.Offset, s.Length));
            }

            while (_datagrams.Count > 0)
            {
                var (channel, data) = _datagrams.Dequeue();
                Make(ref w, NetWire.DatagramFrameHeader + data.Length, datagram, send);
                w.U8(NetWire.FrameDatagram);
                w.U8(channel);
                w.U16((ushort)data.Length);
                w.Bytes(data);
            }

            if (w.Position > 0)
                Emit(w.Written, datagram, send);
        }

        // Makes room for a frame of `size` bytes: sends the packet built so far if it would not fit
        void Make(ref WireWriter w, int size, Span<byte> datagram, Action<Socket, ReadOnlySpan<byte>, IPEndPoint> send)
        {
            if (w.Remaining >= size)
                return;

            Emit(w.Written, datagram, send);
            w.Position = 0;
        }

        void Emit(ReadOnlySpan<byte> plaintext, Span<byte> datagram, Action<Socket, ReadOnlySpan<byte>, IPEndPoint> send)
        {
            ulong number = _protection.NextPacketNumber;
            int slot = (int)(number % SentRing);
            _sentNumbers[slot] = number;
            _sentAt[slot] = MicroClock();

            int length = _protection.Seal(RemoteId, plaintext, datagram);
            BytesOut += length;
            send(Socket, datagram[..length], Address);
        }

        public void Dispose() => _protection.Dispose();

        /// <summary>Microseconds of a monotonic clock: what pings carry, fine enough for a
        /// round trip of a fraction of a millisecond on a local network.</summary>
        public static long MicroClock() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
    }
}
