using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// What a game expects from a window system, on a console that has none.
    ///
    /// The Xbox has no HWND: it has a CoreWindow, and nothing a Win32 program
    /// asks for maps onto it directly. Most of those questions do not need a
    /// real answer to get past — they need a plausible one. A window handle
    /// that is not zero, a class atom that is not zero, a screen that is
    /// 1920x1080. The few that hand back a rectangle are written out properly,
    /// because a game lays itself out from them.
    /// </summary>
    public static class WindowStubs
    {
        private const int Width = 1920;
        private const int Height = 1080;

        /// <summary>A handle that is not real but is not zero either.</summary>
        private static readonly long FakeWindow = 0x00BA5E11;

        /// <summary>Functions whose zero means failure, and what to say instead.</summary>
        private static readonly Dictionary<string, long> Answers =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                { "RegisterClassW", 0xC001 },
                { "RegisterClassA", 0xC001 },
                { "RegisterClassExW", 0xC001 },
                { "RegisterClassExA", 0xC001 },
                { "CreateWindowExW", 0x00BA5E11 },
                { "CreateWindowExA", 0x00BA5E11 },
                { "GetActiveWindow", 0x00BA5E11 },
                { "GetForegroundWindow", 0x00BA5E11 },
                { "GetFocus", 0x00BA5E11 },
                { "SetFocus", 0x00BA5E11 },
                { "SetActiveWindow", 0x00BA5E11 },
                { "SetForegroundWindow", 1 },
                { "ShowWindow", 1 },
                { "UpdateWindow", 1 },
                { "SetWindowPos", 1 },
                { "MoveWindow", 1 },
                { "InvalidateRect", 1 },
                { "ValidateRect", 1 },
                { "DestroyWindow", 1 },
                { "UnregisterClassW", 1 },
                { "AdjustWindowRect", 1 },
                { "AdjustWindowRectEx", 1 },
                { "RegisterRawInputDevices", 1 },
                { "SetWindowTextW", 1 },
                { "IsWindow", 1 },
                { "IsWindowVisible", 1 },
                { "IsIconic", 0 },
                { "ShowCursor", 0 },
                { "ClipCursor", 1 },
                { "SetCursorPos", 1 },
                { "MessageBoxW", 1 },
                { "MessageBoxA", 1 },
                { "MonitorFromWindow", 0x00A0FF01 },
                { "MonitorFromPoint", 0x00A0FF01 },
                { "EnumDisplayMonitors", 1 },
                { "PeekMessageW", 0 },
                { "PeekMessageA", 0 },
                { "GetMessageW", 1 },
                { "TranslateMessage", 1 },
                { "DispatchMessageW", 0 },
                { "DefWindowProcW", 0 },
                { "DefWindowProcA", 0 },
            };

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RectDelegate(IntPtr window, IntPtr rect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MetricDelegate(int index);

        private static RectDelegate rect;
        private static MetricDelegate metric;

        public static void Install(SystemImports imports)
        {
            rect = (window, target) =>
            {
                if (target == IntPtr.Zero) return 0;
                Marshal.WriteInt32(target, 0, 0);
                Marshal.WriteInt32(target, 4, 0);
                Marshal.WriteInt32(target, 8, Width);
                Marshal.WriteInt32(target, 12, Height);
                return 1;
            };

            metric = index =>
            {
                switch (index)
                {
                    case 0: return Width;   // SM_CXSCREEN
                    case 1: return Height;  // SM_CYSCREEN
                    case 16: return Width;  // SM_CXFULLSCREEN
                    case 17: return Height; // SM_CYFULLSCREEN
                    default: return 0;
                }
            };

            var rectAddress = Marshal.GetFunctionPointerForDelegate(rect);
            var metricAddress = Marshal.GetFunctionPointerForDelegate(metric);

            foreach (var module in new[] { "USER32.dll", "user32.dll" })
            {
                imports.Overrides[module + "!GetClientRect"] = rectAddress;
                imports.Overrides[module + "!GetWindowRect"] = rectAddress;
                imports.Overrides[module + "!GetSystemMetrics"] = metricAddress;

                foreach (var pair in Answers)
                {
                    imports.Answers[module + "!" + pair.Key] = pair.Value;
                }
            }
        }
    }
}
