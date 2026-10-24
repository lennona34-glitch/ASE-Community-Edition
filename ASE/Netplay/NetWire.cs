/*
 *
 * Wire format of the online-play protocol (NETPLAY.md §7.7): the constants every packet is
 * built from, and the big-endian reader and writer that build and parse them.
 *
 * The reader never throws. Anything can arrive on a UDP port, and a malformed datagram is
 * the normal case under a flood rather than an exceptional one: reading past the end just
 * clears Ok and returns zeros, and the caller drops the packet once it has looked at Ok.
 *
 * Nothing in the network code depends on Avalonia or on ASE's configuration, so it can be
 * built and exercised in a console program of its own.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using System.Buffers.Binary;
using System.Text;

namespace ASE.Netplay
{
    internal static class NetWire
    {
        /// <summary>Largest datagram ever sent: IPv6's minimum MTU (1280) less its headers,
        /// as QUIC does, so nothing depends on IP fragmentation.</summary>
        public const int MaxDatagram = 1200;

        public const byte ProtocolVersion = 1;

        public static ReadOnlySpan<byte> Magic => "ASEN"u8;

        // ---- Clear packets: "ASEN", protocol version, type ----
        public const int ClearHeader = 6;
        public const byte Query = 1;
        public const byte Announce = 2;
        public const byte Hello = 3;
        public const byte Retry = 4;
        public const byte Welcome = 5;
        public const byte Refuse = 6;

        /// <summary>Hole punching (NETPLAY.md §7.4): a host sends a few of these to the addresses
        /// in a guest's answer code, which opens its own router to the guest's Hellos. They carry
        /// the code's tag and nothing else; the only answer is a guest joining with that code
        /// sending its Hello to the Punch's source, and only when that is one of the code's
        /// addresses with another port (a host whose NAT changes ports per destination).</summary>
        public const byte Punch = 7;
        public const int PunchSize = ClearHeader + 8;

        // Hello flags, after the cookie (a Hello is zero-padded, so an older reader sees none)
        public const byte HelloWithCode = 1;     // followed by the code's tag: the keys include its secret

        /// <summary>A Query is padded to this, and an Announce never exceeds it: an answer is
        /// never bigger than the question, so a forged source gains nothing.</summary>
        public const int QuerySize = 512;

        public const int NonceSize = 16;
        public const int QueryNonceSize = 8;
        public const int PublicKeySize = 65;      // P-256, uncompressed: 0x04 || X || Y
        public const int CookieSize = 16;

        // Why a Refuse was sent
        public const byte RefuseVersion = 1;
        public const byte RefuseNotAccepting = 2;
        public const byte RefuseFull = 3;
        public const byte RefuseCode = 4;        // a Hello made with a code this host no longer has

        // ---- Protected packets: 0x10, receiver's connection id, packet number, AEAD payload ----
        public const byte Protected = 0x10;
        public const int ProtectedHeader = 1 + 4 + 8;
        public const int TagSize = 16;
        public const int MaxPlaintext = MaxDatagram - ProtectedHeader - TagSize;

        // ---- Frames inside the payload ----
        public const byte FramePing = 1;          // u64 sender's clock
        public const byte FramePong = 2;          // u64 the clock the ping carried
        public const byte FrameAck = 3;           // u32 next expected segment, 2 x u64 SACK, u64 newest packet with segments
        public const byte FrameSegment = 4;       // u32 seq, u8 flags, u16 length, data
        public const byte FrameDatagram = 5;      // u8 channel, u16 length, data
        public const byte FrameClose = 6;         // u8 reason, string

        public const int AckFrameSize = 1 + 4 + 16 + 8;
        public const int PingFrameSize = 1 + 8;
        public const int SegmentFrameHeader = 1 + 4 + 1 + 2;
        public const int DatagramFrameHeader = 1 + 1 + 2;

        public const byte SegmentFirst = 1;       // first piece of a message
        public const byte SegmentLast = 2;        // last piece of a message

        /// <summary>Segment payload: leaves room in a packet for an ACK, a ping and a pong.</summary>
        public const int MaxSegmentData = 1100;
        public const int MaxDatagramData = 1100;

        /// <summary>Largest reliable message: a session snapshot with its disks is 1-2 MB, a
        /// library hard-disk game a little more.</summary>
        public const int MaxMessage = 16 * 1024 * 1024;

        // ---- Reliable messages the transport handles itself; the rest go to the session ----
        public const byte MsgJoin = 1;            // guest -> host: name, ASE version
        public const byte MsgAccept = 2;          // host -> guest: name, ASE version, game
        public const byte MsgReject = 3;          // host -> guest: reason

        /// <summary>First message type that belongs to the session (MIDI cable, lockstep…).</summary>
        public const byte FirstSessionMessage = 0x40;

        // Why a connection was closed (FrameClose)
        public const byte CloseNormal = 0;
        public const byte CloseRejected = 1;
        public const byte CloseHostEnded = 2;
        public const byte CloseProtocolError = 3;

        /// <summary>Longest name, version or game title on the wire, in UTF-8 bytes.</summary>
        public const int MaxString = 64;
    }

    /// <summary>Big-endian writer over a buffer the caller sized. Writing past the end throws:
    /// every packet is built from known sizes, so that is a bug, not input.</summary>
    internal ref struct WireWriter
    {
        readonly Span<byte> _buf;
        public int Position;

        public WireWriter(Span<byte> buffer)
        {
            _buf = buffer;
            Position = 0;
        }

        public readonly Span<byte> Written => _buf[..Position];
        public readonly int Remaining => _buf.Length - Position;

        public void U8(byte v) => _buf[Position++] = v;

        public void U16(ushort v)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_buf[Position..], v);
            Position += 2;
        }

        public void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buf[Position..], v);
            Position += 4;
        }

        public void U64(ulong v)
        {
            BinaryPrimitives.WriteUInt64BigEndian(_buf[Position..], v);
            Position += 8;
        }

        public void Bytes(ReadOnlySpan<byte> v)
        {
            v.CopyTo(_buf[Position..]);
            Position += v.Length;
        }

        /// <summary>A string as u8 length + UTF-8, cut to <see cref="NetWire.MaxString"/> bytes
        /// on a character boundary.</summary>
        public void Str(string v)
        {
            byte[] bytes = Truncated(v);
            U8((byte)bytes.Length);
            Bytes(bytes);
        }

        /// <summary>Zeros up to <paramref name="size"/> bytes in all.</summary>
        public void PadTo(int size)
        {
            if (Position < size)
            {
                _buf[Position..size].Clear();
                Position = size;
            }
        }

        public static int StrSize(string v) => 1 + Truncated(v).Length;

        static byte[] Truncated(string v)
        {
            v ??= "";
            byte[] bytes = Encoding.UTF8.GetBytes(v);

            // Shorten by characters, not bytes, so a multi-byte character is never split
            while (bytes.Length > NetWire.MaxString && v.Length > 0)
            {
                v = v[..^1];
                bytes = Encoding.UTF8.GetBytes(v);
            }

            return bytes;
        }
    }

    /// <summary>Big-endian reader that never throws: a read past the end clears
    /// <see cref="Ok"/> and returns zeros or an empty span.</summary>
    internal ref struct WireReader
    {
        readonly ReadOnlySpan<byte> _buf;
        public int Position;
        public bool Ok;

        public WireReader(ReadOnlySpan<byte> buffer)
        {
            _buf = buffer;
            Position = 0;
            Ok = true;
        }

        public readonly int Remaining => _buf.Length - Position;

        bool Take(int n)
        {
            if (!Ok || n < 0 || Remaining < n)
            {
                Ok = false;
                return false;
            }
            return true;
        }

        public byte U8()
        {
            if (!Take(1)) return 0;
            return _buf[Position++];
        }

        public ushort U16()
        {
            if (!Take(2)) return 0;
            ushort v = BinaryPrimitives.ReadUInt16BigEndian(_buf[Position..]);
            Position += 2;
            return v;
        }

        public uint U32()
        {
            if (!Take(4)) return 0;
            uint v = BinaryPrimitives.ReadUInt32BigEndian(_buf[Position..]);
            Position += 4;
            return v;
        }

        public ulong U64()
        {
            if (!Take(8)) return 0;
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(_buf[Position..]);
            Position += 8;
            return v;
        }

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (!Take(n)) return ReadOnlySpan<byte>.Empty;
            var v = _buf.Slice(Position, n);
            Position += n;
            return v;
        }

        public string Str()
        {
            int n = U8();
            if (n > NetWire.MaxString) { Ok = false; return ""; }
            var bytes = Bytes(n);
            return Ok ? Encoding.UTF8.GetString(bytes) : "";
        }
    }
}
