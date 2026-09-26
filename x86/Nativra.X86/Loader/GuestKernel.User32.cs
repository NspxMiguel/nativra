using System;
using System.Collections.Generic;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// What the host feeds a guest's windows: the input the 64-bit side already
    /// collects (PointerBridge on the console) as window messages, the cursor
    /// and the key state. The default has no input at all.
    /// </summary>
    public interface IGuestInput
    {
        /// <summary>The next input message (WM_MOUSEMOVE, WM_KEYDOWN, …), removed or peeked.</summary>
        bool TakeMessage(bool remove, out uint message, out uint wParam, out uint lParam);
        void CursorPosition(out int x, out int y);
        void SetCursorPosition(int x, int y);
        bool KeyDown(int virtualKey);
    }

    internal sealed class NoInput : IGuestInput
    {
        public bool TakeMessage(bool remove, out uint message, out uint wParam, out uint lParam)
        {
            message = wParam = lParam = 0;
            return false;
        }

        public void CursorPosition(out int x, out int y) { x = 960; y = 540; }
        public void SetCursorPosition(int x, int y) { }
        public bool KeyDown(int virtualKey) => false;
    }

    // user32 (and the few gdi32 queries games make about the screen) for a
    // console that has no window system: window classes and windows are
    // bookkeeping, the screen is one 1920x1080 monitor, every thread has its
    // own message queue, and the input the host collects arrives as messages
    // on the game's main window. Window procedures are guest code: dispatch,
    // SendMessage and CallWindowProc jump into them on the guest's own stack
    // (a tail call, so a window procedure that waits can let other threads
    // run); only window creation calls them as a nested run, because it needs
    // their answers before it can return.
    public sealed partial class GuestKernel
    {
        private const uint WmNull = 0x0000, WmCreate = 0x0001, WmDestroy = 0x0002, WmSize = 0x0005;
        private const uint WmActivate = 0x0006, WmSetFocus = 0x0007, WmClose = 0x0010, WmQuit = 0x0012;
        private const uint WmShowWindow = 0x0018, WmActivateApp = 0x001C, WmNcCreate = 0x0081;
        private const uint WmNcHitTest = 0x0084, WmTimer = 0x0113;
        private const uint CwUseDefault = 0x80000000;
        private const uint WsVisible = 0x10000000, WsChild = 0x40000000;
        private const uint FakeMonitor = 0x00A0FF01, FakeDc = 0x00DC0001, FakeCursor = 0x00C50001;

        /// <summary>The one screen the guest sees.</summary>
        public int ScreenWidth { get; set; } = 1920;
        public int ScreenHeight { get; set; } = 1080;

        /// <summary>Where input comes from; the app plugs in the console's.</summary>
        public IGuestInput Input { get; set; } = new NoInput();

        /// <summary>The window input is delivered to (the game's first top-level window).</summary>
        public uint InputWindow { get; private set; }

        public long MessagesDispatched { get; private set; }

        private sealed class WindowClass
        {
            public string Name;
            public uint Atom, Procedure, Instance, Style;
            public int WindowExtra;
        }

        private sealed class Window
        {
            public uint Handle, Procedure, Parent, OwnerThread, Instance, Menu, Style, ExStyle;
            public WindowClass Class;
            public string Text = "";
            public bool Unicode, Visible;
            public int X, Y, Width, Height;
            public readonly Dictionary<int, uint> Longs = new Dictionary<int, uint>();
            public readonly Dictionary<string, uint> Props = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        }

        private struct QueuedMessage
        {
            public uint Window, Message, WParam, LParam;
        }

        private sealed class Timer
        {
            public uint Window, Id, Interval, Procedure, Thread;
            public long Due;
        }

        private readonly Dictionary<string, WindowClass> windowClasses =
            new Dictionary<string, WindowClass>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, WindowClass> classAtoms = new Dictionary<uint, WindowClass>();
        private readonly Dictionary<uint, Window> windows = new Dictionary<uint, Window>();
        private readonly Dictionary<uint, Queue<QueuedMessage>> queues = new Dictionary<uint, Queue<QueuedMessage>>();
        private readonly List<Timer> timers = new List<Timer>();
        private readonly Dictionary<string, uint> registeredMessages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private uint nextWindow = 0x00010010;
        private uint nextTimerId = 0x7F00;
        private int cursorCount;
        private uint cursor = FakeCursor;
        private readonly System.Diagnostics.Stopwatch uiClock = System.Diagnostics.Stopwatch.StartNew();

        private void InstallUser32(GuestImports i)
        {
            const string u = "user32.dll";
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var s = wide ? "W" : "A";

                i.Register(u, "RegisterClass" + s, CallConv.Stdcall, 1, c => RegisterClass(c.Arg(0), false, w));
                i.Register(u, "RegisterClassEx" + s, CallConv.Stdcall, 1, c => RegisterClass(c.Arg(0), true, w));
                i.Register(u, "UnregisterClass" + s, CallConv.Stdcall, 2, c => 1);
                i.Register(u, "GetClassInfo" + s, CallConv.Stdcall, 3, c => ClassFor(c.Arg(1), w) != null ? 1u : 0u);
                i.Register(u, "GetClassInfoEx" + s, CallConv.Stdcall, 3, c => ClassFor(c.Arg(1), w) != null ? 1u : 0u);
                i.Register(u, "CreateWindowEx" + s, CallConv.Stdcall, 12, c => CreateWindow(c, w));
                i.Register(u, "DefWindowProc" + s, CallConv.Stdcall, 4, c => DefWindowProc(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(u, "CallWindowProc" + s, CallConv.Stdcall, 5, c =>
                {
                    TailCall(c, 5, c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4));
                    return 0;
                });
                i.Register(u, "SendMessage" + s, CallConv.Stdcall, 4, c => SendMessage(c, 4, c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(u, "SendMessageTimeout" + s, CallConv.Stdcall, 7, c =>
                {
                    if (c.Arg(6) != 0) memory.Write32(c.Arg(6), 0);
                    return 1;
                });
                i.Register(u, "SendNotifyMessage" + s, CallConv.Stdcall, 4, c => Post(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(u, "PostMessage" + s, CallConv.Stdcall, 4, c => Post(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(u, "PostThreadMessage" + s, CallConv.Stdcall, 4, c =>
                {
                    Queue(c.Arg(0)).Enqueue(new QueuedMessage { Message = c.Arg(1), WParam = c.Arg(2), LParam = c.Arg(3) });
                    return 1;
                });
                i.Register(u, "PeekMessage" + s, CallConv.Stdcall, 5, c =>
                    NextMessage(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), (c.Arg(4) & 1) != 0) ? 1u : 0u);
                i.Register(u, "GetMessage" + s, CallConv.Stdcall, 4, c => GetMessage(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(u, "DispatchMessage" + s, CallConv.Stdcall, 1, c => DispatchMessage(c));
                i.Register(u, "IsDialogMessage" + s, CallConv.Stdcall, 2, c => 0);
                i.Register(u, "TranslateAccelerator" + s, CallConv.Stdcall, 3, c => 0);
                i.Register(u, "RegisterWindowMessage" + s, CallConv.Stdcall, 1, c => RegisterMessage(ReadText(c.Arg(0), w)));
                i.Register(u, "GetWindowLong" + s, CallConv.Stdcall, 2, c => GetWindowLong(c.Arg(0), (int)c.Arg(1)));
                i.Register(u, "SetWindowLong" + s, CallConv.Stdcall, 3, c => SetWindowLong(c.Arg(0), (int)c.Arg(1), c.Arg(2)));
                i.Register(u, "GetWindowLongPtr" + s, CallConv.Stdcall, 2, c => GetWindowLong(c.Arg(0), (int)c.Arg(1)));
                i.Register(u, "SetWindowLongPtr" + s, CallConv.Stdcall, 3, c => SetWindowLong(c.Arg(0), (int)c.Arg(1), c.Arg(2)));
                i.Register(u, "GetClassLong" + s, CallConv.Stdcall, 2, c => 0);
                i.Register(u, "SetClassLong" + s, CallConv.Stdcall, 3, c => 0);
                i.Register(u, "SetWindowText" + s, CallConv.Stdcall, 2, c =>
                {
                    if (windows.TryGetValue(c.Arg(0), out var win)) win.Text = ReadText(c.Arg(1), w);
                    return 1;
                });
                i.Register(u, "GetWindowText" + s, CallConv.Stdcall, 3, c =>
                {
                    var text = windows.TryGetValue(c.Arg(0), out var win) ? win.Text : "";
                    if (c.Arg(2) == 0) return 0;
                    if (text.Length >= c.Arg(2)) text = text.Substring(0, (int)c.Arg(2) - 1);
                    WriteText(c.Arg(1), text, w);
                    return (uint)text.Length;
                });
                i.Register(u, "GetWindowTextLength" + s, CallConv.Stdcall, 1, c =>
                    windows.TryGetValue(c.Arg(0), out var win) ? (uint)win.Text.Length : 0);
                i.Register(u, "GetClassName" + s, CallConv.Stdcall, 3, c =>
                {
                    var name = windows.TryGetValue(c.Arg(0), out var win) ? win.Class?.Name ?? "" : "";
                    if (c.Arg(2) == 0) return 0;
                    if (name.Length >= c.Arg(2)) name = name.Substring(0, (int)c.Arg(2) - 1);
                    WriteText(c.Arg(1), name, w);
                    return (uint)name.Length;
                });
                i.Register(u, "SetProp" + s, CallConv.Stdcall, 3, c =>
                {
                    if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                    win.Props[PropName(c.Arg(1), w)] = c.Arg(2);
                    return 1;
                });
                i.Register(u, "GetProp" + s, CallConv.Stdcall, 2, c =>
                    windows.TryGetValue(c.Arg(0), out var win) && win.Props.TryGetValue(PropName(c.Arg(1), w), out var v) ? v : 0);
                i.Register(u, "RemoveProp" + s, CallConv.Stdcall, 2, c =>
                {
                    if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                    var key = PropName(c.Arg(1), w);
                    if (!win.Props.TryGetValue(key, out var v)) return 0;
                    win.Props.Remove(key);
                    return v;
                });
                i.Register(u, "MessageBox" + s, CallConv.Stdcall, 4, c =>
                {
                    Say("x86 MessageBox: " + ReadText(c.Arg(2), w) + ": " + ReadText(c.Arg(1), w));
                    return 1;   // IDOK
                });
                i.Register(u, "MessageBoxEx" + s, CallConv.Stdcall, 5, c =>
                {
                    Say("x86 MessageBox: " + ReadText(c.Arg(2), w) + ": " + ReadText(c.Arg(1), w));
                    return 1;
                });
                i.Register(u, "LoadCursor" + s, CallConv.Stdcall, 2, c => FakeCursor);
                i.Register(u, "LoadIcon" + s, CallConv.Stdcall, 2, c => FakeCursor);
                i.Register(u, "LoadImage" + s, CallConv.Stdcall, 6, c => FakeCursor);
                i.Register(u, "GetMonitorInfo" + s, CallConv.Stdcall, 2, c => MonitorInfo(c.Arg(1), w));
                i.Register(u, "EnumDisplaySettings" + s, CallConv.Stdcall, 3, c => DisplaySettings(c.Arg(1), c.Arg(2), w));
                i.Register(u, "EnumDisplaySettingsEx" + s, CallConv.Stdcall, 4, c => DisplaySettings(c.Arg(1), c.Arg(2), w));
                i.Register(u, "EnumDisplayDevices" + s, CallConv.Stdcall, 4, c => DisplayDevice(c.Arg(1), c.Arg(2), w));
                i.Register(u, "ChangeDisplaySettings" + s, CallConv.Stdcall, 2, c => 0);    // DISP_CHANGE_SUCCESSFUL
                i.Register(u, "ChangeDisplaySettingsEx" + s, CallConv.Stdcall, 5, c => 0);
                i.Register(u, "SystemParametersInfo" + s, CallConv.Stdcall, 4, c => SystemParameters(c.Arg(0), c.Arg(2)));
                i.Register(u, "MapVirtualKey" + s, CallConv.Stdcall, 2, c => 0);
                i.Register(u, "MapVirtualKeyEx" + s, CallConv.Stdcall, 3, c => 0);
                i.Register(u, "GetKeyNameText" + s, CallConv.Stdcall, 3, c =>
                {
                    if (c.Arg(2) > 0) WriteText(c.Arg(1), "", w);
                    return 0;
                });
                i.Register(u, "SetWindowsHookEx" + s, CallConv.Stdcall, 4, c => 0x00400001);
                i.Register(u, "RegisterDeviceNotification" + s, CallConv.Stdcall, 3, c => 0x00DE0001);
                i.Register(u, "CharUpper" + s, CallConv.Stdcall, 1, c => ChangeCase(c.Arg(0), true, w));
                i.Register(u, "CharLower" + s, CallConv.Stdcall, 1, c => ChangeCase(c.Arg(0), false, w));
            }

            i.Register(u, "DestroyWindow", CallConv.Stdcall, 1, c => DestroyWindow(c.Arg(0)));
            i.Register(u, "ShowWindow", CallConv.Stdcall, 2, c =>
            {
                if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                var was = win.Visible;
                win.Visible = c.Arg(1) != 0;   // SW_HIDE is 0
                return was ? 1u : 0u;
            });
            i.Register(u, "UpdateWindow", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "InvalidateRect", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "ValidateRect", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "RedrawWindow", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "SetWindowPos", CallConv.Stdcall, 7, c =>
            {
                const uint NoSize = 0x1, NoMove = 0x2;
                if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                if ((c.Arg(6) & NoMove) == 0) { win.X = (int)c.Arg(2); win.Y = (int)c.Arg(3); }
                if ((c.Arg(6) & NoSize) == 0) Resize(win, (int)c.Arg(4), (int)c.Arg(5));
                return 1;
            });
            i.Register(u, "MoveWindow", CallConv.Stdcall, 6, c =>
            {
                if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                win.X = (int)c.Arg(1);
                win.Y = (int)c.Arg(2);
                Resize(win, (int)c.Arg(3), (int)c.Arg(4));
                return 1;
            });
            i.Register(u, "GetClientRect", CallConv.Stdcall, 2, c =>
            {
                if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                WriteRect(c.Arg(1), 0, 0, win.Width, win.Height);
                return 1;
            });
            i.Register(u, "GetWindowRect", CallConv.Stdcall, 2, c =>
            {
                if (!windows.TryGetValue(c.Arg(0), out var win))
                {
                    WriteRect(c.Arg(1), 0, 0, ScreenWidth, ScreenHeight);   // the desktop
                    return 1;
                }
                WriteRect(c.Arg(1), win.X, win.Y, win.X + win.Width, win.Y + win.Height);
                return 1;
            });
            i.Register(u, "ClientToScreen", CallConv.Stdcall, 2, c => MovePoint(c.Arg(0), c.Arg(1), +1));
            i.Register(u, "ScreenToClient", CallConv.Stdcall, 2, c => MovePoint(c.Arg(0), c.Arg(1), -1));
            i.Register(u, "MapWindowPoints", CallConv.Stdcall, 4, c => 0);
            i.Register(u, "AdjustWindowRect", CallConv.Stdcall, 3, c => 1);     // borderless: the client is the window
            i.Register(u, "AdjustWindowRectEx", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "AdjustWindowRectExForDpi", CallConv.Stdcall, 5, c => 1);
            i.Register(u, "GetWindowPlacement", CallConv.Stdcall, 2, c =>
            {
                var p = c.Arg(1);
                windows.TryGetValue(c.Arg(0), out var win);
                memory.Write32(p + 4, 0);
                memory.Write32(p + 8, 1);   // SW_SHOWNORMAL
                WriteRect(p + 28, win?.X ?? 0, win?.Y ?? 0, (win?.X ?? 0) + (win?.Width ?? ScreenWidth), (win?.Y ?? 0) + (win?.Height ?? ScreenHeight));
                return 1;
            });
            i.Register(u, "SetWindowPlacement", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "IsWindow", CallConv.Stdcall, 1, c => windows.ContainsKey(c.Arg(0)) ? 1u : 0u);
            i.Register(u, "IsWindowVisible", CallConv.Stdcall, 1, c => windows.TryGetValue(c.Arg(0), out var win) && win.Visible ? 1u : 0u);
            i.Register(u, "IsWindowEnabled", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "IsWindowUnicode", CallConv.Stdcall, 1, c => windows.TryGetValue(c.Arg(0), out var win) && win.Unicode ? 1u : 0u);
            i.Register(u, "IsIconic", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "IsZoomed", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "IsChild", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "EnableWindow", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "GetParent", CallConv.Stdcall, 1, c => windows.TryGetValue(c.Arg(0), out var win) ? win.Parent : 0);
            i.Register(u, "GetAncestor", CallConv.Stdcall, 2, c => c.Arg(0));
            i.Register(u, "GetWindow", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "GetTopWindow", CallConv.Stdcall, 1, c => InputWindow);
            i.Register(u, "GetDesktopWindow", CallConv.Stdcall, 0, c => 0x00010000);
            i.Register(u, "GetShellWindow", CallConv.Stdcall, 0, c => 0x00010000);
            i.Register(u, "GetForegroundWindow", CallConv.Stdcall, 0, c => InputWindow);
            i.Register(u, "GetActiveWindow", CallConv.Stdcall, 0, c => InputWindow);
            i.Register(u, "GetFocus", CallConv.Stdcall, 0, c => InputWindow);
            i.Register(u, "SetFocus", CallConv.Stdcall, 1, c => InputWindow);
            i.Register(u, "SetActiveWindow", CallConv.Stdcall, 1, c => InputWindow);
            i.Register(u, "SetForegroundWindow", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "BringWindowToTop", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "FlashWindow", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "FlashWindowEx", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "GetWindowThreadProcessId", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(1) != 0) memory.Write32(c.Arg(1), GuestProcess.ProcessId);
                return windows.TryGetValue(c.Arg(0), out var win) ? win.OwnerThread : GuestProcess.MainThreadId;
            });
            i.Register(u, "PostQuitMessage", CallConv.Stdcall, 1, c =>
            {
                Queue(Me).Enqueue(new QueuedMessage { Message = WmQuit, WParam = c.Arg(0) });
                return 0;
            });
            i.Register(u, "TranslateMessage", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "WaitMessage", CallConv.Stdcall, 0, c =>
            {
                if (!HasMessage() && !process.WaitTimedOut(4)) process.Block();
                return 1;
            });
            i.Register(u, "GetQueueStatus", CallConv.Stdcall, 1, c => HasMessage() ? 0x00FF00FFu : 0u);
            i.Register(u, "GetInputState", CallConv.Stdcall, 0, c => HasMessage() ? 1u : 0u);
            i.Register(u, "GetMessageTime", CallConv.Stdcall, 0, c => (uint)Milliseconds);
            i.Register(u, "GetMessagePos", CallConv.Stdcall, 0, c =>
            {
                Input.CursorPosition(out var x, out var y);
                return ((uint)(ushort)y << 16) | (ushort)x;
            });
            i.Register(u, "MsgWaitForMultipleObjects", CallConv.Stdcall, 5, c =>
                MessageWait(Handles(c.Arg(1), c.Arg(0)), c.Arg(2) != 0, c.Arg(3)));
            i.Register(u, "MsgWaitForMultipleObjectsEx", CallConv.Stdcall, 5, c =>
                MessageWait(Handles(c.Arg(1), c.Arg(0)), (c.Arg(4) & 1) != 0, c.Arg(2)));
            i.Register(u, "SetTimer", CallConv.Stdcall, 4, c => SetTimer(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(u, "KillTimer", CallConv.Stdcall, 2, c =>
                timers.RemoveAll(t => t.Window == c.Arg(0) && t.Id == c.Arg(1)) > 0 ? 1u : 0u);

            // The screen and its one monitor.
            i.Register(u, "GetSystemMetrics", CallConv.Stdcall, 1, c => SystemMetric(c.Arg(0)));
            i.Register(u, "GetSystemMetricsForDpi", CallConv.Stdcall, 2, c => SystemMetric(c.Arg(0)));
            i.Register(u, "MonitorFromWindow", CallConv.Stdcall, 2, c => FakeMonitor);
            i.Register(u, "MonitorFromPoint", CallConv.Stdcall, 3, c => FakeMonitor);
            i.Register(u, "MonitorFromRect", CallConv.Stdcall, 2, c => FakeMonitor);
            i.Register(u, "EnumDisplayMonitors", CallConv.Stdcall, 4, c =>
            {
                if (c.Arg(2) == 0) return 1;
                var rect = heap.Alloc(16);
                WriteRect(rect, 0, 0, ScreenWidth, ScreenHeight);
                CallGuest(c.Arg(2), FakeMonitor, c.Arg(0), rect, c.Arg(3));
                heap.Free(rect);
                return 1;
            });
            i.Register(u, "GetDpiForWindow", CallConv.Stdcall, 1, c => 96);
            i.Register(u, "GetDpiForSystem", CallConv.Stdcall, 0, c => 96);
            i.Register(u, "SetProcessDPIAware", CallConv.Stdcall, 0, c => 1);
            i.Register(u, "SetProcessDpiAwarenessContext", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SetThreadDpiAwarenessContext", CallConv.Stdcall, 1, c => 0xFFFFFFFC);
            i.Register(u, "IsProcessDPIAware", CallConv.Stdcall, 0, c => 1);
            i.Register(u, "GetDC", CallConv.Stdcall, 1, c => FakeDc);
            i.Register(u, "GetWindowDC", CallConv.Stdcall, 1, c => FakeDc);
            i.Register(u, "GetDCEx", CallConv.Stdcall, 3, c => FakeDc);
            i.Register(u, "ReleaseDC", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "GetSysColor", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "GetSysColorBrush", CallConv.Stdcall, 1, c => 0x0B000001);
            i.Register(u, "MessageBeep", CallConv.Stdcall, 1, c => 1);

            // Cursor and keyboard, from the host's input.
            i.Register(u, "GetCursorPos", CallConv.Stdcall, 1, c =>
            {
                Input.CursorPosition(out var x, out var y);
                memory.Write32(c.Arg(0), (uint)x);
                memory.Write32(c.Arg(0) + 4, (uint)y);
                return 1;
            });
            i.Register(u, "GetPhysicalCursorPos", CallConv.Stdcall, 1, c =>
            {
                Input.CursorPosition(out var x, out var y);
                memory.Write32(c.Arg(0), (uint)x);
                memory.Write32(c.Arg(0) + 4, (uint)y);
                return 1;
            });
            i.Register(u, "SetCursorPos", CallConv.Stdcall, 2, c => { Input.SetCursorPosition((int)c.Arg(0), (int)c.Arg(1)); return 1; });
            i.Register(u, "ShowCursor", CallConv.Stdcall, 1, c => (uint)(c.Arg(0) != 0 ? ++cursorCount : --cursorCount));
            i.Register(u, "SetCursor", CallConv.Stdcall, 1, c => { var was = cursor; cursor = c.Arg(0); return was; });
            i.Register(u, "GetCursor", CallConv.Stdcall, 0, c => cursor);
            i.Register(u, "ClipCursor", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "GetClipCursor", CallConv.Stdcall, 1, c => { WriteRect(c.Arg(0), 0, 0, ScreenWidth, ScreenHeight); return 1; });
            i.Register(u, "DestroyCursor", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "DestroyIcon", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SetCapture", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "ReleaseCapture", CallConv.Stdcall, 0, c => 1);
            i.Register(u, "GetCapture", CallConv.Stdcall, 0, c => 0);
            i.Register(u, "TrackMouseEvent", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "GetAsyncKeyState", CallConv.Stdcall, 1, c => Input.KeyDown((int)(c.Arg(0) & 0xFF)) ? 0xFFFF8000u : 0u);
            i.Register(u, "GetKeyState", CallConv.Stdcall, 1, c => Input.KeyDown((int)(c.Arg(0) & 0xFF)) ? 0xFFFF8000u : 0u);
            i.Register(u, "GetKeyboardState", CallConv.Stdcall, 1, c =>
            {
                for (var k = 0; k < 256; k++) memory.Write8(c.Arg(0) + (uint)k, Input.KeyDown(k) ? (byte)0x80 : (byte)0);
                return 1;
            });
            i.Register(u, "GetKeyboardLayout", CallConv.Stdcall, 1, c => 0x04090409);
            i.Register(u, "GetKeyboardLayoutList", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(0) > 0 && c.Arg(1) != 0) memory.Write32(c.Arg(1), 0x04090409);
                return 1;
            });
            i.Register(u, "GetKeyboardType", CallConv.Stdcall, 1, c => c.Arg(0) == 0 ? 4u : c.Arg(0) == 2 ? 12u : 0u);
            i.Register(u, "ToUnicode", CallConv.Stdcall, 6, c => 0);
            i.Register(u, "ToAscii", CallConv.Stdcall, 5, c => 0);
            i.Register(u, "RegisterRawInputDevices", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "GetRawInputData", CallConv.Stdcall, 5, c => 0);
            i.Register(u, "GetRawInputDeviceList", CallConv.Stdcall, 3, c =>
            {
                if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0);
                return 0;
            });
            i.Register(u, "UnhookWindowsHookEx", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "CallNextHookEx", CallConv.Stdcall, 4, c => 0);
            i.Register(u, "UnregisterDeviceNotification", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "OpenClipboard", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "IsClipboardFormatAvailable", CallConv.Stdcall, 1, c => 0);

            // gdi32: what games ask about the screen through a DC.
            i.Register("gdi32.dll", "GetDeviceCaps", CallConv.Stdcall, 2, c =>
            {
                switch (c.Arg(1))
                {
                    case 8: return (uint)ScreenWidth;    // HORZRES
                    case 10: return (uint)ScreenHeight;  // VERTRES
                    case 12: return 32;                  // BITSPIXEL
                    case 14: return 1;                   // PLANES
                    case 88: case 90: return 96;         // LOGPIXELSX/Y
                    case 116: return 60;                 // VREFRESH
                    case 117: return (uint)ScreenHeight; // DESKTOPVERTRES
                    case 118: return (uint)ScreenWidth;  // DESKTOPHORZRES
                    default: return 0;
                }
            });
            i.Register("gdi32.dll", "GetStockObject", CallConv.Stdcall, 1, c => 0x0B000001);
            i.Register("gdi32.dll", "DeleteObject", CallConv.Stdcall, 1, c => 1);
        }

        // --- classes and windows ---------------------------------------------

        private uint RegisterClass(uint description, bool extended, bool wide)
        {
            // WNDCLASS: style, lpfnWndProc, cbClsExtra, cbWndExtra, hInstance,
            // hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName.
            // WNDCLASSEX adds cbSize in front (and hIconSm at the end).
            var p = extended ? description + 4 : description;
            var namePtr = memory.Read32(p + 36);
            var name = namePtr < 0x10000 ? "#" + namePtr : ReadText(namePtr, wide);
            if (windowClasses.TryGetValue(name, out var existing)) { process.LastError = 1410; return 0; }   // ERROR_CLASS_ALREADY_EXISTS
            var entry = new WindowClass
            {
                Name = name,
                Atom = 0xC001 + (uint)windowClasses.Count,
                Style = memory.Read32(p),
                Procedure = memory.Read32(p + 4),
                WindowExtra = (int)memory.Read32(p + 12),
                Instance = memory.Read32(p + 16),
            };
            windowClasses[name] = entry;
            classAtoms[entry.Atom] = entry;
            return entry.Atom;
        }

        private WindowClass ClassFor(uint nameOrAtom, bool wide)
        {
            if (nameOrAtom < 0x10000) return classAtoms.TryGetValue(nameOrAtom, out var byAtom) ? byAtom : null;
            return windowClasses.TryGetValue(ReadText(nameOrAtom, wide), out var byName) ? byName : null;
        }

        private uint CreateWindow(GuestCall c, bool wide)
        {
            uint exStyle = c.Arg(0), style = c.Arg(3), parent = c.Arg(8);
            var entry = ClassFor(c.Arg(1), wide);
            if (entry == null) { process.LastError = 1407; return 0; }   // ERROR_CANNOT_FIND_WND_CLASS

            int x = (int)c.Arg(4), y = (int)c.Arg(5), width = (int)c.Arg(6), height = (int)c.Arg(7);
            if (c.Arg(4) == CwUseDefault) { x = 0; y = 0; }
            if (c.Arg(6) == CwUseDefault || width <= 0 || height <= 0) { width = ScreenWidth; height = ScreenHeight; }

            var win = new Window
            {
                Handle = nextWindow,
                Class = entry,
                Procedure = entry.Procedure,
                Parent = parent,
                OwnerThread = Me,
                Instance = c.Arg(10),
                Menu = c.Arg(9),
                Style = style,
                ExStyle = exStyle,
                Text = ReadText(c.Arg(2), wide),
                Unicode = wide,
                Visible = (style & WsVisible) != 0,
                X = x, Y = y, Width = width, Height = height,
            };
            nextWindow += 4;
            windows[win.Handle] = win;

            // CREATESTRUCT for WM_NCCREATE/WM_CREATE: lpCreateParams, hInstance,
            // hMenu, hwndParent, cy, cx, y, x, style, lpszName, lpszClass, dwExStyle.
            var cs = heap.Alloc(48, zero: true);
            memory.Write32(cs + 0, c.Arg(11));
            memory.Write32(cs + 4, win.Instance);
            memory.Write32(cs + 8, win.Menu);
            memory.Write32(cs + 12, parent);
            memory.Write32(cs + 16, (uint)height);
            memory.Write32(cs + 20, (uint)width);
            memory.Write32(cs + 24, (uint)y);
            memory.Write32(cs + 28, (uint)x);
            memory.Write32(cs + 32, style);
            memory.Write32(cs + 36, c.Arg(2));
            memory.Write32(cs + 40, c.Arg(1));
            memory.Write32(cs + 44, exStyle);
            var created = CallGuest(win.Procedure, win.Handle, WmNcCreate, 0, cs) != 0 &&
                          CallGuest(win.Procedure, win.Handle, WmCreate, 0, cs) != 0xFFFFFFFF;
            heap.Free(cs);
            if (!created)
            {
                windows.Remove(win.Handle);
                return 0;
            }

            // The first top-level window is the game's: input goes there, and
            // it is told what a desktop tells a new window — shown, active,
            // focused, and its size — because an engine waits to hear it.
            if ((style & WsChild) == 0 && InputWindow == 0)
            {
                InputWindow = win.Handle;
                win.Visible = true;
                Post(win.Handle, WmShowWindow, 1, 0);
                Post(win.Handle, WmActivateApp, 1, 0);
                Post(win.Handle, WmActivate, 1, 0);
                Post(win.Handle, WmSetFocus, 0, 0);
                Post(win.Handle, WmSize, 0, SizeParam(width, height));
            }
            return win.Handle;
        }

        private static uint SizeParam(int width, int height) => ((uint)(ushort)height << 16) | (ushort)width;

        private void Resize(Window win, int width, int height)
        {
            if (width <= 0 || height <= 0 || (width == win.Width && height == win.Height)) return;
            win.Width = width;
            win.Height = height;
            Post(win.Handle, WmSize, 0, SizeParam(width, height));
        }

        private uint DestroyWindow(uint handle)
        {
            if (!windows.TryGetValue(handle, out var win)) return 0;
            CallGuest(win.Procedure, handle, WmDestroy, 0, 0);
            windows.Remove(handle);
            timers.RemoveAll(t => t.Window == handle);
            if (InputWindow == handle) InputWindow = 0;
            return 1;
        }

        private uint GetWindowLong(uint handle, int index)
        {
            if (!windows.TryGetValue(handle, out var win)) { process.LastError = 1400; return 0; }   // ERROR_INVALID_WINDOW_HANDLE
            switch (index)
            {
                case -4: return win.Procedure;     // GWL_WNDPROC
                case -6: return win.Instance;      // GWL_HINSTANCE
                case -8: return win.Parent;        // GWL_HWNDPARENT
                case -12: return win.Menu;         // GWL_ID
                case -16: return win.Style;        // GWL_STYLE
                case -20: return win.ExStyle;      // GWL_EXSTYLE
                default: return win.Longs.TryGetValue(index, out var v) ? v : 0;   // GWL_USERDATA, extra bytes
            }
        }

        private uint SetWindowLong(uint handle, int index, uint value)
        {
            if (!windows.TryGetValue(handle, out var win)) { process.LastError = 1400; return 0; }
            var previous = GetWindowLong(handle, index);
            switch (index)
            {
                case -4: win.Procedure = value; break;
                case -6: win.Instance = value; break;
                case -8: win.Parent = value; break;
                case -12: win.Menu = value; break;
                case -16: win.Style = value; break;
                case -20: win.ExStyle = value; break;
                default: win.Longs[index] = value; break;
            }
            return previous;
        }

        private string PropName(uint nameOrAtom, bool wide) => nameOrAtom < 0x10000 ? "#" + nameOrAtom : ReadText(nameOrAtom, wide);

        private uint RegisterMessage(string name)
        {
            if (!registeredMessages.TryGetValue(name, out var id))
            {
                id = 0xC000 + (uint)registeredMessages.Count;
                registeredMessages[name] = id;
            }
            return id;
        }

        private uint MovePoint(uint handle, uint point, int sign)
        {
            if (!windows.TryGetValue(handle, out var win)) return 1;
            memory.Write32(point, (uint)((int)memory.Read32(point) + sign * win.X));
            memory.Write32(point + 4, (uint)((int)memory.Read32(point + 4) + sign * win.Y));
            return 1;
        }

        private void WriteRect(uint p, int left, int top, int right, int bottom)
        {
            if (p == 0) return;
            memory.Write32(p, (uint)left);
            memory.Write32(p + 4, (uint)top);
            memory.Write32(p + 8, (uint)right);
            memory.Write32(p + 12, (uint)bottom);
        }

        // --- messages -----------------------------------------------------------

        private Queue<QueuedMessage> Queue(uint thread)
        {
            if (!queues.TryGetValue(thread, out var q)) queues[thread] = q = new Queue<QueuedMessage>();
            return q;
        }

        private uint Post(uint handle, uint message, uint wParam, uint lParam)
        {
            var thread = windows.TryGetValue(handle, out var win) ? win.OwnerThread : Me;
            Queue(thread).Enqueue(new QueuedMessage { Window = handle, Message = message, WParam = wParam, LParam = lParam });
            return 1;
        }

        private bool OwnsInput => InputWindow != 0 && windows.TryGetValue(InputWindow, out var win) && win.OwnerThread == Me;

        private bool HasMessage() =>
            (queues.TryGetValue(Me, out var q) && q.Count > 0) ||
            (OwnsInput && Input.TakeMessage(false, out _, out _, out _)) ||
            DueTimer(false) != null;

        private static bool InRange(uint message, uint first, uint last) =>
            (first == 0 && last == 0) || (message >= first && message <= last);

        /// <summary>
        /// PeekMessage: this thread's posted messages first, then the host's
        /// input (for the thread that owns the game's window), then timers.
        /// </summary>
        private bool NextMessage(uint msg, uint window, uint first, uint last, bool remove)
        {
            if (queues.TryGetValue(Me, out var q) && q.Count > 0)
            {
                var head = q.Peek();
                if ((window == 0 || head.Window == window || head.Message == WmQuit) && InRange(head.Message, first, last))
                {
                    if (remove) q.Dequeue();
                    WriteMessage(msg, head.Window, head.Message, head.WParam, head.LParam);
                    return true;
                }
            }
            if (OwnsInput && (window == 0 || window == InputWindow) &&
                Input.TakeMessage(false, out var m, out _, out _) && InRange(m, first, last))
            {
                Input.TakeMessage(remove, out m, out var wp, out var lp);
                WriteMessage(msg, InputWindow, m, wp, lp);
                return true;
            }
            var timer = DueTimer(remove);
            if (timer != null && (window == 0 || window == timer.Window) && InRange(WmTimer, first, last))
            {
                WriteMessage(msg, timer.Window, WmTimer, timer.Id, timer.Procedure);
                return true;
            }
            return false;
        }

        private void WriteMessage(uint msg, uint window, uint message, uint wParam, uint lParam)
        {
            if (msg == 0) return;
            Input.CursorPosition(out var x, out var y);
            memory.Write32(msg + 0, window);
            memory.Write32(msg + 4, message);
            memory.Write32(msg + 8, wParam);
            memory.Write32(msg + 12, lParam);
            memory.Write32(msg + 16, (uint)Milliseconds);
            memory.Write32(msg + 20, (uint)x);
            memory.Write32(msg + 24, (uint)y);
        }

        /// <summary>
        /// GetMessage: a message, or 0 for WM_QUIT. Nothing may ever arrive on
        /// a console, so after a short wait (other threads run meanwhile) the
        /// loop is handed WM_NULL and turns once more instead of hanging.
        /// </summary>
        private uint GetMessage(uint msg, uint window, uint first, uint last)
        {
            if (NextMessage(msg, window, first, last, true))
                return memory.Read32(msg + 4) == WmQuit ? 0u : 1u;
            if (!process.WaitTimedOut(4)) { process.Block(); return 0; }
            WriteMessage(msg, InputWindow, WmNull, 0, 0);
            return 1;
        }

        private uint DispatchMessage(GuestCall c)
        {
            var msg = c.Arg(0);
            uint window = memory.Read32(msg), message = memory.Read32(msg + 4);
            uint wParam = memory.Read32(msg + 8), lParam = memory.Read32(msg + 12);
            MessagesDispatched++;
            if (message == WmTimer && lParam != 0)
            {
                // TIMERPROC(hwnd, WM_TIMER, id, time)
                TailCall(c, 1, lParam, window, WmTimer, wParam, (uint)Milliseconds);
                return 0;
            }
            if (!windows.TryGetValue(window, out var win) || win.Procedure == 0) return 0;
            TailCall(c, 1, win.Procedure, window, message, wParam, lParam);
            return 0;
        }

        private uint SendMessage(GuestCall c, int argDwords, uint window, uint message, uint wParam, uint lParam)
        {
            if (!windows.TryGetValue(window, out var win) || win.Procedure == 0)
                return DefWindowProc(window, message, wParam, lParam);
            TailCall(c, argDwords, win.Procedure, window, message, wParam, lParam);
            return 0;
        }

        private uint DefWindowProc(uint window, uint message, uint wParam, uint lParam)
        {
            switch (message)
            {
                case WmNcCreate: return 1;               // TRUE, or creation fails
                case WmClose: DestroyWindow(window); return 0;
                case WmNcHitTest: return 1;              // HTCLIENT
                default: return 0;
            }
        }

        private uint SetTimer(uint window, uint id, uint interval, uint procedure)
        {
            if (window == 0 || id == 0) id = nextTimerId++;
            timers.RemoveAll(t => t.Window == window && t.Id == id);
            var ms = Math.Max(interval, 10u);
            timers.Add(new Timer { Window = window, Id = id, Interval = ms, Procedure = procedure, Thread = Me, Due = uiClock.ElapsedMilliseconds + ms });
            return id;
        }

        private Timer DueTimer(bool take)
        {
            var now = uiClock.ElapsedMilliseconds;
            foreach (var t in timers)
            {
                if (t.Thread != Me || t.Due > now) continue;
                if (take) t.Due = now + t.Interval;
                return t;
            }
            return null;
        }

        private uint MessageWait(uint[] handles, bool all, uint timeout)
        {
            var me = Me;
            if (handles.Length > 0)
            {
                if (all)
                {
                    var every = true;
                    foreach (var h in handles) { var w = Object(h); if (w != null && !w.Ready(me)) { every = false; break; } }
                    if (every) { foreach (var h in handles) Object(h)?.Consume(me); return WaitObject0; }
                }
                else
                {
                    for (var n = 0; n < handles.Length; n++)
                    {
                        var w = Object(handles[n]);
                        if (w == null) return (uint)n;
                        if (!w.Ready(me)) continue;
                        w.Consume(me);
                        return (uint)n;
                    }
                }
            }
            if (HasMessage()) return (uint)handles.Length;   // WAIT_OBJECT_0 + count: read the queue
            if (process.WaitTimedOut(timeout)) return WaitTimeout;
            process.Block();
            return 0;
        }

        // --- the screen ------------------------------------------------------------

        private uint SystemMetric(uint index)
        {
            switch (index)
            {
                case 0: case 16: case 78: return (uint)ScreenWidth;    // SM_CXSCREEN, SM_CXFULLSCREEN, SM_CXVIRTUALSCREEN
                case 1: case 17: case 79: return (uint)ScreenHeight;   // SM_CYSCREEN, SM_CYFULLSCREEN, SM_CYVIRTUALSCREEN
                case 4: return 23;           // SM_CYCAPTION
                case 5: case 6: return 1;    // SM_CXBORDER, SM_CYBORDER
                case 13: case 14: return 32; // SM_CXCURSOR, SM_CYCURSOR
                case 19: return 1;           // SM_MOUSEPRESENT
                case 32: case 33: return 8;  // SM_CXFRAME, SM_CYFRAME
                case 36: case 37: return 4;  // SM_CXDOUBLECLK, SM_CYDOUBLECLK
                case 43: return 3;           // SM_CMOUSEBUTTONS
                case 68: case 69: return 4;  // SM_CXDRAG, SM_CYDRAG
                case 80: return 1;           // SM_CMONITORS
                case 81: return 1;           // SM_SAMEDISPLAYFORMAT
                default: return 0;
            }
        }

        private uint MonitorInfo(uint info, bool wide)
        {
            var size = memory.Read32(info);
            WriteRect(info + 4, 0, 0, ScreenWidth, ScreenHeight);    // rcMonitor
            WriteRect(info + 20, 0, 0, ScreenWidth, ScreenHeight);   // rcWork
            memory.Write32(info + 36, 1);                            // MONITORINFOF_PRIMARY
            if (size >= (wide ? 104u : 72u)) WriteText(info + 40, "\\\\.\\DISPLAY1", wide);
            return 1;
        }

        private static readonly int[,] Modes = { { 1280, 720 }, { 1600, 900 }, { 1920, 1080 } };

        private uint DisplaySettings(uint mode, uint devmode, bool wide)
        {
            int width, height;
            if (mode == 0xFFFFFFFF || mode == 0xFFFFFFFE) { width = ScreenWidth; height = ScreenHeight; }   // current, registry
            else if (mode < Modes.GetLength(0)) { width = Modes[mode, 0]; height = Modes[mode, 1]; }
            else return 0;

            // DEVMODE: the A form's two name arrays are half the size, which
            // moves everything after each of them.
            var shift = wide ? 0u : 32u;
            memory.Write16(devmode + 68 - shift, (ushort)(wide ? 220 : 156));                  // dmSize
            memory.Write32(devmode + 72 - shift, 0x00040000 | 0x00080000 | 0x00100000 | 0x00400000);   // dmFields
            var tail = wide ? 0u : 64u;
            memory.Write32(devmode + 168 - tail, 32);           // dmBitsPerPel
            memory.Write32(devmode + 172 - tail, (uint)width);  // dmPelsWidth
            memory.Write32(devmode + 176 - tail, (uint)height); // dmPelsHeight
            memory.Write32(devmode + 180 - tail, 0);            // dmDisplayFlags
            memory.Write32(devmode + 184 - tail, 60);           // dmDisplayFrequency
            return 1;
        }

        private uint DisplayDevice(uint index, uint device, bool wide)
        {
            if (index != 0) return 0;
            // DISPLAY_DEVICE: cb, DeviceName[32], DeviceString[128], StateFlags, DeviceID[128], DeviceKey[128].
            var ch = wide ? 2u : 1u;
            WriteText(device + 4, "\\\\.\\DISPLAY1", wide);
            WriteText(device + 4 + 32 * ch, "Nativra Display", wide);
            memory.Write32(device + 4 + 160 * ch, 0x1 | 0x4);   // attached to the desktop, primary
            WriteText(device + 8 + 160 * ch, "", wide);
            return 1;
        }

        private uint SystemParameters(uint action, uint output)
        {
            switch (action)
            {
                case 0x30: WriteRect(output, 0, 0, ScreenWidth, ScreenHeight); return 1;   // SPI_GETWORKAREA
                case 0x10: if (output != 0) memory.Write32(output, 0); return 1;           // SPI_GETSCREENSAVEACTIVE
                case 0x70: if (output != 0) memory.Write32(output, 10); return 1;          // SPI_GETMOUSESPEED
                default: return 1;
            }
        }

        private uint ChangeCase(uint text, bool upper, bool wide)
        {
            if (text < 0x10000)
            {
                var ch = (char)text;
                return upper ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch);
            }
            var s = ReadText(text, wide);
            WriteText(text, upper ? s.ToUpperInvariant() : s.ToLowerInvariant(), wide);
            return text;
        }

        // --- calling guest code -------------------------------------------------

        /// <summary>
        /// Replaces the current stdcall import frame (return address plus
        /// <paramref name="importArgs"/> arguments) with a stdcall call to
        /// <paramref name="target"/>, which returns straight to the import's
        /// caller: the guest runs it on its own stack, no nested run.
        /// </summary>
        private void TailCall(GuestCall c, int importArgs, uint target, params uint[] args)
        {
            var cpu = process.Cpu;
            var esp = c.ArgBase + (uint)importArgs * 4;
            for (var n = args.Length - 1; n >= 0; n--)
            {
                esp -= 4;
                memory.Write32(esp, args[n]);
            }
            esp -= 4;
            memory.Write32(esp, c.ReturnAddress);
            cpu.Esp = esp;
            cpu.Eip = target;
            process.Jumped();
        }

        /// <summary>A nested stdcall into guest code, for answers a host call needs before it returns.</summary>
        private uint CallGuest(uint function, params uint[] args)
        {
            if (function == 0) return 0;
            var saved = SaveRegisters();
            var result = process.Call(function, out var eax, 50_000_000, args);
            RestoreRegisters(saved);
            if (!result.Ok) Say($"x86: guest callback 0x{function:X8} stopped: {result}");
            return result.Ok ? eax : 0;
        }
    }
}
