namespace Kiosk.Native
{
    /// <summary>Scan-set-1 metadata for the controller's emulated keyboard keys.</summary>
    internal static class KeyboardMessages
    {
        public static int ScanCode(int key)
        {
            switch (key)
            {
                case 0x57: return 0x11;
                case 0x41: return 0x1E;
                case 0x53: return 0x1F;
                case 0x44: return 0x20;
                case 0x25: return 0x4B;
                case 0x26: return 0x48;
                case 0x27: return 0x4D;
                case 0x28: return 0x50;
                case 0x20: return 0x39;
                case 0x0D: return 0x1C;
                case 0x1B: return 1;
                case 0x08: return 0x0E;
                default: return 0;
            }
        }

        public static bool IsExtended(int key) => key >= 0x25 && key <= 0x28;

        // Only transitions are emitted here, not typematic repeats. Key-up
        // has both previous-state (30) and transition-state (31) bits set.
        // https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-keyup
        public static long TransitionData(int key, bool down) =>
            1L | ((long)ScanCode(key) << 16) | (IsExtended(key) ? 1L << 24 : 0) |
            (down ? 0 : 0xC0000000L);
    }
}
