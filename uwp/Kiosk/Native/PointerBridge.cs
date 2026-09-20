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
                if (dx == 0.0 && dy == 0.0)
                {
                    // The left stick moves the pointer too, for games whose
                    // menus are the only thing that needs one.
                    dx = Lean(reading.LeftThumbstickX);
                    dy = Lean(reading.LeftThumbstickY);
                }

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
