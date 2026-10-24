/*
 *
 * The settings that change how the machine runs without being machine state (NETPLAY.md §5.1):
 * a snapshot does not carry them, so whatever has to run the same machine elsewhere carries
 * them beside it — an input recording in its header, a shared game in the message that starts
 * each epoch.
 *
 * Official repository 👉 https://github.com/thebitculture/ase
 *
 */

using static ASE.Config;

namespace ASE.Netplay
{
    /// <summary>
    /// What changes how the machine runs without being machine state: the snapshot does not
    /// carry it, so the recording or the shared game does. A playback, or a shared game's guest,
    /// runs with the values that came with the machine and gives the user's back when it ends.
    /// </summary>
    internal sealed class MachineSettings
    {
        public bool CycleExactBus;
        public int BusPhase;
        public int MfpWaitCycles;
        public int CpuSyncSliceCycles;
        public float MouseSensitivity;

        // Whether port 0 held the mouse. Not a setting the playback applies — it goes to
        // MachineInput.Begin — but the snapshot does not have it either.
        public bool MouseConnected;

        public static MachineSettings Capture()
        {
            var c = ConfigOptions.RunninConfig;
            return new MachineSettings
            {
                CycleExactBus = c.CycleExactBus,
                BusPhase = c.BusPhase,
                MfpWaitCycles = c.MfpWaitCycles,
                CpuSyncSliceCycles = c.CpuSyncSliceCycles,
                MouseSensitivity = c.MouseSensitivity,
                MouseConnected = ACIA.MouseConnected,
            };
        }

        public void Apply()
        {
            var c = ConfigOptions.RunninConfig;
            c.CycleExactBus = CycleExactBus;
            c.BusPhase = BusPhase;
            c.MfpWaitCycles = MfpWaitCycles;
            c.CpuSyncSliceCycles = CpuSyncSliceCycles;
            c.MouseSensitivity = MouseSensitivity;
        }

        public void Write(Snapshot.Writer w)
        {
            w.Bool(CycleExactBus);
            w.I32(BusPhase);
            w.I32(MfpWaitCycles);
            w.I32(CpuSyncSliceCycles);
            w.F32(MouseSensitivity);
            w.Bool(MouseConnected);
        }

        public static MachineSettings Read(Snapshot.Reader r) => new MachineSettings
        {
            CycleExactBus = r.Bool(),
            BusPhase = r.I32(),
            MfpWaitCycles = r.I32(),
            CpuSyncSliceCycles = r.I32(),
            MouseSensitivity = r.F32(),
            MouseConnected = r.Bool(),
        };
    }
}
