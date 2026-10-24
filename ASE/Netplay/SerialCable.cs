/*
 *
 * The null-modem cable over the network (NETPLAY.md §6.2, phase 6): while a null-modem game
 * is connected, the ST's RS-232 port is wired to the other player's, crossed as a null-modem
 * cable crosses it — one machine's transmit data is the other's receive data, its RTS the
 * other's CTS, its DTR the other's DCD. Each player runs their own ST and their own copy of
 * the game, as with real machines and a real cable; nothing but what goes down the cable
 * travels. A cable has two ends, so a null-modem game is the host and one guest.
 *
 * As with the MIDI cable (MidiCable), what the USART puts on the line leaves in messages, not
 * byte by byte — but at the end of each quarter of a frame rather than of the frame, because
 * while the cable is plugged the emulation runs in slices paced by the wall clock
 * (ASEMain.CableSlices): two machines that each ran a frame ahead in a burst would make every
 * exchange cost a frame or two. What arrives is handed to MfpUsart.Receive on the network
 * thread itself (NetTransport.MessageHook), which queues it for the receiver to take at its
 * own line speed. The bytes leave as the program wrote them, masked to its word length:
 * parity, start and stop bits are each end's business.
 *
 * The modem-control lines go in the same message, so they stay in order with the data: the
 * sender's RTS and DTR as its PSG's port A drives them (bits 3 and 4, active low behind the
 * line drivers), sent whenever they change and twice a second besides (see LinesRepeatFrames).
 * At the other end they become CTS on GPIP2 and
 * DCD on GPIP1 (active low as well), applied at the end of each frame on the emulation
 * thread; unplugged, or before the other end has said anything, both read off — what an ST
 * reads with nothing in its port.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

namespace ASE.Netplay
{
    public static class SerialCable
    {
        /// <summary>Session message, either way: the sender's modem-control lines (one byte,
        /// <see cref="LineRts"/> and <see cref="LineDtr"/>) and the bytes its USART put on the
        /// line, if any.</summary>
        public const byte MsgSerialBytes = 0x47;

        const byte LineRts = 0x01;
        const byte LineDtr = 0x02;

        /// <summary>The other end of the cable. Swapped whole on the UI thread.</summary>
        sealed class Link
        {
            public NetTransport Transport;
            public uint Peer;

            /// <summary>The other end's RTS and DTR as it last said them; -1 until it has.
            /// Written on the network thread, read on the emulation thread.</summary>
            public volatile int RemoteLines = -1;

            /// <summary>This end's lines as last sent; -1 until they have been, so a new
            /// cable starts by saying them. Emulation thread.</summary>
            public int SentLines = -1;

            /// <summary>Frames since the lines were last said. Emulation thread.</summary>
            public int FramesSinceSent;
        }

        // How often the lines are said again when nothing changes them: the other end plugs its
        // side in only once it has been told the game is a null-modem one, and drops what came
        // before; and a cable may be in long before anything goes down it.
        const int LinesRepeatFrames = 25;

        static volatile Link _link;

        // What the USART sent since the last message. Emulation thread only.
        static byte[] _out = new byte[256];
        static int _outCount;

        /// <summary>Whether the ST's RS-232 port is on the network right now.</summary>
        public static bool Plugged => _link != null;

        // ==================== Wiring (UI thread) ====================

        /// <summary>Wires the RS-232 port to <paramref name="peer"/>'s. Plugging the same end
        /// in again changes nothing: the lines the other end already said stay valid.</summary>
        public static void Plug(NetTransport transport, uint peer)
        {
            var link = _link;
            if (link != null && link.Transport == transport && link.Peer == peer)
                return;

            _link = new Link { Transport = transport, Peer = peer };
        }

        /// <summary>Nothing in the RS-232 port any more.</summary>
        public static void Unplug() => _link = null;

        // ==================== The ST's side (emulation thread) ====================

        /// <summary>
        /// A byte the USART put on the line while the cable is plugged in. Reached from the
        /// MFP's timer update in the middle of a CPU slice, so it must not throw: it only
        /// appends to the frame's buffer.
        /// </summary>
        public static void Transmit(byte b)
        {
            if (_outCount == _out.Length)
                Array.Resize(ref _out, _out.Length * 2);

            _out[_outCount++] = b;
        }

        /// <summary>
        /// End of a frame: the other end's lines reach CTS and DCD, and what this end sent since
        /// the last slice leaves in one message, with its own lines whenever they changed.
        /// </summary>
        public static void EndFrame()
        {
            var link = _link;

            // A recording, a playback or a shared machine is never plugged in, and its lines
            // stay as its snapshot has them: on every run, and on every computer
            if (!MachineInput.Deterministic)
                ApplyRemoteLines(link);

            if (link == null)
            {
                // Unplugged since the bytes were sent: they went into a port with no cable
                _outCount = 0;
                return;
            }

            int lines = LocalLines();
            if (_outCount == 0 && lines == link.SentLines && ++link.FramesSinceSent < LinesRepeatFrames)
                return;

            Send(link, lines);
        }

        /// <summary>
        /// The end of a slice, inside a frame (ASEMain.CableSlices): what the USART has sent so
        /// far leaves now rather than at the end of the frame. Nothing to send, nothing sent.
        /// </summary>
        public static void Flush()
        {
            var link = _link;
            if (link != null && _outCount > 0)
                Send(link, LocalLines());
        }

        // One message with this end's lines and what the USART sent since the last one
        static void Send(Link link, int lines)
        {
            byte[] message = new byte[2 + _outCount];
            message[0] = MsgSerialBytes;
            message[1] = (byte)lines;
            Array.Copy(_out, 0, message, 2, _outCount);
            _outCount = 0;
            link.SentLines = lines;
            link.FramesSinceSent = 0;

            try
            {
                link.Transport.SendMessage(link.Peer, message);
            }
            catch (Exception)
            {
                // The transport only queues; nothing it does may take the frame loop down
            }
        }

        // RTS and DTR as the PSG's port A drives them: low is asserted, through the inverting
        // line drivers. A port A set as an input drives nothing, and its pull-ups read off.
        static int LocalLines()
        {
            var ym = ASEMain._ym;
            if (ym == null || (ym.ReadRegister(7) & 0x40) == 0)
                return 0;

            byte portA = ym.ReadRegister(14);
            return ((portA & 0x08) == 0 ? LineRts : 0) | ((portA & 0x10) == 0 ? LineDtr : 0);
        }

        // The other end's RTS onto CTS (GPIP2), its DTR onto DCD (GPIP1); both read 0 when on.
        // Only a change goes through the MFP, whose active edge can raise an interrupt.
        static void ApplyRemoteLines(Link link)
        {
            var mfp = ASEMain._mfp;
            if (mfp == null)
                return;

            int remote = link?.RemoteLines ?? -1;
            bool cts = remote >= 0 && (remote & LineRts) != 0;
            bool dcd = remote >= 0 && (remote & LineDtr) != 0;

            if (cts != ((mfp.GPIP & 0x04) == 0))
                mfp.SetGPIOBit(2, !cts);
            if (dcd != ((mfp.GPIP & 0x02) == 0))
                mfp.SetGPIOBit(1, !dcd);
        }

        // ==================== The other end (network thread) ====================

        /// <summary>
        /// NetTransport.MessageHook: takes the cable's messages on the network thread — the
        /// other end's lines, and its bytes for this end's receiver. False for anything that is
        /// not the cable's, which goes on to the next hook or the session's event queue.
        /// </summary>
        public static bool OnMessage(uint peer, byte[] message)
        {
            if (message.Length == 0 || message[0] != MsgSerialBytes)
                return false;

            var link = _link;
            if (link == null || peer != link.Peer || message.Length < 2)
                return true;

            link.RemoteLines = message[1] & (LineRts | LineDtr);

            if (message.Length > 2)
                MfpUsart.Receive(message.AsSpan(2));

            return true;
        }
    }
}
