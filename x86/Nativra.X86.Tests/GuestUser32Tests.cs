using System.Collections.Generic;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// A window and its message loop, as a game's main loop drives them: the
    /// program is assembled guest code (nasm, org 0x600000).
    /// </summary>
    public sealed class GuestUser32Tests
    {
        private const uint Code = 0x00600000;
        private const uint Data = 0x00601000;
        private const uint WndProc = 0x00600076;

        private sealed class Keys : IGuestInput
        {
            private readonly Queue<uint> pending = new Queue<uint>(new uint[] { 0x41, 0x42 });

            public bool TakeMessage(bool remove, out uint message, out uint wParam, out uint lParam)
            {
                message = wParam = lParam = 0;
                if (pending.Count == 0) return false;
                message = 0x100;   // WM_KEYDOWN
                wParam = remove ? pending.Dequeue() : pending.Peek();
                return true;
            }

            public void CursorPosition(out int x, out int y) { x = 10; y = 20; }
            public void SetCursorPosition(int x, int y) { }
            public bool KeyDown(int virtualKey) => virtualKey == 0x41;
        }

        // main: RegisterClassExA(&wc); CreateWindowExA(0, "Test", 0, WS_VISIBLE,
        //   CW_USEDEFAULT x4, 0, 0, 0, param 0xABCD); while (GetMessageA(&msg))
        //   DispatchMessageA(&msg); return msg.wParam + createParam + keys
        // wndproc: WM_CREATE stores lpCreateParams, WM_SIZE stores lParam,
        //   WM_KEYDOWN counts and posts WM_USER+1 on the second, WM_USER+1
        //   calls PostQuitMessage(0x22), anything else jumps to DefWindowProcA.
        private static readonly byte[] Program =
        {
            0x56, 0x68, 0x00, 0x11, 0x60, 0x00, 0xFF, 0x15, 0x00, 0x10, 0x60, 0x00, 0x68, 0xCD, 0xAB, 0x00,
            0x00, 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x00, 0x00, 0x00, 0x80, 0x68, 0x00, 0x00, 0x00,
            0x80, 0x68, 0x00, 0x00, 0x00, 0x80, 0x68, 0x00, 0x00, 0x00, 0x80, 0x68, 0x00, 0x00, 0x00, 0x10,
            0x6A, 0x00, 0x68, 0x00, 0x12, 0x60, 0x00, 0x6A, 0x00, 0xFF, 0x15, 0x04, 0x10, 0x60, 0x00, 0x89,
            0xC6, 0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x00, 0x13, 0x60, 0x00, 0xFF, 0x15, 0x08, 0x10,
            0x60, 0x00, 0x85, 0xC0, 0x74, 0x0D, 0x68, 0x00, 0x13, 0x60, 0x00, 0xFF, 0x15, 0x0C, 0x10, 0x60,
            0x00, 0xEB, 0xDE, 0xA1, 0x08, 0x13, 0x60, 0x00, 0x03, 0x05, 0x00, 0x14, 0x60, 0x00, 0x03, 0x05,
            0x08, 0x14, 0x60, 0x00, 0x5E, 0xC3, 0x8B, 0x44, 0x24, 0x08, 0x83, 0xF8, 0x01, 0x75, 0x11, 0x8B,
            0x4C, 0x24, 0x10, 0x8B, 0x09, 0x89, 0x0D, 0x00, 0x14, 0x60, 0x00, 0x31, 0xC0, 0xC2, 0x10, 0x00,
            0x83, 0xF8, 0x05, 0x75, 0x0F, 0x8B, 0x4C, 0x24, 0x10, 0x89, 0x0D, 0x04, 0x14, 0x60, 0x00, 0x31,
            0xC0, 0xC2, 0x10, 0x00, 0x3D, 0x00, 0x01, 0x00, 0x00, 0x75, 0x27, 0xFF, 0x05, 0x08, 0x14, 0x60,
            0x00, 0x83, 0x3D, 0x08, 0x14, 0x60, 0x00, 0x02, 0x75, 0x13, 0x6A, 0x00, 0x6A, 0x00, 0x68, 0x01,
            0x04, 0x00, 0x00, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x15, 0x10, 0x10, 0x60, 0x00, 0x31, 0xC0, 0xC2,
            0x10, 0x00, 0x3D, 0x01, 0x04, 0x00, 0x00, 0x75, 0x0D, 0x6A, 0x22, 0xFF, 0x15, 0x14, 0x10, 0x60,
            0x00, 0x31, 0xC0, 0xC2, 0x10, 0x00, 0xFF, 0x25, 0x18, 0x10, 0x60, 0x00,
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MessageLoopRunsTheWindowProcedureUntilQuit(bool jit)
        {
            var p = new GuestProcess(new GuestMemory(), useJit: jit);
            var kernel = new GuestKernel(p) { Input = new Keys() };
            kernel.Install();
            p.Memory.Map(Code, 0x2000);
            p.Memory.WriteBytes(Code, Program);
            string[] imports = { "RegisterClassExA", "CreateWindowExA", "GetMessageA", "DispatchMessageA",
                                 "PostMessageA", "PostQuitMessage", "DefWindowProcA" };
            for (var n = 0; n < imports.Length; n++)
                p.Memory.Write32(Data + (uint)n * 4, p.Imports.Bind("user32.dll", imports[n], -1));

            // WNDCLASSEXA: cbSize, style, lpfnWndProc, … lpszClassName at +40.
            p.Memory.Write32(Data + 0x100, 48);
            p.Memory.Write32(Data + 0x108, WndProc);
            p.Memory.Write32(Data + 0x128, Data + 0x200);
            p.Memory.WriteAnsi(Data + 0x200, "Test");

            var result = p.Call(Code, out var eax, 1_000_000);
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(0x22u + 0xABCDu + 2u, eax);
            Assert.Equal((1080u << 16) | 1920u, p.Memory.Read32(Data + 0x404));   // WM_SIZE told the screen size
            Assert.NotEqual(0u, kernel.InputWindow);
            Assert.True(kernel.MessagesDispatched >= 8);
        }

        [Fact]
        public void ScreenQueriesDescribeOneFullHdMonitor()
        {
            var p = new GuestProcess(new GuestMemory(), useJit: false);
            var kernel = new GuestKernel(p);
            kernel.Install();
            uint Call(string dll, string f, params uint[] args)
            {
                Assert.True(p.Call(p.Imports.Bind(dll, f, -1), out var r, 100_000, args).Ok, f);
                return r;
            }
            Assert.Equal(1920u, Call("user32.dll", "GetSystemMetrics", 0));
            Assert.Equal(1080u, Call("user32.dll", "GetSystemMetrics", 1));
            Assert.Equal(1080u, Call("gdi32.dll", "GetDeviceCaps", 0x00DC0001, 10));

            var devmode = kernel.Heap.Alloc(220, zero: true);
            Assert.Equal(1u, Call("user32.dll", "EnumDisplaySettingsW", 0, 0xFFFFFFFF, devmode));
            Assert.Equal(1920u, p.Memory.Read32(devmode + 172));
            Assert.Equal(60u, p.Memory.Read32(devmode + 184));
            Assert.Equal(0u, Call("user32.dll", "EnumDisplaySettingsA", 0, 3, devmode));   // past the last mode

            var info = kernel.Heap.Alloc(104, zero: true);
            p.Memory.Write32(info, 104);
            Assert.Equal(1u, Call("user32.dll", "GetMonitorInfoW", 0x00A0FF01, info));
            Assert.Equal(1080u, p.Memory.Read32(info + 16));
            Assert.Equal("\\\\.\\DISPLAY1", p.Memory.ReadUnicode(info + 40));
        }
    }
}
