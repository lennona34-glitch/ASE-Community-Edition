using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ASE.Netplay
{
    /// <summary>
    /// A fingerprint of the emulated machine: two runs of a session compare it at checkpoints to
    /// prove they are still the same machine, and the first checkpoint that differs says when
    /// they parted. Covers what a snapshot covers — CPU registers and clock, physical RAM, the
    /// hardware port latches and every chip's saved state — read through the same SaveState
    /// methods, so it can never drift from what a snapshot would restore.
    /// <para>
    /// It must not change the machine, which is why it does not simply take a snapshot:
    /// <see cref="Snapshot"/> completes an STX command in flight before saving, and that would
    /// alter the run it is measuring. For the same reason the CPU's inactive stack pointer is
    /// left out — reading it means toggling the S bit — and a difference there shows up in RAM a
    /// few instructions later anyway.
    /// </para>
    /// SHA-256, truncated to 64 bits: built into .NET, a couple of milliseconds for 4 MB of RAM,
    /// and it is computed once a second. Emulation thread, at a frame boundary.
    /// </summary>
    internal static class MachineHash
    {
        public static ulong Compute()
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var w = new Snapshot.Writer(new HashSink(sha));

            var cpu = CPU._moira;
            for (int i = 0; i < 8; i++) w.U32(cpu.D[i]);
            for (int i = 0; i < 8; i++) w.U32(cpu.A[i]);
            w.U32(cpu.PC);
            w.U32(cpu.PC0);
            w.U16(cpu.SR);
            w.U16(cpu.IRC);
            w.U16(cpu.IRD);
            w.U8(cpu.IPL);
            w.I64(cpu.Clock);

            var mem = ASEMain._mem;
            sha.AppendData(mem.RAM);
            sha.AppendData(mem.Ports);

            ASEMain._mfp.SaveState(w);
            ASEMain._ym.SaveState(w);
            ACIA.SaveState(w);
            WD1772.SaveState(w);
            STEDmaSound.SaveState(w);
            Blitter.SaveState(w);
            MidiAcia.SaveState(w);
            MfpUsart.SaveState(w);
            MachineRandom.SaveState(w);

            Span<byte> digest = stackalloc byte[32];
            sha.GetHashAndReset(digest);
            return BinaryPrimitives.ReadUInt64BigEndian(digest);
        }

        /// <summary>A write-only stream that feeds a hash, so the chips' SaveState can write into it.</summary>
        sealed class HashSink : Stream
        {
            readonly IncrementalHash _hash;
            public HashSink(IncrementalHash hash) { _hash = hash; }

            public override void Write(byte[] buffer, int offset, int count) => _hash.AppendData(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _hash.AppendData(buffer);
            public override void WriteByte(byte value) => _hash.AppendData(new ReadOnlySpan<byte>(in value));

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
