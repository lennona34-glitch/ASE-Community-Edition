/*
 *
 * Joining over the Internet without any server of ours (NETPLAY.md §7.4, phase 5): the codes two
 * players paste to each other, and the STUN messages that tell a computer its public address.
 * Nothing here touches a socket or the emulator, which is what lets tools/NetTest check it.
 *
 * The invitation code carries everything a guest needs: where the host can be reached (its
 * address on the Internet as STUN saw it, on its local network, and IPv6), what kind of NAT it
 * is behind, and a 128-bit secret. The secret goes into the key derivation of a connection made
 * with the code (NetCrypto), so a machine in the middle of the network cannot stand in for
 * either side: without the secret its keys simply do not open the packets.
 *
 * When the host cannot be reached directly (its router lets nothing in that it has not sent out
 * first), the guest answers with a code of its own — its addresses — which the host pastes:
 * the host then sends a few tiny packets to the guest, which opens its router to the guest's
 * Hellos (hole punching).
 *
 * Codes are Crockford base32 (no I, L, O or U, case-insensitive, and the look-alikes read as
 * the digits they look like) in groups of four, with a checksum that catches a typo.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ASE.Netplay
{
    // ==================== NAT ====================

    /// <summary>What sits between a computer and the Internet, as far as STUN can tell.</summary>
    public enum NatKind : byte
    {
        /// <summary>STUN did not answer, or answered once: nothing to compare.</summary>
        Unknown = 0,

        /// <summary>No NAT: the address STUN sees is one of this computer's own.</summary>
        Open = 1,

        /// <summary>The router keeps one public port for the socket whoever it talks to: hole
        /// punching works.</summary>
        Easy = 2,

        /// <summary>A different public port for every destination (symmetric NAT): the port
        /// STUN saw is not the one another player would reach.</summary>
        Symmetric = 3,

        /// <summary>Whatever the NAT, the router forwards the game's port (UPnP, NAT-PMP or PCP):
        /// anybody gets in. Only in codes; STUN cannot tell.</summary>
        PortOpen = 4,
    }

    public static class Nat
    {
        /// <summary>
        /// Classifies what two or more STUN servers answered for the same socket: the address
        /// each one saw. A computer whose own addresses include the public one has no NAT; the
        /// same public port for different servers is an endpoint-independent mapping; different
        /// ports, a symmetric NAT.
        /// </summary>
        public static NatKind Classify(IReadOnlyList<IPAddress> ownAddresses, IReadOnlyList<IPEndPoint> mapped)
        {
            var v4 = mapped.Where(m => m.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (v4.Count == 0)
                return NatKind.Unknown;

            if (ownAddresses.Any(a => a.Equals(v4[0].Address)))
                return NatKind.Open;

            if (v4.Count < 2)
                return NatKind.Unknown;

            return v4.All(m => m.Port == v4[0].Port && m.Address.Equals(v4[0].Address)) ? NatKind.Easy : NatKind.Symmetric;
        }

        public static string Describe(NatKind kind) => kind switch
        {
            NatKind.Open => "no NAT: this computer is on the Internet directly",
            NatKind.Easy => "a NAT that keeps one public port for everybody, the easy kind",
            NatKind.Symmetric => "a symmetric NAT, the hard kind: a direct connection may not be possible",
            NatKind.PortOpen => "a router that opened the game's port",
            _ => "unknown (the STUN servers did not answer)",
        };
    }

    // ==================== The codes ====================

    /// <summary>The host's code: where it can be reached, its NAT, and the session's secret.</summary>
    public sealed class InviteCode
    {
        public const int SecretSize = 16;
        const byte Kind = 0x11;

        public byte[] Secret { get; }
        public NatKind Nat { get; }
        public IReadOnlyList<IPEndPoint> Candidates { get; }

        public InviteCode(byte[] secret, NatKind nat, IReadOnlyList<IPEndPoint> candidates)
        {
            if (secret == null || secret.Length != SecretSize)
                throw new ArgumentException("A code's secret is 16 bytes.");

            Secret = secret;
            Nat = nat;
            Candidates = candidates;
        }

        /// <summary>A fresh code with a random secret.</summary>
        public static InviteCode Create(NatKind nat, IReadOnlyList<IPEndPoint> candidates) =>
            new(RandomNumberGenerator.GetBytes(SecretSize), nat, candidates);

        /// <summary>Names the code without giving its secret away: what a Hello and an answer
        /// carry to say which code they mean.</summary>
        public byte[] Tag => TagOf(Secret);

        public static byte[] TagOf(byte[] secret) => SHA256.HashData([.. "ASE code tag"u8, .. secret])[..CodeText.TagSize];

        public string ToText()
        {
            var bytes = new List<byte> { Kind, (byte)Nat };
            bytes.AddRange(Secret);
            CodeText.WriteCandidates(bytes, Candidates);
            return CodeText.Encode(bytes);
        }

        /// <summary>Reads a code typed or pasted; null with the reason when it is not one.</summary>
        public static InviteCode Parse(string text, out string error)
        {
            byte[] b = CodeText.Decode(text, out error);
            if (b == null)
                return null;

            if (b[0] == AnswerCode.KindByte)
            {
                error = "That is an answer code: it goes in the host's window, not here.";
                return null;
            }

            if (b[0] != Kind || b.Length < 2 + SecretSize + 1)
            {
                error = "That is not an ASE invitation code (or it comes from a newer version of ASE).";
                return null;
            }

            var nat = (NatKind)b[1];
            byte[] secret = b[2..(2 + SecretSize)];
            var candidates = CodeText.ReadCandidates(b, 2 + SecretSize, out bool ok);

            if (!ok || candidates.Count == 0)
            {
                error = "The code is damaged: check that it was copied whole.";
                return null;
            }

            return new InviteCode(secret, Enum.IsDefined(nat) ? nat : NatKind.Unknown, candidates);
        }
    }

    /// <summary>The guest's answer: which invitation it answers, and where it can be reached.</summary>
    public sealed class AnswerCode
    {
        internal const byte KindByte = 0x21;

        public byte[] Tag { get; }
        public IReadOnlyList<IPEndPoint> Candidates { get; }

        public AnswerCode(byte[] tag, IReadOnlyList<IPEndPoint> candidates)
        {
            Tag = tag;
            Candidates = candidates;
        }

        public string ToText()
        {
            var bytes = new List<byte> { KindByte };
            bytes.AddRange(Tag);
            CodeText.WriteCandidates(bytes, Candidates);
            return CodeText.Encode(bytes);
        }

        public static AnswerCode Parse(string text, out string error)
        {
            byte[] b = CodeText.Decode(text, out error);
            if (b == null)
                return null;

            if (b[0] != KindByte || b.Length < 1 + CodeText.TagSize + 1)
            {
                error = b[0] == 0x11
                    ? "That is your own invitation code: paste here the answer code the other player sends back."
                    : "That is not an ASE answer code.";
                return null;
            }

            byte[] tag = b[1..(1 + CodeText.TagSize)];
            var candidates = CodeText.ReadCandidates(b, 1 + CodeText.TagSize, out bool ok);

            if (!ok || candidates.Count == 0)
            {
                error = "The code is damaged: check that it was copied whole.";
                return null;
            }

            return new AnswerCode(tag, candidates);
        }
    }

    /// <summary>The text form of the codes: payload, two checksum bytes, Crockford base32.</summary>
    public static class CodeText
    {
        public const int TagSize = 4;
        const int CheckSize = 2;
        const int MaxCandidates = 8;
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        internal static void WriteCandidates(List<byte> bytes, IReadOnlyList<IPEndPoint> candidates)
        {
            var list = candidates.Take(MaxCandidates).ToList();
            bytes.Add((byte)list.Count);

            foreach (var c in list)
            {
                bool v6 = c.AddressFamily == AddressFamily.InterNetworkV6;
                bytes.Add(v6 ? (byte)6 : (byte)4);
                bytes.AddRange(c.Address.GetAddressBytes());
                bytes.Add((byte)(c.Port >> 8));
                bytes.Add((byte)c.Port);
            }
        }

        internal static List<IPEndPoint> ReadCandidates(byte[] b, int at, out bool ok)
        {
            var list = new List<IPEndPoint>();
            ok = false;

            if (at >= b.Length)
                return list;

            int count = b[at++];
            if (count > MaxCandidates)
                return list;

            for (int i = 0; i < count; i++)
            {
                if (at >= b.Length)
                    return list;

                int size = b[at++] switch { 4 => 4, 6 => 16, _ => -1 };
                if (size < 0 || at + size + 2 > b.Length)
                    return list;

                var address = new IPAddress(b.AsSpan(at, size));
                int port = (b[at + size] << 8) | b[at + size + 1];
                at += size + 2;

                if (port == 0)
                    return list;

                list.Add(new IPEndPoint(address, port));
            }

            ok = at == b.Length;
            return list;
        }

        /// <summary>Payload + checksum, in base32, in groups of four.</summary>
        internal static string Encode(List<byte> payload)
        {
            byte[] check = SHA256.HashData(payload.ToArray());
            byte[] all = [.. payload, .. check[..CheckSize]];

            var sb = new StringBuilder();
            int buffer = 0, bits = 0, written = 0;

            void Put(int value)
            {
                if (written > 0 && written % 4 == 0)
                    sb.Append('-');
                sb.Append(Alphabet[value]);
                written++;
            }

            foreach (byte x in all)
            {
                buffer = ((buffer << 8) | x) & 0xFFFF;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    Put((buffer >> bits) & 31);
                }
            }

            if (bits > 0)
                Put((buffer << (5 - bits)) & 31);

            return sb.ToString();
        }

        /// <summary>The payload of a code (checksum verified, removed), or null with the reason.</summary>
        internal static byte[] Decode(string text, out string error)
        {
            error = null;
            var bytes = new List<byte>();
            int buffer = 0, bits = 0, symbols = 0;

            foreach (char raw in text ?? "")
            {
                if (raw is '-' || char.IsWhiteSpace(raw))
                    continue;

                char c = char.ToUpperInvariant(raw) switch { 'O' => '0', 'I' or 'L' => '1', var u => u };
                int value = Alphabet.IndexOf(c);

                if (value < 0)
                {
                    error = $"'{raw}' cannot be part of a code: check that it was copied whole, and only it.";
                    return null;
                }

                buffer = ((buffer << 5) | value) & 0xFFF;
                bits += 5;
                symbols++;

                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)(buffer >> bits));
                }
            }

            if (symbols == 0)
            {
                error = "Paste the code first.";
                return null;
            }

            if (bytes.Count < 1 + CheckSize || bits >= 5 || (buffer & ((1 << bits) - 1)) != 0)
            {
                error = "The code is incomplete: check that it was copied whole.";
                return null;
            }

            byte[] payload = bytes.Take(bytes.Count - CheckSize).ToArray();
            byte[] check = SHA256.HashData(payload);

            if (check[0] != bytes[^2] || check[1] != bytes[^1])
            {
                error = "The code has a mistake in it: copy and paste it rather than typing it, if you can.";
                return null;
            }

            return payload;
        }
    }

    // ==================== STUN (RFC 5389) ====================

    /// <summary>
    /// The one STUN exchange online play needs: a Binding Request, and the address its answer
    /// says the request came from. About a hundred lines, no dependency (NETPLAY.md §7.3).
    /// </summary>
    public static class Stun
    {
        public const int DefaultPort = 3478;
        const uint MagicCookie = 0x2112A442;
        static readonly byte[] CookieBytes = [0x21, 0x12, 0xA4, 0x42];
        const ushort BindingRequestType = 0x0001;
        const ushort BindingSuccessType = 0x0101;
        const ushort AttrMappedAddress = 0x0001;
        const ushort AttrXorMappedAddress = 0x0020;
        public const int TransactionIdSize = 12;

        /// <summary>The public STUN servers asked, in this order (NETPLAY.md §2): two, at
        /// different addresses, so the NAT can be classified.</summary>
        public static readonly (string Host, int Port)[] Servers =
        [
            ("stun.cloudflare.com", 3478),
            ("stun.l.google.com", 19302),
        ];

        public static byte[] BindingRequest(byte[] transactionId)
        {
            var packet = new byte[20];
            packet[0] = BindingRequestType >> 8;
            packet[1] = BindingRequestType & 0xFF;
            // length 0: no attributes
            CookieBytes.CopyTo(packet, 4);
            transactionId.CopyTo(packet, 8);
            return packet;
        }

        /// <summary>Whether a datagram is STUN rather than ASE's own: the first two bits clear and
        /// the magic cookie in place, which no ASE packet has.</summary>
        public static bool IsStun(ReadOnlySpan<byte> data) =>
            data.Length >= 20 && (data[0] & 0xC0) == 0 && data.Slice(4, 4).SequenceEqual(CookieBytes);

        /// <summary>
        /// Reads a Binding Success Response: its transaction and the address it reports
        /// (XOR-MAPPED-ADDRESS, or the old MAPPED-ADDRESS). False for anything else.
        /// </summary>
        public static bool TryParseResponse(ReadOnlySpan<byte> data, out byte[] transactionId, out IPEndPoint mapped)
        {
            transactionId = null;
            mapped = null;

            if (!IsStun(data))
                return false;

            int type = (data[0] << 8) | data[1];
            int length = (data[2] << 8) | data[3];

            if (type != BindingSuccessType || 20 + length > data.Length || (length & 3) != 0)
                return false;

            transactionId = data.Slice(8, TransactionIdSize).ToArray();
            IPEndPoint plain = null;

            for (int at = 20; at + 4 <= 20 + length;)
            {
                int attr = (data[at] << 8) | data[at + 1];
                int size = (data[at + 2] << 8) | data[at + 3];

                if (at + 4 + size > 20 + length)
                    return false;

                var value = data.Slice(at + 4, size);

                if (attr == AttrXorMappedAddress && TryReadAddress(value, xor: true, data[4..20], out var x))
                {
                    mapped = x;
                    return true;
                }

                if (attr == AttrMappedAddress && TryReadAddress(value, xor: false, data[4..20], out var p))
                    plain = p;

                at += 4 + ((size + 3) & ~3);
            }

            mapped = plain;
            return mapped != null;
        }

        static bool TryReadAddress(ReadOnlySpan<byte> v, bool xor, ReadOnlySpan<byte> cookieAndId, out IPEndPoint address)
        {
            address = null;

            if (v.Length < 8)
                return false;

            int family = v[1];
            int port = (v[2] << 8) | v[3];
            int size = family switch { 1 => 4, 2 => 16, _ => 0 };

            if (size == 0 || v.Length < 4 + size)
                return false;

            byte[] a = v.Slice(4, size).ToArray();

            if (xor)
            {
                port ^= (int)(MagicCookie >> 16);
                for (int i = 0; i < size; i++)
                    a[i] ^= cookieAndId[i];
            }

            address = new IPEndPoint(new IPAddress(a), port);
            return port != 0;
        }

        /// <summary>A Binding Success Response reporting <paramref name="mapped"/>, as a server
        /// would send it. For tests, and nothing else.</summary>
        public static byte[] BindingResponse(byte[] transactionId, IPEndPoint mapped)
        {
            bool v6 = mapped.AddressFamily == AddressFamily.InterNetworkV6;
            int size = v6 ? 16 : 4;
            var packet = new byte[20 + 4 + 4 + size];

            packet[0] = BindingSuccessType >> 8;
            packet[1] = BindingSuccessType & 0xFF;
            packet[2] = 0;
            packet[3] = (byte)(4 + 4 + size);
            BindingRequest(transactionId).AsSpan(4, 16).CopyTo(packet.AsSpan(4));

            packet[20] = AttrXorMappedAddress >> 8;
            packet[21] = AttrXorMappedAddress & 0xFF;
            packet[23] = (byte)(4 + size);
            packet[25] = v6 ? (byte)2 : (byte)1;

            int port = mapped.Port ^ (int)(MagicCookie >> 16);
            packet[26] = (byte)(port >> 8);
            packet[27] = (byte)port;

            byte[] a = mapped.Address.GetAddressBytes();
            for (int i = 0; i < size; i++)
                packet[28 + i] = (byte)(a[i] ^ packet[4 + i]);

            return packet;
        }
    }
}
