/*
 *
 * Playing one machine together (NETPLAY.md §5, phase 4): every computer in an online game runs
 * the same ST, and every player's input reaches every copy at the same frame.
 *
 * The host shares its machine: it takes a session snapshot (disks included), sends it to the
 * guests and rebuilds its own machine from it, and every computer starts that machine at frame
 * 0. From there only input travels (LockstepCore): what a player does during frame N lands on
 * frame N + D everywhere, and a frame waits until it has everybody's. Both joystick ports and
 * the keyboard are shared; each player plugs into them what their own Input tab says.
 *
 * Whatever replaces the machine starts a new epoch the same way: a fingerprint that differs
 * (the guests send theirs once a second and the host compares), a player joining or leaving,
 * the host resetting, restoring a snapshot or starting a library hard-disk game. An epoch is a
 * fresh snapshot, a fresh delay and frame numbers from 0, and anything still in flight from the
 * previous one is recognised by its epoch number and dropped.
 *
 * A guest runs the host's machine without the host's machine ever reaching its config.json:
 * the model, memory, monitor and drive B are borrowed until the guest's next power-on, its own
 * hard disks are off until the shared game ends, and Config.PersistOverlay writes the guest's
 * own values whatever is saved meanwhile.
 *
 * When the game ends — the host stops it, someone leaves, the connection goes — each computer
 * keeps the machine as it was and carries on alone, the other players' keys let go.
 *
 * Threads: the UI thread starts and ends things; the emulation thread runs the epoch (it is
 * MachineInput's session); the network thread hands in input, commands and fingerprints through
 * the transport's hooks. LockstepExchange is the only state the last two share, and it locks.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Text;
using Avalonia.Threading;
using static ASE.Config;

namespace ASE.Netplay
{
    public static class Lockstep
    {
        // Session messages (0x40 is the MIDI cable's)
        public const byte MsgStart = 0x41;      // host -> guest: the machine to run from now on (a new epoch)
        public const byte MsgReady = 0x42;      // guest -> host: running it
        public const byte MsgCommand = 0x43;    // a frame command, either way; the host forwards the guests'
        public const byte MsgHash = 0x44;       // guest -> host: the machine's fingerprint after a frame
        public const byte MsgStop = 0x45;       // host -> guest: no longer playing together

        /// <summary>A guest in a shared game, as the host knows it.</summary>
        public sealed record Guest(uint Peer, string Name, string AseVersion, double RttMs);

        // Fingerprints are compared once a second of PAL time, like a recording's checkpoints
        const int HashInterval = 50;

        // While a frame waits, the input already sent goes out again this often: the other side
        // may be waiting for something that was lost, and a waiting machine sends nothing else
        const long ResendMs = 30;

        // A wait shorter than this is jitter, not worth a word in the status bar
        const long WaitShownMs = 400;

        // Drifting apart again and again means something in the machines differs for good
        const int MaxDesyncsPerMinute = 3;

        // ==================== State (UI thread, unless said otherwise) ====================

        static NetTransport _transport;

        // Host: playing together, whatever epoch is running; the guests taking part
        static bool _sharing;
        static List<Guest> _guests = new();
        static ushort _lastEpoch;
        static readonly Queue<long> _desyncs = new();

        // Guest: inside a host's shared game
        static bool _joined;

        // The running epoch: MachineInput's session. Read by the network thread.
        static volatile Epoch _current;

        // Guest: what the shared game borrowed — the bus settings and the hard disks are given
        // back when it ends, the machine itself at the next power-on
        static MachineSettings _userSettings;
        static bool? _userAcsi, _userGemdos;
        static UserMachine _userMachine;

        sealed record UserMachine(ConfigOptions.STModels Model, ConfigOptions.RAMConfigurations Ram, bool Mono, bool DriveB);

        /// <summary>The shared game started, moved to another epoch or ended (UI thread).</summary>
        public static event Action Changed;

        /// <summary>Whether this computer is playing a shared game, as host or guest.</summary>
        public static bool Active => _sharing || _joined;

        public static bool IsHost => _sharing;

        /// <summary>A guest in a shared game: the machine is the host's, so what replaces it
        /// (a reset, a snapshot, a hard-disk game) is the host's to do.</summary>
        public static bool IsGuest => _joined;

        /// <summary>Frames between a player's input and the frame it lands on, in the running
        /// epoch (0 when none runs).</summary>
        public static int Delay => _current?.Exchange.Delay ?? 0;

        // ==================== Host: starting and stopping ====================

        /// <summary>
        /// Host: shares the machine with <paramref name="guests"/> (every connected guest with
        /// this same version of ASE). Null on success, or what stands in the way.
        /// </summary>
        public static string Start(NetTransport transport, IReadOnlyList<Guest> guests)
        {
            if (_sharing || _joined)
                return "The game is already shared.";

            if (guests.Count == 0)
                return "Nobody has joined yet.";

            var other = guests.FirstOrDefault(g => g.AseVersion != Config.Version);
            if (other != null)
                return $"{other.Name} has ASE {other.AseVersion} and you have {Config.Version}: playing one machine together needs the same version on every computer. The MIDI cable works between any versions, the null-modem cable between any that have it.";

            if (MachineInput.Deterministic)
                return "Stop the recording or the playback first (Emulation menu).";

            if (ASEMain._mem?.ROM == null)
                return "The machine is not running.";

            // A library hard-disk game travels in the snapshot ("HDG "); the user's own hard disks
            // do not, and the program writes to them as it runs
            var config = ConfigOptions.RunninConfig;
            if (!HardDiskGames.Active && (config.GemdosDriveEnabled || config.AcsiImageEnabled))
                return "Your own hard disks cannot be shared: the program writes to the GEMDOS folder and the ACSI image as it runs, " +
                       "and the others would play with different disks. Switch them off in the Hard disk tab (library hard-disk games are fine).";

            _transport = transport;
            _sharing = true;
            _guests = guests.ToList();
            _desyncs.Clear();

            string error = BeginEpoch($"sharing the machine with {Names(_guests)}");
            if (error != null)
            {
                _sharing = false;
                _guests = new();
                Changed?.Invoke();
                return error;
            }

            return null;
        }

        /// <summary>
        /// Host: the connected guests changed (someone joined or left) while sharing. The game
        /// goes on with whoever is there — in a new epoch, everybody from the host's machine —
        /// or ends when nobody is.
        /// </summary>
        public static void GuestsChanged(IReadOnlyList<Guest> connected)
        {
            if (!_sharing)
                return;

            var guests = connected.Where(g => g.AseVersion == Config.Version).ToList();

            foreach (var g in connected.Where(g => g.AseVersion != Config.Version))
                if (!_guests.Any(old => old.Peer == g.Peer))
                    ColoredConsole.WriteLine($"Online play: {g.Name} has ASE {g.AseVersion}, not {Config.Version}, and cannot join the shared machine",
                        ConfigOptions.DebugModes.Quiet);

            if (guests.Select(g => g.Peer).SequenceEqual(_guests.Select(g => g.Peer)))
                return;

            if (guests.Count == 0)
            {
                Stop("everybody else left", tellGuests: false);
                return;
            }

            var joined = guests.Where(g => !_guests.Any(o => o.Peer == g.Peer)).Select(g => g.Name).ToList();
            var left = _guests.Where(o => !guests.Any(g => g.Peer == o.Peer)).Select(g => g.Name).ToList();

            _guests = guests;

            string why = joined.Count > 0 && left.Count > 0 ? $"{string.Join(", ", joined)} joined and {string.Join(", ", left)} left"
                       : joined.Count > 0 ? $"{string.Join(", ", joined)} joined"
                       : $"{string.Join(", ", left)} left";

            string error = BeginEpoch(why);
            if (error != null)
                Stop($"the machine could not be shared again ({error})", tellGuests: true);
        }

        /// <summary>
        /// Ends the shared game on this computer: the host tells its guests (with
        /// <paramref name="tellGuests"/>), and the machine carries on alone with this player's
        /// input only. Harmless when there is none.
        /// </summary>
        public static void Stop(string reason, bool tellGuests = true)
        {
            if (!_sharing && !_joined)
                return;

            var epoch = _current;
            _current = null;

            if (_sharing && tellGuests && _transport != null)
            {
                byte[] message = Message(MsgStop, w => w.Str(reason ?? ""));
                foreach (var g in _guests)
                    Send(g.Peer, message);
            }

            _sharing = false;
            _joined = false;
            _guests = new();

            // The epoch's Close sees it is no longer the current one, and stays quiet
            if (epoch != null && MachineInput.Mode == MachineInput.Modes.Lockstep)
                ASEMain.RunWhilePaused(() => MachineInput.End(reason == null ? "stopped playing together" : $"stopped playing together: {reason}"), out _);

            GiveBackSessionSettings();

            ColoredConsole.WriteLine($"Online play: no longer playing together{(reason == null ? "" : $" ({reason})")}; the machine carries on here",
                ConfigOptions.DebugModes.Quiet);

            Changed?.Invoke();
        }

        /// <summary>The transport is going away (the online session ended): so does the shared
        /// game, without a word to anyone, since every connection is closing anyway.</summary>
        public static void Detach(string reason)
        {
            Stop(reason, tellGuests: false);
            _transport = null;
        }

        static string BeginEpoch(string reason)
        {
            var transport = _transport;
            var guests = _guests;
            var previous = _current;

            int players = guests.Count + 1;
            int delay = DelayFor(guests);
            ulong seed = (ulong)Random.Shared.NextInt64();
            ushort number = ++_lastEpoch;

            byte[] snapshot = null, shared = null;
            InputState machine = null;
            InputState[] states = null;

            if (!ASEMain.RunWhilePaused(() =>
                {
                    // The same machine twice: this computer rebuilds from the one that names its
                    // disks by their paths, the others get the one that names them by file
                    // name only (paths are not machine state, so the two run alike)
                    snapshot = Snapshot.SaveToBytes(includeMedia: true);
                    shared = Snapshot.SaveToBytes(includeMedia: true, shareable: true);

                    // What every player holds goes on into the new epoch: the host's and the
                    // guests' who stay as the running merger knows them, newcomers with nothing.
                    // The first epoch starts from the machine the host has been playing alone.
                    if (previous != null && MachineInput.Mode == MachineInput.Modes.Lockstep)
                    {
                        machine = previous.Merger.Machine;
                        states = new InputState[players];
                        states[0] = previous.Merger.Player(0);
                        for (int g = 0; g < guests.Count; g++)
                        {
                            int old = Array.IndexOf(previous.PeerOf, guests[g].Peer);
                            states[g + 1] = old > 0 ? previous.Merger.Player(old) : new InputState();
                        }
                    }
                    else
                    {
                        var local = MachineInput.LocalState;
                        machine = local.Contribution();
                        states = new InputState[players];
                        states[0] = local;
                        for (int g = 1; g < players; g++)
                            states[g] = new InputState();
                    }
                }, out string error))
                return error;

            var settings = MachineSettings.Capture();
            byte[] packed = Snapshot.Pack(w => w.Bytes(shared));
            string[] names = [PlayerName, .. guests.Select(g => g.Name)];
            uint[] peerOf = [0, .. guests.Select(g => g.Peer)];

            for (int g = 0; g < guests.Count; g++)
            {
                int you = g + 1;
                byte[] message = Message(MsgStart, w =>
                {
                    w.U16(number);
                    w.I64((long)seed);
                    w.U8((byte)delay);
                    w.U8((byte)players);
                    w.U8((byte)you);
                    w.Str(Config.Version);
                    foreach (string n in names)
                        w.Str(n);
                    settings.Write(w);
                    w.Bytes(machine.ToBytes());
                    foreach (var s in states)
                        w.Bytes(s.ToBytes());
                    w.I32(packed.Length);
                    w.Bytes(packed);
                });

                Send(guests[g].Peer, message);
            }

            var epoch = new Epoch(transport, number, players, me: 0, delay, seed, peerOf, names, states, machine);

            if (!Restart(epoch, snapshot, out error))
                return error;

            ColoredConsole.WriteLine($"Online play: playing together — {reason}; {delay} frames of delay (epoch {number})",
                ConfigOptions.DebugModes.Quiet);

            Changed?.Invoke();
            return null;
        }

        // Rebuilds this computer's machine from the epoch's snapshot and starts the epoch on it
        static bool Restart(Epoch epoch, byte[] snapshot, out string error)
        {
            Snapshot.SnapshotFile snap;
            try
            {
                snap = Snapshot.Parse(snapshot);
            }
            catch (Exception ex)
            {
                error = $"the machine could not be read ({ex.Message})";
                return false;
            }

            bool captured = ASEMain.IsMouseCaptured;
            _current = epoch;

            if (!ASEMain.RestartFromSnapshot(snap,
                    () => MachineInput.Begin(MachineInput.Modes.Lockstep, epoch, epoch.Seed, epoch.Merger.Machine.Mouse), out error))
            {
                _current = null;

                // The machine fell back to a clean boot; whatever epoch MachineInput still had is
                // over with the machine it ran on
                if (MachineInput.Mode == MachineInput.Modes.Lockstep)
                    ASEMain.RunWhilePaused(() => MachineInput.End("the shared machine could not be started"), out _);

                return false;
            }

            // A power-on releases the mouse; an epoch is not a power-on for whoever is playing
            if (captured)
                ASEMain.CaptureMouse(true);

            // The drives and drive B come with the machine
            Dispatcher.UIThread.Post(() => ASEMain.MainWindow?.OnSharedMachineChanged());

            error = null;
            return true;
        }

        // D = ceil((one-way time + margin) / frame) + 1 (NETPLAY.md §5.2). With more than one
        // guest, input between two guests goes through the host, so the longest way is two hops.
        // A guest that has only just joined may have no round trip measured yet: it is taken to
        // be as far as the farthest one known (a delay too short only means more waiting).
        static int DelayFor(IReadOnlyList<Guest> guests)
        {
            double known = guests.Where(g => g.RttMs >= 0).Select(g => g.RttMs).DefaultIfEmpty(50).Max();
            var rtts = guests.Select(g => g.RttMs < 0 ? known : g.RttMs).OrderByDescending(r => r).ToList();
            double oneWay = rtts.Count >= 2 ? (rtts[0] + rtts[1]) / 2 : rtts[0] / 2;
            double frameMs = VideoTiming.FRAME_SECONDS * 1000;
            int d = (int)Math.Ceiling((oneWay + Math.Max(8, oneWay / 2)) / frameMs) + 1;
            return Math.Clamp(d, 2, 15);
        }

        // ==================== Guest ====================

        /// <summary>
        /// Guest: the host shares its machine, or starts a new epoch of it. Rebuilds this
        /// computer's machine from it and runs the epoch. UI thread.
        /// </summary>
        static void OnStart(NetTransport transport, uint hostPeer, byte[] message)
        {
            Epoch epoch;
            byte[] snapshot;
            MachineSettings settings;
            string hostVersion;

            try
            {
                var r = new Snapshot.Reader(message);
                r.U8();
                ushort number = r.U16();
                ulong seed = (ulong)r.I64();
                int delay = r.U8();
                int players = r.U8();
                int me = r.U8();
                hostVersion = r.Str();

                if (players < 2 || players > 16 || me < 1 || me >= players || delay < 1 || delay > 60)
                    throw new InvalidDataException("not a shared game this version understands");

                var names = new string[players];
                for (int i = 0; i < players; i++)
                    names[i] = r.Str();

                settings = MachineSettings.Read(r);

                var machine = InputState.FromBytes(r.Bytes(InputState.Size)) ?? throw new InvalidDataException("corrupt input state");
                var states = new InputState[players];
                for (int i = 0; i < players; i++)
                    states[i] = InputState.FromBytes(r.Bytes(InputState.Size)) ?? throw new InvalidDataException("corrupt input state");

                var unpacked = Snapshot.Unpack(r.Bytes(r.I32()));
                snapshot = unpacked.Bytes(unpacked.Remaining);

                var peerOf = new uint[players];
                peerOf[0] = hostPeer;

                epoch = new Epoch(transport, number, players, me, delay, seed, peerOf, names, states, machine);
            }
            catch (Exception ex)
            {
                LeaveWithError($"the host's machine could not be read ({ex.Message})");
                return;
            }

            if (hostVersion != Config.Version)
            {
                LeaveWithError($"the host has ASE {hostVersion} and you have {Config.Version}: playing one machine together needs the same version");
                return;
            }

            bool first = !_joined;

            if (first)
            {
                // What the shared game borrows from this computer's own configuration
                var c = ConfigOptions.RunninConfig;
                _userSettings = MachineSettings.Capture();
                _userAcsi = c.AcsiImageEnabled;
                _userGemdos = c.GemdosDriveEnabled;
                _userMachine ??= new UserMachine(c.STModel, c.RAMConfiguration, c.MonochromeMonitor, c.DriveBEnabled);
                UpdateOverlay();
            }

            _transport = transport;
            _joined = true;

            // The host's bus timing, this player's own mouse; and no hard disks of this computer's,
            // which the host's machine never had (a library game brings its own in the snapshot)
            var config = ConfigOptions.RunninConfig;
            settings.MouseSensitivity = config.MouseSensitivity;
            settings.Apply();
            config.AcsiImageEnabled = false;
            config.GemdosDriveEnabled = false;

            if (!Restart(epoch, snapshot, out string error))
            {
                LeaveWithError($"the host's machine could not be started here ({error})");
                return;
            }

            Send(hostPeer, Message(MsgReady, w => w.U16(epoch.Number)));

            ColoredConsole.WriteLine(first
                ? $"Online play: playing together on {epoch.Names[0]}'s machine — {epoch.Exchange.Delay} frames of delay"
                : $"Online play: {epoch.Names[0]}'s machine again (epoch {epoch.Number})", ConfigOptions.DebugModes.Quiet);

            Changed?.Invoke();
        }

        // A guest that cannot follow the host leaves the online game: the host would otherwise
        // wait for its input for ever
        static void LeaveWithError(string error)
        {
            ColoredConsole.WriteLine($"Online play: [[red]]{error}[[/red]]", ConfigOptions.DebugModes.None);
            Stop(error, tellGuests: false);
            NetSession.Leave();
        }

        // ==================== Giving back what a guest borrowed ====================

        static void GiveBackSessionSettings()
        {
            var config = ConfigOptions.RunninConfig;

            if (_userSettings != null)
            {
                _userSettings.Apply();
                _userSettings = null;
            }

            if (_userAcsi != null)
            {
                config.AcsiImageEnabled = _userAcsi.Value;
                config.GemdosDriveEnabled = _userGemdos.Value;
                _userAcsi = _userGemdos = null;
            }

            UpdateOverlay();
        }

        /// <summary>
        /// Before a power-on that is not a shared game's (ASEMain.HardReset): a guest gets its own
        /// machine back — model, memory, monitor, drive B — which it had been running the host's
        /// of since the shared game.
        /// </summary>
        public static void BeforePowerOn()
        {
            if (_userMachine == null || _joined)
                return;

            var c = ConfigOptions.RunninConfig;
            c.STModel = _userMachine.Model;
            c.RAMConfiguration = _userMachine.Ram;
            c.MonochromeMonitor = _userMachine.Mono;
            c.DriveBEnabled = _userMachine.DriveB;
            _userMachine = null;
            UpdateOverlay();

            Dispatcher.UIThread.Post(() => ASEMain.MainWindow?.OnSharedMachineChanged());
        }

        /// <summary>The Configuration window applied what it shows, the borrowed machine
        /// included: from then on that is the user's own.</summary>
        public static void ForgetUserMachine()
        {
            if (_joined)
                return;

            _userMachine = null;
            UpdateOverlay();
        }

        static void UpdateOverlay()
        {
            var settings = _userSettings;
            bool? acsi = _userAcsi, gemdos = _userGemdos;
            var machine = _userMachine;

            Config.PersistOverlay = settings == null && acsi == null && machine == null ? null : c =>
            {
                if (settings != null)
                {
                    c.CycleExactBus = settings.CycleExactBus;
                    c.BusPhase = settings.BusPhase;
                    c.MfpWaitCycles = settings.MfpWaitCycles;
                    c.CpuSyncSliceCycles = settings.CpuSyncSliceCycles;
                }

                if (acsi != null)
                {
                    c.AcsiImageEnabled = acsi.Value;
                    c.GemdosDriveEnabled = gemdos.Value;
                }

                if (machine != null)
                {
                    c.STModel = machine.Model;
                    c.RAMConfiguration = machine.Ram;
                    c.MonochromeMonitor = machine.Mono;
                    c.DriveBEnabled = machine.DriveB;
                }
            };
        }

        // ==================== Messages ====================

        /// <summary>
        /// The transport's message hook (network thread): commands and fingerprints, which frames
        /// wait for and which the UI's event pump would deliver a tenth of a second late. False
        /// for anything else, which goes on to the event queue (<see cref="OnSessionMessage"/>).
        /// </summary>
        public static bool OnNetworkMessage(uint peer, byte[] message)
        {
            if (message.Length == 0)
                return false;

            switch (message[0])
            {
                case MsgCommand:
                {
                    var epoch = _current;
                    if (epoch == null)
                        return true;

                    if (!LockstepExchange.DecodeCommand(message, out ushort number, out int player, out long frame, out var command)
                        || number != epoch.Number)
                        return true;

                    int from = epoch.PlayerOf(peer);
                    if (from < 0 || !epoch.Exchange.AddCommand(from, player, frame, command))
                        return true;

                    // The host forwards a guest's commands to every other guest, as they came
                    if (epoch.Exchange.IsHost)
                        for (int p = 1; p < epoch.PeerOf.Length; p++)
                            if (p != from)
                                epoch.Transport.SendMessage(epoch.PeerOf[p], message);

                    return true;
                }

                case MsgHash:
                {
                    var epoch = _current;
                    if (epoch == null || !epoch.Exchange.IsHost || message.Length != 1 + 2 + 4 + 8)
                        return true;

                    var r = new Snapshot.Reader(message);
                    r.U8();
                    ushort number = r.U16();
                    long frame = r.U32();
                    ulong hash = (ulong)r.I64();

                    int from = epoch.PlayerOf(peer);
                    if (number == epoch.Number && from > 0)
                        epoch.RecordHash(from, frame, hash);

                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>The transport's datagram hook (network thread): the other players' input.</summary>
        public static bool OnNetworkDatagram(uint peer, byte channel, byte[] data)
        {
            if (channel != LockstepExchange.Channel)
                return false;

            var epoch = _current;
            if (epoch != null)
            {
                int from = epoch.PlayerOf(peer);
                if (from >= 0)
                    epoch.Exchange.OnDatagram(from, data);
            }

            return true;
        }

        /// <summary>
        /// A session message from the event queue (UI thread): a host sharing its machine or
        /// stopping, a guest saying it runs the machine. True when it was the shared game's.
        /// </summary>
        public static bool OnSessionMessage(NetTransport transport, uint peer, byte[] message, bool fromHost)
        {
            if (message.Length == 0)
                return false;

            switch (message[0])
            {
                case MsgStart when fromHost:
                    OnStart(transport, peer, message);
                    return true;

                case MsgStop when fromHost:
                    if (_joined)
                    {
                        string reason;
                        try
                        {
                            var r = new Snapshot.Reader(message);
                            r.U8();
                            reason = r.Str();
                        }
                        catch (Exception) { reason = ""; }

                        string host = _current?.Names[0] ?? "the host";
                        Stop(string.IsNullOrEmpty(reason) ? $"{host} stopped it" : $"{host} stopped it: {reason}", tellGuests: false);
                    }
                    return true;

                case MsgReady:
                    if (message.Length == 3 && _current is { } epoch && epoch.Exchange.IsHost
                        && ((message[1] << 8) | message[2]) == epoch.Number)
                    {
                        int player = epoch.PlayerOf(peer);
                        if (player > 0)
                            epoch.Running[player] = true;
                    }
                    return true;

                case MsgStart:
                case MsgStop:
                case MsgCommand:
                case MsgHash:
                    return true;

                default:
                    return false;
            }
        }

        // ==================== Things an epoch reports (posted to the UI thread) ====================

        // The epoch ended without the shared game ending it: the machine went away under it.
        // The host shares the new one; a guest cannot follow and leaves.
        static void EpochClosed(Epoch epoch, string reason)
        {
            if (epoch != _current)
                return;

            _current = null;

            if (_sharing)
            {
                string error = BeginEpoch(reason ?? "the machine changed");
                if (error != null)
                    Stop($"the machine could not be shared again ({error})", tellGuests: true);
            }
            else if (_joined)
            {
                LeaveWithError(reason ?? "the shared machine stopped");
            }
        }

        // Host: a guest's fingerprint differs from the host's at the same frame
        static void Desynced(Epoch epoch, int player, long frame)
        {
            if (epoch != _current || !_sharing)
                return;

            long now = Environment.TickCount64;
            while (_desyncs.Count > 0 && now - _desyncs.Peek() > 60_000)
                _desyncs.Dequeue();
            _desyncs.Enqueue(now);

            string who = epoch.Names[player];

            if (_desyncs.Count > MaxDesyncsPerMinute)
            {
                Stop($"the machines kept drifting apart ({who}'s, at frame {frame})", tellGuests: true);
                return;
            }

            ColoredConsole.WriteLine($"Online play: [[yellow]]{who}'s machine drifted apart from yours at frame {frame} of epoch {epoch.Number}[[/yellow]]; sending it the machine again",
                ConfigOptions.DebugModes.Quiet);

            string error = BeginEpoch($"{who}'s machine had drifted apart");
            if (error != null)
                Stop($"the machine could not be shared again ({error})", tellGuests: true);
        }

        // ==================== Status ====================

        /// <summary>One line for the status bar while playing together, or null.</summary>
        public static string StatusText
        {
            get
            {
                if (!Active)
                    return null;

                var epoch = _current;
                if (epoch == null)
                    return _sharing ? "Sharing the machine…" : "Waiting for the host's machine…";

                int waiting = epoch.WaitingFor;
                if (waiting >= 0 && Environment.TickCount64 - Interlocked.Read(ref epoch.WaitingSince) >= WaitShownMs)
                {
                    if (epoch.Exchange.IsHost && waiting > 0 && !epoch.Running[waiting])
                        return $"Sending the machine to {epoch.Names[waiting]}…";

                    return $"Waiting for {epoch.Names[waiting]}…";
                }

                string others = epoch.Exchange.IsHost
                    ? (epoch.Names.Length == 2 ? epoch.Names[1] : $"{epoch.Names.Length - 1} players")
                    : epoch.Names[0];

                return $"Playing together · {others} · {epoch.Exchange.Delay} frames";
            }
        }

        /// <summary>The players of the running epoch, host first (empty when none runs).</summary>
        public static IReadOnlyList<string> Players => _current?.Names ?? [];

        // ==================== Snapshots in a shared game ====================

        /// <summary>
        /// F11 in a shared game: the snapshot is taken at the frame this computer's next input
        /// lands on, where every machine completes the STX command in flight with it — taking it
        /// here and now would change this machine alone. False when no shared game runs.
        /// </summary>
        public static bool ScheduleSnapshot(out string path)
        {
            path = ASEMain.NewTimestampedPath(Config.SnapshotsDir, "ase_snapshot", "snap");
            return MachineInput.Schedule(new FrameCommand(CommandKind.Snapshot, 0, Encoding.UTF8.GetBytes(path)));
        }

        // ==================== Helpers ====================

        static string PlayerName => (ConfigOptions.RunninConfig.NetplayPlayerName ?? "").Trim() is { Length: > 0 } n ? n : "Host";

        static string Names(IEnumerable<Guest> guests) => string.Join(", ", guests.Select(g => g.Name));

        static byte[] Message(byte type, Action<Snapshot.Writer> body)
        {
            using var ms = new MemoryStream();
            var w = new Snapshot.Writer(ms);
            w.U8(type);
            body(w);
            return ms.ToArray();
        }

        static void Send(uint peer, byte[] message)
        {
            try { _transport?.SendMessage(peer, message); }
            catch (Exception) { /* the transport only queues */ }
        }

        // ==================== An epoch ====================

        /// <summary>
        /// One machine shared from one snapshot: MachineInput's session for as long as it runs.
        /// Its frame methods run on the emulation thread; the hooks above reach it from the
        /// network thread through its exchange and RecordHash.
        /// </summary>
        sealed class Epoch : MachineInput.IFrameSession
        {
            public readonly NetTransport Transport;
            public readonly LockstepExchange Exchange;
            public readonly InputMerger Merger;
            public readonly ulong Seed;

            /// <summary>Player → transport connection. The host knows every guest's; a guest knows
            /// only the host's (player 0), since everything goes through it.</summary>
            public readonly uint[] PeerOf;

            public readonly string[] Names;

            /// <summary>Host: which guests said they run this epoch's machine.</summary>
            public readonly bool[] Running;

            public ushort Number => Exchange.Epoch;

            /// <summary>The player the frame being started waits for (-1: none), and since when
            /// (Environment.TickCount64). Written on the emulation thread, read by the status.</summary>
            public volatile int WaitingFor = -1;
            public long WaitingSince;

            // Emulation thread
            bool _synced;
            readonly List<InputEvent> _pending = new();
            readonly List<FrameCommand> _pendingCommands = new();
            readonly List<(int Player, InputEvent Event)> _events = new();
            readonly List<(int Player, FrameCommand Command)> _commands = new();
            long _nextFrame;
            long _lastSent = long.MinValue;

            // Host: fingerprints, the own ones from the emulation thread and the guests' from the
            // network thread
            readonly object _hashLock = new();
            readonly Dictionary<long, ulong> _ownHashes = new();
            readonly Dictionary<(int Player, long Frame), ulong> _guestHashes = new();
            bool _desyncReported;

            public Epoch(NetTransport transport, ushort number, int players, int me, int delay, ulong seed,
                         uint[] peerOf, string[] names, InputState[] states, InputState machine)
            {
                Transport = transport;
                Exchange = new LockstepExchange(number, players, me, delay);
                Merger = new InputMerger(states, machine);
                Seed = seed;
                PeerOf = peerOf;
                Names = names;
                Running = new bool[players];
                Running[me] = true;
            }

            /// <summary>The player behind a connection, or -1.</summary>
            public int PlayerOf(uint peer)
            {
                if (!Exchange.IsHost)
                    return peer == PeerOf[0] ? 0 : -1;

                for (int p = 1; p < PeerOf.Length; p++)
                    if (PeerOf[p] == peer)
                        return p;

                return -1;
            }

            public bool Ready(long frame)
            {
                if (Exchange.Ready(frame, out int missing))
                {
                    WaitingFor = -1;
                    return true;
                }

                if (WaitingFor < 0)
                    Interlocked.Exchange(ref WaitingSince, Environment.TickCount64);

                WaitingFor = missing;
                return false;
            }

            public void Idle()
            {
                if (Environment.TickCount64 - _lastSent >= ResendMs)
                    SendInput();
            }

            public bool BeginFrame(long frame, IReadOnlyList<FrameCommand> logged, IReadOnlyList<InputEvent> captured,
                                   out IReadOnlyList<FrameCommand> run, out IReadOnlyList<InputEvent> apply)
            {
                _nextFrame = frame;

                // This player's input since the last frame lands D frames from now. The first
                // frame of an epoch also tells the others what this player is holding: the
                // epoch started from what the host's machine knew, which may be stale.
                var events = new List<InputEvent>();

                if (!_synced)
                {
                    _synced = true;
                    Merger.Player(Exchange.Me).DiffTo(MachineInput.LocalState, events);
                }

                events.AddRange(_pending);
                events.AddRange(captured);
                _pending.Clear();

                if (events.Count > LockstepExchange.MaxEventsPerFrame)
                {
                    _pending.AddRange(events.Skip(LockstepExchange.MaxEventsPerFrame));
                    events.RemoveRange(LockstepExchange.MaxEventsPerFrame, events.Count - LockstepExchange.MaxEventsPerFrame);
                }

                _pendingCommands.AddRange(logged);
                int count = Math.Min(_pendingCommands.Count, LockstepExchange.MaxCommandsPerFrame);
                long target = frame + Exchange.Delay;

                for (int i = 0; i < count; i++)
                {
                    var command = _pendingCommands[i];
                    Exchange.AddCommand(Exchange.Me, Exchange.Me, target, command);

                    // A snapshot's path is this computer's business: the others get the flush
                    var wire = command.Kind == CommandKind.Snapshot ? new FrameCommand(CommandKind.FdcFlush, 0, null) : command;
                    byte[] message = LockstepExchange.EncodeCommand(MsgCommand, Number, Exchange.Me, target, wire);

                    if (Exchange.IsHost)
                        for (int p = 1; p < PeerOf.Length; p++)
                            Transport.SendMessage(PeerOf[p], message);
                    else
                        Transport.SendMessage(PeerOf[0], message);
                }

                _pendingCommands.RemoveRange(0, count);

                Exchange.Produce(target, events, count);
                SendInput();

                // Everybody's input for this frame, combined
                var output = new List<InputEvent>();

                if (frame == 0)
                    Merger.Settle(output);

                _events.Clear();
                _commands.Clear();
                Exchange.Take(frame, _events, _commands);

                foreach (var (player, e) in _events)
                    Merger.Apply(player, e, output);

                run = _commands.Select(c => c.Command).ToList();
                apply = output;
                return true;
            }

            void SendInput()
            {
                _lastSent = Environment.TickCount64;

                try
                {
                    if (Exchange.IsHost)
                    {
                        for (int p = 1; p < PeerOf.Length; p++)
                            Transport.SendDatagram(PeerOf[p], LockstepExchange.Channel, Exchange.BuildDatagram(p, _nextFrame));
                    }
                    else
                    {
                        Transport.SendDatagram(PeerOf[0], LockstepExchange.Channel, Exchange.BuildDatagram(0, _nextFrame));
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The online session closed under the frame loop; the shared game ends with it
                }
            }

            public bool EndFrame(long completed)
            {
                _nextFrame = completed;

                if (completed % HashInterval != 0)
                    return true;

                ulong hash = MachineHash.Compute();

                if (Exchange.IsHost)
                {
                    RecordHash(0, completed, hash);
                }
                else
                {
                    var message = new byte[1 + 2 + 4 + 8];
                    message[0] = MsgHash;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(1), Number);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(3), (uint)completed);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(7), hash);
                    Transport.SendMessage(PeerOf[0], message);
                }

                return true;
            }

            /// <summary>Host: a fingerprint after <paramref name="frame"/>, its own (player 0) or
            /// a guest's. The first one that differs from the host's reports the drift.</summary>
            public void RecordHash(int player, long frame, ulong hash)
            {
                int differs = -1;

                lock (_hashLock)
                {
                    if (_desyncReported)
                        return;

                    if (player == 0)
                    {
                        _ownHashes[frame] = hash;

                        for (int p = 1; p < Names.Length; p++)
                            if (_guestHashes.Remove((p, frame), out ulong theirs) && theirs != hash)
                                differs = p;

                        // A second of fingerprints is plenty to wait for a guest's; older ones go
                        foreach (long old in _ownHashes.Keys.Where(f => f < frame - 20 * HashInterval).ToList())
                            _ownHashes.Remove(old);
                    }
                    else if (_ownHashes.TryGetValue(frame, out ulong mine))
                    {
                        if (mine != hash)
                            differs = player;
                    }
                    else if (_guestHashes.Count < 1000)
                    {
                        _guestHashes[(player, frame)] = hash;
                    }

                    if (differs < 0)
                        return;

                    _desyncReported = true;
                }

                Dispatcher.UIThread.Post(() => Desynced(this, differs, frame));
            }

            public void Leaving(InputState local, List<InputEvent> owed) => Merger.Leave(local, owed);

            public string Close(string reason)
            {
                Dispatcher.UIThread.Post(() => EpochClosed(this, reason));
                return $"Online play: epoch {Number} of the shared machine ended{(reason == null ? "" : $" ({reason})")}";
            }
        }
    }
}
