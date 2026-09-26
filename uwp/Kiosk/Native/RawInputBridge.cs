using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>Controller-backed Win32 raw mouse and keyboard events, not physical devices.</summary>
    internal static class RawInputBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RegisterDelegate(IntPtr devices, uint count, uint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ListDelegate(IntPtr devices, IntPtr count, uint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint DataDelegate(IntPtr handle, uint command, IntPtr data, IntPtr size, uint header);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint InfoDelegate(IntPtr handle, uint command, IntPtr data, IntPtr size);
        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern void SetLastError(uint error);

        private sealed class Registration
        {
            public IntPtr Window;
            public uint Flags;
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<int, Registration> registrations = new Dictionary<int, Registration>();
        private static readonly Dictionary<long, byte[]> packets = new Dictionary<long, byte[]>();
        private static readonly List<Delegate> roots = new List<Delegate>();
        private static long serial = 0x71000000;
        private static readonly Queue<long> packetOrder = new Queue<long>();
        public static long Posted;
        public static long Read;
        public static long Registered;

        private static uint Fail(uint error)
        {
            SetLastError(error);
            return uint.MaxValue;
        }

        private static void Put(byte[] data, int offset, int value) =>
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, offset, 4);

        private static void Emit(int type, byte[] data)
        {
            IntPtr target;
            long handle;
            lock (gate)
            {
                if (!registrations.TryGetValue(type, out var registration)) return;
                target = registration.Window == IntPtr.Zero ? WindowMessages.InputWindow : registration.Window;
                Put(data, 0, type);
                Put(data, 4, data.Length);
                Put(data, 8, 0x700001 + type);
                handle = ++serial;
                // Bound storage even when a caller consumes messages without dispatching them.
                while (packetOrder.Count >= 256) packets.Remove(packetOrder.Dequeue());
                packetOrder.Enqueue(handle);
                packets[handle] = data;
                Posted++;
            }
            PointerBridge.PostRaw(target, handle);
        }

        public static void Release(long handle)
        {
            lock (gate) packets.Remove(handle);
        }

        public static bool SuppressesLegacy(int type)
        {
            lock (gate) return registrations.TryGetValue(type, out var registration) &&
                (registration.Flags & 0x30) == 0x30;
        }

        public static void Mouse(int dx, int dy, int buttons)
        {
            var data = new byte[48]; // RAWINPUTHEADER (24) + RAWMOUSE (24).
            Put(data, 28, buttons);
            Put(data, 36, dx);
            Put(data, 40, dy);
            Emit(0, data);
        }

        public static void Keyboard(int key, bool down)
        {
            var data = new byte[40]; // RAWINPUTHEADER (24) + RAWKEYBOARD (16).
            var scan = KeyboardMessages.ScanCode(key);
            if (scan == 0) return;
            var flags = (down ? 0 : 1) | (KeyboardMessages.IsExtended(key) ? 2 : 0);
            Put(data, 24, scan | (flags << 16));
            Put(data, 28, key << 16);
            Put(data, 32, down ? 0x100 : 0x101);
            Emit(1, data);
        }

        private static int Register(IntPtr devices, uint count, uint size)
        {
            if (size != 16 || devices == IntPtr.Zero || count > 64) { Fail(87); return 0; }
            var changes = new Dictionary<int, Registration>();
            for (var i = 0; i < count; i++)
            {
                var item = devices + i * 16;
                var page = Marshal.ReadInt16(item);
                var usage = Marshal.ReadInt16(item, 2);
                var flags = unchecked((uint)Marshal.ReadInt32(item, 4));
                var window = Marshal.ReadIntPtr(item, 8);
                if ((flags & 1) != 0 && window != IntPtr.Zero) { Fail(87); return 0; }
                // Joysticks, gamepads and other HID pages are accepted and not
                // fed: pads reach games through XInput, which SDL also reads.
                // Refusing them failed SDL_Init outright, for Hades, on the
                // "unregister" SDL does before it registers.
                if (page != 1 || (usage != 2 && usage != 6)) continue;
                changes[usage == 2 ? 0 : 1] = (flags & 1) != 0 ? null : new Registration { Window = window, Flags = flags };
            }
            lock (gate)
            {
                foreach (var change in changes)
                    if (change.Value == null) registrations.Remove(change.Key);
                    else registrations[change.Key] = change.Value;
                Registered = registrations.Count;
            }
            return 1;
        }

        private static uint GetData(IntPtr handle, uint command, IntPtr data, IntPtr size, uint header)
        {
            if (size == IntPtr.Zero || header != 24 || (command != 0x10000003 && command != 0x10000005)) return Fail(87);
            byte[] packet;
            lock (gate) if (!packets.TryGetValue(handle.ToInt64(), out packet)) return Fail(6);
            var required = command == 0x10000005 ? 24 : packet.Length;
            var capacity = Marshal.ReadInt32(size);
            Marshal.WriteInt32(size, required);
            if (data == IntPtr.Zero) return 0;
            if (capacity < required) return Fail(122);
            Marshal.Copy(packet, 0, data, required);
            System.Threading.Interlocked.Increment(ref Read);
            return (uint)required;
        }

        private static uint GetList(IntPtr devices, IntPtr count, uint size)
        {
            if (count == IntPtr.Zero || size != 16) return Fail(87);
            var capacity = Marshal.ReadInt32(count);
            Marshal.WriteInt32(count, 2);
            if (devices == IntPtr.Zero) return 0;
            if (capacity < 2) return Fail(122);
            for (var type = 0; type < 2; type++)
            {
                Marshal.WriteInt64(devices, type * 16, 0x700001 + type);
                Marshal.WriteInt32(devices, type * 16 + 8, type);
                Marshal.WriteInt32(devices, type * 16 + 12, 0);
            }
            return 2;
        }

        private static uint Info(IntPtr handle, uint command, IntPtr data, IntPtr size, bool wide)
        {
            var type = handle.ToInt64() - 0x700001;
            if (type < 0 || type > 1) return Fail(6);
            if (size == IntPtr.Zero) return Fail(87);
            var capacity = Marshal.ReadInt32(size);
            if (command == 0x20000007) // RIDI_DEVICENAME counts characters, not bytes.
            {
                var name = type == 0 ? @"\\?\NATIVRA#MOUSE" : @"\\?\NATIVRA#KEYBOARD";
                Marshal.WriteInt32(size, name.Length + 1);
                if (data == IntPtr.Zero) return 0;
                if (capacity < name.Length + 1) return Fail(122);
                var bytes = wide ? System.Text.Encoding.Unicode.GetBytes(name + "\0") : System.Text.Encoding.ASCII.GetBytes(name + "\0");
                Marshal.Copy(bytes, 0, data, bytes.Length);
                return (uint)name.Length;
            }
            if (command != 0x2000000B) return Fail(50);
            Marshal.WriteInt32(size, 32);
            if (data == IntPtr.Zero) return 0;
            if (capacity < 32) return Fail(122);
            if (Marshal.ReadInt32(data) != 32) return Fail(87);
            var info = new byte[32];
            Put(info, 0, 32); Put(info, 4, (int)type);
            if (type == 0) { Put(info, 12, 2); Put(info, 16, 125); }
            else { Put(info, 8, 4); Put(info, 20, 12); Put(info, 24, 3); Put(info, 28, 101); }
            Marshal.Copy(info, 0, data, 32);
            return 32;
        }

        public static void Install(SystemImports imports)
        {
            void Bind(string name, Delegate function)
            {
                roots.Add(function);
                imports.Overrides["user32.dll!" + name] = Marshal.GetFunctionPointerForDelegate(function);
            }
            Bind("RegisterRawInputDevices", new RegisterDelegate(Register));
            Bind("GetRawInputDeviceList", new ListDelegate(GetList));
            Bind("GetRawInputData", new DataDelegate(GetData));
            Bind("GetRawInputDeviceInfoW", new InfoDelegate((h, c, d, s) => Info(h, c, d, s, true)));
            Bind("GetRawInputDeviceInfoA", new InfoDelegate((h, c, d, s) => Info(h, c, d, s, false)));
        }
    }
}
