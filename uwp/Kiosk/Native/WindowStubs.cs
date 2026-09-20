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
    /// 1920x1080.
    ///
    /// The ones that hand back memory are written out properly, because a game
    /// lays itself out from them and a constant cannot fill a structure. That
    /// includes the message queue: a loop told "there is a message" and handed
    /// nothing reads whatever was on the stack, and a loop told nothing at all
    /// waits forever.
    /// </summary>
    public static class WindowStubs
    {
        private const int Width = 1920;
        private const int Height = 1080;

        private const long FakeWindow = 0x00BA5E11;
        private const long FakeMonitor = 0x00A0FF01;
        private const long FakeDc = 0x00DC0001;
        private const long FakeCursor = 0x00C50001;

        /// <summary>Functions whose zero means failure, and what to say instead.</summary>
        private static readonly Dictionary<string, long> Answers =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                { "RegisterClassW", 0xC001 },
                { "RegisterClassA", 0xC001 },
                { "RegisterClassExW", 0xC001 },
                { "RegisterClassExA", 0xC001 },
                { "CreateWindowExW", FakeWindow },
                { "CreateWindowExA", FakeWindow },
                { "GetActiveWindow", FakeWindow },
                { "GetForegroundWindow", FakeWindow },
                { "GetFocus", FakeWindow },
                { "SetFocus", FakeWindow },
                { "SetActiveWindow", FakeWindow },
                { "GetDesktopWindow", FakeWindow },
                { "SetForegroundWindow", 1 },
                { "ShowWindow", 1 },
                { "UpdateWindow", 1 },
                { "SetWindowPos", 1 },
                { "MoveWindow", 1 },
                { "InvalidateRect", 1 },
                { "ValidateRect", 1 },
                { "DestroyWindow", 1 },
                { "UnregisterClassW", 1 },
                { "UnregisterClassA", 1 },
                { "AdjustWindowRect", 1 },
                { "AdjustWindowRectEx", 1 },
                { "RegisterRawInputDevices", 1 },
                { "GetRawInputData", 0 },
                { "SetWindowTextW", 1 },
                { "SetWindowTextA", 1 },
                { "IsWindow", 1 },
                { "IsWindowVisible", 1 },
                { "IsIconic", 0 },
                { "IsZoomed", 1 },
                { "ShowCursor", 0 },
                { "SetCursor", 0 },
                { "LoadCursorW", FakeCursor },
                { "LoadCursorA", FakeCursor },
                { "LoadIconW", FakeCursor },
                { "LoadIconA", FakeCursor },
                { "ClipCursor", 1 },
                { "SetCursorPos", 1 },
                { "MessageBoxW", 1 },
                { "MessageBoxA", 1 },
                { "MonitorFromWindow", FakeMonitor },
                { "MonitorFromPoint", FakeMonitor },
                { "MonitorFromRect", FakeMonitor },
                { "TranslateMessage", 1 },
                { "DispatchMessageW", 0 },
                { "DispatchMessageA", 0 },
                { "DefWindowProcW", 0 },
                { "DefWindowProcA", 0 },
                { "PostMessageW", 1 },
                { "PostMessageA", 1 },
                { "SendMessageW", 0 },
                { "SendMessageA", 0 },
                { "PostQuitMessage", 0 },
                { "GetDC", FakeDc },
                { "GetWindowDC", FakeDc },
                { "ReleaseDC", 1 },
                { "GetDpiForWindow", 96 },
                { "GetDpiForSystem", 96 },
                { "SetProcessDpiAwarenessContext", 1 },
                { "SetProcessDPIAware", 1 },
                { "AreDpiAwarenessContextsEqual", 0 },
                { "SystemParametersInfoW", 1 },
                { "SystemParametersInfoA", 1 },
                { "SetTimer", 1 },
                { "KillTimer", 1 },
                { "GetKeyboardState", 1 },
                { "GetKeyboardLayout", 0x04090409 },
                { "MapVirtualKeyW", 0 },
                { "SetCapture", 0 },
                { "ReleaseCapture", 1 },
                { "ClientToScreen", 1 },
                { "ScreenToClient", 1 },
                { "SetWindowLongW", 0 },
                { "SetWindowLongA", 0 },
                { "GetWindowLongW", 0 },
                { "GetWindowLongA", 0 },
                { "SetWindowLongPtrW", 0 },
                { "GetWindowLongPtrW", 0 },
                { "SetLayeredWindowAttributes", 1 },
                { "EnumDisplaySettingsW", 0 },
                { "EnumDisplaySettingsA", 0 },
                { "EnumDisplayDevicesW", 0 },
                { "ChangeDisplaySettingsExW", 0 },
                { "ChangeDisplaySettingsW", 0 },
                { "RegisterDeviceNotificationW", 0x00DE0001 },
                { "UnregisterDeviceNotification", 1 },
                { "FlashWindowEx", 0 },
                { "SetWindowsHookExW", 0 },
                { "GetSystemMenu", 0 },
                { "GetPropW", 0 },
                { "SetPropW", 1 },
                { "RemovePropW", 0 },
                { "GetWindowThreadProcessId", 1 },
                { "MsgWaitForMultipleObjects", 0 },
                { "MsgWaitForMultipleObjectsEx", 0 },
                { "WaitMessage", 1 },
                { "QueryDisplayConfig", 0 },
                { "DisplayConfigGetDeviceInfo", 50 },   // ERROR_NOT_SUPPORTED
                { "GetRawInputDeviceInfoW", 0 },
                { "GetRawInputDeviceInfoA", 0 },
                { "GetRawInputBuffer", 0 },
                { "GetKeyboardLayoutList", 0 },
                { "GetDisplayConfigBufferSizes", 50 },
            };

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RectDelegate(IntPtr window, IntPtr rect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MetricDelegate(int index);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PeekDelegate(
            IntPtr message, IntPtr window, uint first, uint last, uint remove);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetMessageDelegate(
            IntPtr message, IntPtr window, uint first, uint last);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PointDelegate(IntPtr point);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SizesDelegate(uint flags, IntPtr paths, IntPtr modes);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint RawListDelegate(IntPtr list, IntPtr count, uint size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MonitorInfoDelegate(IntPtr monitor, IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumMonitorsDelegate(
            IntPtr dc, IntPtr clip, IntPtr callback, IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MonitorEnumProc(
            IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

        // Held in fields so the collector cannot take them while native code
        // still holds their addresses.
        private static RectDelegate rect;
        private static MetricDelegate metric;
        private static PeekDelegate peek;
        private static GetMessageDelegate getMessage;
        private static PointDelegate cursorPos;
        private static MetricDelegate keyState;
        private static SizesDelegate displaySizes;
        private static RawListDelegate rawList;
        private static MonitorInfoDelegate monitorInfo;
        private static EnumMonitorsDelegate enumMonitors;

        /// <summary>
        /// How many times the engine asked for a message. It is the clearest
        /// sign there is that the player loop is turning: a loop that pumps is
        /// a loop that reached the end of its own startup.
        /// </summary>
        public static long Pumped;

        private static void WriteRect(IntPtr target, int left, int top, int right, int bottom)
        {
            Marshal.WriteInt32(target, 0, left);
            Marshal.WriteInt32(target, 4, top);
            Marshal.WriteInt32(target, 8, right);
            Marshal.WriteInt32(target, 12, bottom);
        }

        /// <summary>MSG is 48 bytes on x64; every one of them has to be ours.</summary>
        private static void ClearMessage(IntPtr target)
        {
            if (target == IntPtr.Zero) return;
            for (var offset = 0; offset < 48; offset += 8)
            {
                Marshal.WriteInt64(target, offset, 0);
            }
            Marshal.WriteInt64(target, 0, FakeWindow);
        }

        public static void Install(SystemImports imports)
        {
            rect = (window, target) =>
            {
                if (target == IntPtr.Zero) return 0;
                WriteRect(target, 0, 0, Width, Height);
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
                    case 78: return Width;  // SM_CXVIRTUALSCREEN
                    case 79: return Height; // SM_CYVIRTUALSCREEN
                    case 80: return 1;      // SM_CMONITORS
                    default: return 0;
                }
            };

            // Nothing ever arrives, so the honest answer is "no message" — but
            // the structure is cleared first, because a caller handed a false
            // return still reads the fields on some paths.
            peek = (message, window, first, last, remove) =>
            {
                Pumped++;
                ClearMessage(message);
                // PM_REMOVE: the caller is consuming, not glancing.
                if (PointerBridge.Take(message, (remove & 1) != 0)) return 1;
                return 0;
            };

            // A blocking wait that never returns would stop the game dead. The
            // loop is told it has a message, and the message is WM_NULL, which
            // dispatches to nothing and lets the next turn happen.
            getMessage = (message, window, first, last) =>
            {
                Pumped++;
                ClearMessage(message);
                if (PointerBridge.Take(message, true)) return 1;
                // This call is supposed to block until something arrives, and
                // nothing ever will. Handing back an empty message keeps the
                // loop turning; the pause is so a loop that only waits does not
                // eat a core doing it.
                System.Threading.Thread.Sleep(1);
                return 1;
            };

            cursorPos = point =>
            {
                if (point == IntPtr.Zero) return 0;
                Marshal.WriteInt32(point, 0, PointerBridge.X);
                Marshal.WriteInt32(point, 4, PointerBridge.Y);
                return 1;
            };

            // A game that polls instead of reading messages asks this, and it
            // has to agree with what the messages said.
            keyState = key =>
            {
                if (key == 1 && PointerBridge.Left) return unchecked((int)0xFFFF8001);
                if (key == 2 && PointerBridge.Right) return unchecked((int)0xFFFF8001);
                return 0;
            };

            // MONITORINFO: size, the monitor rectangle, the working area, flags.
            monitorInfo = (monitor, info) =>
            {
                if (info == IntPtr.Zero) return 0;
                WriteRect(info + 4, 0, 0, Width, Height);
                WriteRect(info + 20, 0, 0, Width, Height);
                Marshal.WriteInt32(info, 36, 1); // MONITORINFOF_PRIMARY
                return 1;
            };

            // Answering "yes" without ever calling the callback leaves a game
            // believing the machine has no screens. One screen, handed over
            // properly, is the difference between a resolution list and none.
            enumMonitors = (dc, clip, callback, data) =>
            {
                if (callback == IntPtr.Zero) return 1;
                var area = Marshal.AllocHGlobal(16);
                try
                {
                    WriteRect(area, 0, 0, Width, Height);
                    var each = Marshal.GetDelegateForFunctionPointer<MonitorEnumProc>(callback);
                    each((IntPtr)FakeMonitor, dc, area, data);
                }
                catch
                {
                    // A game that faults inside its own callback is its problem;
                    // the enumeration still reports that it happened.
                }
                finally
                {
                    Marshal.FreeHGlobal(area);
                }
                return 1;
            };

            // Both of these are asked "how many are there", and both were
            // answering "none, and it went fine" without ever writing the
            // number down. The caller then reads whatever was in its own
            // variable and asks for that many — which is how a program that
            // was doing fine walks off the end of something.
            displaySizes = (flags, paths, modes) =>
            {
                if (paths != IntPtr.Zero) Marshal.WriteInt32(paths, 0);
                if (modes != IntPtr.Zero) Marshal.WriteInt32(modes, 0);
                return 0; // ERROR_SUCCESS
            };

            rawList = (list, count, size) =>
            {
                if (count != IntPtr.Zero) Marshal.WriteInt32(count, 0);
                return 0;
            };

            var ours = new Dictionary<string, IntPtr>
            {
                { "GetDisplayConfigBufferSizes",
                    Marshal.GetFunctionPointerForDelegate(displaySizes) },
                { "GetRawInputDeviceList",
                    Marshal.GetFunctionPointerForDelegate(rawList) },
                { "GetClientRect", Marshal.GetFunctionPointerForDelegate(rect) },
                { "GetWindowRect", Marshal.GetFunctionPointerForDelegate(rect) },
                { "GetSystemMetrics", Marshal.GetFunctionPointerForDelegate(metric) },
                { "PeekMessageW", Marshal.GetFunctionPointerForDelegate(peek) },
                { "PeekMessageA", Marshal.GetFunctionPointerForDelegate(peek) },
                { "GetMessageW", Marshal.GetFunctionPointerForDelegate(getMessage) },
                { "GetMessageA", Marshal.GetFunctionPointerForDelegate(getMessage) },
                { "GetCursorPos", Marshal.GetFunctionPointerForDelegate(cursorPos) },
                { "GetAsyncKeyState", Marshal.GetFunctionPointerForDelegate(keyState) },
                { "GetKeyState", Marshal.GetFunctionPointerForDelegate(keyState) },
                { "GetMonitorInfoW", Marshal.GetFunctionPointerForDelegate(monitorInfo) },
                { "GetMonitorInfoA", Marshal.GetFunctionPointerForDelegate(monitorInfo) },
                { "EnumDisplayMonitors", Marshal.GetFunctionPointerForDelegate(enumMonitors) },
            };

            foreach (var module in new[] { "USER32.dll", "user32.dll" })
            {
                foreach (var pair in ours)
                {
                    imports.Overrides[module + "!" + pair.Key] = pair.Value;
                }
                foreach (var pair in Answers)
                {
                    imports.Answers[module + "!" + pair.Key] = pair.Value;
                }
            }
        }
    }
}
