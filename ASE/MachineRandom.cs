namespace ASE
{
    /// <summary>
    /// The one source of randomness inside the emulated machine: the fuzzy bits of an STX
    /// sector and the noise an unformatted track reads as. It is part of the machine state, not
    /// of the host — seeded at every power-on, saved in snapshots (section "RNG ") and restored
    /// with them — so that two runs started from the same snapshot draw the same values.
    /// <para>
    /// That is what makes a machine replayable: the input recorder and, later, online play run
    /// the same snapshot twice (two sessions, or two computers) and expect the same machine out
    /// of the same inputs. <c>Random.Shared</c> answered with different bits on each run, so a
    /// protection reading a fuzzy sector took a different path every time.
    /// </para>
    /// <para>
    /// SplitMix64: one 64-bit word of state, no allocation, and good enough statistics for a
    /// disk that is only meant to read differently on every pass. Emulation thread only.
    /// </para>
    /// </summary>
    public static class MachineRandom
    {
        static ulong _state;

        /// <summary>A fresh seed from the host, at power-on. Nothing about the first boot of a
        /// session needs to be reproducible: a session always starts from a snapshot, which
        /// carries the state this leaves behind.</summary>
        public static void Reseed() => _state = (ulong)Random.Shared.NextInt64();

        public static ulong NextU64()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public static byte NextByte() => (byte)(NextU64() >> 56);

        public static void NextBytes(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = NextByte();
        }

        public static void SaveState(Snapshot.Writer w) => w.I64((long)_state);

        public static void LoadState(Snapshot.Reader r) => _state = (ulong)r.I64();
    }
}
