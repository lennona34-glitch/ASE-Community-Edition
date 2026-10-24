/*
 *
 * The USART inside the MFP 68901: the ST's RS-232 port.
 *
 * References:
 * http://www.bitsavers.org/components/motorola/68000/MC68901_Multi-Function_Peripheral_Jan84.pdf
 * MAME's mc68901.cpp (register semantics) and the MiSTer core's usart_*.vhd (WF68901IP, gate level)
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// The MFP's USART ($FFFA27-$FFFA2F): SCR, UCR, RSR, TSR and UDR. On the ST both its
    /// transmit and receive clocks are Timer D's output, so every bit on the line is a number
    /// of Timer D timeouts — 32 in the ÷16 mode TOS uses (TDO toggles once per timeout, and
    /// the USART takes 16 TDO periods per bit), 2 in ÷1. TOS' default of 9600 baud is Timer D
    /// at ÷4 with a data register of 2: 307200 timeouts a second, 320 per 8N1 character.
    /// <see cref="MFP68901"/> hands the timeouts over as they happen (<see cref="Clock"/>), so
    /// a stopped Timer D stops the line, as on the real chip.
    ///
    /// The model works in characters, not bits — like <see cref="MidiAcia"/>: a character
    /// occupies the line for the frame its format gives it (start, data, parity and stop
    /// bits), the transmitter is double buffered (UDR, then the shift register), and the
    /// receiver takes the bytes of whatever is plugged into the port (<see cref="Receive"/>)
    /// one frame at a time into its single buffer, with overrun when the program does not
    /// read in time. Parity and framing errors never happen (the bytes arrive clean), break
    /// is neither sent nor detected, and synchronous mode only drops the start and stop bits
    /// from the frame: no sync-character fill, search or strip.
    ///
    /// Three choices where the references disagree or say nothing, all of them about the
    /// transmitter. With TE clear the buffer reads empty and a write to UDR is lost (MiSTer:
    /// a program that polls BE with the transmitter off never hangs; Hatari goes further and
    /// always answers "empty", because Treasure Trap and The Deep write debug output to the
    /// port). Underrun is raised when a character ends and nothing is waiting (MAME), with an
    /// interrupt on the transmit error channel; END, when the transmitter is disabled and the
    /// line is idle, on the same channel (MiSTer). Loopback (TSR H and L both set) feeds the
    /// transmitter straight into the receiver instead of the port.
    ///
    /// Emulation thread, except <see cref="Receive"/>, which the network thread calls.
    /// </summary>
    public static class MfpUsart
    {
        // Register offsets from MFP68901.MFP_BASE
        public const uint SCR = 0x27, UCR = 0x29, RSR = 0x2B, TSR = 0x2D, UDR = 0x2F;

        // UCR: clock divide, word length (8 - bits 6-5), format (sync / 1 / 1.5 / 2 stop bits),
        // parity enable, even parity
        const byte UCR_DIV16 = 0x80;
        const byte UCR_PARITY = 0x04;

        // RSR
        const byte RSR_RE = 0x01;       // receiver enable
        const byte RSR_SS = 0x02;       // sync strip enable
        const byte RSR_CIP = 0x04;      // character in progress (asynchronous mode)
        const byte RSR_BREAK = 0x08;
        const byte RSR_FE = 0x10;
        const byte RSR_PE = 0x20;
        const byte RSR_OE = 0x40;       // overrun
        const byte RSR_BF = 0x80;       // buffer full

        // TSR
        const byte TSR_TE = 0x01;       // transmitter enable
        const byte TSR_LOOP = 0x06;     // H and L both set: loopback
        const byte TSR_END = 0x10;      // end of transmission
        const byte TSR_AT = 0x20;       // auto turnaround
        const byte TSR_UE = 0x40;       // underrun
        const byte TSR_BE = 0x80;       // buffer empty
        const byte TSR_WRITABLE = 0x2F; // TE, L, H, B, AT

        static readonly object _syncLock = new();

        static byte _scr, _ucr, _rsr;
        static byte _tsrControl;            // the TSR bits the program writes
        static bool _txEnd, _txUnderrun;

        // Transmitter: the buffer (UDR as written) and the shift register's remaining time
        static byte _txData;
        static bool _txFull;
        static int _txRemaining;            // Timer D timeouts left on the line; 0 = idle

        // Receiver: the buffer (UDR as read), the character on the line and its remaining time,
        // and an overrun that is reported when the buffer is read, as the chip does
        static byte _rxData;
        static byte _rxIncoming;
        static int _rxRemaining;
        static bool _overrunPending;

        // Bytes from whatever is plugged into the port, waiting to come down the line. Filled
        // on any thread; _rxQueued lets the timeout path see there is work without the lock.
        static readonly Queue<byte> _rxQueue = new();
        const int RX_QUEUE_MAX = 4096;      // seconds of any baud rate an ST can set
        static volatile int _rxQueued;

        static bool TransmitterOn => (_tsrControl & TSR_TE) != 0;
        static bool ReceiverOn => (_rsr & RSR_RE) != 0;
        static bool Synchronous => (_ucr & 0x18) == 0;
        static int WordMask => 0xFF >> ((_ucr >> 5) & 3);

        /// <summary>Power-on: every register cleared but the data, the transmitter disabled
        /// and therefore ended, nothing on the line.</summary>
        public static void Reset()
        {
            lock (_syncLock)
            {
                _scr = _ucr = _rsr = 0;
                _tsrControl = 0;
                _txEnd = true;
                _txUnderrun = false;
                _txFull = false;
                _txRemaining = 0;
                _rxRemaining = 0;
                _overrunPending = false;
                _rxQueue.Clear();
                _rxQueued = 0;
            }
        }

        // ==================== Registers (CPU bus, emulation thread) ====================

        public static byte Read(uint offset)
        {
            switch (offset)
            {
                case SCR:
                    return _scr;

                case UCR:
                    return (byte)(_ucr & 0xFE);     // bit 0 is not there

                case RSR:
                    {
                        // Reading the status is what clears an overrun
                        byte rsr = _rsr;
                        _rsr &= unchecked((byte)~RSR_OE);
                        return rsr;
                    }

                case TSR:
                    {
                        byte tsr = (byte)(_tsrControl
                                          | (_txEnd ? TSR_END : 0)
                                          | (_txUnderrun ? TSR_UE : 0)
                                          | (_txFull ? 0 : TSR_BE));
                        _txUnderrun = false;        // cleared by reading TSR
                        return tsr;
                    }

                case UDR:
                    {
                        byte data = _rxData;
                        _rsr &= unchecked((byte)~RSR_BF);

                        // A character that arrived while the buffer was full: the error shows
                        // once the program has read the one it missed it for
                        if (_overrunPending)
                        {
                            _overrunPending = false;
                            _rsr |= RSR_OE;
                            Interrupt(MFP68901.RegA.RX_Error);
                        }

                        if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                            ColoredConsole.WriteLine($"[[cyan]]RS-232[[/cyan]] RX read [[green]]${data:X2}[[/green]] (PC=${CPU._moira.PC0:X6})");

                        return data;
                    }
            }

            return 0xFF;
        }

        public static void Write(uint offset, byte v)
        {
            switch (offset)
            {
                case SCR:
                    _scr = v;
                    break;

                case UCR:
                    _ucr = v;
                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Information)
                        ColoredConsole.WriteLine($"[[cyan]]RS-232[[/cyan]] UCR = [[yellow]]${v:X2}[[/yellow]] ({Format()})");
                    break;

                case RSR:
                    if ((v & RSR_RE) == 0)
                    {
                        // Disabled: every status bit goes, and what is on the line or still to
                        // come down it passes a receiver that is not listening
                        lock (_syncLock)
                        {
                            _rsr = 0;
                            _overrunPending = false;
                            _rxRemaining = 0;
                            _rxQueue.Clear();
                            _rxQueued = 0;
                        }
                    }
                    else
                    {
                        // Only the enables are written; the rest is the receiver's
                        _rsr = (byte)((_rsr & ~(RSR_RE | RSR_SS)) | (v & (RSR_RE | RSR_SS)));
                    }
                    break;

                case TSR:
                    _tsrControl = (byte)(v & TSR_WRITABLE);

                    if (TransmitterOn)
                    {
                        _txEnd = false;
                    }
                    else
                    {
                        // With the transmitter off the buffer reads empty and the underrun
                        // goes; the character on the line, if any, still finishes, and END
                        // comes when the line is idle
                        _txFull = false;
                        _txUnderrun = false;
                        if (_txRemaining <= 0)
                            SetEnd();
                    }
                    break;

                case UDR:
                    if (!TransmitterOn)
                        break;      // nowhere to go (see the class summary)

                    if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                        ColoredConsole.WriteLine($"[[cyan]]RS-232[[/cyan]] TX [[yellow]]${v:X2}[[/yellow]] (PC=${CPU._moira.PC0:X6})");

                    // The shift register takes it at the next clock edge (Clock): with Timer D
                    // stopped it stays in the buffer
                    _txData = v;
                    _txFull = true;
                    break;
            }
        }

        // ==================== The line (emulation thread) ====================

        /// <summary>
        /// <paramref name="timeouts"/> Timer D timeouts: the USART's clock edges. Called from
        /// the MFP's timer update, every CPU slice in which Timer D ran out at least once, so
        /// the idle case has to cost nothing.
        /// </summary>
        public static void Clock(int timeouts)
        {
            if (_txRemaining <= 0 && !_txFull && _rxRemaining <= 0 && _rxQueued == 0)
                return;

            // Transmitter: when a character ends, the one waiting in the buffer follows it at
            // once, which is what sustains the full line rate
            if (_txRemaining > 0)
            {
                _txRemaining -= timeouts;
                if (_txRemaining <= 0)
                {
                    int late = -_txRemaining;
                    _txRemaining = 0;

                    if (_txFull && TransmitterOn)
                    {
                        StartTransmit();
                        _txRemaining = Math.Max(1, _txRemaining - late);
                    }
                    else
                    {
                        EndOfCharacter();
                    }
                }
            }
            else if (_txFull && TransmitterOn)
            {
                StartTransmit();
            }

            // Receiver: one character at a time off the line, back to back while there are more
            if (_rxRemaining > 0)
            {
                _rxRemaining -= timeouts;
                if (_rxRemaining <= 0)
                {
                    int late = -_rxRemaining;
                    _rxRemaining = 0;
                    CompleteReceive();

                    if (_rxQueued > 0 && StartReceive())
                        _rxRemaining = Math.Max(1, _rxRemaining - late);
                }
            }
            else if (_rxQueued > 0)
            {
                StartReceive();
            }
        }

        // The buffer goes into the shift register: the character is on the line, and the
        // buffer is free for the next one
        static void StartTransmit()
        {
            byte b = (byte)(_txData & WordMask);
            _txFull = false;
            _txRemaining = FrameTimeouts();
            Interrupt(MFP68901.RegA.TX_Empty);

            // Loopback: the transmitter's output is the receiver's input, inside the chip
            if ((_tsrControl & TSR_LOOP) == TSR_LOOP)
            {
                Enqueue(b);
                return;
            }

            // What is plugged into the port; nothing, mostly. Never throws: this runs inside
            // the MFP's timer update, reached from the frame loop.
            if (Netplay.SerialCable.Plugged)
                Netplay.SerialCable.Transmit(b);
        }

        // The shift register has emptied with nothing in the buffer
        static void EndOfCharacter()
        {
            if (!TransmitterOn)
            {
                SetEnd();
            }
            else if (!_txUnderrun)
            {
                _txUnderrun = true;
                Interrupt(MFP68901.RegA.TX_Error);
            }
        }

        static void SetEnd()
        {
            if (_txEnd)
                return;

            _txEnd = true;
            Interrupt(MFP68901.RegA.TX_Error);

            // Auto turnaround: the end of transmission enables the receiver, once
            if ((_tsrControl & TSR_AT) != 0)
            {
                _tsrControl &= unchecked((byte)~TSR_AT);
                _rsr |= RSR_RE;
            }
        }

        // The next byte on the line starts coming in. False when there was none after all.
        static bool StartReceive()
        {
            lock (_syncLock)
            {
                if (_rxQueue.Count == 0)
                {
                    _rxQueued = 0;
                    return false;
                }

                _rxIncoming = _rxQueue.Dequeue();
                _rxQueued = _rxQueue.Count;
            }

            _rxRemaining = FrameTimeouts();
            if (!Synchronous)
                _rsr |= RSR_CIP;
            return true;
        }

        // A whole character has come in
        static void CompleteReceive()
        {
            _rsr &= unchecked((byte)~RSR_CIP);

            if (!ReceiverOn)
                return;

            // Full buffer: the character is lost, and the overrun shows once the program reads
            // the buffer. With an overrun still unread the receiver takes nothing either.
            if ((_rsr & RSR_BF) != 0)
            {
                _overrunPending = true;
                if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                    ColoredConsole.WriteLine($"[[cyan]]RS-232[[/cyan]] [[red]]overrun[[/red]], ${_rxIncoming:X2} lost");
                return;
            }

            if ((_rsr & RSR_OE) != 0)
                return;

            _rxData = (byte)(_rxIncoming & WordMask);
            _rsr = (byte)((_rsr & ~(RSR_PE | RSR_FE | RSR_BREAK)) | RSR_BF);
            Interrupt(MFP68901.RegA.RX_Full);

            if (ConfigOptions.RunninConfig.DebugMode >= ConfigOptions.DebugModes.Full)
                ColoredConsole.WriteLine($"[[cyan]]RS-232[[/cyan]] RX [[green]]${_rxData:X2}[[/green]] (queue={_rxQueued})");
        }

        /// <summary>How long one character occupies the line, in Timer D timeouts: start, data,
        /// parity and stop bits (1, 1½ or 2; none of start and stop when synchronous), in half
        /// bits of 16 timeouts each at ÷16, of one at ÷1.</summary>
        static int FrameTimeouts()
        {
            int bits = 8 - ((_ucr >> 5) & 3) + ((_ucr & UCR_PARITY) != 0 ? 1 : 0);
            int halfBits = ((_ucr >> 3) & 3) switch
            {
                0 => 2 * bits,
                1 => 2 * (bits + 1) + 2,
                2 => 2 * (bits + 1) + 3,
                _ => 2 * (bits + 1) + 4,
            };

            return halfBits * ((_ucr & UCR_DIV16) != 0 ? 16 : 1);
        }

        static string Format()
        {
            string parity = (_ucr & UCR_PARITY) == 0 ? "N" : (_ucr & 0x02) != 0 ? "E" : "O";
            string stop = ((_ucr >> 3) & 3) switch { 0 => "sync", 1 => "1", 2 => "1.5", _ => "2" };
            return $"{8 - ((_ucr >> 5) & 3)}{parity}{stop}, ÷{((_ucr & UCR_DIV16) != 0 ? 16 : 1)}";
        }

        static void Interrupt(byte channel) => ASEMain._mfp.SetInterruptPending(channel, false);

        // ==================== The port's other end (any thread) ====================

        /// <summary>
        /// Bytes from whatever is plugged into the port, called from its own thread (the
        /// network's, for the online null-modem cable). They queue here and come down the line
        /// at the receiver's own speed. A receiver that is not enabled does not see them pass.
        /// </summary>
        public static void Receive(ReadOnlySpan<byte> bytes)
        {
            // Nothing from outside may reach a recording, a playback or a shared machine: its
            // timing is the other computer's
            if (bytes.Length == 0 || Netplay.MachineInput.Deterministic)
                return;

            lock (_syncLock)
            {
                if (!ReceiverOn)
                    return;

                foreach (byte b in bytes)
                {
                    if (_rxQueue.Count >= RX_QUEUE_MAX)
                        break;
                    _rxQueue.Enqueue(b);
                }

                _rxQueued = _rxQueue.Count;
            }
        }

        static void Enqueue(byte b)
        {
            lock (_syncLock)
            {
                if (_rxQueue.Count < RX_QUEUE_MAX)
                    _rxQueue.Enqueue(b);
                _rxQueued = _rxQueue.Count;
            }
        }

        // ==================== Snapshot ====================

        public static void SaveState(Snapshot.Writer w)
        {
            w.U8(_scr);
            w.U8(_ucr);
            w.U8(_rsr);
            w.U8(_tsrControl);
            w.Bool(_txEnd);
            w.Bool(_txUnderrun);
            w.U8(_txData);
            w.Bool(_txFull);
            w.I32(_txRemaining);
            w.U8(_rxData);
            w.U8(_rxIncoming);
            w.I32(_rxRemaining);
            w.Bool(_overrunPending);
            // The bytes still to come down the line belong to whatever is plugged into the
            // port, not to the machine, and are not stored (as with the MIDI ACIA)
        }

        public static void LoadState(Snapshot.Reader r)
        {
            lock (_syncLock)
            {
                _scr = r.U8();
                _ucr = r.U8();
                _rsr = r.U8();
                _tsrControl = r.U8();
                _txEnd = r.Bool();
                _txUnderrun = r.Bool();
                _txData = r.U8();
                _txFull = r.Bool();
                _txRemaining = r.I32();
                _rxData = r.U8();
                _rxIncoming = r.U8();
                _rxRemaining = r.I32();
                _overrunPending = r.Bool();

                _rxQueue.Clear();
                _rxQueued = 0;
            }
        }
    }
}
