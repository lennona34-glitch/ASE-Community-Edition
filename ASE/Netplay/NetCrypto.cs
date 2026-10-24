/*
 *
 * The cryptography of online play (NETPLAY.md §7.7): an ephemeral ECDH key exchange per
 * connection, and the AES-256-GCM protection of every packet that follows it.
 *
 * Both are built into .NET on Windows, Linux and macOS — nothing to ship, nothing to pay for.
 * Each direction has its own key and IV, so the packet number alone keeps nonces unique: it
 * only grows, and a key never encrypts for the other side. A 64-packet window throws away
 * replays, and it only moves once a packet has authenticated, so forged numbers cannot push
 * it forward.
 *
 * Without a code the exchange is not authenticated (a machine in the middle of the network
 * could stand in for either side), and the host accepting the guest by name is the check. A
 * connection made with an invitation code (NetCodes) has the code's secret in the salt, which
 * closes that hole: whoever does not have the secret derives keys that open nothing.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ASE.Netplay
{
    /// <summary>One side's ephemeral P-256 key pair, used for a single connection.</summary>
    internal sealed class KeyExchange : IDisposable
    {
        readonly ECDiffieHellman _ecdh;

        /// <summary>The public key as sent on the wire: 0x04 || X || Y.</summary>
        public byte[] PublicKey { get; }

        public KeyExchange()
        {
            _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            var q = _ecdh.ExportParameters(false).Q;
            PublicKey = new byte[NetWire.PublicKeySize];
            PublicKey[0] = 0x04;
            q.X.CopyTo(PublicKey, 1);
            q.Y.CopyTo(PublicKey, 33);
        }

        /// <summary>
        /// The shared secret with the other side's public key, or null when that key is not a
        /// point of the curve (the import validates it on every platform) — a malformed Hello,
        /// which is dropped.
        /// </summary>
        public byte[] Agree(ReadOnlySpan<byte> theirs)
        {
            if (theirs.Length != NetWire.PublicKeySize || theirs[0] != 0x04)
                return null;

            try
            {
                var parameters = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = theirs.Slice(1, 32).ToArray(), Y = theirs.Slice(33, 32).ToArray() },
                };

                using var other = ECDiffieHellman.Create(parameters);
                return _ecdh.DeriveRawSecretAgreement(other.PublicKey);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or ArgumentException)
            {
                // Windows reports a point off the curve as an unsupported curve
                return null;
            }
        }

        public void Dispose() => _ecdh.Dispose();
    }

    /// <summary>
    /// The protection of a connection's packets once both sides share a secret: sealing with
    /// this side's key and the next packet number, opening with the other side's key and the
    /// replay window. Used by the network thread only.
    /// </summary>
    internal sealed class PacketProtection : IDisposable
    {
        const int KeySize = 32;
        const int IvSize = 12;

        static readonly byte[] Label = Encoding.ASCII.GetBytes("ASE netplay 1");

        readonly AesGcm _sendAead;
        readonly AesGcm _recvAead;
        readonly byte[] _sendIv;
        readonly byte[] _recvIv;

        ulong _nextPacket = 1;

        /// <summary>The number the next sealed packet gets.</summary>
        public ulong NextPacketNumber => _nextPacket;

        // Replay window: the highest packet number that authenticated, and which of the 64
        // below it did too (bit n = highest - n).
        ulong _highest;
        ulong _seen;

        PacketProtection(byte[] sendKey, byte[] sendIv, byte[] recvKey, byte[] recvIv)
        {
            _sendAead = new AesGcm(sendKey, NetWire.TagSize);
            _recvAead = new AesGcm(recvKey, NetWire.TagSize);
            _sendIv = sendIv;
            _recvIv = recvIv;
        }

        /// <summary>
        /// Derives both directions' keys: HKDF-SHA256 over the ECDH secret, salted with both
        /// nonces and, for a connection made with an invitation code, the code's secret, bound
        /// to both public keys.
        /// </summary>
        public static PacketProtection Create(byte[] secret, ReadOnlySpan<byte> guestNonce, ReadOnlySpan<byte> hostNonce,
                                              ReadOnlySpan<byte> guestPublic, ReadOnlySpan<byte> hostPublic, bool isHost,
                                              ReadOnlySpan<byte> codeSecret = default)
        {
            byte[] salt = [.. guestNonce, .. hostNonce, .. codeSecret];
            byte[] info = [.. Label, .. guestPublic, .. hostPublic];

            byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 2 * (KeySize + IvSize), salt, info);

            // Guest to host first, then host to guest
            byte[] g2hKey = okm[..KeySize];
            byte[] g2hIv = okm[KeySize..(KeySize + IvSize)];
            byte[] h2gKey = okm[(KeySize + IvSize)..(2 * KeySize + IvSize)];
            byte[] h2gIv = okm[(2 * KeySize + IvSize)..];

            CryptographicOperations.ZeroMemory(okm);

            return isHost
                ? new PacketProtection(h2gKey, h2gIv, g2hKey, g2hIv)
                : new PacketProtection(g2hKey, g2hIv, h2gKey, h2gIv);
        }

        /// <summary>
        /// Builds a protected datagram into <paramref name="datagram"/> (at least
        /// <see cref="NetWire.MaxDatagram"/> bytes) and returns its length.
        /// </summary>
        public int Seal(uint receiverId, ReadOnlySpan<byte> plaintext, Span<byte> datagram)
        {
            ulong number = _nextPacket++;

            var header = datagram[..NetWire.ProtectedHeader];
            header[0] = NetWire.Protected;
            BinaryPrimitives.WriteUInt32BigEndian(header[1..], receiverId);
            BinaryPrimitives.WriteUInt64BigEndian(header[5..], number);

            Span<byte> nonce = stackalloc byte[IvSize];
            MakeNonce(_sendIv, number, nonce);

            var ciphertext = datagram.Slice(NetWire.ProtectedHeader, plaintext.Length);
            var tag = datagram.Slice(NetWire.ProtectedHeader + plaintext.Length, NetWire.TagSize);
            _sendAead.Encrypt(nonce, plaintext, ciphertext, tag, header);

            return NetWire.ProtectedHeader + plaintext.Length + NetWire.TagSize;
        }

        /// <summary>
        /// Authenticates and decrypts a protected datagram into <paramref name="plaintext"/>.
        /// False for anything forged, corrupted or already seen; the window only moves on
        /// success. <paramref name="number"/> is the packet's number.
        /// </summary>
        public bool TryOpen(ReadOnlySpan<byte> datagram, Span<byte> plaintext, out int length, out ulong number)
        {
            length = 0;
            number = 0;

            int bodyLength = datagram.Length - NetWire.ProtectedHeader - NetWire.TagSize;
            if (bodyLength < 0 || bodyLength > plaintext.Length)
                return false;

            number = BinaryPrimitives.ReadUInt64BigEndian(datagram[5..]);
            if (number == 0 || IsReplay(number))
                return false;

            Span<byte> nonce = stackalloc byte[IvSize];
            MakeNonce(_recvIv, number, nonce);

            try
            {
                _recvAead.Decrypt(nonce,
                    datagram.Slice(NetWire.ProtectedHeader, bodyLength),
                    datagram.Slice(NetWire.ProtectedHeader + bodyLength, NetWire.TagSize),
                    plaintext[..bodyLength],
                    datagram[..NetWire.ProtectedHeader]);
            }
            catch (CryptographicException)
            {
                return false;
            }

            MarkSeen(number);
            length = bodyLength;
            return true;
        }

        bool IsReplay(ulong number)
        {
            if (number > _highest)
                return false;

            ulong age = _highest - number;
            return age >= 64 || (_seen & (1UL << (int)age)) != 0;
        }

        void MarkSeen(ulong number)
        {
            if (number > _highest)
            {
                ulong shift = number - _highest;
                _seen = shift >= 64 ? 0 : _seen << (int)shift;
                _highest = number;
                _seen |= 1;
            }
            else
            {
                _seen |= 1UL << (int)(_highest - number);
            }
        }

        // The IV with the packet number XORed into its last eight bytes (as TLS 1.3 does)
        static void MakeNonce(byte[] iv, ulong number, Span<byte> nonce)
        {
            iv.CopyTo(nonce);
            ulong tail = BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]) ^ number;
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], tail);
        }

        public void Dispose()
        {
            _sendAead.Dispose();
            _recvAead.Dispose();
        }
    }

    /// <summary>
    /// The host's stateless cookies (as in DTLS): proof that a guest receives at the address it
    /// claims, checked before the host keeps any state or does any key exchange for it.
    /// </summary>
    internal sealed class CookieJar
    {
        const long WindowMs = 30_000;

        readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

        public byte[] Make(System.Net.IPEndPoint from, long nowMs) => Compute(from, nowMs / WindowMs);

        /// <summary>Valid for the window it was made in and the one after.</summary>
        public bool Check(System.Net.IPEndPoint from, ReadOnlySpan<byte> cookie, long nowMs)
        {
            if (cookie.Length != NetWire.CookieSize)
                return false;

            long window = nowMs / WindowMs;
            return CryptographicOperations.FixedTimeEquals(cookie, Compute(from, window))
                || CryptographicOperations.FixedTimeEquals(cookie, Compute(from, window - 1));
        }

        byte[] Compute(System.Net.IPEndPoint from, long window)
        {
            byte[] address = from.Address.GetAddressBytes();
            byte[] data = new byte[address.Length + 2 + 8];
            address.CopyTo(data, 0);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(address.Length), (ushort)from.Port);
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(address.Length + 2), window);

            return HMACSHA256.HashData(_secret, data)[..NetWire.CookieSize];
        }
    }
}
