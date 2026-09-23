using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace Kiosk.Native
{
    /// <summary>
    /// A mouse, driven by a thumbstick.
    ///
    /// Half the PC games ever made expect a pointer somewhere on screen, and a
    /// console has none. Asking each game to grow controller support is exactly
    /// the per-game work this whole project exists to avoid — so instead the
    /// pointer is made real: a position that moves with the right stick, a
    /// button that is the A button, and the same messages Windows would have
    /// sent had a hand been on a mouse.
    ///
    /// A game cannot tell the difference, because there is nothing to tell:
    /// what it reads is what a mouse would have written.
    /// </summary>
    public static class PointerBridge
    {
        private const int Width = 1920;
        private const int Height = 1080;

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;

        /// <summary>Where the pointer is, in screen coordinates.</summary>
        public static int X = Width / 2;
        public static int Y = Height / 2;

        /// <summary>Whether each button is held, in the order Windows numbers them.</summary>
        public static bool Left;
        public static bool Right;

        /// <summary>How far the stick has to lean before it counts as a push.</summary>
        private const double Deadzone = 0.22;

        /// <summary>Screen widths per second at full lean.</summary>
        private const double Speed = 1200.0;

        public static long Moves;

        /// <summary>How many key presses the stick has produced.</summary>
        public static long Keys;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;

        // Which keys a console can produce, and what produces them. A game
        // written for a keyboard has no idea a keyboard is missing: the stick
        // and the buttons send exactly the messages the keys would have.
        private const int VK_BACK = 0x08;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;

        private static readonly int[] Emulated =
        {
            0x57, 0x41, 0x53, 0x44,          // W A S D
            VK_UP, VK_LEFT, VK_DOWN, VK_RIGHT,
            VK_SPACE, VK_RETURN, VK_ESCAPE, VK_BACK,
        };

        private static readonly bool[] held = new bool[256];
        private static volatile bool[] hostKeys = new bool[256];
        private static readonly bool[] emptyHost = new bool[256];
        private static readonly object hostGate = new object();

        /// <summary>Keyboard and portal gamepad events delivered to the host window.</summary>
        public static void HostKey(int key, bool down)
        {
            if (key < 0 || key >= 256) return;
            lock (hostGate)
            {
                if (hostKeys[key] == down) return;
                var next = (bool[])hostKeys.Clone();
                next[key] = down;
                hostKeys = next;
            }
        }

        public static void ReleaseHostKeys()
        {
            lock (hostGate) hostKeys = new bool[256];
        }

        /// <summary>Whether a key is down, for a game that polls instead of reading.</summary>
        public static bool Down(int key) => key >= 0 && key < 256 && held[key];

        private static void Key(int code, bool down)
        {
            if (held[code] == down) return;
            held[code] = down;
            if (down) Keys++;
            RawInputBridge.Keyboard(code, down);
            // lParam carries the repeat count and the scan code; a game that
            // reads only the key itself is the common case, and the rest being
            // zero is what a synthesised key looks like anywhere.
            if (!RawInputBridge.SuppressesLegacy(1)) Post(down ? WM_KEYDOWN : WM_KEYUP, code, 1);
        }

        private sealed class Waiting
        {
            public IntPtr Window;
            public int Message;
            public long Word;
            public long Long;
        }

        private static readonly Queue<Waiting> pending = new Queue<Waiting>();

        internal static void PostRaw(IntPtr window, long handle) => Post(0xFF, 0, handle, window);

        private static void Post(int message, long word, long extra, IntPtr window = default(IntPtr))
        {
            lock (pending)
            {
                // A game that stops reading should not be allowed to grow this
                // without limit; the oldest movement is the least interesting.
                while (pending.Count > 128)
                {
                    var dropped = pending.Dequeue();
                    if (dropped.Message == 0xFF) RawInputBridge.Release(dropped.Long);
                }
                pending.Enqueue(new Waiting { Window = window, Message = message, Word = word, Long = extra });
            }
        }

        private static long Packed() => ((long)(Y & 0xFFFF) << 16) | (uint)(X & 0xFFFF);

        /// <summary>Writes the next message into a caller's MSG, if there is one.</summary>
        public static bool Take(IntPtr target, bool remove)
        {
            Waiting next;
            lock (pending)
            {
                if (pending.Count == 0) return false;
                next = remove ? pending.Dequeue() : pending.Peek();
            }
            if (target == IntPtr.Zero) return true;

            Marshal.WriteIntPtr(target, 0, next.Window == IntPtr.Zero ? WindowMessages.InputWindow : next.Window);
            Marshal.WriteInt32(target, 8, next.Message);
            Marshal.WriteInt32(target, 12, 0);
            Marshal.WriteInt64(target, 16, next.Word);
            Marshal.WriteInt64(target, 24, next.Long);
            Marshal.WriteInt32(target, 32, Environment.TickCount);
            // MSG.pt sits right after the timestamp: x then y, and then a word
            // the system keeps to itself.
            Marshal.WriteInt32(target, 36, X);
            Marshal.WriteInt32(target, 40, Y);
            Marshal.WriteInt32(target, 44, 0);
            return true;
        }


        /// <summary>
        /// The messages a window sends the moment it appears.
        ///
        /// On a desktop these arrive before a program has finished asking for
        /// the window: it is shown, it is sized, it is given focus, and the
        /// program is told all three. Here nobody sends them, and an engine
        /// waiting to be told its own size waits for something that is never
        /// coming. So they are put in the queue at the start, once, exactly as
        /// they would have arrived.
        /// </summary>
        public static void Announce()
        {
            const int WM_SHOWWINDOW = 0x0018;
            const int WM_SIZE = 0x0005;
            const int WM_ACTIVATE = 0x0006;
            const int WM_ACTIVATEAPP = 0x001C;
            const int WM_SETFOCUS = 0x0007;

            var size = ((long)(Height & 0xFFFF) << 16) | (uint)(Width & 0xFFFF);
            Post(WM_SHOWWINDOW, 1, 0);
            Post(WM_SIZE, 0, size);          // SIZE_RESTORED
            Post(WM_ACTIVATEAPP, 1, 0);
            Post(WM_ACTIVATE, 1, 0);         // WA_ACTIVE
            Post(WM_SETFOCUS, 0, 0);
        }

        private static double Lean(double value) =>
            Math.Abs(value) < Deadzone ? 0.0 : (value - Math.Sign(value) * Deadzone) / (1 - Deadzone);

        /// <summary>
        /// Reads the pad and turns it into pointer movement. Called on a timer
        /// of its own so it keeps up whether or not the game is asking.
        /// </summary>
        public static void Step(double seconds)
        {
            try
            {
                var pads = Gamepad.Gamepads;
                var reading = pads.Count == 0 ? default(GamepadReading) : pads[0].GetCurrentReading();
                var host = hostKeys;
                var previousMode = ControllerMode.Desktop;
                ControllerMode.Update(
                    (reading.Buttons & GamepadButtons.Menu) != 0 || host[0xCF],
                    (reading.Buttons & GamepadButtons.View) != 0 || host[0xD0], seconds);
                if (previousMode != ControllerMode.Desktop)
                {
                    foreach (var key in Emulated) Key(key, false);
                }
                var padHost = ControllerMode.Desktop ? host : emptyHost;
                if (!ControllerMode.Desktop) reading = default(GamepadReading);
                var buttons = reading.Buttons;

                // VirtualKey gamepad events are also how Device Portal sends
                // input. They need not appear in Gamepad.Gamepads readings.
                var dx = Lean(reading.RightThumbstickX) + (padHost[0xD9] ? 1 : 0) - (padHost[0xDA] ? 1 : 0);
                var dy = Lean(reading.RightThumbstickY) + (padHost[0xD7] ? 1 : 0) - (padHost[0xD8] ? 1 : 0);
                if (dx != 0.0 || dy != 0.0)
                {
                    var wasX = X;
                    var wasY = Y;
                    X = (int)Math.Max(0, Math.Min(Width - 1, X + dx * Speed * Settings.PointerSensitivity * seconds));
                    // Screen coordinates grow downwards; a stick pushed up
                    // should move the pointer up.
                    Y = (int)Math.Max(0, Math.Min(Height - 1, Y - dy * Speed * Settings.PointerSensitivity * seconds));
                    if (X != wasX || Y != wasY)
                    {
                        Moves++;
                        RawInputBridge.Mouse(X - wasX, Y - wasY, 0);
                        if (!RawInputBridge.SuppressesLegacy(0)) Post(WM_MOUSEMOVE, Left ? 1 : 0, Packed());
                    }
                }

                // The left stick is the keyboard's arrows and WASD at once:
                // a game reads one or the other and never both, and guessing
                // wrong costs the player the game.
                var lx = Lean(reading.LeftThumbstickX);
                var ly = Lean(reading.LeftThumbstickY);
                var up = ly > 0.4 || (buttons & GamepadButtons.DPadUp) != 0 || padHost[0xCB] || padHost[0xD3];
                var down = ly < -0.4 || (buttons & GamepadButtons.DPadDown) != 0 || padHost[0xCC] || padHost[0xD4];
                var left = lx < -0.4 || (buttons & GamepadButtons.DPadLeft) != 0 || padHost[0xCD] || padHost[0xD6];
                var right = lx > 0.4 || (buttons & GamepadButtons.DPadRight) != 0 || padHost[0xCE] || padHost[0xD5];
                Key(0x57, up || host[0x57]); Key(VK_UP, up || host[VK_UP]);
                Key(0x53, down || host[0x53]); Key(VK_DOWN, down || host[VK_DOWN]);
                Key(0x41, left || host[0x41]); Key(VK_LEFT, left || host[VK_LEFT]);
                Key(0x44, right || host[0x44]); Key(VK_RIGHT, right || host[VK_RIGHT]);

                Key(VK_SPACE, (buttons & GamepadButtons.Y) != 0 || padHost[0xC6] || host[VK_SPACE]);
                Key(VK_RETURN, (ControllerMode.Desktop && (ControllerMode.SystemButtons & 0x10) != 0) || host[VK_RETURN]);
                Key(VK_ESCAPE, (ControllerMode.Desktop && (ControllerMode.SystemButtons & 0x20) != 0) || host[VK_ESCAPE]);
                Key(VK_BACK, (buttons & GamepadButtons.B) != 0 || padHost[0xC4] || host[VK_BACK]);

                var a = (buttons & GamepadButtons.A) != 0 || padHost[0xC3];
                if (a != Left)
                {
                    Left = a;
                    RawInputBridge.Mouse(0, 0, a ? 1 : 2);
                    if (!RawInputBridge.SuppressesLegacy(0)) Post(a ? WM_LBUTTONDOWN : WM_LBUTTONUP, a ? 1 : 0, Packed());
                }

                var x = (buttons & GamepadButtons.X) != 0 || padHost[0xC5];
                if (x != Right)
                {
                    Right = x;
                    RawInputBridge.Mouse(0, 0, x ? 4 : 8);
                    if (!RawInputBridge.SuppressesLegacy(0)) Post(x ? WM_RBUTTONDOWN : WM_RBUTTONUP, x ? 2 : 0, Packed());
                }
            }
            catch
            {
                // No pad, or the pad went away mid-read. Neither is fatal.
            }
        }
    }
}
