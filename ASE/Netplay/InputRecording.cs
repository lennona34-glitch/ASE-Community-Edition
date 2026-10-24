/*
 *
 * Input recordings (.asrec): a machine and everything that was done to it, frame by frame.
 *
 * A recording is a session snapshot (disks included, see Snapshot.SaveToBytes) followed by the
 * input of every frame and, once a second, a fingerprint of the whole machine (MachineHash).
 * Playing it back rebuilds the machine from the snapshot, feeds it the same input at the same
 * points and compares the fingerprints: if every checkpoint matches, the emulator ran the same
 * machine twice — which is exactly what online play will need from two computers, and the
 * first checkpoint that differs says when, and from there what, made them part.
 *
 * Recording starts from the running machine: it is snapshotted and then rebuilt from that very
 * snapshot, so the recorded run and every playback start from the same restored state (a
 * snapshot does not carry everything a running machine has, and the difference would show up
 * as a desync at frame 0).
 *
 * File layout, big-endian like snapshots:
 *
 *   "ASEREC01", u16 format version, ASE version (string), u64 session seed,
 *   u16 frames between checkpoints, the machine settings that are not machine state
 *   (MachineSettings), u32 snapshot length + the ASESNAP2 snapshot, then records:
 *
 *     'C' u8 kind, u8 drive, i32 length (-1: none) + payload   a change before the next frame
 *     'F' u16 count + events                                    one frame's input
 *     'H' u64                                                   the checkpoint after a frame
 *     'X' i64 frames                                            end of the recording
 *
 * A file cut short (ASE closed while recording) plays up to where it stops.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Text;
using Avalonia.Threading;
using static ASE.Config;
using static ASE.Netplay.MachineInput;

namespace ASE.Netplay
{
    public static class InputRecording
    {
        public const string Extension = "asrec";

        const string Magic = "ASEREC01";
        const ushort FormatVersion = 1;

        // One checkpoint a second of PAL time: cheap (a hash of the RAM), and fine enough to
        // say where two runs parted.
        const ushort HashInterval = 50;

        const byte TagCommand = (byte)'C';
        const byte TagFrame = (byte)'F';
        const byte TagHash = (byte)'H';
        const byte TagEnd = (byte)'X';

        public static bool IsRecording => Mode == Modes.Record;
        public static bool IsReplaying => Mode == Modes.Replay;

        // A session started from the command line with --exit-when-done: ASE closes when it ends,
        // and the exit code says how it went (0 = recorded, or played back in sync).
        static bool _exitWhenDone;

        // ==================== Starting and stopping (UI thread) ====================

        /// <summary>
        /// Starts recording the running machine into a new file in the snapshots directory
        /// (<paramref name="path"/>). <paramref name="stopAfterFrames"/> &gt; 0 stops it by itself
        /// after that many frames.
        /// </summary>
        public static bool StartRecording(out string path, out string error, long stopAfterFrames = 0)
        {
            path = null;

            if (Deterministic)
            {
                error = Busy;
                return false;
            }

            if (ASEMain._mem?.ROM == null)
            {
                error = "the machine is not running";
                return false;
            }

            // A library hard-disk game is fine: its disk travels in the snapshot ("HDG "). The
            // user's own hard disks do not, and the program writes to them as it runs, so the
            // playback would start from disks the recording never saw.
            var config = ConfigOptions.RunninConfig;
            if (!HardDiskGames.Active && (config.GemdosDriveEnabled || config.AcsiImageEnabled))
            {
                error = "the hard disks cannot be recorded: the program writes to the GEMDOS folder and the ACSI image " +
                        "as it runs, so a playback would start from different disks. Switch them off in the Hard disk tab to record.";
                return false;
            }

            byte[] snapshot = null;
            if (!ASEMain.RunWhilePaused(() => snapshot = Snapshot.SaveToBytes(includeMedia: true), out error))
                return false;

            Snapshot.SnapshotFile snap;
            Recorder recorder;
            var settings = MachineSettings.Capture();
            ulong seed = (ulong)Random.Shared.NextInt64();

            try
            {
                snap = Snapshot.Parse(snapshot);

                string dir = Config.SnapshotsDir;
                Directory.CreateDirectory(dir);
                path = ASEMain.NewTimestampedPath(dir, "ase_recording", Extension);

                recorder = Recorder.Create(path, seed, settings, snapshot, stopAfterFrames);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            // The machine the recording describes is the one rebuilt from its snapshot, so that is
            // what runs from here (the few frames since the snapshot was taken are run again).
            if (!ASEMain.RestartFromSnapshot(snap,
                    () => Begin(Modes.Record, recorder, seed, settings.MouseConnected), out error))
            {
                recorder.Discard();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Plays a recording back: rebuilds its machine, runs it with the recorded input and the
        /// recorded settings, and checks every checkpoint. When it ends, the machine carries on
        /// running live from wherever the playback left it.
        /// </summary>
        public static bool StartReplay(string path, out string error)
        {
            if (Deterministic)
            {
                error = Busy;
                return false;
            }

            Snapshot.Reader r;
            Snapshot.SnapshotFile snap;
            MachineSettings recorded;
            ulong seed;
            ushort interval;

            try
            {
                byte[] file = File.ReadAllBytes(path);

                if (file.Length < Magic.Length || Encoding.ASCII.GetString(file, 0, Magic.Length) != Magic)
                    throw new InvalidDataException("not an ASE input recording");

                r = new Snapshot.Reader(file);
                r.Bytes(Magic.Length);

                ushort version = r.U16();
                if (version != FormatVersion)
                    throw new InvalidDataException($"recording format {version} is not supported by this version of ASE");

                string recordedWith = r.Str();
                seed = (ulong)r.I64();
                interval = r.U16();
                recorded = MachineSettings.Read(r);
                snap = Snapshot.Parse(r.Bytes(r.I32()));

                if (interval == 0)
                    throw new InvalidDataException("corrupt recording header");

                if (recordedWith != Config.Version)
                    ColoredConsole.WriteLine(
                        $"[[yellow]]This recording was made with ASE {recordedWith}[[/yellow]]: any change to the emulation " +
                        "since then can make the playback drift away from it.", ConfigOptions.DebugModes.Quiet);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            var user = MachineSettings.Capture();
            recorded.Apply();

            var player = new Player(path, r, interval, user);

            if (!ASEMain.RestartFromSnapshot(snap,
                    () => Begin(Modes.Replay, player, seed, recorded.MouseConnected), out error))
            {
                user.Apply();
                return false;
            }

            return true;
        }

        static string Busy => Mode == Modes.Lockstep
            ? "a shared online game is running (Online play)"
            : "a recording or a playback is already running";

        /// <summary>Stops the recording or playback that is running, if any. UI thread.</summary>
        public static void Stop()
        {
            if (!IsRecording && !IsReplaying)
                return;

            // Stopping is how a recording normally ends; a playback normally runs to its end.
            string reason = IsRecording ? null : "stopped by the user";
            ASEMain.RunWhilePaused(() => End(reason), out _);
        }

        /// <summary>
        /// Ends whatever session is running because the machine is going away under it (a reset,
        /// a snapshot restore, ASE closing). Called by ASEMain with the emulation thread stopped.
        /// A shared game's host shares the new machine once it is up (Lockstep).
        /// </summary>
        public static void EndSession(string reason) => End(reason);

        /// <summary>
        /// Starts the session the command line asked for (--record, --replay), once the machine
        /// is up. A failure is reported, and with --exit-when-done closes ASE with exit code 1.
        /// </summary>
        public static void StartFromCommandLine()
        {
            bool replay = !string.IsNullOrEmpty(Config.StartupReplay);

            if (!replay && !Config.StartupRecord)
                return;

            string error;
            bool started;

            if (replay)
            {
                started = StartReplay(Config.StartupReplay, out error);
                if (started)
                    ColoredConsole.WriteLine($"Playing back [[green]]{Config.StartupReplay}[[/green]]", ConfigOptions.DebugModes.Quiet);
            }
            else
            {
                started = StartRecording(out string path, out error, Config.StartupRecordFrames);
                if (started)
                    ColoredConsole.WriteLine($"Recording input into [[green]]{path}[[/green]]", ConfigOptions.DebugModes.Quiet);
            }

            if (started)
            {
                _exitWhenDone = Config.ExitWhenSessionEnds;
                return;
            }

            ColoredConsole.WriteLine($"Could not start the {(replay ? "playback" : "recording")}: [[red]]{error}[[/red]]");

            if (Config.ExitWhenSessionEnds)
                Dispatcher.UIThread.Post(() => ExitAse(1));
        }

        /// <summary>
        /// Closes ASE with an exit code (UI thread). Through the desktop lifetime, which closes the
        /// windows the ordinary way (MainWindow.OnClosed shuts the machine down) and is what sets
        /// the process exit code: a value put in Environment.ExitCode before closing the window is
        /// overwritten by the lifetime's own on the way out.
        /// </summary>
        static void ExitAse(int code)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(code);
            else
                ASEMain.MainWindow?.Close();
        }

        /// <summary>Reports the end of a session in the status bar (the console already has it)
        /// and closes ASE if the command line asked for that. Any thread.</summary>
        static void Notify(string text, bool success)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // A session ended by ASE closing reports to a window that is already gone.
                var window = ASEMain.MainWindow;
                if (window == null || !window.IsVisible)
                    return;

                window.OnSessionEnded(text);

                if (_exitWhenDone)
                {
                    _exitWhenDone = false;
                    ExitAse(success ? 0 : 1);
                }
            });
        }

        static string Duration(long frames)
        {
            var t = TimeSpan.FromSeconds(frames * VideoTiming.FRAME_SECONDS);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        // ==================== Records ====================

        static void WriteEvents(Snapshot.Writer w, IReadOnlyList<InputEvent> events)
        {
            w.U16((ushort)events.Count);

            foreach (var e in events)
            {
                w.U8((byte)e.Kind);

                switch (e.Kind)
                {
                    case EventKind.Key:
                    case EventKind.ConnectMouse:
                        w.U8((byte)e.A);
                        break;
                    case EventKind.Joysticks:
                        w.U8((byte)e.A);
                        w.U8((byte)e.B);
                        break;
                    case EventKind.MouseMotion:
                        w.I16((short)e.A);
                        w.I16((short)e.B);
                        break;
                    case EventKind.MouseButton:
                        w.U8((byte)(e.A | (e.B << 1)));
                        break;
                }
            }
        }

        static InputEvent[] ReadEvents(Snapshot.Reader r)
        {
            var events = new InputEvent[r.U16()];

            for (int i = 0; i < events.Length; i++)
            {
                var kind = (EventKind)r.U8();

                switch (kind)
                {
                    case EventKind.Key:
                    case EventKind.ConnectMouse:
                        events[i] = new InputEvent(kind, r.U8());
                        break;
                    case EventKind.Joysticks:
                        events[i] = new InputEvent(kind, r.U8(), r.U8());
                        break;
                    case EventKind.MouseMotion:
                        events[i] = new InputEvent(kind, r.I16(), r.I16());
                        break;
                    case EventKind.MouseButton:
                        byte b = r.U8();
                        events[i] = new InputEvent(kind, b & 1, (b >> 1) & 1);
                        break;
                    default:
                        throw new InvalidDataException($"corrupt recording (unknown input event {(byte)kind})");
                }
            }

            return events;
        }

        // ==================== Recorder ====================

        sealed class Recorder : IFrameSession
        {
            readonly string _path;
            readonly BufferedStream _file;
            readonly Snapshot.Writer _w;
            readonly long _stopAfter;
            long _frames;

            Recorder(string path, BufferedStream file, long stopAfter)
            {
                _path = path;
                _file = file;
                _w = new Snapshot.Writer(file);
                _stopAfter = stopAfter;
            }

            public static Recorder Create(string path, ulong seed, MachineSettings settings, byte[] snapshot, long stopAfter)
            {
                var file = new BufferedStream(File.Create(path), 64 * 1024);

                try
                {
                    var w = new Snapshot.Writer(file);
                    w.Bytes(Encoding.ASCII.GetBytes(Magic));
                    w.U16(FormatVersion);
                    w.Str(Config.Version);
                    w.I64((long)seed);
                    w.U16(HashInterval);
                    settings.Write(w);
                    w.I32(snapshot.Length);
                    w.Bytes(snapshot);
                    file.Flush();
                }
                catch
                {
                    file.Dispose();
                    File.Delete(path);
                    throw;
                }

                return new Recorder(path, file, stopAfter);
            }

            /// <summary>The session never started: the file goes away.</summary>
            public void Discard()
            {
                _file.Dispose();
                try { File.Delete(_path); } catch { /* nothing worth reporting over */ }
            }

            public bool BeginFrame(long frame, IReadOnlyList<FrameCommand> logged, IReadOnlyList<InputEvent> captured,
                                   out IReadOnlyList<FrameCommand> run, out IReadOnlyList<InputEvent> apply)
            {
                foreach (var c in logged)
                {
                    _w.U8(TagCommand);
                    _w.U8((byte)c.Kind);
                    _w.U8((byte)c.Drive);
                    _w.I32(c.Payload?.Length ?? -1);
                    if (c.Payload != null)
                        _w.Bytes(c.Payload);
                }

                _w.U8(TagFrame);
                WriteEvents(_w, captured);

                // The commands already happened (the UI made the change); the input is this frame's.
                run = Array.Empty<FrameCommand>();
                apply = captured;
                return true;
            }

            public bool EndFrame(long completed)
            {
                _frames = completed;

                if (completed % HashInterval == 0)
                {
                    _w.U8(TagHash);
                    _w.I64((long)MachineHash.Compute());

                    // Once a second, so ASE closing mid-recording loses at most that.
                    _file.Flush();
                }

                return _stopAfter <= 0 || completed < _stopAfter;
            }

            public string Close(string reason)
            {
                _w.U8(TagEnd);
                _w.I64(_frames);
                _file.Dispose();

                string why = reason == null ? "" : $" ({reason})";
                string text = $"Recording saved{why}: {_frames} frames ({Duration(_frames)}) in {Path.GetFileName(_path)}";

                Notify(text, success: true);
                return $"{text} — {_path}";
            }
        }

        // ==================== Player ====================

        sealed class Player : IFrameSession
        {
            readonly string _path;
            readonly Snapshot.Reader _r;
            readonly ushort _interval;
            readonly MachineSettings _user;

            long _frames;
            long _checkpoints;
            long _desyncAt = -1;
            bool _complete;     // reached the end record, i.e. the recording was closed properly

            public Player(string path, Snapshot.Reader r, ushort interval, MachineSettings user)
            {
                _path = path;
                _r = r;
                _interval = interval;
                _user = user;
            }

            public bool BeginFrame(long frame, IReadOnlyList<FrameCommand> logged, IReadOnlyList<InputEvent> captured,
                                   out IReadOnlyList<FrameCommand> run, out IReadOnlyList<InputEvent> apply)
            {
                var commands = new List<FrameCommand>();
                run = commands;
                apply = Array.Empty<InputEvent>();

                while (true)
                {
                    // A recording cut short ends where it stops.
                    if (_r.Remaining == 0)
                        return false;

                    byte tag = _r.U8();

                    switch (tag)
                    {
                        case TagCommand:
                            var kind = (CommandKind)_r.U8();
                            int drive = _r.U8();
                            int length = _r.I32();
                            byte[] payload = length < 0 ? null : _r.Bytes(length);

                            if ((kind != CommandKind.Media && kind != CommandKind.FdcFlush) || drive > 1)
                                throw new InvalidDataException("corrupt recording (unknown command)");

                            commands.Add(new FrameCommand(kind, drive, payload));
                            continue;

                        case TagFrame:
                            apply = ReadEvents(_r);
                            return true;

                        case TagHash:
                            _r.I64();     // a checkpoint out of place: nothing to compare it with
                            continue;

                        case TagEnd:
                            _r.I64();
                            _complete = true;
                            return false;

                        default:
                            throw new InvalidDataException($"corrupt recording (unknown record '{(char)tag}')");
                    }
                }
            }

            public bool EndFrame(long completed)
            {
                _frames = completed;

                if (completed % _interval != 0 || _r.Remaining < 9 || _r.PeekU8() != TagHash)
                    return true;

                _r.U8();
                ulong expected = (ulong)_r.I64();
                _checkpoints++;

                if (MachineHash.Compute() == expected)
                    return true;

                // From here the recorded input describes a different machine: carrying on would
                // only hide where it went wrong. The machine stays at this frame, live.
                _desyncAt = completed;
                return false;
            }

            public string Close(string reason)
            {
                _user.Apply();

                string text;
                bool success = false;

                if (_desyncAt >= 0)
                    text = $"Playback OUT OF SYNC at frame {_desyncAt} ({Duration(_desyncAt)}): the machine no longer matches the recording " +
                           $"({_checkpoints - 1} checkpoints matched before it)";
                else if (reason != null)
                    text = $"Playback stopped ({reason}) after {_frames} frames, {_checkpoints} checkpoints in sync";
                else
                {
                    success = true;
                    text = $"Playback finished in sync: {_frames} frames ({Duration(_frames)}), {_checkpoints} checkpoints matched" +
                           (_complete ? "" : " (the recording was cut short)");
                }

                Notify(text, success);
                return $"{text} — {Path.GetFileName(_path)}";
            }
        }
    }
}
