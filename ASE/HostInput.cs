namespace ASE
{
    /// <summary>
    /// Combines physical host controls before they reach the IKBD. Two controls mapped
    /// to the same ST key/switch hold it until BOTH are released (keyboard vs gamepad,
    /// d-pad vs analogue stick, or two gamepad buttons mapped to Fire/Space).
    /// Host ownership is not guest hardware state and does not belong in snapshots.
    /// </summary>
    internal static class HostInput
    {
        public const int GamepadButtons = 1024;
        public const int GamepadAxisX = 2048;
        public const int GamepadAxisY = 2049;
        static readonly Dictionary<int, (byte Key, byte Joy)> Held = new();

        public static void Reset() => Held.Clear();

        public static void Key(int source, byte key, bool pressed) => Set(source, key, 0, pressed);
        public static void Joystick(int source, byte mask, bool pressed) => Set(source, 0, mask, pressed);

        static void Set(int source, byte key, byte joy, bool pressed)
        {
            Held.TryGetValue(source, out var previous);
            // Use the binding made on PRESS even if settings changed in the meantime.
            if (pressed && previous != default) return;
            bool keyWasDown = key != 0 && Held.Values.Any(v => v.Key == key);
            Held.Remove(source);
            if (pressed && (key != 0 || joy != 0)) Held[source] = (key, joy);

            if (previous.Key != 0 && !Held.Values.Any(v => v.Key == previous.Key))
                ACIA.PushIkbd((byte)(previous.Key | 0x80));
            if (pressed && key != 0 && !keyWasDown) ACIA.PushIkbd(key);

            byte combined = 0;
            foreach (var binding in Held.Values) combined |= binding.Joy;
            byte released = (byte)(ACIA.JoystickState & ~combined);
            byte added = (byte)(combined & ~ACIA.JoystickState);
            if (released != 0) ACIA.UpdateJoystick(released, false);
            if (added != 0) ACIA.UpdateJoystick(added, true);
        }

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
