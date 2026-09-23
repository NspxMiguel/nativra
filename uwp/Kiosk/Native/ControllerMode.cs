using System;

namespace Kiosk.Native
{
    /// <summary>Exclusive desktop mapping or native gamepad input.</summary>
    public static class ControllerMode
    {
        // Preserve the existing desktop mapping during the pre-alpha. Games
        // with native controller support can switch without restarting.
        public static volatile bool Desktop = true;
        public static volatile int SystemButtons;
        public static int Changes;
        private const double HoldSeconds = 0.35;
        private static double held;
        private static int pending;
        private static bool chord;
        private static bool toggled;
        private static double tapRemaining;
        private static int tapButtons;

        public static void Update(bool menu, bool view, double seconds)
        {
            SystemButtons = tapRemaining > 0 ? tapButtons : 0;
            tapRemaining = Math.Max(0, tapRemaining - seconds);
            var buttons = (menu ? 0x10 : 0) | (view ? 0x20 : 0);
            if (buttons != 0)
            {
                SystemButtons = 0;
                tapRemaining = 0;
            }
            if (buttons == 0x30)
            {
                if (!chord) held = 0;
                chord = true;
                pending = 0;
                held += Math.Max(0, seconds);
                if (!toggled && held >= HoldSeconds)
                {
                    Desktop = !Desktop;
                    Changes++;
                    toggled = true;
                }
                return;
            }
            if (chord)
            {
                // Do not leak either half of the shortcut into the game.
                if (buttons == 0)
                {
                    chord = toggled = false;
                    held = 0;
                }
                return;
            }
            if (buttons != 0)
            {
                if (pending != buttons) held = 0;
                pending = buttons;
                held += Math.Max(0, seconds);
                if (held >= HoldSeconds) SystemButtons = buttons;
                return;
            }
            // A short single-button tap is delivered on release. Waiting
            // briefly lets the two-button gesture be consumed as one action.
            if (pending != 0 && held < HoldSeconds)
            {
                SystemButtons = tapButtons = pending;
                tapRemaining = 0.1;
            }
            pending = 0;
            held = 0;
        }
    }
}
