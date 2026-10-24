/*
 *
 * Asking the router to open the game's port (NETPLAY.md §7.3, phase 5b): PCP, NAT-PMP and
 * UPnP-IGD, the three ways a home router lets a program forward a port to itself. With the
 * port open, a player behind any NAT — a phone's connection included, whose symmetric NAT
 * defeats hole punching against a router that filters by port — reaches the host with the
 * code alone, which is what forwarding the port by hand did in the first test over the
 * Internet (NETPLAY.md §2, 17-10-2026).
 *
 * Only on demand, like STUN: when a code is made or used, never for a game on the local
 * network. Every mapping is asked for a limited time (an hour), renewed while the session
 * lasts and removed at its end, so a crash leaves nothing open for long. A router that only
 * takes permanent mappings (UPnP error 725) gets one, removed at the end the same way.
 *
 * PCP is tried first and falls back to NAT-PMP when the router answers in that version; UPnP
 * is looked for at the same time and used only when neither answered. Never two at once:
 * routers that speak more than one keep a single rule behind them, and removing a duplicate
 * through one protocol would remove the other's.
 *
 * Nothing here touches the emulator, which is what lets tools/NetTest check it against fake
 * routers on loopback.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ASE.Netplay
{
    public enum PortMappingProtocol { Pcp, NatPmp, Upnp }

    /// <summary>Whether a port the router opened reaches the Internet.</summary>
    public enum MappingReach
    {
        /// <summary>The router's public address is the one the Internet sees (or nothing says
        /// otherwise): the port is open to everybody.</summary>
        Public,

        /// <summary>The router's own outside address is a private or shared one, or not the one
        /// STUN sees: there is another NAT beyond it (the operator's CG-NAT, or a second router),
        /// and the port it opened leads nowhere.</summary>
        BehindAnotherNat,
    }

    /// <summary>Where the routers are asked. The defaults are the real ones; tests point them at
    /// fake routers on loopback.</summary>
    public sealed class PortMapperOptions
    {
        /// <summary>The routers asked by PCP and NAT-PMP; null for this computer's default
        /// gateways.</summary>
        public IReadOnlyList<IPAddress> Gateways;

        public int PmpPort = 5351;

        /// <summary>Where the UPnP search goes: the SSDP multicast group.</summary>
        public IPEndPoint SsdpTarget = new(IPAddress.Parse("239.255.255.250"), 1900);

        /// <summary>Lets a router answer from loopback (tests only): otherwise only addresses on
        /// a local network are believed.</summary>
        public bool AllowLoopback;

        public int LifetimeSeconds = 3600;

        /// <summary>Renewals never come closer together than this, whatever lifetime the router
        /// granted.</summary>
        public TimeSpan MinRenewal = TimeSpan.FromSeconds(30);

        public TimeSpan SsdpWait = TimeSpan.FromSeconds(2);

        public static readonly PortMapperOptions Default = new();
    }

    /// <summary>A UDP port this computer's router forwards to it, until <see cref="Release"/>.</summary>
    public sealed class PortMapping
    {
        public const string Description = "ASE online play";

        public PortMappingProtocol Protocol { get; }
        public int InternalPort { get; }
        public int ExternalPort { get; private set; }

        /// <summary>The router's outside address, as it tells it; null when it would not say.</summary>
        public IPAddress ExternalAddress { get; private set; }

        /// <summary>The router took only a permanent mapping: removed at the end, or it stays.</summary>
        public bool Permanent { get; }

        /// <summary>The router's address, for whoever tells the user.</summary>
        public IPAddress Router { get; }

        public string ProtocolName => Protocol switch
        {
            PortMappingProtocol.Pcp => "PCP",
            PortMappingProtocol.NatPmp => "NAT-PMP",
            _ => "UPnP",
        };

        readonly PortMapperOptions _options;
        readonly Action<string> _log;
        readonly CancellationTokenSource _renewals = new();
        int _released;

        // PCP and NAT-PMP: the router and, for PCP, the mapping's nonce and our address
        readonly IPEndPoint _gateway;
        readonly byte[] _nonce;
        readonly IPAddress _local;

        // UPnP: the router's control point and the client the requests are made with
        readonly UpnpService _upnp;
        readonly HttpClient _http;

        PortMapping(PortMappingProtocol protocol, int internalPort, int externalPort, IPAddress external, IPAddress router,
                    PortMapperOptions options, Action<string> log, IPEndPoint gateway = null, byte[] nonce = null, IPAddress local = null,
                    UpnpService upnp = null, HttpClient http = null, bool permanent = false)
        {
            Protocol = protocol;
            InternalPort = internalPort;
            ExternalPort = externalPort;
            ExternalAddress = external;
            Router = router;
            Permanent = permanent;
            _options = options;
            _log = log;
            _gateway = gateway;
            _nonce = nonce;
            _local = local;
            _upnp = upnp;
            _http = http;
        }

        // ==================== Opening ====================

        /// <summary>
        /// Asks the router to forward UDP <paramref name="internalPort"/> to this computer: PCP or
        /// NAT-PMP, otherwise UPnP. The mapping, renewed until <see cref="Release"/>, or null when
        /// no router answered or every one refused. <paramref name="log"/> hears what happened,
        /// on whatever thread the answer arrived.
        /// </summary>
        public static async Task<PortMapping> Open(int internalPort, Action<string> log, PortMapperOptions options = null, CancellationToken cancel = default)
        {
            options ??= PortMapperOptions.Default;
            log ??= _ => { };

            using var search = CancellationTokenSource.CreateLinkedTokenSource(cancel);

            // The UPnP search only listens, so it can run while PCP is asked
            var upnpSearch = FindUpnp(options, log, search.Token);
            var gateways = options.Gateways ?? DefaultGateways();

            PortMapping mapping = null;
            foreach (var gateway in gateways)
            {
                mapping = await OpenPcp(new IPEndPoint(gateway, options.PmpPort), internalPort, options, log, cancel).ConfigureAwait(false);
                if (mapping != null)
                    break;
            }

            if (mapping == null)
            {
                foreach (var service in await upnpSearch.ConfigureAwait(false))
                {
                    mapping = await OpenUpnp(service, internalPort, options, log, cancel).ConfigureAwait(false);
                    if (mapping != null)
                        break;
                }
            }
            else
            {
                search.Cancel();
                try { await upnpSearch.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }

            if (mapping == null)
            {
                log("no router opened the port (none answered UPnP, NAT-PMP or PCP, or they refused)");
                return null;
            }

            if (cancel.IsCancellationRequested)
            {
                await mapping.Release().ConfigureAwait(false);
                return null;
            }

            log($"the router ({mapping.Router}, {mapping.ProtocolName}) forwards UDP port {mapping.ExternalPort} to port {mapping.InternalPort} of this computer" +
                (mapping.ExternalAddress != null ? $"; its outside address is {mapping.ExternalAddress}" : "") +
                (mapping.Permanent ? " (it only takes permanent mappings: removed when the session ends)" : ""));
            return mapping;
        }

        /// <summary>Removes the mapping (harmless twice). Waits for the router at most a couple of
        /// seconds; a router that does not answer lets an hour's mapping run out on its own.</summary>
        public async Task Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            _renewals.Cancel();

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                bool ok = Protocol switch
                {
                    PortMappingProtocol.Pcp => await PcpRequest(_gateway, _local, _nonce, InternalPort, ExternalPort, ExternalAddress, 0, timeout.Token).ConfigureAwait(false) != null,
                    PortMappingProtocol.NatPmp => await PmpMap(_gateway, InternalPort, 0, 0, timeout.Token).ConfigureAwait(false) != null,
                    _ => (await Soap(_http, _upnp, "DeletePortMapping",
                            [("NewRemoteHost", ""), ("NewExternalPort", ExternalPort.ToString()), ("NewProtocol", "UDP")], timeout.Token).ConfigureAwait(false)).Ok,
                };

                _log(ok ? $"the router closed port {ExternalPort} again" : $"the router did not confirm closing port {ExternalPort}");
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or OperationCanceledException or XmlException)
            {
                _log($"the router did not confirm closing port {ExternalPort}");
            }
            finally
            {
                _http?.Dispose();
            }
        }

        // Renews the mapping at half its lifetime until it is released
        void KeepRenewing(int grantedSeconds)
        {
            if (grantedSeconds <= 0)
                return;

            var every = TimeSpan.FromSeconds(grantedSeconds / 2.0);
            if (every < _options.MinRenewal)
                every = _options.MinRenewal;

            var token = _renewals.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(every, token).ConfigureAwait(false);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));

                        bool ok = Protocol switch
                        {
                            PortMappingProtocol.Pcp => await PcpRequest(_gateway, _local, _nonce, InternalPort, ExternalPort, ExternalAddress, _options.LifetimeSeconds, timeout.Token).ConfigureAwait(false) != null,
                            PortMappingProtocol.NatPmp => await PmpMap(_gateway, InternalPort, ExternalPort, _options.LifetimeSeconds, timeout.Token).ConfigureAwait(false) != null,
                            _ => (await AddUpnpMapping(_http, _upnp, _local, InternalPort, ExternalPort, _options.LifetimeSeconds, timeout.Token).ConfigureAwait(false)).Ok,
                        };

                        if (!ok && !token.IsCancellationRequested)
                            _log($"the router did not renew port {ExternalPort}; trying again later");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is SocketException or HttpRequestException or OperationCanceledException or XmlException or ObjectDisposedException)
                    {
                        // The next turn tries again
                    }
                }
            }, token);
        }

        // ==================== Where the Internet is ====================

        /// <summary>Whether a router's outside address reaches the Internet, against the public
        /// address STUN saw (either may be unknown).</summary>
        public static MappingReach Reach(IPAddress routerExternal, IPAddress stunPublic)
        {
            if (routerExternal == null)
                return MappingReach.Public;

            if (IsPrivateOrShared(routerExternal))
                return MappingReach.BehindAnotherNat;

            if (stunPublic != null && !Normal(routerExternal).Equals(Normal(stunPublic)))
                return MappingReach.BehindAnotherNat;

            return MappingReach.Public;
        }

        /// <summary>Addresses that never reach the Internet: private networks (RFC 1918),
        /// operators' shared space (RFC 6598, CG-NAT), link-local, loopback, unspecified.</summary>
        public static bool IsPrivateOrShared(IPAddress address)
        {
            byte[] a = Normal(address).GetAddressBytes();
            if (a.Length != 4)
                return false;

            return a[0] == 10
                || a[0] == 127
                || a[0] == 0
                || (a[0] == 172 && (a[1] & 0xF0) == 16)
                || (a[0] == 192 && a[1] == 168)
                || (a[0] == 169 && a[1] == 254)
                || (a[0] == 100 && (a[1] & 0xC0) == 64);
        }

        static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

        // Where a router may answer from: a local network, or loopback when testing
        static bool OnLocalNetwork(IPAddress address, PortMapperOptions options)
        {
            address = Normal(address);
            if (IPAddress.IsLoopback(address))
                return options.AllowLoopback;
            return address.AddressFamily == AddressFamily.InterNetwork && IsPrivateOrShared(address) && !address.Equals(IPAddress.Any);
        }

        static List<IPAddress> DefaultGateways()
        {
            var list = new List<IPAddress>();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var g in nic.GetIPProperties().GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any) && !list.Contains(g.Address))
                            list.Add(g.Address);
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // No gateways to ask by PCP; UPnP still finds a router by itself
            }

            return list;
        }

        // The address this computer uses to reach a router (what the router knows it as)
        static IPAddress LocalAddressFor(IPAddress router)
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(router, 9));
            return ((IPEndPoint)s.LocalEndPoint).Address;
        }

        // ==================== PCP (RFC 6887) and NAT-PMP (RFC 6886) ====================

        const byte PcpVersion = 2;
        const byte PcpMap = 1;
        const int PcpSize = 60;
        const int UdpProtocol = 17;

        static async Task<PortMapping> OpenPcp(IPEndPoint gateway, int internalPort, PortMapperOptions options, Action<string> log, CancellationToken cancel)
        {
            if (!OnLocalNetwork(gateway.Address, options))
                return null;

            IPAddress local;
            try
            {
                local = LocalAddressFor(gateway.Address);
            }
            catch (SocketException)
            {
                return null;
            }

            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] reply;

            try
            {
                reply = await PcpRequest(gateway, local, nonce, internalPort, internalPort, null, options.LifetimeSeconds, cancel, acceptPmp: true).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return null;    // nobody listens there (the system said so: port unreachable)
            }

            if (reply == null)
                return null;

            // A NAT-PMP router answers a PCP request in its own version, refusing it
            if (reply[0] == 0)
                return await OpenNatPmp(gateway, internalPort, options, log, cancel).ConfigureAwait(false);

            int result = reply[3];
            if (result != 0)
            {
                log($"the router ({gateway.Address}) refused the port by PCP: {PcpResult(result)}");
                return null;
            }

            int granted = (int)Math.Min(int.MaxValue, U32(reply, 4));
            int external = U16(reply, 42);
            var externalAddress = Normal(new IPAddress(reply.AsSpan(44, 16)));

            var mapping = new PortMapping(PortMappingProtocol.Pcp, internalPort, external,
                externalAddress.Equals(IPAddress.Any) ? null : externalAddress, gateway.Address, options, log,
                gateway: gateway, nonce: nonce, local: local);
            mapping.KeepRenewing(granted);
            return mapping;
        }

        // A MAP request (lifetime 0 removes), answered with the router's reply or null. With
        // acceptPmp a NAT-PMP router's refusal counts as an answer, so the caller can switch.
        static async Task<byte[]> PcpRequest(IPEndPoint gateway, IPAddress local, byte[] nonce, int internalPort, int suggestedPort,
                                             IPAddress suggestedAddress, int lifetime, CancellationToken cancel, bool acceptPmp = false)
        {
            byte[] p = new byte[PcpSize];
            p[0] = PcpVersion;
            p[1] = PcpMap;
            PutU32(p, 4, (uint)lifetime);
            local.MapToIPv6().GetAddressBytes().CopyTo(p, 8);
            nonce.CopyTo(p, 24);
            p[36] = UdpProtocol;
            PutU16(p, 40, internalPort);
            PutU16(p, 42, suggestedPort);
            (suggestedAddress ?? IPAddress.Any).MapToIPv6().GetAddressBytes().CopyTo(p, 44);

            return await Exchange(gateway, p, cancel, r =>
                (acceptPmp && r.Length >= 4 && r[0] == 0 && U16(r, 2) == 1)
                || (r.Length >= PcpSize && r[0] == PcpVersion && r[1] == (0x80 | PcpMap)
                    && r.AsSpan(24, 12).SequenceEqual(nonce) && r[36] == UdpProtocol && U16(r, 40) == internalPort)).ConfigureAwait(false);
        }

        static async Task<PortMapping> OpenNatPmp(IPEndPoint gateway, int internalPort, PortMapperOptions options, Action<string> log, CancellationToken cancel)
        {
            byte[] reply;
            byte[] address;

            try
            {
                reply = await PmpMap(gateway, internalPort, internalPort, options.LifetimeSeconds, cancel).ConfigureAwait(false);
                if (reply == null)
                    return null;

                if (U16(reply, 2) != 0)
                {
                    log($"the router ({gateway.Address}) refused the port by NAT-PMP: {PmpResult(U16(reply, 2))}");
                    return null;
                }

                address = await Exchange(gateway, [0, 0], cancel, r => r.Length >= 12 && r[0] == 0 && r[1] == 128).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return null;
            }

            IPAddress external = address != null && U16(address, 2) == 0 ? new IPAddress(address.AsSpan(8, 4)) : null;
            var mapping = new PortMapping(PortMappingProtocol.NatPmp, internalPort, U16(reply, 10),
                external != null && external.Equals(IPAddress.Any) ? null : external, gateway.Address, options, log, gateway: gateway);
            mapping.KeepRenewing((int)Math.Min(int.MaxValue, U32(reply, 12)));
            return mapping;
        }

        // A NAT-PMP UDP mapping request (lifetime 0 and port 0 remove it)
        static Task<byte[]> PmpMap(IPEndPoint gateway, int internalPort, int suggestedPort, int lifetime, CancellationToken cancel)
        {
            byte[] p = new byte[12];
            p[1] = 1;   // map UDP
            PutU16(p, 4, internalPort);
            PutU16(p, 6, suggestedPort);
            PutU32(p, 8, (uint)lifetime);
            return Exchange(gateway, p, cancel, r => r.Length >= 16 && r[0] == 0 && r[1] == 129 && U16(r, 8) == internalPort);
        }

        // One request to a router and the first answer that fits: tries at 0, 250 and 750 ms and
        // waits until 1750 ms (RFC 6886's schedule, cut short: a router answers within
        // milliseconds or not at all). A SocketException means nobody listens there.
        static async Task<byte[]> Exchange(IPEndPoint to, byte[] request, CancellationToken cancel, Func<byte[], bool> fits)
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(to);

            byte[] buffer = new byte[1100];
            int[] waits = [250, 500, 1000];

            foreach (int wait in waits)
            {
                await s.SendAsync(request, SocketFlags.None, cancel).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(wait);

                try
                {
                    while (true)
                    {
                        int n = await s.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                        var reply = buffer[..n];
                        if (fits(reply))
                            return reply;
                    }
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    // Not yet: send again
                }
            }

            return null;
        }

        static string PcpResult(int code) => code switch
        {
            1 => "unsupported version",
            2 => "not authorised (the router does not let programs open ports)",
            3 => "malformed request",
            4 => "unsupported opcode",
            7 => "network failure",
            8 => "no resources",
            9 => "unsupported protocol",
            10 => "user quota exceeded",
            11 => "cannot provide an outside address",
            12 => "address mismatch",
            _ => $"error {code}",
        };

        static string PmpResult(int code) => code switch
        {
            1 => "unsupported version",
            2 => "not authorised (the router does not let programs open ports)",
            3 => "network failure",
            4 => "out of resources",
            5 => "unsupported opcode",
            _ => $"error {code}",
        };

        // ==================== UPnP-IGD ====================

        sealed record UpnpService(Uri Control, string ServiceType, IPAddress Router);

        static readonly string[] SearchTargets =
        [
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        ];

        // In order of preference: a router listing both kinds of connection usually has only one
        // of them up, which GetExternalIPAddress tells apart
        static readonly string[] ConnectionServices =
        [
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        ];

        const int MaxDocument = 256 * 1024;

        // SSDP search: M-SEARCH to the multicast group, twice, and the first router whose
        // description lists a WAN connection. Only answers from the local network count, and
        // only descriptions on the address that answered, so nobody can steer the requests
        // elsewhere.
        static async Task<List<UpnpService>> FindUpnp(PortMapperOptions options, Action<string> log, CancellationToken cancel)
        {
            var services = new List<UpnpService>();
            var seen = new HashSet<string>();

            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            }
            catch (SocketException)
            {
                return services;
            }

            var deadline = DateTime.UtcNow + options.SsdpWait;
            var resendAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
            bool resent = false;
            byte[] buffer = new byte[2048];

            void Search()
            {
                foreach (string target in SearchTargets)
                {
                    string m = "M-SEARCH * HTTP/1.1\r\n" +
                               $"HOST: {options.SsdpTarget}\r\n" +
                               "MAN: \"ssdp:discover\"\r\n" +
                               "MX: 2\r\n" +
                               $"ST: {target}\r\n\r\n";
                    try { s.SendTo(Encoding.ASCII.GetBytes(m), options.SsdpTarget); } catch (SocketException) { }
                }
            }

            Search();

            using var http = NewHttpClient();

            while (services.Count == 0)
            {
                var now = DateTime.UtcNow;
                if (now >= deadline || cancel.IsCancellationRequested)
                    break;

                if (!resent && now >= resendAt)
                {
                    Search();
                    resent = true;
                }

                var until = resent ? deadline : (resendAt < deadline ? resendAt : deadline);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(until - now);

                SocketReceiveFromResult got;
                try
                {
                    got = await s.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }

                var from = (IPEndPoint)got.RemoteEndPoint;
                string location = SsdpLocation(Encoding.ASCII.GetString(buffer, 0, got.ReceivedBytes));

                if (location == null || !seen.Add(location) || !OnLocalNetwork(from.Address, options))
                    continue;

                if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
                    || !IPAddress.TryParse(uri.Host, out var host) || !Normal(host).Equals(Normal(from.Address)))
                {
                    log($"a UPnP device at {from.Address} gave its description at {location}, which is not on its own address: ignored");
                    continue;
                }

                try
                {
                    using var timeoutHttp = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeoutHttp.CancelAfter(TimeSpan.FromSeconds(3));
                    string xml = await GetLimited(http, uri, timeoutHttp.Token).ConfigureAwait(false);
                    services.AddRange(ParseDescription(xml, uri, Normal(from.Address)));
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or XmlException or InvalidOperationException)
                {
                    if (!cancel.IsCancellationRequested)
                        log($"the UPnP device at {from.Address} did not give a usable description");
                }
            }

            return services;
        }

        // The LOCATION header of an SSDP answer, or null
        static string SsdpLocation(string text)
        {
            if (!text.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (string line in text.Split("\r\n"))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                    return line[(colon + 1)..].Trim();
            }

            return null;
        }

        static List<UpnpService> ParseDescription(string xml, Uri location, IPAddress router)
        {
            var doc = SafeXml(xml);
            var found = new List<UpnpService>();

            string urlBase = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
            var baseUri = Uri.TryCreate(urlBase, UriKind.Absolute, out var b) ? b : location;

            foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                string type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value.Trim();
                string control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value.Trim();

                if (type == null || string.IsNullOrEmpty(control) || !ConnectionServices.Contains(type))
                    continue;

                if (!Uri.TryCreate(baseUri, control, out var url) || url.Scheme != Uri.UriSchemeHttp
                    || !IPAddress.TryParse(url.Host, out var host) || !Normal(host).Equals(router))
                    continue;

                found.Add(new UpnpService(url, type, router));
            }

            return found.OrderBy(f => Array.IndexOf(ConnectionServices, f.ServiceType)).ToList();
        }

        static async Task<PortMapping> OpenUpnp(UpnpService service, int internalPort, PortMapperOptions options, Action<string> log, CancellationToken cancel)
        {
            var http = NewHttpClient();

            try
            {
                IPAddress local = LocalAddressFor(service.Router);

                var ip = await Soap(http, service, "GetExternalIPAddress", [], cancel).ConfigureAwait(false);
                IPAddress external = ip.Ok && IPAddress.TryParse(ip.Value("NewExternalIPAddress"), out var e) && !e.Equals(IPAddress.Any) ? e : null;

                // A router listing a connection that is down says so with an empty address; the
                // next one listed may be the one in use
                if (ip.Ok && external == null && service.ServiceType.Contains("PPP"))
                    log("the router's PPP connection has no outside address");

                // The game's own port first; another one if a mapping elsewhere has it
                var ports = new List<int> { internalPort };
                for (int i = 0; i < 3; i++)
                    ports.Add(RandomNumberGenerator.GetInt32(20000, 60000));

                int lease = options.LifetimeSeconds;
                foreach (int port in ports)
                {
                    var add = await AddUpnpMapping(http, service, local, internalPort, port, lease, cancel).ConfigureAwait(false);

                    if (!add.Ok && add.ErrorCode == 725 && lease != 0)
                    {
                        lease = 0;
                        add = await AddUpnpMapping(http, service, local, internalPort, port, lease, cancel).ConfigureAwait(false);
                    }

                    if (add.Ok)
                    {
                        var mapping = new PortMapping(PortMappingProtocol.Upnp, internalPort, port, external, service.Router, options, log,
                            local: local, upnp: service, http: http, permanent: lease == 0);
                        mapping.KeepRenewing(lease);
                        http = null;    // the mapping keeps it
                        return mapping;
                    }

                    if (add.ErrorCode != 718)   // anything but "that port is taken"
                    {
                        // An error is a failure even when the router lists the mapping afterwards:
                        // that one forwards nothing, and is removed so it does not linger
                        bool phantom = await IsUpnpMappingOurs(http, service, local, internalPort, port, cancel).ConfigureAwait(false);
                        if (phantom)
                            await Soap(http, service, "DeletePortMapping",
                                [("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", "UDP")], cancel).ConfigureAwait(false);

                        log($"the router ({service.Router}) refused the port by UPnP: {add.Error}" +
                            (phantom ? "; it listed the mapping all the same, which forwards nothing on such routers, and it was removed" : ""));
                        return null;
                    }
                }

                log($"the router ({service.Router}) had no free port to give by UPnP");
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or XmlException or SocketException)
            {
                log($"the router ({service.Router}) did not answer its UPnP requests");
                return null;
            }
            finally
            {
                http?.Dispose();
            }
        }

        static Task<SoapResult> AddUpnpMapping(HttpClient http, UpnpService service, IPAddress local, int internalPort, int externalPort, int lease, CancellationToken cancel) =>
            Soap(http, service, "AddPortMapping",
            [
                ("NewRemoteHost", ""),
                ("NewExternalPort", externalPort.ToString()),
                ("NewProtocol", "UDP"),
                ("NewInternalPort", internalPort.ToString()),
                ("NewInternalClient", local.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", Description),
                ("NewLeaseDuration", lease.ToString()),
            ], cancel);

        // Whether the router lists a UDP mapping of externalPort to this computer's internalPort.
        // A MitraStar HGU (Movistar's and O2's fibre routers, MiniUPnPd 2.3.7) answers every new
        // AddPortMapping with 501 "Action Failed", lists the mapping anyway (TCP as well as UDP),
        // answers OK when asked again for it, and forwards nothing through it: a STUN answer
        // from another address (RFC 5780 CHANGE-REQUEST) never arrived, waiting or not, asked
        // twice or not (17-10-2026, the author's router). Such a phantom is removed.
        static async Task<bool> IsUpnpMappingOurs(HttpClient http, UpnpService service, IPAddress local, int internalPort, int externalPort, CancellationToken cancel)
        {
            var entry = await Soap(http, service, "GetSpecificPortMappingEntry",
                [("NewRemoteHost", ""), ("NewExternalPort", externalPort.ToString()), ("NewProtocol", "UDP")], cancel).ConfigureAwait(false);

            return entry.Ok
                && entry.Value("NewInternalPort") == internalPort.ToString()
                && IPAddress.TryParse(entry.Value("NewInternalClient"), out var client) && Normal(client).Equals(Normal(local));
        }

        sealed record SoapResult(bool Ok, XDocument Body, int ErrorCode, string Error)
        {
            public string Value(string name) => Body?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        }

        // One SOAP action on the router's control point: the answer, or the UPnP error it gave
        static async Task<SoapResult> Soap(HttpClient http, UpnpService service, string action, (string Name, string Value)[] args, CancellationToken cancel)
        {
            var body = new StringBuilder();
            body.Append("<?xml version=\"1.0\"?>\r\n");
            body.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
            body.Append($"<u:{action} xmlns:u=\"{service.ServiceType}\">");
            foreach (var (name, value) in args)
                body.Append($"<{name}>{SecurityElement.Escape(value)}</{name}>");
            body.Append($"</u:{action}></s:Body></s:Envelope>\r\n");

            using var request = new HttpRequestMessage(HttpMethod.Post, service.Control)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.ServiceType}#{action}\"");
            request.Headers.ExpectContinue = false;
            request.Headers.ConnectionClose = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            string text = await ReadLimited(response, timeout.Token).ConfigureAwait(false);

            XDocument doc = null;
            try { doc = SafeXml(text); } catch (XmlException) { }

            if (response.IsSuccessStatusCode)
                return new SoapResult(true, doc, 0, null);

            string code = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value.Trim();
            string description = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value.Trim();
            int.TryParse(code, out int errorCode);

            return new SoapResult(false, doc, errorCode,
                errorCode != 0 ? $"error {errorCode}{(string.IsNullOrEmpty(description) ? "" : $" ({description})")}" : $"HTTP {(int)response.StatusCode}");
        }

        // No proxy (the router is on the local network, and nothing of this goes elsewhere), no
        // redirections, and short waits
        static HttpClient NewHttpClient() => new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        static async Task<string> GetLimited(HttpClient http, Uri uri, CancellationToken cancel)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await ReadLimited(response, cancel).ConfigureAwait(false);
        }

        // A router's answer, refusing anything larger than any description needs
        static async Task<string> ReadLimited(HttpResponseMessage response, CancellationToken cancel)
        {
            if (response.Content.Headers.ContentLength > MaxDocument)
                throw new HttpRequestException("The router's answer is too large.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192];
            int n;

            while ((n = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                memory.Write(buffer, 0, n);
                if (memory.Length > MaxDocument)
                    throw new HttpRequestException("The router's answer is too large.");
            }

            return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }

        // A router's XML, read without DTDs or external entities
        static XDocument SafeXml(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = MaxDocument * 2,
            };

            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader);
        }

        // ==================== Bytes ====================

        static int U16(byte[] b, int at) => (b[at] << 8) | b[at + 1];
        static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

        static void PutU16(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 8);
            b[at + 1] = (byte)v;
        }

        static void PutU32(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }
    }
}
