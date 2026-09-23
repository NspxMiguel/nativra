using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>Window class registration, subclassing and message dispatch for hosted games.</summary>
    public static class WindowMessages
    {
        private sealed class WindowClass
        {
            public string Name;
            public ushort Atom;
            public IntPtr Procedure;
        }

        private sealed class WindowState
        {
            public readonly Dictionary<int, IntPtr> Values = new Dictionary<int, IntPtr>();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate ushort RegisterDelegate(IntPtr description);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr CreateDelegate(uint extendedStyle, IntPtr name, IntPtr title,
            uint style, int x, int y, int width, int height, IntPtr parent,
            IntPtr menu, IntPtr instance, IntPtr parameter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr ProcedureDelegate(IntPtr window, uint message, IntPtr word, IntPtr extra);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr CallDelegate(IntPtr procedure, IntPtr window, uint message, IntPtr word, IntPtr extra);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr DispatchDelegate(IntPtr message);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr GetLongDelegate(IntPtr window, int index);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr SetLongDelegate(IntPtr window, int index, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr ActiveDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int KeyboardDelegate(IntPtr keys);

        private static readonly object gate = new object();
        private static readonly Dictionary<string, WindowClass> classes =
            new Dictionary<string, WindowClass>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<long, WindowClass> atoms = new Dictionary<long, WindowClass>();
        private static readonly Dictionary<IntPtr, WindowState> windows = new Dictionary<IntPtr, WindowState>();
        private static readonly List<Delegate> roots = new List<Delegate>();
        private static long nextWindow = 0x00BA5E11;
        public static IntPtr InputWindow = (IntPtr)0x00BA5E11;
        public static long Dispatched;
        public static string Note = "no window";

        private static string Name(IntPtr value, bool wide) => value.ToInt64() <= 0xFFFF
            ? null : wide ? Marshal.PtrToStringUni(value) : Marshal.PtrToStringAnsi(value);

        private static ushort Register(IntPtr description, bool wide)
        {
            if (description == IntPtr.Zero) return 0;
            var name = Name(Marshal.ReadIntPtr(description, 64), wide);
            if (string.IsNullOrEmpty(name)) return 0;
            lock (gate)
            {
                if (classes.TryGetValue(name, out var existing)) return existing.Atom;
                var entry = new WindowClass
                {
                    Name = name,
                    Atom = (ushort)(0xC001 + classes.Count),
                    Procedure = Marshal.ReadIntPtr(description, 8),
                };
                classes[name] = entry;
                atoms[entry.Atom] = entry;
                return entry.Atom;
            }
        }

        private static IntPtr Create(uint extendedStyle, IntPtr name, uint style, int width,
            int height, IntPtr parent, IntPtr menu, IntPtr instance, bool wide)
        {
            lock (gate)
            {
                WindowClass entry;
                var text = Name(name, wide);
                if (text == null) atoms.TryGetValue(name.ToInt64(), out entry);
                else classes.TryGetValue(text, out entry);
                if (entry == null) return IntPtr.Zero;
                var handle = (IntPtr)nextWindow++;
                var state = new WindowState();
                state.Values[-4] = entry.Procedure; // GWLP_WNDPROC
                state.Values[-6] = instance;
                state.Values[-12] = menu;
                state.Values[-16] = (IntPtr)(long)style;
                state.Values[-20] = (IntPtr)(long)extendedStyle;
                windows[handle] = state;
                if (entry.Name.IndexOf("UnityWndClass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (parent == IntPtr.Zero && width > 0 && height > 0))
                    InputWindow = handle;
                Note = entry.Name + " hwnd=0x" + handle.ToInt64().ToString("X");
                return handle;
            }
        }

        private static IntPtr GetLong(IntPtr window, int index)
        {
            lock (gate)
                return windows.TryGetValue(window, out var state) && state.Values.TryGetValue(index, out var value)
                    ? value : IntPtr.Zero;
        }

        private static IntPtr SetLong(IntPtr window, int index, IntPtr value)
        {
            lock (gate)
            {
                if (!windows.TryGetValue(window, out var state)) return IntPtr.Zero;
                state.Values.TryGetValue(index, out var previous);
                state.Values[index] = value;
                return previous;
            }
        }

        private static IntPtr Call(IntPtr procedure, IntPtr window, uint message, IntPtr word, IntPtr extra)
        {
            if (procedure == IntPtr.Zero) return IntPtr.Zero;
            var callback = Marshal.GetDelegateForFunctionPointer<ProcedureDelegate>(procedure);
            return callback(window, message, word, extra);
        }

        public static void Install(SystemImports imports)
        {
            void Bind(string name, Delegate function)
            {
                roots.Add(function);
                imports.Overrides["user32.dll!" + name] = Marshal.GetFunctionPointerForDelegate(function);
            }

            foreach (var wide in new[] { false, true })
            {
                var unicode = wide;
                var suffix = wide ? "W" : "A";
                RegisterDelegate register = description => Register(description, unicode);
                Bind("RegisterClass" + suffix, register);
                Bind("RegisterClassEx" + suffix, register);
                Bind("CreateWindowEx" + suffix, new CreateDelegate((ex, name, title, style, x, y, w, h, parent, menu, instance, parameter) =>
                    Create(ex, name, style, w, h, parent, menu, instance, unicode)));
                Bind("GetWindowLong" + suffix, new GetLongDelegate(GetLong));
                Bind("GetWindowLongPtr" + suffix, new GetLongDelegate(GetLong));
                Bind("SetWindowLong" + suffix, new SetLongDelegate(SetLong));
                Bind("SetWindowLongPtr" + suffix, new SetLongDelegate(SetLong));
                Bind("CallWindowProc" + suffix, new CallDelegate(Call));
                Bind("DispatchMessage" + suffix, new DispatchDelegate(message =>
                {
                    if (message == IntPtr.Zero) return IntPtr.Zero;
                    var window = Marshal.ReadIntPtr(message);
                    var procedure = GetLong(window, -4);
                    if (procedure == IntPtr.Zero) return IntPtr.Zero;
                    Dispatched++;
                    var kind = (uint)Marshal.ReadInt32(message, 8);
                    var extra = Marshal.ReadIntPtr(message, 24);
                    try { return Call(procedure, window, kind, Marshal.ReadIntPtr(message, 16), extra); }
                    finally { if (kind == 0xFF) RawInputBridge.Release(extra.ToInt64()); }
                }));
                Bind("SendMessage" + suffix, new ProcedureDelegate((window, message, word, extra) =>
                    Call(GetLong(window, -4), window, message, word, extra)));
            }
            foreach (var name in new[] { "GetActiveWindow", "GetForegroundWindow", "GetFocus" })
                Bind(name, new ActiveDelegate(() => InputWindow));
            Bind("GetKeyboardState", new KeyboardDelegate(keys =>
            {
                if (keys == IntPtr.Zero) return 0;
                for (var key = 0; key < 256; key++)
                {
                    var down = PointerBridge.Down(key) || (key == 1 && PointerBridge.Left) ||
                        (key == 2 && PointerBridge.Right);
                    Marshal.WriteByte(keys, key, down ? (byte)0x80 : (byte)0);
                }
                return 1;
            }));
        }
    }
}
