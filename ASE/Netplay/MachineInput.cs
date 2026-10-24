/*
 *
 * The door host input goes through on its way into the emulated machine.
 *
 * Keys, joysticks, mouse motion and buttons all cross here before they reach the IKBD
 * (ACIA). In normal use this is a straight wire: every call is forwarded the instant the host
 * reports it, at whatever scanline happens to drain it, exactly as before this file existed.
 *
 * It exists so that a machine can be made to run twice the same way. Two runs started from the
 * same snapshot only stay identical if every input lands at the same point of the emulated
 * timeline, and the host's timing has nothing to do with that: an event arrives whenever the UI
 * thread pumped it, which is a different scanline on every run (and a different computer, in
 * online play). In a deterministic session the input is therefore captured during a frame and
 * applied at a later one, all of it at one scanline, and that scanline is derived from the
 * session seed and the frame number — so every run agrees on it without exchanging anything.
 *
 * The scanline is pseudo-random and never fixed on purpose. Applying the input at the same
 * point of every frame would lock the IKBD's first byte to the frame, the very phase CLAUDE.md
 * describes in the IKBD section (a keyboard interrupt landing inside the VBL handler, every
 * time, and code waiting on a VBL counter at IPL 4 never returning). A real IKBD runs off its
 * own crystal; a different line each frame stands in for that.
 *
 * Changes made outside the input stream — a disk inserted or ejected, an STX command completed
 * early by a snapshot — always happen at a frame boundary, so they are logged as commands of a
 * frame and replayed at the start of it.
 *
 * What the session does with all of it (write a file, read one back, share the machine with
 * other computers) is IFrameSession's business: this class only owns the frame mechanics, and
 * what this computer's player is holding (LocalState), which a shared game needs to know.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE.Netplay
{
    public static class MachineInput
    {
        public enum Modes
        {
            /// <summary>Normal use: input reaches the IKBD the moment the host reports it.</summary>
            Live,

            /// <summary>Input is captured during a frame and applied at the next one, at the
            /// session's scanline for it; the session gets a copy of everything.</summary>
            Record,

            /// <summary>Host input is ignored: the session supplies every frame's input.</summary>
            Replay,

            /// <summary>A shared game (NETPLAY.md §5): input is captured and handed to the
            /// session, which applies everybody's at the frame it lands on; a frame does not start
            /// until the session has all of it.</summary>
            Lockstep,
        }

        /// <summary>
        /// What a deterministic session plugs into the frame loop. Called on the emulation thread,
        /// at frame boundaries. Throwing ends the session with the exception's message.
        /// </summary>
        public interface IFrameSession
        {
            /// <summary>
            /// Start of frame <paramref name="frame"/> (0-based since the session started). Gets the
            /// commands the UI logged and the input captured since the previous frame started;
            /// answers the commands to run now and the input to apply during this frame. False ends
            /// the session (a playback that has nothing left).
            /// </summary>
            bool BeginFrame(long frame, IReadOnlyList<FrameCommand> logged, IReadOnlyList<InputEvent> captured,
                            out IReadOnlyList<FrameCommand> run, out IReadOnlyList<InputEvent> apply);

            /// <summary>End of a frame: <paramref name="completed"/> frames have now run since the
            /// session started. False ends the session.</summary>
            bool EndFrame(long completed);

            /// <summary>The session is over: <paramref name="reason"/> says why, or is null when it
            /// ran its course. Returns the line that reports it.</summary>
            string Close(string reason);

            /// <summary>Whether frame <paramref name="frame"/> can start: a shared game waits for
            /// the other players' input. Asked again a millisecond later while it says no.</summary>
            bool Ready(long frame) => true;

            /// <summary>The frame loop is not running frames (waiting, or paused): a chance to keep
            /// the other machines informed.</summary>
            void Idle() { }

            /// <summary>The session is ending: the events that leave the machine with only this
            /// computer's player's input, <paramref name="local"/>.</summary>
            void Leaving(InputState local, List<InputEvent> owed) { }
        }

        // Written under _lock; read without it on the hot paths (volatile is enough there: the
        // mode only changes with the emulation parked or stopped, or on the emulation thread).
        static volatile Modes _mode = Modes.Live;

        public static Modes Mode => _mode;

        /// <summary>True while a recording, a playback or a shared game is running.</summary>
        public static bool Deterministic => _mode != Modes.Live;

        static bool Capturing => _mode == Modes.Record || _mode == Modes.Lockstep;

        static readonly object _lock = new();
        static IFrameSession _session;

        // Since the previous frame started: input from the host (emulation thread, plus the UI
        // thread's pointer overlay) and commands the UI logged.
        static readonly List<InputEvent> _captured = new();
        static readonly List<FrameCommand> _logged = new();

        // This frame's input, and the scanline it lands on (-1: none pending). Emulation thread.
        static IReadOnlyList<InputEvent> _scheduled = Array.Empty<InputEvent>();
        static int _applyLine = -1;

        static ulong _seed;
        static long _frame;

        // Last joystick states captured. HostInput re-routes both ports on every key, so most
        // Joysticks calls repeat what the IKBD already has; only changes are worth a record.
        static int _lastJoy0 = -1, _lastJoy1 = -1;

        // What this computer's player holds, whatever the mode (under _lock)
        static readonly InputState _local = new();

        // A shared game: host mouse motion not yet worth a whole ST unit (see MouseMotion)
        static double _motionX, _motionY;

        // ==================== Input (any thread) ====================

        public static void Key(byte code)
        {
            Track(new InputEvent(EventKind.Key, code));

            if (_mode == Modes.Live) { ACIA.PushIkbd(code); return; }
            Capture(new InputEvent(EventKind.Key, code));
        }

        public static void Joysticks(byte joy0, byte joy1)
        {
            Track(new InputEvent(EventKind.Joysticks, joy0, joy1));

            if (_mode == Modes.Live) { ACIA.SetJoysticks(joy0, joy1); return; }

            lock (_lock)
            {
                if (!Capturing || (joy0 == _lastJoy0 && joy1 == _lastJoy1))
                    return;

                _lastJoy0 = joy0;
                _lastJoy1 = joy1;
                _captured.Add(new InputEvent(EventKind.Joysticks, joy0, joy1));
            }
        }

        public static void MouseMotion(int dx, int dy)
        {
            if (_mode == Modes.Live) { ACIA.MouseMotion(dx, dy); return; }

            lock (_lock)
            {
                if (!Capturing)
                    return;

                if (_mode == Modes.Lockstep)
                {
                    // Each player keeps their own sensitivity (NETPLAY.md §5.2): the host pixels
                    // become ST units here, with this computer's divisor and the fraction carried
                    // over, and the shared machine divides by 1. A player with no mouse in port 0
                    // moves nothing.
                    if (!_local.Mouse)
                        return;

                    double sens = ConfigOptions.RunninConfig.MouseSensitivity;
                    if (sens < 0.1) sens = 1.0;

                    _motionX += dx;
                    _motionY += dy;
                    dx = (int)(_motionX / sens);
                    dy = (int)(_motionY / sens);
                    _motionX -= dx * sens;
                    _motionY -= dy * sens;

                    if (dx == 0 && dy == 0)
                        return;
                }

                // The IKBD only accumulates motion until its next sample tick, so consecutive
                // deltas are one delta: a host mouse at 1000 Hz would otherwise be 20 records a frame.
                int last = _captured.Count - 1;
                if (last >= 0 && _captured[last].Kind == EventKind.MouseMotion)
                    _captured[last] = new InputEvent(EventKind.MouseMotion,
                        Clamp16(_captured[last].A + dx), Clamp16(_captured[last].B + dy));
                else
                    _captured.Add(new InputEvent(EventKind.MouseMotion, Clamp16(dx), Clamp16(dy)));
            }
        }

        public static void MouseButton(bool left, bool pressed, bool isDoubleClick = false)
        {
            Track(new InputEvent(EventKind.MouseButton, left ? 1 : 0, pressed ? 1 : 0));

            if (_mode == Modes.Live) { ACIA.MouseButtonChanged(left, pressed, isDoubleClick); return; }
            Capture(new InputEvent(EventKind.MouseButton, left ? 1 : 0, pressed ? 1 : 0));
            if (isDoubleClick && left && pressed)
            {
                Capture(new InputEvent(EventKind.MouseButton, 1, 0));
                Capture(new InputEvent(EventKind.MouseButton, 1, 1));
            }
        }

        public static void ConnectMouse(bool connected)
        {
            Track(new InputEvent(EventKind.ConnectMouse, connected ? 1 : 0));

            if (_mode == Modes.Live) { ACIA.ConnectMouse(connected); return; }
            Capture(new InputEvent(EventKind.ConnectMouse, connected ? 1 : 0));
        }

        static void Track(InputEvent e)
        {
            lock (_lock)
                _local.Track(e);
        }

        static void Capture(InputEvent e)
        {
            lock (_lock)
            {
                if (Capturing)
                    _captured.Add(e);
            }
        }

        /// <summary>
        /// A power-on made the host's input forget the keys and controls held (HostInput.Reset):
        /// their releases will never come, so they are not held here either. A shared game's new
        /// epoch then tells every machine they were let go.
        /// </summary>
        public static void ForgetHeld()
        {
            lock (_lock)
                _local.ReleaseKeysAndPorts();
        }

        /// <summary>What this computer's player is holding right now, whatever reaches the
        /// machine: a shared game tells the others from it.</summary>
        public static InputState LocalState
        {
            get { lock (_lock) return _local.Clone(); }
        }

        // Recordings store motion as 16-bit values; a frame's worth of host motion never gets
        // near that, and clamping before storing keeps both runs on the same number.
        static int Clamp16(int v) => Math.Clamp(v, short.MinValue, short.MaxValue);

        static void Apply(InputEvent e)
        {
            switch (e.Kind)
            {
                case EventKind.Key: ACIA.PushIkbd((byte)e.A); break;
                case EventKind.Joysticks: ACIA.SetJoysticks((byte)e.A, (byte)e.B); break;
                case EventKind.MouseMotion: ACIA.MouseMotion(e.A, e.B); break;
                case EventKind.MouseButton: ACIA.MouseButtonChanged(left: e.A != 0, pressed: e.B != 0); break;
                case EventKind.ConnectMouse: ACIA.ConnectMouse(e.A != 0); break;
            }
        }

        // ==================== Frame-boundary changes ====================

        /// <summary>
        /// A disk went into or out of <paramref name="drive"/>. Called with the emulation parked
        /// at a frame boundary, right after the change, so a recording can replay it at the start
        /// of the frame about to run.
        /// </summary>
        public static void NoteMediaChange(int drive)
        {
            if (_mode != Modes.Record)
                return;

            byte[] media = Snapshot.Pack(w => ASEMain.Drive(drive).SaveMedia(w));

            lock (_lock)
                _logged.Add(new FrameCommand(CommandKind.Media, drive, media));
        }

        /// <summary>A snapshot was taken, which completes any STX command in flight
        /// (WD1772.FlushPendingOp): that changes the machine, so a recording replays it.</summary>
        public static void NoteFdcFlush()
        {
            if (_mode != Modes.Record)
                return;

            lock (_lock)
                _logged.Add(new FrameCommand(CommandKind.FdcFlush, 0, null));
        }

        /// <summary>
        /// A change to a shared machine (a disk swap, a snapshot), from the UI thread. Unlike a
        /// recording's, it does not happen here and now: every machine makes it at the start of
        /// the frame this computer's next input lands on. False when no shared game is running.
        /// </summary>
        public static bool Schedule(FrameCommand command)
        {
            lock (_lock)
            {
                if (_mode != Modes.Lockstep)
                    return false;

                _logged.Add(command);
                return true;
            }
        }

        static void Execute(FrameCommand c)
        {
            switch (c.Kind)
            {
                case CommandKind.Media:
                    // Same notification an insert or eject gives, so TOS sees the swap.
                    ASEMain.Drive(c.Drive).RestoreMedia(Snapshot.Unpack(c.Payload), signalTransition: true);
                    break;

                case CommandKind.FdcFlush:
                    WD1772.FlushPendingOp();
                    break;

                case CommandKind.Snapshot:
                    // Completes the STX command in flight like FdcFlush does on the other machines,
                    // and writes the file on this one. A failure costs the file, not the game.
                    string path = System.Text.Encoding.UTF8.GetString(c.Payload);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        Snapshot.Save(path);
                        ColoredConsole.WriteLine($"Snapshot saved: [[green]]{path}[[/green]]", ConfigOptions.DebugModes.Quiet);
                    }
                    catch (Exception ex)
                    {
                        WD1772.FlushPendingOp();
                        ColoredConsole.WriteLine($"Could not save snapshot [[red]]{path}[[/red]]: {ex.Message}");
                    }
                    break;
            }
        }

        // ==================== Session ====================

        /// <summary>
        /// Starts a session. Called with the emulation thread NOT running: right after the machine
        /// was rebuilt from the session snapshot and before the thread starts
        /// (ASEMain.RestartFromSnapshot), so frame 0 is the first frame of that machine.
        /// <paramref name="mouseConnected"/> is whether port 0 holds the mouse: not machine state,
        /// and not in the snapshot, but every run has to start from the same answer. A session
        /// already running is replaced without being closed (a shared game's next epoch).
        /// </summary>
        public static void Begin(Modes mode, IFrameSession session, ulong seed, bool mouseConnected)
        {
            lock (_lock)
            {
                _session = session;
                _seed = seed;
                _frame = 0;
                _captured.Clear();
                _logged.Clear();
                _scheduled = Array.Empty<InputEvent>();
                _applyLine = -1;
                _lastJoy0 = _lastJoy1 = -1;
                _motionX = _motionY = 0;

                ACIA.RestoreMouseConnection(mouseConnected);

                // In a shared game every player's motion arrives already divided by their own
                // sensitivity (see MouseMotion)
                ACIA.MouseDivisorOverride = mode == Modes.Lockstep ? 1f : 0f;

                _mode = mode;
            }
        }

        /// <summary>
        /// Top of a frame that is going to run, before <see cref="BeginFrame"/>: false while the
        /// session is still waiting for what that frame needs (the other players' input). The
        /// frame loop then drains host input, waits a moment and asks again. Emulation thread.
        /// </summary>
        public static bool FrameReady()
        {
            if (_mode != Modes.Lockstep)
                return true;

            try
            {
                if (_session.Ready(_frame))
                    return true;

                _session.Idle();
                return false;
            }
            catch (Exception ex)
            {
                End($"the session failed: {ex.Message}");
                return true;
            }
        }

        /// <summary>The frame loop is paused (a dialog is open): the session may keep the other
        /// machines informed. Emulation thread.</summary>
        public static void Idle()
        {
            if (_mode != Modes.Lockstep)
                return;

            try { _session.Idle(); }
            catch (Exception ex) { End($"the session failed: {ex.Message}"); }
        }

        /// <summary>Start of a frame that is going to run (not while paused). Emulation thread.</summary>
        public static void BeginFrame()
        {
            if (_mode == Modes.Live)
                return;

            IReadOnlyList<FrameCommand> logged;
            IReadOnlyList<InputEvent> captured;

            lock (_lock)
            {
                logged = _logged.Count == 0 ? Array.Empty<FrameCommand>() : _logged.ToArray();
                captured = _captured.Count == 0 ? Array.Empty<InputEvent>() : _captured.ToArray();
                _logged.Clear();
                _captured.Clear();
            }

            try
            {
                if (!_session.BeginFrame(_frame, logged, captured, out var run, out var apply))
                {
                    End(null);
                    return;
                }

                foreach (var command in run)
                    Execute(command);

                _scheduled = apply;
                _applyLine = ApplyLine(_seed, _frame);
            }
            catch (Exception ex)
            {
                // What was captured is still owed to the machine (End applies it when recording).
                _scheduled = captured;
                End($"the session failed: {ex.Message}");
            }
        }

        /// <summary>Top of every scanline. Applies this frame's input on its line. Emulation
        /// thread. In normal use the line is -1 and this is one compare.</summary>
        public static void AtScanline(int line)
        {
            if (line != _applyLine)
                return;

            _applyLine = -1;

            var events = _scheduled;
            _scheduled = Array.Empty<InputEvent>();

            foreach (var e in events)
                Apply(e);
        }

        /// <summary>End of a frame that ran in full (after the VBL). Emulation thread.</summary>
        public static void EndFrame()
        {
            if (_mode == Modes.Live)
                return;

            _frame++;

            try
            {
                if (!_session.EndFrame(_frame))
                    End(null);
            }
            catch (Exception ex)
            {
                End($"the session failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ends the running session, if there is one: <paramref name="reason"/> says why, or null
        /// when it ran its course. From the emulation thread, or from any thread while the
        /// emulation is parked or stopped.
        /// </summary>
        public static void End(string reason)
        {
            IFrameSession session;
            var owed = new List<InputEvent>();

            lock (_lock)
            {
                if (_mode == Modes.Live)
                    return;

                session = _session;

                // A recording owes the machine what the host has sent and the frame has not
                // applied yet: a key released in the last frame must not stay down. A playback
                // owes it nothing — the host input it captured was never meant for the machine.
                // A shared game owes it the difference between what everybody held and what
                // this computer's player holds: the others' keys and buttons are let go.
                if (_mode == Modes.Record)
                {
                    owed.AddRange(_scheduled);
                    owed.AddRange(_captured);
                }
                else if (_mode == Modes.Lockstep)
                {
                    owed.AddRange(_scheduled);
                    try { session.Leaving(_local.Clone(), owed); }
                    catch (Exception) { /* nothing better to do than leave the keys as they are */ }
                }

                _session = null;
                _scheduled = Array.Empty<InputEvent>();
                _applyLine = -1;
                _captured.Clear();
                _logged.Clear();
                ACIA.MouseDivisorOverride = 0f;
                _mode = Modes.Live;
            }

            foreach (var e in owed)
                Apply(e);

            // The port configuration went into the session (or nowhere, in a playback): what is
            // plugged in has to be applied live again at the next event drain.
            HostInput.InvalidatePortConfiguration();

            string summary;
            try { summary = session.Close(reason); }
            catch (Exception ex) { summary = $"Session closed with an error: {ex.Message}"; }

            ColoredConsole.WriteLine(summary, ConfigOptions.DebugModes.Quiet);
        }

        /// <summary>
        /// The scanline frame <paramref name="frame"/>'s input lands on: SplitMix64 of the seed
        /// and the frame number, so every run of the session agrees on it without being told.
        /// </summary>
        static int ApplyLine(ulong seed, long frame)
        {
            ulong z = seed + (ulong)frame * 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z % (ulong)VideoTiming.SCANLINES_PER_FRAME);
        }
    }
}
