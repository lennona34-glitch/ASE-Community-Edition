/*
 *
 * The network side of online play (NETPLAY.md §7, protocol in §7.7): the sockets, the thread
 * that owns them, finding games on the local network, the handshake, and the connections.
 *
 * One UDP socket per address family (IPv4 and, when the system has it, IPv6), on the same
 * port number: the host's configured port, or an ephemeral one for a guest. All protocol state
 * lives on one thread of its own and needs no locks. Other threads talk to it through a
 * command queue and wake it with a one-byte datagram to a loopback socket (the self-pipe
 * trick), so a send leaves at once instead of at the next poll; what the thread has to say
 * comes back through an event queue the caller drains when it suits it. Nothing the network
 * does throws into the caller's thread, and nothing here touches the emulation.
 *
 * Network only on demand (NETPLAY.md §1.2): a transport exists only between the user starting
 * to host or to look for a game and the end of it, and Dispose closes everything.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ASE.Netplay
{
    // ==================== What the transport reports ====================

    public abstract record NetEvent;

    /// <summary>A host on the local network answered the search.</summary>
    public sealed record HostFound(IPEndPoint Address, string Name, string AseVersion, string Game,
                                   int Players, int MaxPlayers, bool Accepting, bool Compatible) : NetEvent;

    /// <summary>Guest: the host answered and the keys are agreed; now it has to accept.</summary>
    public sealed record JoinPending(uint Peer, IPEndPoint Address) : NetEvent;

    /// <summary>Host: a guest asks to join and the user has to answer (Accept / Reject).
    /// <paramref name="WithCode"/>: it connected with the invitation code, which proves it has it.</summary>
    public sealed record JoinRequest(uint Peer, string Name, string AseVersion, IPEndPoint Address, bool WithCode = false) : NetEvent;

    /// <summary>A connection is up: on the host, a guest it accepted; on a guest, the host
    /// accepted it.</summary>
    public sealed record PeerConnected(uint Peer, string Name, string AseVersion, string Game, IPEndPoint Address) : NetEvent;

    /// <summary>A session message (type ≥ <see cref="NetTransport.FirstSessionMessage"/>) on the
    /// reliable channel, whole and in order.</summary>
    public sealed record MessageReceived(uint Peer, byte[] Data) : NetEvent;

    /// <summary>A datagram on the unreliable channel.</summary>
    public sealed record DatagramReceived(uint Peer, byte Channel, byte[] Data) : NetEvent;

    /// <summary>A connection is gone: closed by either side, declined, or silent too long.</summary>
    public sealed record PeerLeft(uint Peer, string Name, string Reason, bool WasConnected) : NetEvent;

    /// <summary>Guest: the connection could not be made (no answer, refused, declined).</summary>
    /// <summary>A guest's attempt ended without a connection; <paramref name="NoAnswer"/> when the
    /// host never answered (rather than turned it away).</summary>
    public sealed record ConnectFailed(string Reason, bool NoAnswer = false) : NetEvent;

    /// <summary>What the STUN servers asked with <see cref="NetTransport.QueryStun"/> saw: for
    /// each one that answered, the address this computer's packets came from.</summary>
    public sealed record StunResult(IReadOnlyList<(IPEndPoint Server, IPEndPoint Mapped)> Answers) : NetEvent;

    /// <summary>Something worth a line in the log.</summary>
    /// <summary>Something worth a line in the console: an error, a notable step (shown in
    /// ordinary use), or a detail (shown with the debug level raised).</summary>
    public sealed record NetLog(string Text, bool Error = false, bool Notable = false) : NetEvent;

    public enum PeerStates { Joining, AwaitingDecision, Connected }

    /// <summary>A connection as the UI shows it; refreshed a few times a second.</summary>
    public sealed record PeerInfo(uint Id, string Name, string AseVersion, IPEndPoint Address, PeerStates State,
                                  double RttMs, long Retransmissions, long BytesIn, long BytesOut, bool WithCode = false);

    // ==================== The transport ====================

    public sealed class NetTransport : IDisposable
    {
        public const int DefaultPort = 19850;
        public const byte FirstSessionMessage = NetWire.FirstSessionMessage;

        const long HelloInterval = 500;
        const long ConnectTimeout = 10_000;

        // With an invitation code the host may first have to paste the guest's answer and punch
        // its router open, and the answer goes from one computer to the other by hand: five
        // minutes, with a Hello every two seconds after the first twenty (the Hellos keep this
        // side's NAT mapping alive meanwhile)
        const long CodeConnectTimeout = 300_000;
        const long CodeHelloSlowAfter = 20_000;
        const long CodeHelloSlowInterval = 2000;

        const long StunTimeout = 3000;
        const long StunFirstRetry = 250;
        const int StunTries = 4;
        const long StunKeepAliveInterval = 20_000;      // NAT mappings for UDP last about 30 s
        const long PunchInterval = 200;
        const long JoinTimeout = 10_000;        // host: keys agreed but no JOIN
        const long SilenceTimeout = 10_000;
        const long QueryInterval = 1000;
        const long PeersRefresh = 250;
        const long NetworksRefresh = 10_000;
        const int SocketBuffer = 1 << 20;

        public bool IsHost { get; }

        /// <summary>The port the sockets are bound to (the host's port, or a guest's ephemeral one).</summary>
        public int Port { get; }

        /// <summary>The IPv6 socket's port: the host's port, or another ephemeral one on a guest
        /// (0 without IPv6).</summary>
        public int PortV6 { get; }

        public bool HasIPv6 => _v6 != null;

        readonly Socket _v4;
        readonly Socket _v6;
        readonly Socket _wake;
        int _wakePending;

        readonly Thread _thread;
        volatile bool _stop;

        readonly ConcurrentQueue<Action> _commands = new();
        readonly ConcurrentQueue<NetEvent> _events = new();

        readonly Dictionary<uint, NetPeer> _peers = new();
        readonly NetPeer.Received _received = new();

        readonly byte[] _rx = new byte[2048];
        readonly byte[] _plain = new byte[2048];
        readonly byte[] _tx = new byte[2048];

        volatile PeerInfo[] _peerInfo = [];
        long _nextPeersRefresh;

        // Who this side is
        string _name;
        readonly string _aseVersion;

        // Host
        readonly CookieJar _cookies;
        string _game = "";
        int _maxGuests;
        bool _accepting = true;

        // Host: the invitation code in force (NetCodes), whose secret a Hello made with it shares
        byte[] _codeSecret, _codeTag;

        // STUN: the servers being asked, and the one that keeps the NAT mapping alive meanwhile
        sealed class StunQuery
        {
            public IPEndPoint Server;
            public byte[] Id;
            public int Tries;
            public long NextSend;
            public IPEndPoint Mapped;
        }
        List<StunQuery> _stun;
        long _stunDeadline;
        IPEndPoint _stunKeepAlive;
        long _nextKeepAlive;

        // Host: the guest addresses an answer code gave, punched for a while
        IPEndPoint[] _punchTargets;
        long _punchUntil, _nextPunch;

        // Guest: the connection being made
        sealed class Attempt
        {
            public IPEndPoint[] Candidates;
            public byte[] CodeSecret, CodeTag;
            public long Timeout;
            public byte[][] Cookies;
            public KeyExchange Keys;
            public byte[] Nonce;
            public uint ConnectionId;
            public long StartedAt;
            public long NextHello;
        }
        Attempt _attempt;

        // Guest: the search for games on the local network
        int _discoveryPort;
        long _nextQuery;
        readonly Queue<ulong> _queryNonces = new();

        List<(byte[] Network, byte[] Mask)> _localNetworks = new();
        long _networksAt = long.MinValue;

        NetTransport(bool isHost, Socket v4, Socket v6, Socket wake, string name, string aseVersion, int maxGuests)
        {
            IsHost = isHost;
            _v4 = v4;
            _v6 = v6;
            _wake = wake;
            _name = name ?? "";
            _aseVersion = aseVersion ?? "";
            _maxGuests = maxGuests;
            Port = ((IPEndPoint)v4.LocalEndPoint).Port;
            PortV6 = v6 != null ? ((IPEndPoint)v6.LocalEndPoint).Port : 0;

            if (isHost)
                _cookies = new CookieJar();

            _thread = new Thread(Run) { IsBackground = true, Name = "ASE netplay" };
            _thread.Start();
        }

        // ==================== Starting ====================

        /// <summary>
        /// Starts hosting on <paramref name="port"/> (IPv4, plus IPv6 on the same port when the
        /// system has it). Null with the reason when the port cannot be taken.
        /// </summary>
        public static NetTransport StartHost(int port, string name, string aseVersion, int maxGuests, out string error)
        {
            error = null;
            Socket v4 = null, v6 = null, wake = null;

            try
            {
                v4 = OpenSocket(AddressFamily.InterNetwork, port, exclusive: true);
            }
            catch (SocketException ex)
            {
                error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"UDP port {port} is already in use (another ASE hosting a game, or another program). Choose another port."
                    : $"UDP port {port} could not be opened: {ex.Message}";
                return null;
            }

            // IPv6 is a bonus: a system without it, or with the port taken there, still hosts on IPv4
            try { v6 = Socket.OSSupportsIPv6 ? OpenSocket(AddressFamily.InterNetworkV6, port, exclusive: true) : null; }
            catch (SocketException) { v6 = null; }

            wake = OpenWakeSocket();
            return new NetTransport(true, v4, v6, wake, name, aseVersion, maxGuests);
        }

        /// <summary>
        /// Opens a guest's sockets: on <paramref name="port"/> when it is free, otherwise (or with
        /// 0) on ephemeral ports. A guest over the Internet asks for the game's port, so a port
        /// forwarded by hand in its router serves it as a guest too (NETPLAY.md §7.7). Null with
        /// the reason on failure.
        /// </summary>
        public static NetTransport StartGuest(string name, string aseVersion, out string error, int port = 0)
        {
            error = null;
            Socket v4, v6 = null;

            try
            {
                v4 = OpenGuestSocket(AddressFamily.InterNetwork, port);
                v4.EnableBroadcast = true;
            }
            catch (SocketException ex)
            {
                error = $"The network could not be opened: {ex.Message}";
                return null;
            }

            try { v6 = Socket.OSSupportsIPv6 ? OpenGuestSocket(AddressFamily.InterNetworkV6, port) : null; }
            catch (SocketException) { v6 = null; }

            return new NetTransport(false, v4, v6, OpenWakeSocket(), name, aseVersion, 0);
        }

        // The port asked for, or any when it is taken (an ASE hosting on this computer, say)
        static Socket OpenGuestSocket(AddressFamily family, int port)
        {
            if (port != 0)
            {
                try { return OpenSocket(family, port, exclusive: true); }
                catch (SocketException) { }
            }

            return OpenSocket(family, 0, exclusive: false);
        }

        static Socket OpenSocket(AddressFamily family, int port, bool exclusive)
        {
            var s = new Socket(family, SocketType.Dgram, ProtocolType.Udp);

            try
            {
                if (family == AddressFamily.InterNetworkV6)
                    s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);

                // Nobody else may bind the host's port on top of it and read its traffic
                if (exclusive && OperatingSystem.IsWindows())
                    s.ExclusiveAddressUse = true;

                // Windows turns an ICMP "port unreachable" into a failure of the next receive
                if (OperatingSystem.IsWindows())
                {
                    const int SIO_UDP_CONNRESET = -1744830452;
                    s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
                }

                s.ReceiveBufferSize = SocketBuffer;
                s.SendBufferSize = SocketBuffer;
                s.Blocking = false;

                s.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, port));
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        // The self-pipe. Without it (no loopback at all) the thread polls a little faster instead.
        static Socket OpenWakeSocket()
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                s.Blocking = false;
                return s;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        // ==================== Commands (any thread) ====================

        /// <summary>The next event, if any. Any thread; meant for one consumer.</summary>
        public bool TryGetEvent(out NetEvent e) => _events.TryDequeue(out e);

        /// <summary>The connections as of a fraction of a second ago.</summary>
        public IReadOnlyList<PeerInfo> Peers => _peerInfo;

        /// <summary>
        /// Sees every session message of a connected peer on the network thread, before it is
        /// queued as an event; true when it took the message, which then never reaches the
        /// queue. For traffic that cannot wait for whoever drains the events (the MIDI cable's
        /// bytes, which the UI would otherwise pick up a tenth of a second late). It must be
        /// quick, must not block, and may call this transport's own commands; an exception is
        /// reported and the message dropped.
        /// </summary>
        public Func<uint, byte[], bool> MessageHook { get; set; }

        /// <summary>
        /// The same for datagrams: sees each one of a connected peer on the network thread, with
        /// its channel, and true means it was taken. For the lockstep's input, which a frame is
        /// waiting for. Same rules as <see cref="MessageHook"/>.
        /// </summary>
        public Func<uint, byte, byte[], bool> DatagramHook { get; set; }

        /// <summary>The name the other side sees: a guest's goes out with JOIN, a host's with
        /// every Announce and ACCEPT.</summary>
        public void SetName(string name) => Post(() => _name = name ?? "");

        /// <summary>Host: the game announced to the network and sent to whoever joins.</summary>
        public void SetGame(string title) => Post(() => _game = title ?? "");

        /// <summary>Host: whether new guests are let in (a game under way turns it off).</summary>
        public void SetAccepting(bool accepting) => Post(() => _accepting = accepting);

        /// <summary>Guest: looks for games on the local network, asking every second on
        /// <paramref name="port"/> until <see cref="StopDiscovery"/>.</summary>
        public void StartDiscovery(int port) => Post(() =>
        {
            _discoveryPort = port;
            _nextQuery = 0;
        });

        public void StopDiscovery() => Post(() => _discoveryPort = 0);

        /// <summary>
        /// Guest: connects to a host. Every candidate (the addresses a name resolved to, or an
        /// invitation code's) gets the same Hello; the first to answer wins. With
        /// <paramref name="code"/> the keys include its secret, the attempt lasts five minutes
        /// (the host may have to punch its router open first), and an answer is taken from
        /// whatever address it comes, since a NAT may give the host another port for us.
        /// </summary>
        public void Connect(IReadOnlyList<IPEndPoint> candidates, InviteCode code = null) => Post(() => BeginAttempt(candidates, code));

        /// <summary>Host: the invitation code in force, or null for none. A Hello made with an
        /// older code is refused; one made without a code goes on as on a local network.</summary>
        public void SetCode(byte[] secret) => Post(() =>
        {
            _codeSecret = secret?.ToArray();
            _codeTag = secret == null ? null : InviteCode.TagOf(secret);
        });

        /// <summary>
        /// Asks STUN servers which address this computer's packets come from, from the sockets
        /// the game uses (the NAT mapping is per socket). A <see cref="StunResult"/> follows within
        /// three seconds, with whichever answered.
        /// </summary>
        public void QueryStun(IReadOnlyList<IPEndPoint> servers) => Post(() =>
        {
            long now = Now();
            _stun = servers.Select(s => new StunQuery { Server = s, Id = RandomNumberGenerator.GetBytes(Stun.TransactionIdSize), NextSend = now }).ToList();
            _stunDeadline = now + StunTimeout;
        });

        /// <summary>Keeps the NAT mapping alive while a code is out, with a STUN request to
        /// <paramref name="server"/> every 20 seconds; null stops it.</summary>
        public void KeepMappingAlive(IPEndPoint server) => Post(() =>
        {
            _stunKeepAlive = server;
            _nextKeepAlive = Now() + StunKeepAliveInterval;
        });

        /// <summary>Host: sends tiny Punch packets to <paramref name="targets"/> (a guest's answer
        /// code) five times a second for a while, which opens this side's router to that guest.</summary>
        public void Punch(IReadOnlyList<IPEndPoint> targets, long durationMs = 30_000) => Post(() =>
        {
            _punchTargets = targets.Where(t => t.AddressFamily == AddressFamily.InterNetwork || _v6 != null).ToArray();
            _punchUntil = Now() + durationMs;
            _nextPunch = 0;
        });

        /// <summary>Host: lets in a guest that asked to join.</summary>
        public void Accept(uint peer) => Post(() =>
        {
            if (_peers.TryGetValue(peer, out var p) && p.State == NetPeer.States.AwaitingDecision)
                AcceptPeer(p);
        });

        /// <summary>Host: turns a guest away.</summary>
        public void Reject(uint peer, string reason = "") => Post(() =>
        {
            if (_peers.TryGetValue(peer, out var p) && p.State != NetPeer.States.Closed)
            {
                p.SendMessage(Message(NetWire.MsgReject, reason));
                Flush(p);
                p.Close(NetWire.CloseRejected, reason);
            }
        });

        /// <summary>Sends a session message on the reliable channel. Its first byte is its type,
        /// at least <see cref="FirstSessionMessage"/>.</summary>
        public void SendMessage(uint peer, byte[] message)
        {
            if (message == null || message.Length == 0 || message[0] < NetWire.FirstSessionMessage || message.Length > NetWire.MaxMessage)
                throw new ArgumentException("Not a session message.", nameof(message));

            Post(() =>
            {
                if (_peers.TryGetValue(peer, out var p) && p.State == NetPeer.States.Connected)
                    p.SendMessage(message);
            });
        }

        /// <summary>Sends a datagram on the unreliable channel (at most 1100 bytes).</summary>
        public void SendDatagram(uint peer, byte channel, byte[] data) => Post(() =>
        {
            if (_peers.TryGetValue(peer, out var p) && p.State == NetPeer.States.Connected)
                p.SendDatagram(channel, data);
        });

        /// <summary>Closes one connection (or, on a guest, the attempt to make one).</summary>
        public void Disconnect(uint peer) => Post(() =>
        {
            if (_peers.TryGetValue(peer, out var p))
                CloseLocally(p, NetWire.CloseNormal, "disconnected");
        });

        /// <summary>
        /// Ends everything: every connection gets its CLOSE, the thread stops and the sockets
        /// close. Waits a moment for the CLOSE frames to go out, never long.
        /// </summary>
        public void Dispose()
        {
            if (_stop)
                return;

            Post(() =>
            {
                foreach (var p in _peers.Values)
                    CloseLocally(p, IsHost ? NetWire.CloseHostEnded : NetWire.CloseNormal, IsHost ? "the host ended the game" : "disconnected");

                foreach (var p in _peers.Values)
                    Flush(p);

                _stop = true;
            });

            if (Thread.CurrentThread != _thread)
                _thread.Join(500);

            _stop = true;

            _v4.Dispose();
            _v6?.Dispose();
            _wake?.Dispose();
        }

        void Post(Action command)
        {
            if (_stop)
                return;

            _commands.Enqueue(command);

            if (_wake != null && Interlocked.Exchange(ref _wakePending, 1) == 0)
            {
                try { _wake.SendTo([1], _wake.LocalEndPoint); }
                catch (Exception) { /* the poll timeout picks the command up anyway */ }
            }
        }

        // ==================== The network thread ====================

        void Run()
        {
            var readable = new List<Socket>(3);
            long lastError = long.MinValue;

            while (!_stop)
            {
                try
                {
                    readable.Clear();
                    readable.Add(_v4);
                    if (_v6 != null) readable.Add(_v6);
                    if (_wake != null) readable.Add(_wake);

                    // Short while anything has timers running; the wake socket covers commands
                    bool busy = _peers.Count > 0 || _attempt != null || _discoveryPort != 0;
                    int timeoutUs = _wake == null ? 5_000 : busy ? 10_000 : 100_000;

                    Socket.Select(readable, null, null, timeoutUs);

                    if (_stop)
                        break;

                    foreach (var s in readable)
                    {
                        if (s == _wake)
                            DrainWake();
                        else
                            Drain(s);
                    }

                    RunCommands();
                    Tick(Now());
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Contained here, whatever it is (NETPLAY.md §9.4); reported at most once a second
                    long now = Now();
                    if (now - lastError > 1000)
                    {
                        _events.Enqueue(new NetLog($"Network error: {ex.Message}", Error: true));
                        lastError = now;
                    }
                }
            }

            foreach (var p in _peers.Values)
                p.Dispose();
            _peers.Clear();
            _attempt?.Keys.Dispose();
            _attempt = null;
        }

        void DrainWake()
        {
            var any = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            try
            {
                for (int i = 0; i < 64 && _wake.Poll(0, SelectMode.SelectRead); i++)
                    _wake.ReceiveFrom(_rx, ref any);
            }
            catch (SocketException) { }

            Volatile.Write(ref _wakePending, 0);
        }

        void RunCommands()
        {
            while (_commands.TryDequeue(out var command))
                command();
        }

        void Drain(Socket s)
        {
            var any = (EndPoint)new IPEndPoint(s.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

            // A bounded batch, so a flood cannot keep the thread from its timers. Poll and not
            // Available: an empty datagram leaves Available at zero while Select keeps reporting
            // the socket readable, and the thread would spin on it.
            for (int i = 0; i < 256; i++)
            {
                int n;
                try
                {
                    if (!s.Poll(0, SelectMode.SelectRead))
                        break;
                    n = s.ReceiveFrom(_rx, ref any);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    if (ex.SocketErrorCode == SocketError.WouldBlock)
                        break;
                    continue;
                }

                Dispatch(_rx.AsSpan(0, n), (IPEndPoint)any, s);
            }
        }

        void Dispatch(ReadOnlySpan<byte> data, IPEndPoint from, Socket socket)
        {
            long now = Now();

            if (data.Length >= NetWire.ProtectedHeader + NetWire.TagSize && data[0] == NetWire.Protected)
            {
                uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[1..]);
                if (_peers.TryGetValue(id, out var peer) && peer.State != NetPeer.States.Closed)
                    ReceiveProtected(peer, data, from, socket, now);
                return;
            }

            if (Stun.IsStun(data))
            {
                OnStun(data, from);
                return;
            }

            if (data.Length < NetWire.ClearHeader || !data[..4].SequenceEqual(NetWire.Magic))
                return;

            byte version = data[4];
            var r = new WireReader(data[NetWire.ClearHeader..]);

            switch (data[5])
            {
                case NetWire.Query when IsHost && data.Length >= NetWire.QuerySize:
                    OnQuery(ref r, from, socket);
                    break;
                case NetWire.Announce when !IsHost && _discoveryPort != 0:
                    OnAnnounce(ref r, version, from);
                    break;
                case NetWire.Hello when IsHost && data.Length == NetWire.MaxDatagram:
                    OnHello(ref r, version, from, socket, now);
                    break;
                case NetWire.Retry when _attempt != null:
                    OnRetry(ref r, from, now);
                    break;
                case NetWire.Welcome when _attempt != null:
                    OnWelcome(ref r, from, socket, now);
                    break;
                case NetWire.Refuse when _attempt != null:
                    OnRefuse(ref r, version);
                    break;
                case NetWire.Punch when !IsHost && data.Length == NetWire.PunchSize:
                    OnPunch(ref r, from);
                    break;
            }
        }

        // ==================== Timers ====================

        void Tick(long now)
        {
            TickStun(now);
            TickPunch(now);

            if (_attempt != null)
            {
                if (now - _attempt.StartedAt > _attempt.Timeout)
                {
                    string where = string.Join(", ", _attempt.Candidates.Select(c => c.ToString()));
                    FailAttempt(_attempt.CodeTag != null
                        ? $"No answer from the host in five minutes ({where}). If the host is on a phone's connection, or its router lets nothing in, it has to paste your answer code while you are still trying."
                        : $"No answer from {where}. Check the address and the port, and that the host's firewall or router lets UDP through on that port.", noAnswer: true);
                }
                else if (now >= _attempt.NextHello)
                {
                    for (int i = 0; i < _attempt.Candidates.Length; i++)
                        SendHello(i);
                    bool slow = _attempt.CodeTag != null && now - _attempt.StartedAt > CodeHelloSlowAfter;
                    _attempt.NextHello = now + (slow ? CodeHelloSlowInterval : HelloInterval);
                }
            }

            if (_discoveryPort != 0 && now >= _nextQuery)
            {
                SendQueries();
                _nextQuery = now + QueryInterval;
            }

            List<NetPeer> gone = null;

            foreach (var p in _peers.Values)
            {
                if (p.State != NetPeer.States.Closed)
                {
                    bool unanswered = IsHost && p.State == NetPeer.States.Joining && now - p.CreatedAt > JoinTimeout;

                    if (unanswered)
                    {
                        // Agreed keys and never said who it was: nobody saw it, nobody needs to hear of it
                        p.Close(NetWire.CloseProtocolError, "no JOIN");
                    }
                    else if (now - p.LastHeard > SilenceTimeout)
                    {
                        Report(p, p.State, "no answer for 10 seconds: the connection was lost");
                        p.Close(NetWire.CloseNormal, "timed out");
                    }
                }

                Flush(p);

                if (p.Finished)
                    (gone ??= new()).Add(p);
            }

            if (gone != null)
            {
                foreach (var p in gone)
                {
                    _peers.Remove(p.LocalId);
                    p.Dispose();
                }
                _nextPeersRefresh = 0;
            }

            if (now >= _nextPeersRefresh)
            {
                PublishPeers();
                _nextPeersRefresh = now + PeersRefresh;
            }
        }

        void Flush(NetPeer p) => p.Flush(Now(), _plain, _tx, SendRaw);

        void PublishPeers()
        {
            var list = new List<PeerInfo>(_peers.Count);

            foreach (var p in _peers.Values)
            {
                if (p.State == NetPeer.States.Closed)
                    continue;

                var state = p.State switch
                {
                    NetPeer.States.AwaitingDecision => PeerStates.AwaitingDecision,
                    NetPeer.States.Connected => PeerStates.Connected,
                    _ => PeerStates.Joining,
                };

                list.Add(new PeerInfo(p.LocalId, p.Name, p.AseVersion, p.Address, state,
                    p.Rtt.HasSample ? p.Rtt.Srtt : -1, p.Retransmissions, p.BytesIn, p.BytesOut, p.WithCode));
            }

            _peerInfo = [.. list];
        }

        // ==================== Discovery ====================

        void SendQueries()
        {
            ulong nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            _queryNonces.Enqueue(nonce);
            while (_queryNonces.Count > 4)
                _queryNonces.Dequeue();

            Span<byte> packet = stackalloc byte[NetWire.QuerySize];
            var w = ClearPacket(packet, NetWire.Query);
            w.U64(nonce);
            w.PadTo(NetWire.QuerySize);

            var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            foreach (var (network, mask) in LocalNetworks(Now()))
            {
                if (network.Length != 4)
                    continue;

                byte[] broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(network[i] | ~mask[i]);
                targets.Add(new IPAddress(broadcast));
            }

            foreach (var address in targets)
                SendRaw(_v4, w.Written, new IPEndPoint(address, _discoveryPort));
        }

        void OnQuery(ref WireReader r, IPEndPoint from, Socket socket)
        {
            ulong nonce = r.U64();
            if (!r.Ok || !IsLocal(from.Address))
                return;

            int players = 1 + _peers.Values.Count(p => p.State == NetPeer.States.Connected);

            Span<byte> packet = stackalloc byte[NetWire.QuerySize];
            var w = ClearPacket(packet, NetWire.Announce);
            w.U64(nonce);
            w.Str(_name);
            w.Str(_aseVersion);
            w.Str(_game);
            w.U8((byte)Math.Min(players, 255));
            w.U8((byte)Math.Min(_maxGuests + 1, 255));
            w.U8((byte)(_accepting && players <= _maxGuests ? 1 : 0));

            SendRaw(socket, w.Written, from);
        }

        void OnAnnounce(ref WireReader r, byte version, IPEndPoint from)
        {
            ulong nonce = r.U64();
            string name = r.Str();
            string aseVersion = r.Str();
            string game = r.Str();
            int players = r.U8();
            int maxPlayers = r.U8();
            bool accepting = r.U8() != 0;

            if (!r.Ok || !_queryNonces.Contains(nonce))
                return;

            _events.Enqueue(new HostFound(from, name, aseVersion, game, players, maxPlayers, accepting,
                Compatible: version == NetWire.ProtocolVersion));
        }

        /// <summary>
        /// Whether an address is on this computer's own networks: loopback, link-local, or inside
        /// one of its interfaces' subnets. Only those get an Announce; a Query that came in from
        /// the Internet through a forwarded port is ignored.
        /// </summary>
        bool IsLocal(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal)
                return true;

            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                return true;

            foreach (var (network, mask) in LocalNetworks(Now()))
            {
                if (network.Length != bytes.Length)
                    continue;

                bool match = true;
                for (int i = 0; i < bytes.Length && match; i++)
                    match = (bytes[i] & mask[i]) == (network[i] & mask[i]);

                if (match)
                    return true;
            }

            return false;
        }

        List<(byte[] Network, byte[] Mask)> LocalNetworks(long now)
        {
            if (now - _networksAt < NetworksRefresh)
                return _localNetworks;

            var list = new List<(byte[], byte[])>();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        byte[] address = unicast.Address.GetAddressBytes();
                        int prefix = unicast.PrefixLength;
                        if (prefix <= 0 || prefix >= address.Length * 8)
                            continue;

                        byte[] mask = new byte[address.Length];
                        for (int bit = 0; bit < prefix; bit++)
                            mask[bit / 8] |= (byte)(0x80 >> (bit % 8));

                        list.Add((address, mask));
                    }
                }
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }

            _localNetworks = list;
            _networksAt = now;
            return list;
        }

        /// <summary>
        /// This computer's addresses worth giving to another player: IPv4 on the local networks,
        /// and global IPv6 (reachable from the Internet without a NAT, firewall permitting).
        /// </summary>
        public static List<IPAddress> LocalAddresses()
        {
            var list = new List<IPAddress>();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        var a = unicast.Address;
                        bool v4 = a.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocalV4(a);
                        bool v6 = a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal
                                  && !a.IsIPv6Multicast && (a.GetAddressBytes()[0] & 0xE0) == 0x20;   // 2000::/3, global unicast

                        if ((v4 || v6) && !list.Contains(a))
                            list.Add(a);
                    }
                }
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }

            return list;
        }

        static bool IsLinkLocalV4(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return b[0] == 169 && b[1] == 254;
        }

        // ==================== STUN and hole punching ====================

        void TickStun(long now)
        {
            if (_stun != null)
            {
                foreach (var q in _stun)
                {
                    if (q.Mapped != null || q.Tries >= StunTries || now < q.NextSend)
                        continue;

                    var socket = q.Server.AddressFamily == AddressFamily.InterNetwork ? _v4 : _v6;
                    if (socket == null)
                    {
                        q.Tries = StunTries;
                        continue;
                    }

                    SendRaw(socket, Stun.BindingRequest(q.Id), q.Server);
                    q.NextSend = now + (StunFirstRetry << q.Tries);
                    q.Tries++;
                }

                if (_stun.All(q => q.Mapped != null) || now >= _stunDeadline)
                {
                    var answers = _stun.Where(q => q.Mapped != null).Select(q => (q.Server, q.Mapped)).ToList();
                    _stun = null;
                    _events.Enqueue(new StunResult(answers));
                }
            }

            if (_stunKeepAlive != null && now >= _nextKeepAlive)
            {
                var socket = _stunKeepAlive.AddressFamily == AddressFamily.InterNetwork ? _v4 : _v6;
                if (socket != null)
                    SendRaw(socket, Stun.BindingRequest(RandomNumberGenerator.GetBytes(Stun.TransactionIdSize)), _stunKeepAlive);
                _nextKeepAlive = now + StunKeepAliveInterval;
            }
        }

        // A STUN answer counts only if it is to a request of ours, from the server it went to
        void OnStun(ReadOnlySpan<byte> data, IPEndPoint from)
        {
            if (_stun == null || !Stun.TryParseResponse(data, out byte[] id, out var mapped))
                return;

            foreach (var q in _stun)
                if (q.Mapped == null && q.Id.AsSpan().SequenceEqual(id) && SameEndpoint(q.Server, from))
                    q.Mapped = mapped;
        }

        void TickPunch(long now)
        {
            if (_punchTargets == null)
                return;

            if (now >= _punchUntil)
            {
                _punchTargets = null;
                return;
            }

            if (now < _nextPunch)
                return;

            // The code's tag, so a guest whose router lets the Punch in knows it is the host's
            Span<byte> packet = stackalloc byte[NetWire.PunchSize];
            var w = ClearPacket(packet, NetWire.Punch);
            w.Bytes(_codeTag ?? RandomNumberGenerator.GetBytes(CodeText.TagSize));
            w.Bytes(RandomNumberGenerator.GetBytes(NetWire.PunchSize - NetWire.ClearHeader - CodeText.TagSize));

            foreach (var target in _punchTargets)
                SendRaw(target.AddressFamily == AddressFamily.InterNetwork ? _v4 : _v6, w.Written, target);

            _nextPunch = now + PunchInterval;
        }

        // ==================== Handshake, guest side ====================

        void BeginAttempt(IReadOnlyList<IPEndPoint> candidates, InviteCode code)
        {
            if (_attempt != null || _peers.Count > 0)
            {
                _events.Enqueue(new ConnectFailed("Already connected or connecting."));
                return;
            }

            var usable = candidates.Where(c => c.AddressFamily == AddressFamily.InterNetwork || _v6 != null).ToArray();
            if (usable.Length == 0)
            {
                _events.Enqueue(new ConnectFailed("That address cannot be reached from this computer (IPv6 is not available here)."));
                return;
            }

            long now = Now();
            _attempt = new Attempt
            {
                Candidates = usable,
                Cookies = new byte[usable.Length][],
                Keys = new KeyExchange(),
                Nonce = RandomNumberGenerator.GetBytes(NetWire.NonceSize),
                ConnectionId = NewConnectionId(),
                StartedAt = now,
                NextHello = now,
                CodeSecret = code?.Secret.ToArray(),
                CodeTag = code?.Tag,
                Timeout = code != null ? CodeConnectTimeout : ConnectTimeout,
            };
        }

        void SendHello(int candidate)
        {
            var a = _attempt;
            byte[] cookie = a.Cookies[candidate];

            Span<byte> packet = stackalloc byte[NetWire.MaxDatagram];
            var w = ClearPacket(packet, NetWire.Hello);
            w.Bytes(a.Nonce);
            w.Bytes(a.Keys.PublicKey);
            w.U32(a.ConnectionId);
            w.U8((byte)(cookie?.Length ?? 0));
            if (cookie != null)
                w.Bytes(cookie);
            w.U8(a.CodeTag != null ? NetWire.HelloWithCode : (byte)0);
            if (a.CodeTag != null)
                w.Bytes(a.CodeTag);
            w.PadTo(NetWire.MaxDatagram);

            var to = a.Candidates[candidate];
            SendRaw(to.AddressFamily == AddressFamily.InterNetwork ? _v4 : _v6, w.Written, to);
        }

        int CandidateIndex(IPEndPoint from)
        {
            for (int i = 0; i < _attempt.Candidates.Length; i++)
                if (SameEndpoint(_attempt.Candidates[i], from))
                    return i;
            return -1;
        }

        // An answer to our Hello (it carries our nonce) from an address we did not send it to: a
        // NAT that gives the host a different port for us, the hard side of hole punching. Only
        // taken with a code — its secret is in the keys, so whoever answers must have it — and
        // added as a candidate so the rest of the handshake goes there.
        int AdoptCandidate(IPEndPoint from, string why = null)
        {
            var a = _attempt;
            if (a.CodeSecret == null || a.Candidates.Length >= 16)
                return -1;

            a.Candidates = [.. a.Candidates, from];
            a.Cookies = [.. a.Cookies, null];
            _events.Enqueue(new NetLog(why ?? $"The host answered from {from}, an address its code did not have.", Notable: true));
            return a.Candidates.Length - 1;
        }

        // Says once per address what became of a Punch or a Hello: the steps of joining over
        // the Internet are what a failed attempt is read from, and they are rare
        readonly HashSet<IPEndPoint> _noted = new();

        void NoteOnce(IPEndPoint from, string text)
        {
            if (_noted.Count < 32 && _noted.Add(from))
                _events.Enqueue(new NetLog(text, Notable: true));
        }

        // A host behind a NAT that gives it a new port for every destination (a phone's
        // connection) punches towards us from a port its code could not have. When our side
        // lets the Punch in (a port the router opened, or no NAT), its source is where the host
        // can be reached, and a Hello there gets through the host's NAT, which is waiting for
        // packets from us. Taken only during an attempt with a code, with the code's tag, and
        // from the address of one of its candidates: nobody else can point our Hellos anywhere.
        void OnPunch(ref WireReader r, IPEndPoint from)
        {
            var a = _attempt;
            var tag = r.Bytes(CodeText.TagSize);

            if (!r.Ok)
                return;

            if (a == null && _peers.Count > 0)
                return;     // already in: the host punches on for a while

            if (a == null || a.CodeTag == null)
            {
                NoteOnce(from, $"The host's way in arrived from {from}, but this computer is no longer joining with its code: join again, and the host pastes your new answer code.");
                return;
            }

            if (!tag.SequenceEqual(a.CodeTag))
            {
                NoteOnce(from, $"A Punch from {from} was ignored: it is for another code.");
                return;
            }

            if (CandidateIndex(from) >= 0)
                return;

            var source = Unmapped(from.Address);
            if (!a.Candidates.Any(c => Unmapped(c.Address).Equals(source)))
            {
                NoteOnce(from, $"A Punch from {from} was ignored: {from.Address} is none of the code's addresses.");
                return;
            }

            int index = AdoptCandidate(from, $"The host opened the way from {from}: trying there too.");
            if (index >= 0)
                SendHello(index);
        }

        static IPAddress Unmapped(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

        void OnRetry(ref WireReader r, IPEndPoint from, long now)
        {
            var nonce = r.Bytes(NetWire.NonceSize);
            var cookie = r.Bytes(NetWire.CookieSize);
            int index = CandidateIndex(from);

            if (!r.Ok || !nonce.SequenceEqual(_attempt.Nonce))
                return;

            if (index < 0 && (index = AdoptCandidate(from)) < 0)
                return;

            _attempt.Cookies[index] = cookie.ToArray();
            SendHello(index);
        }

        void OnWelcome(ref WireReader r, IPEndPoint from, Socket socket, long now)
        {
            var guestNonce = r.Bytes(NetWire.NonceSize);
            var hostNonce = r.Bytes(NetWire.NonceSize);
            var hostPublic = r.Bytes(NetWire.PublicKeySize);
            uint hostId = r.U32();

            var a = _attempt;
            if (!r.Ok || !guestNonce.SequenceEqual(a.Nonce))
                return;

            if (CandidateIndex(from) < 0 && AdoptCandidate(from) < 0)
                return;

            byte[] secret = a.Keys.Agree(hostPublic);
            if (secret == null)
                return;

            var protection = PacketProtection.Create(secret, a.Nonce, hostNonce, a.Keys.PublicKey, hostPublic, isHost: false, a.CodeSecret);
            CryptographicOperations.ZeroMemory(secret);

            var peer = new NetPeer(a.ConnectionId, hostId, isHostSide: false, from, socket, protection, now)
            {
                WithCode = a.CodeSecret != null,
            };
            _peers[peer.LocalId] = peer;

            a.Keys.Dispose();
            _attempt = null;

            peer.SendMessage(Message(NetWire.MsgJoin, _name, _aseVersion));
            Flush(peer);

            _events.Enqueue(new JoinPending(peer.LocalId, from));
            _nextPeersRefresh = 0;
        }

        void OnRefuse(ref WireReader r, byte hostVersion)
        {
            var nonce = r.Bytes(NetWire.NonceSize);
            byte reason = r.U8();

            if (!r.Ok || !nonce.SequenceEqual(_attempt.Nonce))
                return;

            FailAttempt(reason switch
            {
                NetWire.RefuseVersion => $"The host runs a different version of the online-play protocol ({hostVersion}, this ASE speaks {NetWire.ProtocolVersion}): both players need the same version of ASE.",
                NetWire.RefuseFull => "The game is full.",
                NetWire.RefuseCode => "That invitation code is no longer valid: ask the host for a new one.",
                _ => "The host is not letting anyone in right now.",
            });
        }

        void FailAttempt(string reason, bool noAnswer = false)
        {
            _attempt.Keys.Dispose();
            _attempt = null;
            _events.Enqueue(new ConnectFailed(reason, noAnswer));
        }

        // ==================== Handshake, host side ====================

        void OnHello(ref WireReader r, byte version, IPEndPoint from, Socket socket, long now)
        {
            var guestNonce = r.Bytes(NetWire.NonceSize);
            var guestPublic = r.Bytes(NetWire.PublicKeySize);
            uint guestId = r.U32();
            int cookieLength = r.U8();
            var cookie = r.Bytes(cookieLength);
            bool withCode = (r.U8() & NetWire.HelloWithCode) != 0;
            var tag = withCode ? r.Bytes(CodeText.TagSize) : default;

            if (!r.Ok)
                return;

            if (version != NetWire.ProtocolVersion)
            {
                SendRefuse(guestNonce, NetWire.RefuseVersion, from, socket);
                return;
            }

            if (withCode)
                NoteOnce(from, $"A guest with a code reached this computer from {from}.");

            // The Welcome was lost: the same answer again, not a second key exchange
            foreach (var p in _peers.Values)
            {
                if (p.GuestNonce != null && p.State == NetPeer.States.Joining && SameEndpoint(p.Address, from)
                    && guestNonce.SequenceEqual(p.GuestNonce))
                {
                    SendRaw(socket, p.WelcomePacket, from);
                    return;
                }
            }

            int guests = _peers.Values.Count(p => p.State != NetPeer.States.Closed);
            if (!_accepting || guests >= _maxGuests)
            {
                SendRefuse(guestNonce, _accepting ? NetWire.RefuseFull : NetWire.RefuseNotAccepting, from, socket);
                return;
            }

            // Proof that the guest receives at this address, before any state or key work for it
            if (!_cookies.Check(from, cookie, now))
            {
                Span<byte> retry = stackalloc byte[NetWire.ClearHeader + NetWire.NonceSize + NetWire.CookieSize];
                var w = ClearPacket(retry, NetWire.Retry);
                w.Bytes(guestNonce);
                w.Bytes(_cookies.Make(from, now));
                SendRaw(socket, w.Written, from);
                return;
            }

            // A Hello made with a code must be made with the one in force: an old one is told so
            // rather than left to time out on keys that would not match
            if (withCode && (_codeTag == null || !tag.SequenceEqual(_codeTag)))
            {
                SendRefuse(guestNonce, NetWire.RefuseCode, from, socket);
                return;
            }

            using var keys = new KeyExchange();
            byte[] secret = keys.Agree(guestPublic);
            if (secret == null)
                return;

            byte[] hostNonce = RandomNumberGenerator.GetBytes(NetWire.NonceSize);
            var protection = PacketProtection.Create(secret, guestNonce, hostNonce, guestPublic, keys.PublicKey, isHost: true,
                                                     withCode ? _codeSecret : default);
            CryptographicOperations.ZeroMemory(secret);

            var peer = new NetPeer(NewConnectionId(), guestId, isHostSide: true, from, socket, protection, now)
            {
                GuestNonce = guestNonce.ToArray(),
                WithCode = withCode,
            };

            byte[] welcome = new byte[NetWire.ClearHeader + 2 * NetWire.NonceSize + NetWire.PublicKeySize + 4];
            var ww = ClearPacket(welcome, NetWire.Welcome);
            ww.Bytes(guestNonce);
            ww.Bytes(hostNonce);
            ww.Bytes(keys.PublicKey);
            ww.U32(peer.LocalId);
            peer.WelcomePacket = welcome;

            _peers[peer.LocalId] = peer;
            SendRaw(socket, welcome, from);
            _nextPeersRefresh = 0;
        }

        void SendRefuse(ReadOnlySpan<byte> guestNonce, byte reason, IPEndPoint to, Socket socket)
        {
            Span<byte> packet = stackalloc byte[NetWire.ClearHeader + NetWire.NonceSize + 1];
            var w = ClearPacket(packet, NetWire.Refuse);
            w.Bytes(guestNonce);
            w.U8(reason);
            SendRaw(socket, w.Written, to);
        }

        void AcceptPeer(NetPeer p)
        {
            p.State = NetPeer.States.Connected;
            p.SendMessage(Message(NetWire.MsgAccept, _name, _aseVersion, _game));
            Flush(p);

            _events.Enqueue(new PeerConnected(p.LocalId, p.Name, p.AseVersion, _game, p.Address));
            _nextPeersRefresh = 0;
        }

        // ==================== Connections ====================

        void ReceiveProtected(NetPeer peer, ReadOnlySpan<byte> data, IPEndPoint from, Socket socket, long now)
        {
            _received.Clear();

            // A CLOSE in this packet changes the state; what to report depends on the one before
            var before = peer.State;

            if (!peer.Receive(data, from, socket, now, _plain, _received))
                return;

            if (_received.Moved)
                _events.Enqueue(new NetLog($"{Label(peer)} moved to {from}."));

            foreach (var message in _received.Messages)
            {
                if (peer.State == NetPeer.States.Closed)
                    break;
                OnMessage(peer, message);
            }

            if (peer.State == NetPeer.States.Connected)
                foreach (var (channel, payload) in _received.Datagrams)
                    OnDatagram(peer, channel, payload);

            if (_received.ProtocolError && peer.State != NetPeer.States.Closed)
            {
                Report(peer, peer.State, "the other side sent something this version of ASE does not understand");
                peer.Close(NetWire.CloseProtocolError, "protocol error");
            }
            else if (_received.Closed && before != NetPeer.States.Closed)
            {
                Report(peer, before, CloseText(_received.CloseReason));
            }

            // Acknowledge and answer pings straight away rather than at the next timer
            Flush(peer);
        }

        void OnDatagram(NetPeer peer, byte channel, byte[] payload)
        {
            var hook = DatagramHook;
            if (hook != null)
            {
                try
                {
                    if (hook(peer.LocalId, channel, payload))
                        return;
                }
                catch (Exception ex)
                {
                    _events.Enqueue(new NetLog($"A datagram could not be handled: {ex.Message}", Error: true));
                    return;
                }
            }

            _events.Enqueue(new DatagramReceived(peer.LocalId, channel, payload));
        }

        void OnMessage(NetPeer peer, byte[] message)
        {
            var r = new WireReader(message);
            byte type = r.U8();

            if (type >= NetWire.FirstSessionMessage)
            {
                if (peer.State != NetPeer.States.Connected)
                    return;

                var hook = MessageHook;
                if (hook != null)
                {
                    try
                    {
                        if (hook(peer.LocalId, message))
                            return;
                    }
                    catch (Exception ex)
                    {
                        _events.Enqueue(new NetLog($"A session message could not be handled: {ex.Message}", Error: true));
                        return;
                    }
                }

                _events.Enqueue(new MessageReceived(peer.LocalId, message));
                return;
            }

            switch (type)
            {
                case NetWire.MsgJoin when IsHost && peer.State == NetPeer.States.Joining:
                    peer.Name = r.Str();
                    peer.AseVersion = r.Str();
                    if (!r.Ok)
                        break;

                    if (string.IsNullOrWhiteSpace(peer.Name))
                        peer.Name = "(no name)";

                    peer.State = NetPeer.States.AwaitingDecision;
                    _events.Enqueue(new JoinRequest(peer.LocalId, peer.Name, peer.AseVersion, peer.Address, peer.WithCode));
                    _nextPeersRefresh = 0;
                    break;

                case NetWire.MsgAccept when !IsHost && peer.State == NetPeer.States.Joining:
                    peer.Name = r.Str();
                    peer.AseVersion = r.Str();
                    peer.Game = r.Str();
                    if (!r.Ok)
                        break;

                    peer.State = NetPeer.States.Connected;
                    _events.Enqueue(new PeerConnected(peer.LocalId, peer.Name, peer.AseVersion, peer.Game, peer.Address));
                    _nextPeersRefresh = 0;
                    break;

                case NetWire.MsgReject when !IsHost:
                    string reason = r.Str();
                    _events.Enqueue(new ConnectFailed(string.IsNullOrEmpty(reason) ? "The host declined." : $"The host declined: {reason}"));
                    peer.Close(NetWire.CloseNormal, "declined");
                    break;
            }
        }

        // A connection the other side, a timeout or an error ended, in state `state` until then:
        // one event for whoever listens
        void Report(NetPeer peer, NetPeer.States state, string reason)
        {
            bool wasConnected = state == NetPeer.States.Connected;

            // A guest still waiting to be accepted gets a failed connection, not a departure
            if (!IsHost && !wasConnected)
                _events.Enqueue(new ConnectFailed(Capitalised(reason)));
            else if (IsHost && state == NetPeer.States.Joining)
                return;     // never introduced itself: nothing to report
            else
                _events.Enqueue(new PeerLeft(peer.LocalId, peer.Name, reason, wasConnected));

            _nextPeersRefresh = 0;
        }

        void CloseLocally(NetPeer peer, byte reason, string text)
        {
            if (peer.State == NetPeer.States.Closed)
                return;

            if (!IsHost || peer.State != NetPeer.States.Joining)
                _events.Enqueue(new PeerLeft(peer.LocalId, peer.Name, text, peer.State == NetPeer.States.Connected));

            peer.Close(reason, text);
            _nextPeersRefresh = 0;
        }

        string CloseText(byte reason) => reason switch
        {
            NetWire.CloseHostEnded => "the host ended the game",
            NetWire.CloseRejected => "declined",
            NetWire.CloseProtocolError => "the other side reported a protocol error",
            _ => IsHost ? "left the game" : "the host closed the connection",
        };

        static string Capitalised(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

        static string Label(NetPeer p) => string.IsNullOrEmpty(p.Name) ? p.Address.ToString() : p.Name;

        // ==================== Helpers ====================

        uint NewConnectionId()
        {
            uint id;
            do id = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
            while (id == 0 || _peers.ContainsKey(id));
            return id;
        }

        static WireWriter ClearPacket(Span<byte> buffer, byte type)
        {
            var w = new WireWriter(buffer);
            w.Bytes(NetWire.Magic);
            w.U8(NetWire.ProtocolVersion);
            w.U8(type);
            return w;
        }

        static byte[] Message(byte type, params string[] fields)
        {
            int size = 1 + fields.Sum(WireWriter.StrSize);
            byte[] message = new byte[size];
            var w = new WireWriter(message);
            w.U8(type);
            foreach (var f in fields)
                w.Str(f);
            return message;
        }

        // IPv4 endpoints may come back as IPv4-mapped IPv6 on some systems; compare what they mean
        static bool SameEndpoint(IPEndPoint a, IPEndPoint b)
        {
            if (a.Port != b.Port)
                return false;

            var x = a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address;
            var y = b.Address.IsIPv4MappedToIPv6 ? b.Address.MapToIPv4() : b.Address;
            return x.Equals(y);
        }

        void SendRaw(Socket socket, ReadOnlySpan<byte> data, IPEndPoint to)
        {
            if (socket == null)
                return;

            try
            {
                socket.SendTo(data, SocketFlags.None, to);
            }
            catch (SocketException)
            {
                // UDP: a datagram that cannot leave is a lost datagram, which the protocol expects
            }
        }

        static long Now() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
    }
}
