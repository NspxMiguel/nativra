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

        /// <summary>Whether a key is down, for a game that polls instead of reading.</summary>
        public static bool Down(int key) => key >= 0 && key < 256 && held[key];

        private static void Key(int code, bool down)
        {
            if (held[code] == down) return;
            held[code] = down;
            if (down) Keys++;
            // lParam carries the repeat count and the scan code; a game that
            // reads only the key itself is the common case, and the rest being
            // zero is what a synthesised key looks like anywhere.
            Post(down ? WM_KEYDOWN : WM_KEYUP, code, 1);
        }

        private sealed class Waiting
        {
            public int Message;
            public long Word;
            public long Long;
        }

        private static readonly Queue<Waiting> pending = new Queue<Waiting>();

        private static void Post(int message, long word, long extra)
        {
            lock (pending)
            {
                // A game that stops reading should not be allowed to grow this
                // without limit; the oldest movement is the least interesting.
                while (pending.Count > 128) pending.Dequeue();
                pending.Enqueue(new Waiting { Message = message, Word = word, Long = extra });
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

            Marshal.WriteInt64(target, 0, 0x00BA5E11);      // hwnd
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
            const int WM_WINDOWPOSCHANGED = 0x0047;

            var size = ((long)(Height & 0xFFFF) << 16) | (uint)(Width & 0xFFFF);
            Post(WM_SHOWWINDOW, 1, 0);
            Post(WM_WINDOWPOSCHANGED, 0, 0);
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
                if (pads.Count == 0) return;
                var reading = pads[0].GetCurrentReading();

                var dx = Lean(reading.RightThumbstickX);
                var dy = Lean(reading.RightThumbstickY);
                if (dx != 0.0 || dy != 0.0)
                {
                    var wasX = X;
                    var wasY = Y;
                    X = (int)Math.Max(0, Math.Min(Width - 1, X + dx * Speed * seconds));
                    // Screen coordinates grow downwards; a stick pushed up
                    // should move the pointer up.
                    Y = (int)Math.Max(0, Math.Min(Height - 1, Y - dy * Speed * seconds));
                    if (X != wasX || Y != wasY)
                    {
                        Moves++;
                        Post(WM_MOUSEMOVE, Left ? 1 : 0, Packed());
                    }
                }

                // The left stick is the keyboard's arrows and WASD at once:
                // a game reads one or the other and never both, and guessing
                // wrong costs the player the game.
                var lx = Lean(reading.LeftThumbstickX);
                var ly = Lean(reading.LeftThumbstickY);
                Key(0x57, ly > 0.4);  Key(VK_UP, ly > 0.4);
                Key(0x53, ly < -0.4); Key(VK_DOWN, ly < -0.4);
                Key(0x41, lx < -0.4); Key(VK_LEFT, lx < -0.4);
                Key(0x44, lx > 0.4);  Key(VK_RIGHT, lx > 0.4);

                Key(VK_SPACE, (reading.Buttons & GamepadButtons.Y) != 0);
                Key(VK_RETURN, (reading.Buttons & GamepadButtons.Menu) != 0);
                Key(VK_ESCAPE, (reading.Buttons & GamepadButtons.View) != 0);
                Key(VK_BACK, (reading.Buttons & GamepadButtons.B) != 0);

                var a = (reading.Buttons & GamepadButtons.A) != 0;
                if (a != Left)
                {
                    Left = a;
                    Post(a ? WM_LBUTTONDOWN : WM_LBUTTONUP, a ? 1 : 0, Packed());
                }

                var x = (reading.Buttons & GamepadButtons.X) != 0;
                if (x != Right)
                {
                    Right = x;
                    Post(x ? WM_RBUTTONDOWN : WM_RBUTTONUP, x ? 2 : 0, Packed());
                }
            }
            catch
            {
                // No pad, or the pad went away mid-read. Neither is fatal.
            }
        }
    }
}
