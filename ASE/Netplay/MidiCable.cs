/*
 *
 * The MIDI cable over the network (NETPLAY.md §6.1, phase 3): while an online session is
 * connected, the ST's MIDI sockets are wired to the other players' instead of to the host's
 * MIDI ports or the built-in MT-32 — a ring, as MIDI Maze and the other MIDI network games
 * expect. Each player runs their own ST and their own copy of the game, exactly as with
 * real machines and real cables; nothing but the bytes on the wire travels.
 *
 * The ring is the host's to keep: it is machine 0, the guests follow in the order they were
 * accepted, and every machine's MIDI OUT goes to the next one's MIDI IN, the last one's back
 * to the host. A guest only ever talks to the host, so the host forwards what a guest sent to
 * whoever comes after it. With two players that is simply a cable each way. Someone leaving
 * re-forms the ring without them, like pulling one machine out of a real ring.
 *
 * Bytes leave raw — never through MidiStreamParser: a game owes the wire no valid MIDI, and
 * the parser reorders real-time messages. What the ACIA puts on the line goes out in messages,
 * not byte by byte: at the end of each quarter of a frame, because while a cable is plugged
 * the emulation runs in slices paced by the wall clock (ASEMain.CableSlices) — a machine that
 * emulated each frame in a burst and then slept would only see the previous machine's bytes in
 * its next burst, a frame or two per hop of the ring. The network carries 200 messages a
 * second at most instead of one per byte. What arrives is handed to MidiAcia.Receive on the
 * network thread itself (NetTransport.MessageHook), which queues it and delivers it at 31250
 * baud — nothing waits for the UI's 100 ms event pump.
 *
 * While the cable is plugged in, the ST's MIDI OUT reaches only the network (the configured
 * output hears nothing) and the host's own MIDI IN port is ignored: one cable per socket.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE.Netplay
{
    public static class MidiCable
    {
        /// <summary>Session message: bytes for the receiver's MIDI IN (host to guest), or the
        /// bytes the sender's MIDI OUT put on the wire (guest to host).</summary>
        public const byte MsgMidiBytes = NetTransport.FirstSessionMessage;

        /// <summary>Who the ST's MIDI sockets are wired to. Immutable, swapped whole.</summary>
        sealed class Link
        {
            public NetTransport Transport;
            public bool IsHost;

            /// <summary>Guest: the host's connection.</summary>
            public uint HostPeer;

            /// <summary>Host: the guests in ring order (machine 1, 2…; the host is 0).</summary>
            public uint[] Ring;
        }

        // Written on the UI thread, read on the emulation and network threads
        static volatile Link _link;

        // What the ACIA sent since the last message. Emulation thread only.
        static byte[] _out = new byte[256];
        static int _outCount;

        /// <summary>Whether the ST's MIDI sockets are on the network right now.</summary>
        public static bool Plugged => _link != null;

        // ==================== Wiring (UI thread) ====================

        /// <summary>Host: wires the MIDI sockets into a ring with <paramref name="guests"/>, in
        /// that order. No guests unplugs the cable.</summary>
        public static void PlugHost(NetTransport transport, IReadOnlyList<uint> guests)
        {
            _link = guests.Count == 0 ? null : new Link { Transport = transport, IsHost = true, Ring = [.. guests] };
        }

        /// <summary>Guest: wires the MIDI sockets to the host, which keeps the ring.</summary>
        public static void PlugGuest(NetTransport transport, uint hostPeer)
        {
            _link = new Link { Transport = transport, IsHost = false, HostPeer = hostPeer };
        }

        /// <summary>Back to the MIDI wiring the configuration chose.</summary>
        public static void Unplug() => _link = null;

        // ==================== The ST's MIDI OUT (emulation thread) ====================

        /// <summary>
        /// A byte the MIDI ACIA put on the wire while the cable is plugged in. Reached from the
        /// CPU's bus-write callback through MidiManager.TransmitByte, so it must not throw: it
        /// only appends to the frame's buffer.
        /// </summary>
        public static void Transmit(byte b)
        {
            if (_outCount == _out.Length)
                Array.Resize(ref _out, _out.Length * 2);

            _out[_outCount++] = b;
        }

        /// <summary>
        /// End of a frame: what the ST sent since the last slice leaves as one message — to the
        /// first guest in the ring when this is the host, to the host when this is a guest.
        /// Emulation thread.
        /// </summary>
        public static void EndFrame() => Flush();

        /// <summary>
        /// The end of a slice, inside a frame (ASEMain.CableSlices), or of the frame: what the
        /// ST has sent so far leaves now, as one message. Nothing to send, nothing sent.
        /// Emulation thread.
        /// </summary>
        public static void Flush()
        {
            if (_outCount == 0)
                return;

            var link = _link;
            int count = _outCount;
            _outCount = 0;

            // Unplugged since the bytes were sent: they went into a socket with no cable
            if (link == null)
                return;

            byte[] message = new byte[1 + count];
            message[0] = MsgMidiBytes;
            Array.Copy(_out, 0, message, 1, count);

            try
            {
                link.Transport.SendMessage(link.IsHost ? link.Ring[0] : link.HostPeer, message);
            }
            catch (Exception)
            {
                // The transport only queues; nothing it does may take the frame loop down
            }
        }

        // ==================== The ST's MIDI IN (network thread) ====================

        /// <summary>
        /// NetTransport.MessageHook: takes the MIDI messages on the network thread. On a guest
        /// they are its MIDI IN. On the host they are what a guest sent, and go to whoever comes
        /// after it in the ring — the host itself after the last guest. False for anything that
        /// is not MIDI, which goes on to the session's event queue.
        /// </summary>
        public static bool OnMessage(uint peer, byte[] message)
        {
            if (message.Length == 0 || message[0] != MsgMidiBytes)
                return false;

            var link = _link;
            if (link == null || message.Length == 1)
                return true;

            if (!link.IsHost)
            {
                if (peer == link.HostPeer)
                    MidiAcia.Receive(message[1..]);
                return true;
            }

            int index = Array.IndexOf(link.Ring, peer);
            if (index < 0)
                return true;        // not (or no longer) in the ring

            if (index + 1 < link.Ring.Length)
                link.Transport.SendMessage(link.Ring[index + 1], message);
            else
                MidiAcia.Receive(message[1..]);

            return true;
        }
    }
}
