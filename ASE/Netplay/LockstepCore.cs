/*
 *
 * The bookkeeping half of online play's lockstep (NETPLAY.md §5, phase 4): what every player
 * pressed, frame by frame, how it is combined into the one machine everybody runs, and the
 * datagrams that carry it. Nothing here knows about the emulator or the network, which is what
 * lets tools/NetTest run it over a simulated lossy line.
 *
 * Every computer runs the same ST. The input a player gives during local frame N is applied at
 * frame N + D on every machine (D, the delay, is fixed for an epoch and chosen from the round
 * trip), so it has D frames to travel; a frame does not run until every player's input for it
 * is there. That is the whole synchronisation: nothing else is exchanged to keep the machines
 * together, and the fingerprints compared once a second only check that it worked.
 *
 * Input travels on the unreliable channel with redundancy instead of retransmission: every
 * datagram carries whatever the receiver has not confirmed yet, and the receiver confirms by
 * saying which frame it needs next. A lost datagram costs nothing; the next one repeats it.
 *
 * The host is the hub of a star (NETPLAY.md §7.7): a guest only ever talks to the host, which
 * relays each guest's input to the others. With two players that is simply each side's input
 * going to the other.
 *
 * Frame commands (a disk swap, the flush a snapshot needs) are too big for a datagram and must
 * not be lost: they travel on the reliable channel, and a frame's input says how many commands
 * its player attached to it, so the frame waits for them too.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE.Netplay
{
    // ==================== What crosses the input boundary ====================

    public enum EventKind : byte
    {
        Key = 1,            // A = make/break byte for the IKBD stream
        Joysticks = 2,      // A = port 0, B = port 1 (whole states)
        MouseMotion = 3,    // A = dx, B = dy: host pixels in a recording, ST units in a shared game
        MouseButton = 4,    // A = 1 left / 0 right, B = 1 pressed / 0 released
        ConnectMouse = 5,   // A = 1 plugged into port 0, 0 unplugged
    }

    /// <summary>One input event, as the IKBD receives it.</summary>
    public readonly record struct InputEvent(EventKind Kind, int A, int B = 0);

    public enum CommandKind : byte
    {
        Media = 1,      // Drive = 0/1, Payload = the drive's disk (FloppyImage.SaveMedia, packed)
        FdcFlush = 2,   // an in-flight STX command completed early (Snapshot.Save)

        /// <summary>A snapshot asked for in a shared game: the machine that asked writes the file
        /// (Payload = its path, UTF-8), and it travels to the others as the FdcFlush it implies.</summary>
        Snapshot = 3,
    }

    /// <summary>A change to the machine made outside the input stream, at a frame boundary.</summary>
    public sealed record FrameCommand(CommandKind Kind, int Drive, byte[] Payload);

    // ==================== One player's controls ====================

    /// <summary>
    /// What a player's controls hold — or, combined, what the machine has been told: the keys
    /// down, both joystick ports, the mouse buttons, and whether a mouse is plugged into port 0.
    /// </summary>
    public sealed class InputState
    {
        public const int Size = 8 + 8 + 1 + 1 + 1 + 1;

        ulong _keys0, _keys1;   // ST scancodes 0-63 and 64-127

        public byte Joy0, Joy1;

        /// <summary>Mouse buttons held, in the IKBD's mask: 2 is the left one, 1 the right.</summary>
        public int Buttons;

        /// <summary>A mouse plugged into port 0.</summary>
        public bool Mouse;

        public bool KeyDown(int code) =>
            code < 64 ? ((_keys0 >> code) & 1) != 0 : ((_keys1 >> (code - 64)) & 1) != 0;

        void SetKey(int code, bool down)
        {
            if (code < 64)
                _keys0 = down ? _keys0 | (1UL << code) : _keys0 & ~(1UL << code);
            else
                _keys1 = down ? _keys1 | (1UL << (code - 64)) : _keys1 & ~(1UL << (code - 64));
        }

        /// <summary>Follows one event. Motion changes nothing held.</summary>
        public void Track(InputEvent e)
        {
            switch (e.Kind)
            {
                case EventKind.Key:
                    SetKey(e.A & 0x7F, (e.A & 0x80) == 0);
                    break;

                case EventKind.Joysticks:
                    Joy0 = (byte)e.A;
                    Joy1 = (byte)e.B;
                    break;

                case EventKind.MouseButton:
                    int mask = e.A != 0 ? 2 : 1;
                    Buttons = e.B != 0 ? Buttons | mask : Buttons & ~mask;
                    break;

                case EventKind.ConnectMouse:
                    Mouse = e.A != 0;
                    break;
            }
        }

        /// <summary>What reaches the machine from these controls: the buttons of a mouse that is
        /// not plugged in are pressed on nothing.</summary>
        public InputState Contribution()
        {
            var c = Clone();
            if (!c.Mouse)
                c.Buttons = 0;
            return c;
        }

        /// <summary>
        /// The events that take a machine that was told this state to <paramref name="target"/>.
        /// A mouse is plugged in before its buttons are pressed and unplugged after they are
        /// released, so no button is ever reported on a port without a mouse.
        /// </summary>
        public void DiffTo(InputState target, List<InputEvent> output)
        {
            if (target.Mouse && !Mouse)
                output.Add(new InputEvent(EventKind.ConnectMouse, 1));

            for (int half = 0; half < 2; half++)
            {
                ulong mine = half == 0 ? _keys0 : _keys1;
                ulong theirs = half == 0 ? target._keys0 : target._keys1;
                ulong changed = mine ^ theirs;

                for (int bit = 0; changed != 0; bit++, changed >>= 1)
                {
                    if ((changed & 1) == 0)
                        continue;

                    int code = half * 64 + bit;
                    output.Add(new InputEvent(EventKind.Key, target.KeyDown(code) ? code : code | 0x80));
                }
            }

            if (Joy0 != target.Joy0 || Joy1 != target.Joy1)
                output.Add(new InputEvent(EventKind.Joysticks, target.Joy0, target.Joy1));

            int buttons = Buttons ^ target.Buttons;
            if ((buttons & 2) != 0)
                output.Add(new InputEvent(EventKind.MouseButton, 1, (target.Buttons & 2) != 0 ? 1 : 0));
            if ((buttons & 1) != 0)
                output.Add(new InputEvent(EventKind.MouseButton, 0, (target.Buttons & 1) != 0 ? 1 : 0));

            if (!target.Mouse && Mouse)
                output.Add(new InputEvent(EventKind.ConnectMouse, 0));
        }

        /// <summary>Lets go of every key and both ports: what the host's input forgets on a
        /// power-on (HostInput.Reset), so it never sends their releases.</summary>
        public void ReleaseKeysAndPorts()
        {
            _keys0 = _keys1 = 0;
            Joy0 = Joy1 = 0;
        }

        public void Clear()
        {
            _keys0 = _keys1 = 0;
            Joy0 = Joy1 = 0;
            Buttons = 0;
            Mouse = false;
        }

        /// <summary>Adds what <paramref name="other"/> puts into the machine: its keys and port
        /// bits, and its mouse with its buttons if it has one plugged in.</summary>
        public void Combine(InputState other)
        {
            _keys0 |= other._keys0;
            _keys1 |= other._keys1;
            Joy0 |= other.Joy0;
            Joy1 |= other.Joy1;

            if (other.Mouse)
            {
                Mouse = true;
                Buttons |= other.Buttons;
            }
        }

        public void CopyFrom(InputState other)
        {
            _keys0 = other._keys0;
            _keys1 = other._keys1;
            Joy0 = other.Joy0;
            Joy1 = other.Joy1;
            Buttons = other.Buttons;
            Mouse = other.Mouse;
        }

        public InputState Clone()
        {
            var c = new InputState();
            c.CopyFrom(this);
            return c;
        }

        public bool SameAs(InputState other) =>
            _keys0 == other._keys0 && _keys1 == other._keys1 && Joy0 == other.Joy0 && Joy1 == other.Joy1
            && Buttons == other.Buttons && Mouse == other.Mouse;

        public byte[] ToBytes()
        {
            var b = new byte[Size];
            var w = new WireWriter(b);
            w.U64(_keys0);
            w.U64(_keys1);
            w.U8(Joy0);
            w.U8(Joy1);
            w.U8((byte)Buttons);
            w.U8(Mouse ? (byte)1 : (byte)0);
            return b;
        }

        /// <summary>Null when <paramref name="data"/> is not one.</summary>
        public static InputState FromBytes(ReadOnlySpan<byte> data)
        {
            var r = new WireReader(data);
            var s = new InputState
            {
                _keys0 = r.U64(),
                _keys1 = r.U64(),
                Joy0 = r.U8(),
                Joy1 = r.U8(),
                Buttons = r.U8() & 3,
                Mouse = r.U8() != 0,
            };
            return r.Ok ? s : null;
        }
    }

    // ==================== The players, combined ====================

    /// <summary>
    /// Combines every player's input into the one machine they share (NETPLAY.md §5.2): both
    /// ports and the keyboard belong to everybody, and each player decides on their own computer
    /// what goes into each port. A port reads the OR of what every player put into it, a key is
    /// down while anybody holds it, a mouse button likewise, and mouse motion adds up — only from
    /// players with a mouse in port 0. The machine is told only about changes of the combination,
    /// so two players holding the same key give one make and one break, the break from whoever
    /// lets go last. Every machine feeds it the same events in the same order, so every machine
    /// tells its IKBD the same things.
    /// </summary>
    public sealed class InputMerger
    {
        readonly InputState[] _players;
        readonly InputState _machine;
        readonly InputState _combined = new();

        /// <param name="players">What each player holds, in player order (copied).</param>
        /// <param name="machine">What the machine has been told so far (copied).</param>
        public InputMerger(IReadOnlyList<InputState> players, InputState machine)
        {
            _players = players.Select(p => p.Clone()).ToArray();
            _machine = machine.Clone();
        }

        public int Players => _players.Length;

        /// <summary>A copy of what player <paramref name="p"/> holds.</summary>
        public InputState Player(int p) => _players[p].Clone();

        /// <summary>A copy of what the machine has been told.</summary>
        public InputState Machine => _machine.Clone();

        /// <summary>Tells the machine whatever separates what it was told from what the players
        /// hold: the first thing a new epoch does, when someone joined or left.</summary>
        public void Settle(List<InputEvent> output) => Update(output);

        public void Apply(int player, InputEvent e, List<InputEvent> output)
        {
            var p = _players[player];

            if (e.Kind == EventKind.MouseMotion)
            {
                if (p.Mouse && (e.A != 0 || e.B != 0))
                    output.Add(e);
                return;
            }

            p.Track(e);
            Update(output);
        }

        /// <summary>The events that leave the machine with <paramref name="local"/>'s input only:
        /// a shared game ending, every other player's keys and buttons let go.</summary>
        public void Leave(InputState local, List<InputEvent> output)
        {
            var target = local.Contribution();
            _machine.DiffTo(target, output);
            _machine.CopyFrom(target);
        }

        void Update(List<InputEvent> output)
        {
            _combined.Clear();
            foreach (var p in _players)
                _combined.Combine(p);

            if (_combined.SameAs(_machine))
                return;

            _machine.DiffTo(_combined, output);
            _machine.CopyFrom(_combined);
        }
    }

    // ==================== The input of every player, frame by frame ====================

    /// <summary>
    /// Every player's input for every frame of an epoch, what each other machine has confirmed,
    /// and the datagrams that carry it (see the top of this file). Thread-safe: the emulation
    /// thread produces and takes frames, the network thread hands in what arrives.
    /// </summary>
    public sealed class LockstepExchange
    {
        /// <summary>The unreliable channel the input travels on.</summary>
        public const byte Channel = 1;

        /// <summary>Most events one player's frame may carry, so that it always fits in a
        /// datagram on its own; the rest wait for the next frame.</summary>
        public const int MaxEventsPerFrame = 100;

        /// <summary>Most commands one player may attach to a frame.</summary>
        public const int MaxCommandsPerFrame = 16;

        const int StreamHeader = 1 + 4 + 2;

        sealed class FrameInput
        {
            public InputEvent[] Events;
            public int Commands;
        }

        sealed class Stream
        {
            public readonly Dictionary<long, FrameInput> Frames = new();
            public readonly Dictionary<long, List<FrameCommand>> Commands = new();

            /// <summary>Frames [Low, Next) are held; Next is the first one not received yet.</summary>
            public long Low, Next;
        }

        readonly object _lock = new();
        readonly Stream[] _streams;

        // [peer, stream]: the next frame of that stream the peer says it needs. Only the pairs
        // this machine sends are used: on the host every guest and every stream but its own, on
        // a guest the host and the guest's own stream.
        readonly long[,] _acked;

        // The frame each peer said it was about to run, for the status line
        readonly long[] _peerFrame;

        long _taken = -1;
        int _rotation;

        public ushort Epoch { get; }
        public int Players { get; }

        /// <summary>This machine's player index; the host is 0.</summary>
        public int Me { get; }

        /// <summary>Frames between a player's input and the frame it lands on.</summary>
        public int Delay { get; }

        public bool IsHost => Me == 0;

        public LockstepExchange(ushort epoch, int players, int me, int delay)
        {
            if (players < 1 || players > 16 || me < 0 || me >= players || delay < 1)
                throw new ArgumentException("Not a lockstep epoch.");

            Epoch = epoch;
            Players = players;
            Me = me;
            Delay = delay;

            // Nobody produces input for the first D frames: they are empty for everyone
            _streams = new Stream[players];
            for (int p = 0; p < players; p++)
                _streams[p] = new Stream { Low = delay, Next = delay };

            _acked = new long[players, players];
            for (int a = 0; a < players; a++)
                for (int b = 0; b < players; b++)
                    _acked[a, b] = delay;

            _peerFrame = new long[players];
        }

        // ==================== Local input (emulation thread) ====================

        /// <summary>
        /// This machine's input for <paramref name="frame"/> (the local frame being started plus
        /// the delay), with the number of commands attached to it, which go in through
        /// <see cref="AddCommand"/>. Frames are produced in order, one each.
        /// </summary>
        public void Produce(long frame, IReadOnlyList<InputEvent> events, int commands)
        {
            if (events.Count > MaxEventsPerFrame || commands > MaxCommandsPerFrame)
                throw new ArgumentException("Too much input for one frame.");

            lock (_lock)
            {
                var s = _streams[Me];
                if (frame != s.Next)
                    throw new InvalidOperationException($"Input for frame {frame} produced out of order (expected {s.Next}).");

                s.Frames[frame] = new FrameInput { Events = events.ToArray(), Commands = commands };
                s.Next = frame + 1;
            }
        }

        /// <summary>The frame after the last one this machine produced input for.</summary>
        public long Produced
        {
            get { lock (_lock) return _streams[Me].Next; }
        }

        // ==================== Running frames (emulation thread) ====================

        /// <summary>
        /// Whether frame <paramref name="frame"/> has everything it needs: every player's input
        /// and every command that input announced. When not, <paramref name="missing"/> is the
        /// first player it waits for.
        /// </summary>
        public bool Ready(long frame, out int missing)
        {
            missing = -1;

            if (frame < Delay)
                return true;

            lock (_lock)
            {
                for (int p = 0; p < Players; p++)
                {
                    var s = _streams[p];

                    if (frame >= s.Next || !s.Frames.TryGetValue(frame, out var input))
                    {
                        missing = p;
                        return false;
                    }

                    if (input.Commands > 0 && (!s.Commands.TryGetValue(frame, out var list) || list.Count < input.Commands))
                    {
                        missing = p;
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Hands over frame <paramref name="frame"/> (which must be <see cref="Ready"/>): every
        /// player's events and commands, in player order. Frames are taken in order, one each.
        /// </summary>
        public void Take(long frame, List<(int Player, InputEvent Event)> events, List<(int Player, FrameCommand Command)> commands)
        {
            lock (_lock)
            {
                if (frame != _taken + 1)
                    throw new InvalidOperationException($"Frame {frame} taken out of order (expected {_taken + 1}).");

                if (frame >= Delay)
                {
                    for (int p = 0; p < Players; p++)
                    {
                        var s = _streams[p];
                        var input = s.Frames[frame];

                        foreach (var e in input.Events)
                            events.Add((p, e));

                        if (input.Commands > 0)
                            foreach (var c in s.Commands[frame].Take(input.Commands))
                                commands.Add((p, c));
                    }
                }

                _taken = frame;
                Trim();
            }
        }

        // What nobody can ask for again: frames taken here and confirmed by every machine this
        // one forwards them to
        void Trim()
        {
            for (int q = 0; q < Players; q++)
            {
                var s = _streams[q];
                long low = _taken + 1;

                if (IsHost)
                {
                    for (int g = 1; g < Players; g++)
                        if (g != q)
                            low = Math.Min(low, _acked[g, q]);
                }
                else if (q == Me)
                {
                    low = Math.Min(low, _acked[0, Me]);
                }

                for (; s.Low < low && s.Low < s.Next; s.Low++)
                    s.Frames.Remove(s.Low);

                if (s.Commands.Count > 0)
                    foreach (long f in s.Commands.Keys.Where(f => f <= _taken).ToList())
                        s.Commands.Remove(f);
            }
        }

        /// <summary>The frame player <paramref name="player"/> last said it was about to run
        /// (0 before it said anything).</summary>
        public long PeerFrame(int player)
        {
            lock (_lock) return _peerFrame[player];
        }

        // ==================== Datagrams ====================

        /// <summary>
        /// The datagram for player <paramref name="to"/>: which frames this machine needs next of
        /// what that player sends it, and every frame it owes that player and has not had
        /// confirmed, as many as fit. <paramref name="currentFrame"/> is the frame this machine is
        /// about to run.
        /// </summary>
        public byte[] BuildDatagram(int to, long currentFrame)
        {
            if (to == Me || to < 0 || to >= Players || (!IsHost && to != 0))
                throw new ArgumentException("No such peer.", nameof(to));

            var buffer = new byte[NetWire.MaxDatagramData];
            var w = new WireWriter(buffer);

            lock (_lock)
            {
                w.U16(Epoch);
                w.U32((uint)Math.Max(currentFrame, 0));

                // What this machine needs next: on the host, of the guest's own stream; on a
                // guest, of every stream the host forwards to it
                if (IsHost)
                {
                    w.U8(1);
                    w.U8((byte)to);
                    w.U32((uint)_streams[to].Next);
                }
                else
                {
                    w.U8((byte)(Players - 1));
                    for (int q = 0; q < Players; q++)
                    {
                        if (q == Me)
                            continue;
                        w.U8((byte)q);
                        w.U32((uint)_streams[q].Next);
                    }
                }

                // What it owes: the host forwards every stream but the guest's own, a guest sends
                // its own. Taken in turns, so a big frame of one stream is not always last.
                int countAt = w.Position;
                w.U8(0);
                int blocks = 0;

                int[] order = IsHost
                    ? Enumerable.Range(0, Players).Where(q => q != to).ToArray()
                    : [Me];

                int start = order.Length > 1 ? _rotation++ % order.Length : 0;

                for (int i = 0; i < order.Length; i++)
                {
                    int q = order[(start + i) % order.Length];
                    var s = _streams[q];
                    long from = Math.Max(_acked[to, q], s.Low);

                    if (from >= s.Next || w.Remaining < StreamHeader + 2)
                        continue;

                    int headerAt = w.Position;
                    w.U8((byte)q);
                    w.U32((uint)from);
                    int frameCountAt = w.Position;
                    w.U16(0);

                    int frames = 0;
                    for (long f = from; f < s.Next && frames < ushort.MaxValue; f++)
                    {
                        var input = s.Frames[f];
                        if (RecordSize(input) > w.Remaining)
                            break;

                        WriteRecord(ref w, input);
                        frames++;
                    }

                    if (frames == 0)
                    {
                        w.Position = headerAt;
                        continue;
                    }

                    int end = w.Position;
                    w.Position = frameCountAt;
                    w.U16((ushort)frames);
                    w.Position = end;
                    blocks++;
                }

                int length = w.Position;
                w.Position = countAt;
                w.U8((byte)blocks);
                w.Position = length;
            }

            return buffer.AsSpan(0, w.Position).ToArray();
        }

        /// <summary>
        /// A datagram from player <paramref name="from"/>. False when it does not belong to this
        /// epoch or does not parse; what parsed before a problem is kept.
        /// </summary>
        public bool OnDatagram(int from, ReadOnlySpan<byte> data)
        {
            if (from == Me || from < 0 || from >= Players || (!IsHost && from != 0))
                return false;

            var r = new WireReader(data);
            ushort epoch = r.U16();
            uint peerFrame = r.U32();

            if (!r.Ok || epoch != Epoch)
                return false;

            lock (_lock)
            {
                _peerFrame[from] = Math.Max(_peerFrame[from], peerFrame);

                int acks = r.U8();
                for (int i = 0; i < acks && r.Ok; i++)
                {
                    int q = r.U8();
                    long next = r.U32();

                    // A guest confirms what the host forwards it (every stream but its own); the
                    // host confirms the guest's own stream
                    bool meaningful = q < Players && (IsHost ? q != from : q == Me);
                    if (!r.Ok || !meaningful)
                        return false;

                    // Never further than what exists: a peer cannot make this machine forget input
                    _acked[from, q] = Math.Max(_acked[from, q], Math.Min(next, _streams[q].Next));
                }

                int blocks = r.U8();
                for (int i = 0; i < blocks && r.Ok; i++)
                {
                    int q = r.U8();
                    long first = r.U32();
                    int frames = r.U16();

                    // A guest sends its own input; the host sends everyone's but the receiver's
                    bool allowed = q < Players && (IsHost ? q == from : q != Me);
                    if (!r.Ok || !allowed)
                        return false;

                    var s = _streams[q];

                    for (int k = 0; k < frames; k++)
                    {
                        var input = ReadRecord(ref r);
                        if (input == null)
                            return false;

                        if (first + k == s.Next)
                        {
                            s.Frames[s.Next] = input;
                            s.Next++;
                        }
                    }
                }

                // Confirmations may have released input the host was keeping for a guest
                Trim();
            }

            return r.Ok;
        }

        static int RecordSize(FrameInput input)
        {
            int size = 2;
            foreach (var e in input.Events)
                size += 1 + EventSize(e.Kind);
            return size;
        }

        static int EventSize(EventKind kind) => kind switch
        {
            EventKind.Joysticks => 2,
            EventKind.MouseMotion => 4,
            _ => 1,
        };

        static void WriteRecord(ref WireWriter w, FrameInput input)
        {
            w.U8((byte)input.Events.Length);

            foreach (var e in input.Events)
            {
                w.U8((byte)e.Kind);

                switch (e.Kind)
                {
                    case EventKind.Joysticks:
                        w.U8((byte)e.A);
                        w.U8((byte)e.B);
                        break;
                    case EventKind.MouseMotion:
                        w.U16((ushort)(short)e.A);
                        w.U16((ushort)(short)e.B);
                        break;
                    case EventKind.MouseButton:
                        w.U8((byte)((e.A != 0 ? 1 : 0) | (e.B != 0 ? 2 : 0)));
                        break;
                    default:
                        w.U8((byte)e.A);
                        break;
                }
            }

            w.U8((byte)input.Commands);
        }

        static FrameInput ReadRecord(ref WireReader r)
        {
            int count = r.U8();
            if (count > MaxEventsPerFrame)
                return null;

            var events = new InputEvent[count];

            for (int i = 0; i < count; i++)
            {
                var kind = (EventKind)r.U8();

                switch (kind)
                {
                    case EventKind.Key:
                        events[i] = new InputEvent(kind, r.U8());
                        break;
                    case EventKind.ConnectMouse:
                        events[i] = new InputEvent(kind, r.U8() != 0 ? 1 : 0);
                        break;
                    case EventKind.Joysticks:
                        events[i] = new InputEvent(kind, r.U8(), r.U8());
                        break;
                    case EventKind.MouseMotion:
                        events[i] = new InputEvent(kind, (short)r.U16(), (short)r.U16());
                        break;
                    case EventKind.MouseButton:
                        byte b = r.U8();
                        events[i] = new InputEvent(kind, b & 1, (b >> 1) & 1);
                        break;
                    default:
                        return null;
                }
            }

            int commands = r.U8();
            if (!r.Ok || commands > MaxCommandsPerFrame)
                return null;

            return new FrameInput { Events = events, Commands = commands };
        }

        // ==================== Commands (reliable channel) ====================

        /// <summary>
        /// A command player <paramref name="player"/> attached to <paramref name="frame"/> — this
        /// machine's own, or one that arrived. False when it does not fit this epoch: from a
        /// machine that cannot have sent it, for a frame already run, or not one at all.
        /// </summary>
        public bool AddCommand(int from, int player, long frame, FrameCommand command)
        {
            if (player < 0 || player >= Players || command == null || command.Drive is < 0 or > 1)
                return false;

            // The own commands go in locally; a guest's arrive from that guest at the host; at a
            // guest, everyone else's arrive from the host
            bool allowed = from == Me ? player == Me
                         : IsHost ? player == from
                         : from == 0 && player != Me;

            if (!allowed)
                return false;

            lock (_lock)
            {
                if (frame <= _taken)
                    return false;

                var s = _streams[player];
                if (!s.Commands.TryGetValue(frame, out var list))
                    s.Commands[frame] = list = new List<FrameCommand>();

                if (list.Count >= MaxCommandsPerFrame)
                    return false;

                list.Add(command);
            }

            return true;
        }

        /// <summary>A command as a session message: type, epoch, player, frame, kind, drive and
        /// payload.</summary>
        public static byte[] EncodeCommand(byte type, ushort epoch, int player, long frame, FrameCommand command)
        {
            byte[] payload = command.Payload ?? [];
            var message = new byte[1 + 2 + 1 + 4 + 1 + 1 + 4 + payload.Length];
            var w = new WireWriter(message);

            w.U8(type);
            w.U16(epoch);
            w.U8((byte)player);
            w.U32((uint)frame);
            w.U8((byte)command.Kind);
            w.U8((byte)command.Drive);
            w.U32(command.Payload == null ? uint.MaxValue : (uint)payload.Length);
            w.Bytes(payload);

            return message;
        }

        /// <summary>Reverses <see cref="EncodeCommand"/>; false on anything that is not one.</summary>
        public static bool DecodeCommand(ReadOnlySpan<byte> message, out ushort epoch, out int player, out long frame, out FrameCommand command)
        {
            var r = new WireReader(message);
            r.U8();
            epoch = r.U16();
            player = r.U8();
            frame = r.U32();
            var kind = (CommandKind)r.U8();
            int drive = r.U8();
            uint length = r.U32();

            byte[] payload = length == uint.MaxValue ? null : r.Bytes((int)Math.Min(length, int.MaxValue)).ToArray();

            command = null;

            if (!r.Ok || r.Remaining != 0 || (kind != CommandKind.Media && kind != CommandKind.FdcFlush) || drive > 1)
                return false;

            command = new FrameCommand(kind, drive, payload);
            return true;
        }
    }
}
