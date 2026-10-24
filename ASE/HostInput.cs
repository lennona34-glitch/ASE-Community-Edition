using static ASE.Config;

namespace ASE
{
    /// <summary>
    /// Combines physical host controls before they reach the IKBD. Two controls mapped
    /// to the same ST key/switch hold it until BOTH are released (keyboard vs gamepad,
    /// d-pad vs analogue stick, or two gamepad buttons mapped to Fire/Space).
    /// It also routes the joystick bits to the ports: the keyboard joystick and the gamepad
    /// each drive whichever ports <c>Config.Port0Device</c>/<c>Port1Device</c> plug them into.
    /// Host ownership is not guest hardware state and does not belong in snapshots.
    /// </summary>
    internal static class HostInput
    {
        public const int GamepadButtons = 1024;
        public const int GamepadAxisX = 2048;
        public const int GamepadAxisY = 2049;
        static readonly Dictionary<int, (byte Key, byte Joy)> Held = new();

        // The port configuration as last applied (ApplyPortConfiguration). Unapplied after a
        // reset, so the first drain of the new session applies whatever the config says.
        const ConfigOptions.PortDevices Unapplied = (ConfigOptions.PortDevices)(-1);
        static ConfigOptions.PortDevices _port0 = Unapplied, _port1 = Unapplied;

        public static void Reset()
        {
            Held.Clear();
            _port0 = _port1 = Unapplied;

            // What is forgotten here is never released: the input boundary forgets it too
            Netplay.MachineInput.ForgetHeld();
        }

        /// <summary>
        /// Forgets which port configuration was applied, so the next event drain applies it again.
        /// For the end of a recording or a playback: what is plugged into the ports went into the
        /// session meanwhile (or, in a playback, nowhere), and the live machine has to get it back.
        /// Unlike <see cref="Reset"/> it keeps the controls held on the host.
        /// </summary>
        public static void InvalidatePortConfiguration() => _port0 = _port1 = Unapplied;

        /// <summary>
        /// Whether a port takes the keyboard joystick. Only then are its keys kept from the ST;
        /// otherwise they reach it as the ordinary keys they are (the keypad, by default).
        /// </summary>
        public static bool KeyboardDrivesJoystick => TakesKeys(_port0) || TakesKeys(_port1);

        static bool TakesKeys(ConfigOptions.PortDevices device) =>
            device == ConfigOptions.PortDevices.KeysAndGamepad || device == ConfigOptions.PortDevices.KeysOnly;

        /// <summary>
        /// Plugs into the ports what the configuration says, when it changed since the last
        /// call. Runs on the emulation thread before every event drain, so a change made in the
        /// Configuration window lands there and not on the UI thread that made it.
        /// </summary>
        public static void ApplyPortConfiguration()
        {
            var config = ConfigOptions.RunninConfig;
            if (config.Port0Device == _port0 && config.Port1Device == _port1) return;

            _port0 = config.Port0Device;
            _port1 = config.Port1Device;

            // The IKBD decodes a mouse on port 0 only; one configured on port 1 is plugged
            // into nothing and that port reads empty (see PortState).
            Netplay.MachineInput.ConnectMouse(_port0 == ConfigOptions.PortDevices.Mouse);
            Route();
        }

        public static void Key(int source, byte key, bool pressed)
        {
            if (pressed) Services.AtariExecutableLauncher.CancelDesktopAutoClick();
            Set(source, key, 0, pressed);
        }

        public static void Joystick(int source, byte mask, bool pressed)
        {
            if (pressed) Services.AtariExecutableLauncher.CancelDesktopAutoClick();
            Set(source, 0, mask, pressed);
        }

        static void Set(int source, byte key, byte joy, bool pressed)
        {
            Held.TryGetValue(source, out var previous);
            // Use the binding made on PRESS even if settings changed in the meantime.
            if (pressed && previous != default) return;
            bool keyWasDown = key != 0 && Held.Values.Any(v => v.Key == key);
            Held.Remove(source);
            if (pressed && (key != 0 || joy != 0)) Held[source] = (key, joy);

            if (previous.Key != 0 && !Held.Values.Any(v => v.Key == previous.Key))
                Netplay.MachineInput.Key((byte)(previous.Key | 0x80));
            if (pressed && key != 0 && !keyWasDown) Netplay.MachineInput.Key(key);

            Route();
        }

        /// <summary>Hands each port the joystick bits of the devices plugged into it.</summary>
        static void Route()
        {
            byte keys = 0, pad = 0;
            foreach (var (source, binding) in Held)
            {
                if (source < GamepadButtons) keys |= binding.Joy;
                else pad |= binding.Joy;
            }

            Netplay.MachineInput.Joysticks(PortState(_port0, keys, pad), PortState(_port1, keys, pad));
        }

        static byte PortState(ConfigOptions.PortDevices device, byte keys, byte pad) => device switch
        {
            ConfigOptions.PortDevices.KeysAndGamepad => (byte)(keys | pad),
            ConfigOptions.PortDevices.KeysOnly => keys,
            ConfigOptions.PortDevices.GamepadOnly => pad,
            _ => 0      // empty, or the mouse
        };

        public static void Axis(int source, byte mask)
        {
            // Axis events replace a direction, unlike a held button's repeated make.
            if (Held.TryGetValue(source, out var old) && old.Joy == mask) return;
            Held.Remove(source);
            Set(source, 0, mask, mask != 0);
        }

        public static void ReleaseGamepad()
        {
            foreach (int source in Held.Keys.Where(k => k >= GamepadButtons).ToArray())
                Set(source, 0, 0, false);
        }

        public static void ReleaseAll()
        {
            foreach (int source in Held.Keys.ToArray()) Set(source, 0, 0, false);
        }
    }
}
