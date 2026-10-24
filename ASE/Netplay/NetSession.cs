/*
 *
 * Online play as the rest of ASE sees it (NETPLAY.md, phase 2): hosting a game, looking for
 * one, joining, and who is connected — one session at a time, on the UI thread.
 *
 * The network itself is NetTransport's, on a thread of its own. This class owns the
 * transport while a session lasts, drains its events on a dispatcher timer, writes them to
 * the console and hands them to whoever listens (the Online play window, the status bar).
 * Nothing exists before the user starts hosting or opens the Join window, and Stop closes it
 * all (NETPLAY.md §1.2).
 *
 * In a MIDI cable game the ST's MIDI sockets are the network (MidiCable, phase 3): this class
 * keeps the host's ring — the guests in the order they were accepted — and plugs the cable in
 * and out as players come and go. A null-modem game does the same with the RS-232 port
 * (SerialCable, phase 6), between the host and its one guest. The host can also share its
 * machine with everybody connected (Lockstep, phase 4); while that runs the cables are out,
 * since every computer runs the same machine, and players joining or leaving reach the shared
 * game from here.
 *
 * Over the Internet (phase 5) the host gives out an invitation code and a guest joins with it;
 * when the host's router lets nothing in, the guest's answer code tells the host where to punch
 * through (NetCodes). The public STUN servers are asked only then — when a code is made or used
 * — never just for hosting, since asking tells them this computer's address (NETPLAY.md §8.7).
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using static ASE.Config;

namespace ASE.Netplay
{
    public static class NetSession
    {
        public enum Roles { None, Host, Guest }

        /// <summary>What the host chose to play: its machine shared with whoever joins (on the
        /// local network, or over the Internet with a code), the MIDI cable, or the null-modem
        /// cable.</summary>
        public enum HostKinds { TogetherLocal, TogetherInternet, MidiCable, NullModem }

        /// <summary>What a guest's host said it plays (<see cref="MsgKindOfGame"/>).</summary>
        public enum GameKinds { Unknown, Shared, MidiCable, NullModem }

        /// <summary>How a guest looks for its game.</summary>
        public enum GuestWays { LocalNetwork, Internet }

        /// <summary>Session message: the host tells each guest what kind of game it hosts (one
        /// byte: 0 for a shared machine, 1 for the MIDI cable, 2 for the null-modem cable), so
        /// the guest plugs a cable in only for a cable game and can tell its player what happens
        /// next. A guest of a version before the null-modem cable takes 2 for a shared machine,
        /// and waits for one.</summary>
        public const byte MsgKindOfGame = 0x46;

        /// <summary>A null-modem cable has two ends: the host and one guest.</summary>
        public const int NullModemGuests = 1;

        public enum GuestStates
        {
            /// <summary>Looking for games on the local network; not connecting to anyone.</summary>
            Searching,
            Connecting,
            /// <summary>Connected, waiting for the host to accept.</summary>
            WaitingForAccept,
            Connected,
        }

        /// <summary>A guest still connected when the host lets in its 16th machine (the MIDI
        /// ring's limit, NETPLAY.md §6.1).</summary>
        public const int MaxGuests = 15;

        public const int MaxNameLength = 32;

        public static Roles Role { get; private set; }
        public static GuestStates GuestState { get; private set; }

        /// <summary>Guest: who the session is with (the address or name typed, then the host's
        /// name once it accepted).</summary>
        public static string Remote { get; private set; } = "";

        /// <summary>Guest: the host's connection, once connected.</summary>
        public static uint HostPeer { get; private set; }

        /// <summary>Host: what it chose to play.</summary>
        public static HostKinds HostKind { get; private set; }

        /// <summary>Guest: how it looks for its game.</summary>
        public static GuestWays GuestWay { get; private set; }

        /// <summary>Guest: what the host said it plays (<see cref="GameKinds.Unknown"/> until it says).</summary>
        public static GameKinds HostGame { get; private set; }

        /// <summary>Host: the port it listens on.</summary>
        public static int Port => _transport?.Port ?? 0;

        public static bool HasIPv6 => _transport?.HasIPv6 == true;

        public static bool Active => _transport != null;

        public static IReadOnlyList<PeerInfo> Peers => _transport?.Peers ?? [];

        /// <summary>Every event of the transport, on the UI thread.</summary>
        public static event Action<NetEvent> EventReceived;

        /// <summary>The role or the guest's state changed, on the UI thread.</summary>
        public static event Action StateChanged;

        /// <summary>Every pass of the event pump (ten a second, while there is a session), on
        /// the UI thread: for whatever shows round trips and player lists.</summary>
        public static event Action Pumped;

        static NetTransport _transport;
        static DispatcherTimer _pump;
        static string _game;

        // Over the Internet: the host's invitation code, a guest's answer, and a STUN question
        // waiting for the transport's answer
        static InviteCode _invitation;
        static InviteCode _joinedWith;
        static string _answerText;
        static NatKind _nat;
        static TaskCompletionSource<StunResult> _stunWaiter;

        // The port the router forwards to the game while a code is out (NETPLAY.md §7.3): the
        // request (made once per port and session), and the removal under way once it ends
        static Task<PortMapping> _routerTask;
        static int _routerPort;
        static Task _routerClosing;

        // Invitation links through the website (NETPLAY.md §8): the host's link in force and its
        // wait for the answer, and the link a guest joined with
        static WebMailbox _mailbox;
        static WebCode _hostLink, _guestLink;
        static CancellationTokenSource _linkWait;
        static bool _linkReported;

        // Guest: the report of how joining went is signed with the key that went with the answer,
        // so one that comes first (a connection on the local network beats STUN) waits for it
        static bool _answerPending;
        static (bool Connected, IPEndPoint Address)? _pendingReport;

        // Host: the guests in the MIDI ring, in the order they were accepted, their names and
        // their versions of ASE
        static readonly List<uint> _ring = new();
        static readonly Dictionary<uint, string> _names = new();
        static readonly Dictionary<uint, string> _versions = new();

        static NetSession()
        {
            // The shared game starting or ending rewires the cables and changes the status
            Lockstep.Changed += () =>
            {
                RewireCables();
                StateChanged?.Invoke();
            };
        }

        // ==================== Starting and stopping (UI thread) ====================

        /// <summary>Starts hosting on the configured port, for <paramref name="kind"/>.</summary>
        public static bool StartHosting(HostKinds kind, out string error)
        {
            if (!CheckCanStart(requireName: true, out error))
                return false;

            // A second guest asking to join a null-modem game is told it is full
            var config = ConfigOptions.RunninConfig;
            var transport = NetTransport.StartHost(config.NetplayPort, PlayerName, Config.Version,
                                                   kind == HostKinds.NullModem ? NullModemGuests : MaxGuests, out error);
            if (transport == null)
                return false;

            _game = CurrentGame();
            transport.SetGame(_game);

            HostKind = kind;
            Begin(transport, Roles.Host);
            ColoredConsole.WriteLine($"Online play: hosting [[green]]{_game}[[/green]] on UDP port {transport.Port}" +
                (transport.HasIPv6 ? " (IPv4 and IPv6)" : " (IPv4)"), ConfigOptions.DebugModes.Quiet);
            return true;
        }

        /// <summary>Opens the network as a guest, and for <see cref="GuestWays.LocalNetwork"/>
        /// starts looking for games on it. Joining comes after, by picking one, with a code or
        /// typing an address.</summary>
        public static bool StartSearching(GuestWays way, out string error)
        {
            if (!CheckCanStart(requireName: false, out error))
                return false;

            // Over the Internet, the game's port: one forwarded by hand then serves this side as
            // a guest too. On the local network nothing needs it, and a host on this computer has it
            int port = ConfigOptions.RunninConfig.NetplayPort;
            var transport = NetTransport.StartGuest(PlayerName, Config.Version, out error, way == GuestWays.Internet ? port : 0);
            if (transport == null)
                return false;

            if (way == GuestWays.Internet && transport.Port != port)
                ColoredConsole.WriteLine($"Online play: UDP port {port} is in use on this computer; joining from port {transport.Port}, which a port forwarded by hand does not reach",
                    ConfigOptions.DebugModes.Quiet);

            GuestWay = way;
            Begin(transport, Roles.Guest);
            GuestState = GuestStates.Searching;

            if (way == GuestWays.LocalNetwork)
                transport.StartDiscovery(ConfigOptions.RunninConfig.NetplayPort);

            return true;
        }

        /// <summary>Guest: joins the host at <paramref name="address"/>, as found on the local
        /// network. Null, or the reason it cannot.</summary>
        public static string Join(IPEndPoint address, string label)
        {
            if (Role != Roles.Guest || GuestState != GuestStates.Searching)
                return "Already connecting.";

            if (string.IsNullOrWhiteSpace(PlayerName))
                return NameMissing;

            Remote = label;
            GuestState = GuestStates.Connecting;
            _transport.SetName(PlayerName);
            _transport.StopDiscovery();
            _transport.Connect([address]);
            StateChanged?.Invoke();
            return null;
        }

        /// <summary>
        /// Guest: joins the host typed by the user — an IPv4 or IPv6 address or a name, with
        /// ":port" when it is not the configured one (an IPv6 address with a port goes in
        /// brackets). False with the reason when it cannot even be resolved.
        /// </summary>
        public static async Task<(bool ok, string error)> Join(string text)
        {
            if (Role != Roles.Guest || GuestState != GuestStates.Searching)
                return (false, "Already connecting.");

            if (string.IsNullOrWhiteSpace(PlayerName))
                return (false, NameMissing);

            if (!TryParseAddress(text, ConfigOptions.RunninConfig.NetplayPort, out string host, out int port))
                return (false, $"'{text}' is not an address. Type an IP address or a name, followed by :port if it is not {ConfigOptions.RunninConfig.NetplayPort}.");

            IPAddress[] addresses;

            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                GuestState = GuestStates.Connecting;
                Remote = host;
                StateChanged?.Invoke();

                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
                {
                    addresses = [];
                }

                // The session may have been stopped while the name was being resolved
                if (Role != Roles.Guest)
                    return (false, null);

                GuestState = GuestStates.Searching;

                if (addresses.Length == 0)
                {
                    StateChanged?.Invoke();
                    return (false, $"The name '{host}' could not be found.");
                }
            }

            Remote = host;
            GuestState = GuestStates.Connecting;
            _transport.SetName(PlayerName);
            _transport.StopDiscovery();
            _transport.Connect(addresses.Select(a => new IPEndPoint(a, port)).ToArray());
            StateChanged?.Invoke();
            return (true, null);
        }

        /// <summary>Host: lets in a guest that asked to join.</summary>
        public static void Accept(uint peer) => _transport?.Accept(peer);

        /// <summary>Host: turns a guest away.</summary>
        public static void Reject(uint peer) => _transport?.Reject(peer);

        /// <summary>Host: closes a guest's connection.</summary>
        public static void Kick(uint peer) => _transport?.Disconnect(peer);

        /// <summary>
        /// Whether the Online play window is open. Looking for games belongs to it (NETPLAY.md
        /// §7.2): a guest whose game ends goes back to looking only while it is open, and
        /// otherwise the network closes.
        /// </summary>
        public static bool WindowOpen { get; set; }

        /// <summary>Guest: leaves the game (or gives up joining) and goes back to looking for
        /// one, if the Online play window is there to show what it finds.</summary>
        public static void Leave()
        {
            if (Role != Roles.Guest || _transport == null)
                return;

            // A fresh transport rather than reusing this one: whatever the old connection left
            // behind (a late Welcome, a parked attempt) dies with it
            var way = GuestWay;
            Stop();

            if (WindowOpen)
                StartSearching(way, out _);
        }

        /// <summary>Ends the session, whatever it is: every connection gets its CLOSE and the
        /// network closes. Harmless when there is none.</summary>
        public static void Stop()
        {
            if (_transport == null)
                return;

            var transport = _transport;
            _transport = null;
            _pump?.Stop();

            if (Role == Roles.Host)
                ColoredConsole.WriteLine("Online play: no longer hosting", ConfigOptions.DebugModes.Quiet);

            // A shared game ends with the connections; each computer keeps its machine
            Lockstep.Detach("the online game ended");

            Role = Roles.None;
            GuestState = GuestStates.Searching;
            Remote = "";
            HostPeer = 0;
            HostGame = GameKinds.Unknown;

            MidiCable.Unplug();
            SerialCable.Unplug();
            _ring.Clear();
            _names.Clear();
            _versions.Clear();

            _invitation = null;
            _joinedWith = null;
            _answerText = null;
            _nat = NatKind.Unknown;
            _stunWaiter?.TrySetResult(new StunResult([]));
            _stunWaiter = null;
            CloseRouterPort();
            CancelLink();
            _guestLink = null;
            AnswerThroughWeb = false;
            _answerPending = false;
            _pendingReport = null;

            transport.Dispose();
            StateChanged?.Invoke();
        }

        /// <summary>Ends the session as ASE closes: <see cref="Stop"/>, and a moment's wait for the
        /// router to close the port it opened.</summary>
        public static void Shutdown()
        {
            Stop();

            try
            {
                _routerClosing?.Wait(TimeSpan.FromSeconds(2.5));
            }
            catch (AggregateException)
            {
                // Whatever the router did, ASE closes; the mapping runs out within the hour
            }
        }

        const string NameMissing = "Type your name first: it is what the other players see.";

        static bool CheckCanStart(bool requireName, out string error)
        {
            error = null;

            if (_transport != null)
            {
                error = Role == Roles.Host ? "This ASE is already hosting a game." : "This ASE is already looking for a game.";
                return false;
            }

            if (requireName && string.IsNullOrWhiteSpace(PlayerName))
            {
                error = NameMissing;
                return false;
            }

            return true;
        }

        static void Begin(NetTransport transport, Roles role)
        {
            _transport = transport;
            Role = role;
            Remote = "";
            HostPeer = 0;
            HostGame = GameKinds.Unknown;

            // The cables' bytes and the shared game's input, commands and fingerprints are taken
            // on the network thread, not from the event queue
            transport.MessageHook = (peer, message) => MidiCable.OnMessage(peer, message) || SerialCable.OnMessage(peer, message)
                                                       || Lockstep.OnNetworkMessage(peer, message);
            transport.DatagramHook = Lockstep.OnNetworkDatagram;

            _pump ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Input, (_, _) => Pump());
            _pump.Start();

            StateChanged?.Invoke();
        }

        static string PlayerName
        {
            get
            {
                string name = (ConfigOptions.RunninConfig.NetplayPlayerName ?? "").Trim();
                return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
            }
        }

        /// <summary>What the host is running, as announced: the library hard-disk game, or the
        /// disk in drive A.</summary>
        static string CurrentGame()
        {
            if (HardDiskGames.Active && !string.IsNullOrEmpty(HardDiskGames.Title))
                return HardDiskGames.Title;

            string disk = ASEMain.Drive(0).DisplayName;
            return string.IsNullOrEmpty(disk) ? "No disk" : Path.GetFileNameWithoutExtension(disk);
        }

        // ==================== Events (UI thread, every 100 ms) ====================

        static void Pump()
        {
            var transport = _transport;
            if (transport == null)
                return;

            // The announced game follows whatever the host puts in drive A
            if (Role == Roles.Host)
            {
                string game = CurrentGame();
                if (game != _game)
                {
                    _game = game;
                    transport.SetGame(game);
                }
            }

            while (transport == _transport && transport.TryGetEvent(out var e))
            {
                Handle(e);
                EventReceived?.Invoke(e);
            }

            Pumped?.Invoke();
        }

        static void Handle(NetEvent e)
        {
            switch (e)
            {
                case NetLog log:
                    ColoredConsole.WriteLine(log.Error ? $"Online play: [[red]]{log.Text}[[/red]]" : $"Online play: {log.Text}",
                        log.Error ? ConfigOptions.DebugModes.None : log.Notable ? ConfigOptions.DebugModes.Quiet : ConfigOptions.DebugModes.Information);
                    break;

                case JoinPending:
                    if (Role == Roles.Guest && GuestState == GuestStates.Connecting)
                    {
                        GuestState = GuestStates.WaitingForAccept;
                        StateChanged?.Invoke();
                    }
                    break;

                case JoinRequest req:
                    ColoredConsole.WriteLine($"Online play: [[cyan]]{req.Name}[[/cyan]] (ASE {req.AseVersion}) asks to join from {req.Address}",
                        ConfigOptions.DebugModes.Quiet);
                    break;

                case PeerConnected c:
                    ColoredConsole.WriteLine(Role == Roles.Host
                        ? $"Online play: [[green]]{c.Name}[[/green]] joined from {c.Address}"
                        : $"Online play: joined [[green]]{c.Name}[[/green]]'s game ({c.Game}, ASE {c.AseVersion}) at {c.Address}",
                        ConfigOptions.DebugModes.Quiet);

                    if (Role == Roles.Guest)
                    {
                        GuestState = GuestStates.Connected;
                        HostPeer = c.Peer;
                        Remote = c.Name;
                        RewireCables();
                        ReportLink(connected: true, c.Address);
                        StateChanged?.Invoke();
                    }
                    else
                    {
                        byte kind = HostKind switch { HostKinds.MidiCable => 1, HostKinds.NullModem => 2, _ => 0 };
                        _transport.SendMessage(c.Peer, [MsgKindOfGame, kind]);
                        _ring.Add(c.Peer);
                        _names[c.Peer] = c.Name;
                        _versions[c.Peer] = c.AseVersion;
                        UpdateRing();
                        Lockstep.GuestsChanged(SharingGuests());
                    }
                    break;

                case PeerLeft left:
                    ColoredConsole.WriteLine($"Online play: {(string.IsNullOrEmpty(left.Name) ? "a player" : left.Name)} — {left.Reason}",
                        ConfigOptions.DebugModes.Quiet);

                    if (Role == Roles.Guest)
                    {
                        // The host went away: back to looking for a game on a fresh transport
                        GuestState = GuestStates.Searching;
                        HostPeer = 0;
                        Leave();
                    }
                    else if (_ring.Remove(left.Peer))
                    {
                        _names.Remove(left.Peer);
                        _versions.Remove(left.Peer);
                        UpdateRing();
                        Lockstep.GuestsChanged(SharingGuests());
                    }
                    break;

                case ConnectFailed failed:
                    ColoredConsole.WriteLine($"Online play: [[yellow]]{failed.Reason}[[/yellow]]", ConfigOptions.DebugModes.Quiet);

                    if (failed.NoAnswer)
                        ReportLink(connected: false, null);

                    if (Role == Roles.Guest)
                    {
                        GuestState = GuestStates.Searching;
                        HostPeer = 0;
                        Leave();
                    }
                    break;

                case MessageReceived m:
                    bool fromHost = Role == Roles.Guest && GuestState == GuestStates.Connected && m.Peer == HostPeer;

                    // The shared game's own (the rest of its traffic is taken on the network thread)
                    if (Lockstep.OnSessionMessage(_transport, m.Peer, m.Data, fromHost))
                        break;

                    if (fromHost && m.Data.Length == 2 && m.Data[0] == MsgKindOfGame)
                    {
                        HostGame = m.Data[1] switch { 1 => GameKinds.MidiCable, 2 => GameKinds.NullModem, _ => GameKinds.Shared };
                        RewireCables();

                        if (HostGame == GameKinds.MidiCable)
                            ColoredConsole.WriteLine($"Online play: MIDI ports linked through {Remote}'s ring", ConfigOptions.DebugModes.Quiet);
                        else if (HostGame == GameKinds.NullModem)
                            ColoredConsole.WriteLine($"Online play: serial port linked to {Remote}'s by a null-modem cable", ConfigOptions.DebugModes.Quiet);

                        StateChanged?.Invoke();
                    }
                    break;

                case DatagramReceived:
                    // Nothing else speaks on the unreliable channel
                    break;

                case StunResult stun:
                    _stunWaiter?.TrySetResult(stun);
                    break;
            }
        }

        // ==================== The cables: the MIDI ring, the null modem ====================

        // Host: re-forms the ring (or the null-modem cable) after someone joined or left, and says so
        static void UpdateRing()
        {
            RewireCables();

            if (Lockstep.Active)
                return;

            if (HostKind == HostKinds.MidiCable)
                ColoredConsole.WriteLine(_ring.Count == 0
                    ? "Online play: MIDI ports back to the configured MIDI"
                    : $"Online play: MIDI ring — {MidiLinkText}", ConfigOptions.DebugModes.Quiet);
            else if (HostKind == HostKinds.NullModem)
                ColoredConsole.WriteLine(_ring.Count == 0
                    ? "Online play: nothing in the serial port any more"
                    : $"Online play: serial port linked by a null-modem cable — {SerialLinkText}", ConfigOptions.DebugModes.Quiet);
        }

        // Where the ST's MIDI sockets and RS-232 port go: into the ring or the null-modem cable
        // only in a game of that kind, and only while the machine is not shared (every computer
        // runs it then, and each hears its own MIDI out). Anywhere else the MIDI stays with the
        // configured MIDI — a host waiting for players with the MT-32 keeps hearing it — and
        // nothing is in the serial port.
        static void RewireCables()
        {
            bool host = _transport != null && !Lockstep.Active && Role == Roles.Host;
            bool guest = _transport != null && !Lockstep.Active && Role == Roles.Guest && GuestState == GuestStates.Connected;

            if (host && HostKind == HostKinds.MidiCable)
                MidiCable.PlugHost(_transport, _ring);
            else if (guest && HostGame == GameKinds.MidiCable)
                MidiCable.PlugGuest(_transport, HostPeer);
            else
                MidiCable.Unplug();

            if (host && HostKind == HostKinds.NullModem && _ring.Count > 0)
                SerialCable.Plug(_transport, _ring[0]);
            else if (guest && HostGame == GameKinds.NullModem)
                SerialCable.Plug(_transport, HostPeer);
            else
                SerialCable.Unplug();
        }

        // ==================== The shared machine ====================

        /// <summary>Host: the connected guests, in the order they were accepted, as the shared
        /// game sees them.</summary>
        static List<Lockstep.Guest> SharingGuests()
        {
            var peers = Peers;
            return _ring.Select(p => new Lockstep.Guest(p, _names.GetValueOrDefault(p, "?"), _versions.GetValueOrDefault(p, ""),
                                                        peers.FirstOrDefault(i => i.Id == p)?.RttMs ?? -1)).ToList();
        }

        /// <summary>Host: shares the machine with every connected guest. Null, or why not.</summary>
        public static string StartSharing()
        {
            if (Role != Roles.Host || _transport == null)
                return "Host a game first.";

            if (HostKind is HostKinds.MidiCable or HostKinds.NullModem)
                return "This is a cable game: every player runs their own ST.";

            return Lockstep.Start(_transport, SharingGuests());
        }

        /// <summary>Host: stops playing together; every computer keeps its machine.</summary>
        public static void StopSharing() => Lockstep.Stop("the host stopped playing together");

        /// <summary>How the ST's MIDI sockets are wired, for the Online play window; null when
        /// there is nothing to say.</summary>
        public static string MidiLinkText
        {
            get
            {
                if (Lockstep.Active)
                    return null;

                if (Role == Roles.Host && HostKind != HostKinds.MidiCable)
                    return null;

                if (Role == Roles.Host)
                    return _ring.Count == 0
                        ? null
                        : "you → " + string.Join(" → ", _ring.Select(p => _names.GetValueOrDefault(p, "?"))) + " → back to you";

                if (Role == Roles.Guest && GuestState == GuestStates.Connected && HostGame == GameKinds.MidiCable)
                    return $"through {Remote}'s ring";

                return null;
            }
        }

        /// <summary>Who the ST's RS-232 port is linked to, for the Online play window; null when
        /// nothing is in it.</summary>
        public static string SerialLinkText
        {
            get
            {
                if (Lockstep.Active)
                    return null;

                if (Role == Roles.Host && HostKind == HostKinds.NullModem && _ring.Count > 0)
                    return $"you ↔ {_names.GetValueOrDefault(_ring[0], "?")}";

                if (Role == Roles.Guest && GuestState == GuestStates.Connected && HostGame == GameKinds.NullModem)
                    return $"you ↔ {Remote}";

                return null;
            }
        }

        // ==================== Over the Internet: codes ====================

        /// <summary>Host: the invitation code in force, as text, or null.</summary>
        public static string InvitationText => _invitation?.ToText();

        /// <summary>Guest: the answer code for the host, once STUN has answered, or null.</summary>
        public static string AnswerText => _answerText;

        /// <summary>Whether this ASE joined, or is joining, with an invitation code.</summary>
        public static bool JoinedWithCode => _joinedWith != null;

        /// <summary>What STUN made of this computer's NAT, once a code was made or used.</summary>
        public static NatKind Nat => _nat;

        /// <summary>The STUN servers asked, for whoever tells the user.</summary>
        public static string StunServers => string.Join(" and ", Stun.Servers.Select(s => s.Host));

        /// <summary>
        /// Host: makes an invitation code — asks the STUN servers for the public address of the
        /// game's socket, adds the addresses on the local network and IPv6, and a fresh secret.
        /// A new code replaces the old one, which stops working. Null, or what went wrong.
        /// </summary>
        public static async Task<string> CreateInvitation()
        {
            if (Role != Roles.Host || _transport == null)
                return "Host a game first.";

            var transport = _transport;
            var router = OpenRouterPort(transport.Port);
            var mapped = await AskStun();
            var mapping = await router;

            if (transport != _transport)
                return null;    // stopped meanwhile

            var own = NetTransport.LocalAddresses();
            _nat = Netplay.Nat.Classify(own, mapped);

            var candidates = new List<IPEndPoint>();

            // The port the router opened, which lets anybody in; the address the router shows
            // the Internet; the local ones (a player on the same network, or behind the same
            // router); and IPv6, which needs no NAT at all
            var publicV4 = mapped.FirstOrDefault(m => m.AddressFamily == AddressFamily.InterNetwork);
            var opened = UseRouterPort(mapping, publicV4);
            if (opened != null)
                candidates.Add(opened);
            if (publicV4 != null && !publicV4.Equals(opened))
                candidates.Add(publicV4);

            foreach (var a in own.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(publicV4?.Address)).Take(2))
                candidates.Add(new IPEndPoint(a, transport.Port));

            if (transport.HasIPv6)
                foreach (var a in own.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(2))
                    candidates.Add(new IPEndPoint(a, transport.PortV6));

            if (candidates.Count == 0)
                return "This computer has no network address to give out.";

            _invitation = InviteCode.Create(opened != null ? NatKind.PortOpen : _nat, candidates);
            transport.SetCode(_invitation.Secret);

            ColoredConsole.WriteLine(publicV4 != null
                ? $"Online play: invitation code made — public address {publicV4} ({Netplay.Nat.Describe(_nat)})"
                : "Online play: invitation code made without a public address: the STUN servers did not answer", ConfigOptions.DebugModes.Quiet);

            await PublishLink(transport, _invitation);

            StateChanged?.Invoke();
            return null;
        }

        /// <summary>
        /// Host: a guest's answer code. The host sends a few small packets to the addresses in
        /// it, which opens its own router to that guest's Hellos (hole punching). Null, or why not.
        /// </summary>
        public static string UseAnswer(string text)
        {
            if (Role != Roles.Host || _transport == null)
                return "Host a game first.";

            var answer = AnswerCode.Parse(text, out string error);
            if (answer == null)
                return error;

            if (_invitation == null || !answer.Tag.AsSpan().SequenceEqual(_invitation.Tag))
                return "That answer is for another invitation code, or an older one: ask the player to join with your current code.";

            _transport.Punch(answer.Candidates);
            ColoredConsole.WriteLine($"Online play: opening the way to {string.Join(", ", answer.Candidates)}", ConfigOptions.DebugModes.Quiet);
            return null;
        }

        /// <summary>
        /// Guest: joins with an invitation code. The Hellos go to every address in it, with the
        /// code's secret in the keys, for up to five minutes; meanwhile the STUN servers are asked
        /// for this side's public address, which makes the answer code (<see cref="AnswerText"/>)
        /// for a host whose router has to be opened from its side. Null, or why not.
        /// </summary>
        public static async Task<string> JoinWithCode(string text)
        {
            if (string.IsNullOrWhiteSpace(PlayerName))
                return NameMissing;

            // A link (or its 20-character code): the invitation is read from the website first
            if (WebCode.TryParse(text, out var link, out string error))
            {
                var web = WebAddress;
                if (web == null)
                    return "Invitation links go through a website, and this ASE is set to use none (NetplayWebUrl): ask the host for the code instead.";

                var read = await Mailbox(web).ReadOffer(link, CancellationToken.None);
                if (!read.Ok)
                    return read.Message;

                text = read.Text;
                ColoredConsole.WriteLine($"Online play: invitation read from {web.Host} (the host has ASE {read.HostVersion})", ConfigOptions.DebugModes.Quiet);
                if (read.HostVersion != Config.Version)
                    ColoredConsole.WriteLine($"Online play: [[yellow]]the host has ASE {read.HostVersion} and this is {Config.Version}[[/yellow]]: the cables work between versions that have them, playing together on one machine needs the same one",
                        ConfigOptions.DebugModes.Quiet);
            }
            else if (error != null)
            {
                return error;
            }

            var code = InviteCode.Parse(text, out error);
            if (code == null)
                return error;

            // Joining with a code needs the network open, not a search of the local one
            if (_transport == null && !StartSearching(GuestWays.Internet, out error))
                return error;

            if (Role != Roles.Guest || GuestState != GuestStates.Searching)
                return "Already connecting.";

            var transport = _transport;
            _joinedWith = code;
            _answerText = null;
            _guestLink = link;
            _linkReported = false;
            _answerPending = link != null;
            _pendingReport = null;
            AnswerThroughWeb = false;

            Remote = "the host";
            GuestState = GuestStates.Connecting;
            transport.SetName(PlayerName);
            transport.StopDiscovery();
            transport.Connect(code.Candidates, code);
            StateChanged?.Invoke();

            ColoredConsole.WriteLine($"Online play: joining with a code — trying {string.Join(", ", code.Candidates)}", ConfigOptions.DebugModes.Quiet);

            // This side's port open too lets a host on a phone's connection reach it: its
            // punches get in, and the Hello answering them gets out to the right port
            var router = OpenRouterPort(transport.Port);
            var mapped = await AskStun();
            var mapping = await router;

            if (transport != _transport || _joinedWith != code)
                return null;    // gave up, or connected and left, meanwhile

            var own = NetTransport.LocalAddresses();
            _nat = Netplay.Nat.Classify(own, mapped);

            var candidates = new List<IPEndPoint>();
            var publicV4 = mapped.FirstOrDefault(m => m.AddressFamily == AddressFamily.InterNetwork);
            var opened = UseRouterPort(mapping, publicV4);
            if (opened != null)
                candidates.Add(opened);
            if (publicV4 != null && !publicV4.Equals(opened))
                candidates.Add(publicV4);

            foreach (var a in own.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(publicV4?.Address)).Take(2))
                candidates.Add(new IPEndPoint(a, transport.Port));

            if (transport.HasIPv6)
                foreach (var a in own.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(2))
                    candidates.Add(new IPEndPoint(a, transport.PortV6));

            if (candidates.Count > 0)
                _answerText = new AnswerCode(code.Tag, candidates).ToText();

            // Joined with a link: the answer goes to the host through the website, which spares
            // the copying; the answer code stays on screen for when that fails
            if (link != null && _answerText != null && WebAddress is { } site)
            {
                var posted = await Mailbox(site).PostAnswer(link, _answerText, CancellationToken.None);
                if (transport != _transport || _joinedWith != code)
                    return null;

                AnswerThroughWeb = posted.Ok;
                ColoredConsole.WriteLine(posted.Ok
                    ? $"Online play: answer sent to the host through {site.Host}"
                    : $"Online play: [[yellow]]the answer could not go through {site.Host}[[/yellow]]: {posted.Message} Send the host the answer code instead.", ConfigOptions.DebugModes.Quiet);
            }

            _answerPending = false;
            if (_pendingReport is { } report)
            {
                _pendingReport = null;
                ReportLink(report.Connected, report.Address);
            }

            ColoredConsole.WriteLine(publicV4 != null
                ? $"Online play: answer code made — public address {publicV4} ({Netplay.Nat.Describe(_nat)}); the host's NAT: {Netplay.Nat.Describe(code.Nat)}"
                : "Online play: answer code made without a public address: the STUN servers did not answer", ConfigOptions.DebugModes.Quiet);

            if (_nat == NatKind.Symmetric && code.Nat == NatKind.Symmetric && opened == null)
                ColoredConsole.WriteLine("Online play: [[yellow]]both computers are behind a symmetric NAT[[/yellow]]: a direct connection is unlikely. " +
                    "IPv6, a port forwarded in the host's router, or a VPN such as Tailscale or ZeroTier are the ways round it.", ConfigOptions.DebugModes.Quiet);

            StateChanged?.Invoke();
            return null;
        }

        // ==================== Over the Internet: links ====================

        /// <summary>What became of the host's invitation link.</summary>
        public enum LinkStates { None, Making, Ready, Used, Expired, Failed }

        public static LinkStates Link { get; private set; }

        /// <summary>Host: the link to send (https://…/j/…#…) and the same as a code to type.</summary>
        public static string LinkText { get; private set; }
        public static string LinkCode { get; private set; }

        /// <summary>Host: why there is no link, when the website could not make one.</summary>
        public static string LinkProblem { get; private set; }

        /// <summary>Guest: whether the answer reached the host through the website.</summary>
        public static bool AnswerThroughWeb { get; private set; }

        /// <summary>The website links go through, or null when this ASE is set to use none.</summary>
        public static Uri WebAddress =>
            Uri.TryCreate(ConfigOptions.RunninConfig.NetplayWebUrl?.Trim(), UriKind.Absolute, out var web)
            && (web.Scheme == Uri.UriSchemeHttps || web.Scheme == Uri.UriSchemeHttp) ? web : null;

        static WebMailbox Mailbox(Uri web)
        {
            if (_mailbox == null || _mailbox.Web != web)
            {
                _mailbox?.Dispose();
                _mailbox = new WebMailbox(web, Config.Version);
            }
            return _mailbox;
        }

        // Host: leaves the invitation on the website under a fresh link, and waits there for the
        // answer. Any failure leaves the manual code, which works without the website.
        static async Task PublishLink(NetTransport transport, InviteCode invitation)
        {
            CancelLink();

            var web = WebAddress;
            if (web == null)
                return;

            Link = LinkStates.Making;
            StateChanged?.Invoke();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var link = WebCode.Create();
                var reply = await Mailbox(web).PublishOffer(link, invitation.ToText(), CancellationToken.None);

                if (transport != _transport || invitation != _invitation)
                    return;     // stopped, or another code made meanwhile

                if (reply.Status == WebStatus.Taken)
                    continue;

                if (!reply.Ok)
                {
                    Link = LinkStates.Failed;
                    LinkProblem = reply.Message;
                    ColoredConsole.WriteLine($"Online play: [[yellow]]no invitation link[[/yellow]]: {reply.Message}", ConfigOptions.DebugModes.Quiet);
                    return;
                }

                _hostLink = link;
                Link = LinkStates.Ready;
                LinkText = link.Link(web);
                LinkCode = link.ToText();
                ColoredConsole.WriteLine($"Online play: invitation link made — {web.Host} keeps it, encrypted, for ten minutes and one player", ConfigOptions.DebugModes.Quiet);

                var wait = _linkWait = new CancellationTokenSource();
                _ = WaitForAnswer(Mailbox(web), link, transport, wait.Token);
                return;
            }

            Link = LinkStates.Failed;
            LinkProblem = "The website could not file the invitation.";
        }

        // Host: asks the website for the guest's answer, one long wait after another, until it
        // comes, the link expires or the session moves on. The website sets the pace.
        static async Task WaitForAnswer(WebMailbox mailbox, WebCode link, NetTransport transport, CancellationToken cancel)
        {
            int failures = 0;

            while (true)
            {
                WebReply reply;
                try
                {
                    reply = await mailbox.WaitAnswer(link, cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (cancel.IsCancellationRequested || transport != _transport || _hostLink != link)
                    return;

                TimeSpan pause;
                switch (reply.Status)
                {
                    case WebStatus.Ok:
                        Link = LinkStates.Used;
                        string error = UseAnswer(reply.Text);
                        ColoredConsole.WriteLine(error == null
                            ? "Online play: a player answered the invitation link: opening the way to them"
                            : $"Online play: [[yellow]]the answer through the link could not be used[[/yellow]]: {error}", ConfigOptions.DebugModes.Quiet);
                        StateChanged?.Invoke();
                        return;

                    case WebStatus.Pending:
                        failures = 0;
                        pause = reply.RetryAfter;
                        break;

                    case WebStatus.Expired:
                        Link = LinkStates.Expired;
                        ColoredConsole.WriteLine("Online play: the invitation link expired unused (the code still works)", ConfigOptions.DebugModes.Quiet);
                        StateChanged?.Invoke();
                        return;

                    case WebStatus.Refused or WebStatus.Invalid:
                        Link = LinkStates.Failed;
                        LinkProblem = reply.Message;
                        ColoredConsole.WriteLine($"Online play: [[yellow]]the invitation link stopped working[[/yellow]]: {reply.Message}", ConfigOptions.DebugModes.Quiet);
                        StateChanged?.Invoke();
                        return;

                    default:
                        // Unreachable for a moment: back off, as the website asks or doubling up to 30 s
                        failures++;
                        pause = reply.RetryAfter > TimeSpan.Zero ? reply.RetryAfter : TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5)));
                        break;
                }

                try
                {
                    if (pause > TimeSpan.Zero)
                        await Task.Delay(pause, cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        // Host: the link in force is done with — stop waiting, and take it back from the website
        // if nobody answered (the website would drop it within ten minutes anyway)
        static void CancelLink()
        {
            _linkWait?.Cancel();
            _linkWait = null;

            if (_hostLink != null && Link == LinkStates.Ready && _mailbox != null)
            {
                var mailbox = _mailbox;
                var link = _hostLink;
                _ = Task.Run(() => mailbox.WithdrawOffer(link, CancellationToken.None));
            }

            _hostLink = null;
            Link = LinkStates.None;
            LinkText = LinkCode = LinkProblem = null;
        }

        // Guest: tells the website, once, whether joining with its link worked and by which way,
        // for its statistics — the outcome, the way, and the two NAT kinds, nothing else
        static void ReportLink(bool connected, IPEndPoint address)
        {
            var link = _guestLink;
            var code = _joinedWith;
            if (link == null || code == null || _linkReported || _mailbox == null)
                return;

            if (_answerPending)
            {
                _pendingReport = (connected, address);
                return;
            }

            // The website only takes a report from the guest that answered through it
            if (!AnswerThroughWeb)
                return;

            _linkReported = true;
            string path = connected ? WayIn(address, code) : null;
            var mailbox = _mailbox;
            var guestNat = _nat;
            _ = Task.Run(() => mailbox.PostResult(link, connected, path, code.Nat, guestNat, CancellationToken.None));
        }

        // Which of the ways in a connection took: the port the host's router opened, an address
        // in the code, one the host's punches came from, the local network or IPv6
        static string WayIn(IPEndPoint address, InviteCode code)
        {
            var a = address.Address.IsIPv4MappedToIPv6 ? address.Address.MapToIPv4() : address.Address;
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return "ipv6";
            if (PortMapping.IsPrivateOrShared(a))
                return "lan";

            int index = code.Candidates.ToList().FindIndex(c => (c.Address.IsIPv4MappedToIPv6 ? c.Address.MapToIPv4() : c.Address).Equals(a) && c.Port == address.Port);
            return index < 0 ? "punch" : code.Nat == NatKind.PortOpen && index == 0 ? "router" : "direct";
        }

        /// <summary>What became of asking the router to open the game's port.</summary>
        public enum RouterStates { NotAsked, Opened, NotOpened, BehindAnotherNat }

        /// <summary>The router's answer for the code in force; with <see cref="RouterMapping"/>
        /// for the port when it opened one.</summary>
        public static RouterStates Router { get; private set; }

        public static PortMapping RouterMapping { get; private set; }

        // Asks the router to forward the game's port, once per port and session: UPnP, NAT-PMP
        // or PCP, whichever it speaks (NETPLAY.md §7.3). Never fails: a router can answer
        // anything, and the code is made with or without the port.
        static Task<PortMapping> OpenRouterPort(int port)
        {
            if (_routerTask != null && _routerPort == port)
                return _routerTask;

            CloseRouterPort();
            _routerPort = port;
            _routerTask = Ask(port);
            return _routerTask;

            static async Task<PortMapping> Ask(int port)
            {
                try
                {
                    return await PortMapping.Open(port, message => Dispatcher.UIThread.Post(() =>
                        ColoredConsole.WriteLine($"Online play: {message}", ConfigOptions.DebugModes.Quiet))).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => ColoredConsole.WriteLine($"Online play: asking the router to open the port failed: {ex.Message}", ConfigOptions.DebugModes.Quiet));
                    return null;
                }
            }
        }

        // Removes the router's mapping, as soon as the request that makes it has finished
        static void CloseRouterPort()
        {
            var task = _routerTask;
            _routerTask = null;
            Router = RouterStates.NotAsked;
            RouterMapping = null;

            if (task != null)
                _routerClosing = Task.Run(async () =>
                {
                    var mapping = await task.ConfigureAwait(false);
                    if (mapping != null)
                        await mapping.Release().ConfigureAwait(false);
                });
        }

        // What the router's mapping is worth against what STUN saw: the address to put in a
        // code, or null. With another NAT beyond the router (CG-NAT, or a second router) the
        // port leads nowhere; it stays until the session ends, harmless, so a new code does not
        // ask again.
        static IPEndPoint UseRouterPort(PortMapping mapping, IPEndPoint publicV4)
        {
            RouterMapping = mapping;

            if (mapping == null)
            {
                Router = RouterStates.NotOpened;
                return null;
            }

            if (PortMapping.Reach(mapping.ExternalAddress, publicV4?.Address) == MappingReach.BehindAnotherNat)
            {
                Router = RouterStates.BehindAnotherNat;
                ColoredConsole.WriteLine($"Online play: [[yellow]]the router's outside address ({mapping.ExternalAddress}) is not the one the Internet sees " +
                    $"({publicV4?.Address.ToString() ?? "unknown"})[[/yellow]]: there is another NAT beyond it, the operator's (CG-NAT) or a second router's, " +
                    "and the port it opened leads nowhere.", ConfigOptions.DebugModes.Quiet);
                return null;
            }

            Router = RouterStates.Opened;
            var address = mapping.ExternalAddress ?? publicV4?.Address;
            return address == null ? null : new IPEndPoint(address, mapping.ExternalPort);
        }

        // Asks the public STUN servers (by name, IPv4) what this computer looks like from the
        // Internet, from the session's own sockets, and keeps the mapping alive afterwards.
        // Whatever answered; nothing when the servers cannot be reached.
        static async Task<IReadOnlyList<IPEndPoint>> AskStun()
        {
            var servers = new List<IPEndPoint>();
            var names = new Dictionary<IPEndPoint, string>();

            foreach (var (host, port) in Stun.Servers)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
                    var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (v4 != null)
                    {
                        servers.Add(new IPEndPoint(v4, port));
                        names[servers[^1]] = host;
                    }
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
                {
                    // That server cannot be asked; the other may still answer
                }
            }

            var transport = _transport;
            if (servers.Count == 0 || transport == null)
                return [];

            var waiter = new TaskCompletionSource<StunResult>();
            _stunWaiter?.TrySetResult(new StunResult([]));
            _stunWaiter = waiter;

            transport.QueryStun(servers);
            var result = await waiter.Task;

            if (transport == _transport)
                transport.KeepMappingAlive(servers[0]);

            // Every answer, not only the verdict: on a symmetric NAT the ports show how the
            // router hands them out, which is what decides whether they can be predicted
            foreach (var (server, mapped) in result.Answers)
                ColoredConsole.WriteLine($"Online play: {names.GetValueOrDefault(server, server.ToString())} sees this computer as {mapped}", ConfigOptions.DebugModes.Quiet);

            return result.Answers.Select(a => a.Mapped).ToList();
        }

        // ==================== Status ====================

        /// <summary>One line for the status bar, or null when there is no session.</summary>
        public static string StatusText
        {
            get
            {
                if (_transport == null)
                    return null;

                if (Lockstep.StatusText is { } shared)
                    return shared;

                var peers = Peers;

                if (Role == Roles.Host)
                {
                    var connected = peers.Where(p => p.State == PeerStates.Connected).ToList();
                    int asking = peers.Count(p => p.State == PeerStates.AwaitingDecision);

                    string text = connected.Count switch
                    {
                        0 => $"Hosting on port {Port}",
                        1 => $"Hosting · {connected[0].Name} {Rtt(connected[0])}",
                        _ => $"Hosting · {connected.Count} players",
                    };

                    return asking > 0 ? $"{text} · {asking} asking to join" : text;
                }

                return GuestState switch
                {
                    GuestStates.Connecting => $"Connecting to {Remote}…",
                    GuestStates.WaitingForAccept => $"Waiting for {Remote} to accept…",
                    GuestStates.Connected => $"Online · {Remote} {Rtt(peers.FirstOrDefault())}",
                    _ => null,      // just looking: not worth a place in the status bar
                };
            }
        }

        public static string Rtt(PeerInfo peer) =>
            peer == null || peer.RttMs < 0 ? "" : peer.RttMs < 1 ? "<1 ms" : $"{peer.RttMs:F0} ms";

        // ==================== Addresses ====================

        /// <summary>
        /// Splits what the user typed into a host and a port: "1.2.3.4", "1.2.3.4:5000",
        /// "name", "name:5000", a bare IPv6 address, or "[IPv6]:5000".
        /// </summary>
        public static bool TryParseAddress(string text, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            text = (text ?? "").Trim();

            if (text.Length == 0)
                return false;

            string portText = null;

            if (text.StartsWith('['))
            {
                int close = text.IndexOf(']');
                if (close < 0)
                    return false;

                host = text[1..close];
                string rest = text[(close + 1)..];
                if (rest.Length > 0)
                {
                    if (!rest.StartsWith(':'))
                        return false;
                    portText = rest[1..];
                }
            }
            else if (text.Count(c => c == ':') == 1)
            {
                int colon = text.IndexOf(':');
                host = text[..colon];
                portText = text[(colon + 1)..];
            }
            else
            {
                host = text;     // a name, an IPv4 address, or a bare IPv6 address
            }

            if (portText != null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
                return false;

            return host.Length > 0 && !host.Any(char.IsWhiteSpace);
        }
    }
}
