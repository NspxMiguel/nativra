using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Ansi = Nativra.X86.Loader.GuestKernel.Ansi;

namespace Kiosk.Native
{
    /// <summary>A per-game registry persisted in application storage, independent of the Xbox registry.</summary>
    internal sealed class RegistryBridge
    {
        private sealed class Value
        {
            public uint Type;
            public byte[] Data;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Dictionary<string, Value>> keys =
            new Dictionary<string, Dictionary<string, Value>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, string> handles = new Dictionary<long, string>();
        private readonly List<Delegate> roots = new List<Delegate>();
        private static readonly List<RegistryBridge> instances = new List<RegistryBridge>();
        private readonly string file;
        private long nextHandle = 0x4E520000;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OpenDelegate(IntPtr key, IntPtr name, uint options, uint access, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDelegate(IntPtr key, IntPtr name, uint reserved, IntPtr className,
            uint options, uint access, IntPtr security, IntPtr result, IntPtr disposition);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryDelegate(IntPtr key, IntPtr name, IntPtr reserved, IntPtr type, IntPtr data, IntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetDelegate(IntPtr key, IntPtr name, uint reserved, uint type, IntPtr data, uint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int KeyDelegate(IntPtr key);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DeleteDelegate(IntPtr key, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LegacyOpenDelegate(IntPtr key, IntPtr name, IntPtr result);

        internal RegistryBridge(string file)
        {
            this.file = file;
            foreach (var root in new[] { "HKCR", "HKCU", "HKLM", "HKU", "HKCC" }) Ensure(root);
            Ensure(@"HKCU\Software");
            Ensure(@"HKLM\Software");
            if (!File.Exists(file)) return;
            foreach (var line in File.ReadAllLines(file))
            {
                var parts = line.Split('\t');
                try
                {
                    if (parts.Length == 2 && parts[0] == "K") Ensure(Decode(parts[1]));
                    else if (parts.Length == 5 && parts[0] == "V" && uint.TryParse(parts[3], out var type))
                        Ensure(Decode(parts[1]))[Decode(parts[2])] = new Value { Type = type, Data = Convert.FromBase64String(parts[4]) };
                }
                catch (FormatException) { } // A malformed record must not discard unrelated saved settings.
            }
        }

        private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        private static string Decode(string text) => Encoding.UTF8.GetString(Convert.FromBase64String(text));
        private static bool IsText(uint type) => type == 1 || type == 2 || type == 7;

        private static string ReadName(IntPtr name, bool wide)
        {
            if (name == IntPtr.Zero) return "";
            if (wide) return Marshal.PtrToStringUni(name);
            var bytes = new List<byte>();
            for (var offset = 0; ; offset++)
            {
                var b = Marshal.ReadByte(name, offset);
                if (b == 0) return Ansi.Decode(bytes.ToArray());
                bytes.Add(b);
            }
        }

        private string PathOf(IntPtr handle)
        {
            var number = handle.ToInt64();
            var low = unchecked((uint)number);
            if (number == low || number == unchecked((int)low))
            {
                switch (low)
                {
                    case 0x80000000: return "HKCR";
                    case 0x80000001: return "HKCU";
                    case 0x80000002: return "HKLM";
                    case 0x80000003: return "HKU";
                    case 0x80000005: return "HKCC";
                }
            }
            return handles.TryGetValue(number, out var path) ? path : null;
        }

        private Dictionary<string, Value> Ensure(string path)
        {
            if (keys.TryGetValue(path, out var values)) return values;
            var slash = path.LastIndexOf('\\');
            if (slash > 0) Ensure(path.Substring(0, slash));
            values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            keys[path] = values;
            return values;
        }

        private int Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var lines = new List<string>();
                foreach (var key in keys)
                {
                    lines.Add("K\t" + Encode(key.Key));
                    foreach (var value in key.Value)
                        lines.Add("V\t" + Encode(key.Key) + "\t" + Encode(value.Key) + "\t" + value.Value.Type + "\t" + Convert.ToBase64String(value.Value.Data));
                }
                var temporary = file + ".tmp";
                File.WriteAllLines(temporary, lines);
                if (File.Exists(file)) File.Replace(temporary, file, null);
                else File.Move(temporary, file);
                return 0;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return 29; // ERROR_WRITE_FAULT: a failed save must not look durable.
            }
        }

        internal int Open(IntPtr key, IntPtr name, IntPtr result, IntPtr disposition, bool create, bool wide)
        {
            if (result == IntPtr.Zero) return 87;
            lock (gate)
            {
                var path = PathOf(key);
                if (path == null) return 6;
                var subkey = ReadName(name, wide).Trim('\\');
                if (subkey.Length != 0) path += "\\" + subkey;
                var existed = keys.ContainsKey(path);
                if (!existed && !create) return 2;
                if (!existed)
                {
                    Ensure(path);
                    var error = Save();
                    if (error != 0) return error;
                }
                var handle = ++nextHandle;
                handles[handle] = path;
                Marshal.WriteIntPtr(result, new IntPtr(handle));
                if (disposition != IntPtr.Zero) Marshal.WriteInt32(disposition, existed ? 2 : 1);
                return 0;
            }
        }

        internal int Set(IntPtr key, IntPtr name, uint reserved, uint type, IntPtr data, uint size, bool wide)
        {
            if (reserved != 0 || (data == IntPtr.Zero && size != 0)) return 87;
            if (size > 16 * 1024 * 1024) return 8;
            if (wide && IsText(type) && (size & 1) != 0) return 87;
            lock (gate)
            {
                var path = PathOf(key);
                if (path == null || !keys.TryGetValue(path, out var values)) return 6;
                var bytes = new byte[(int)size];
                if (size != 0) Marshal.Copy(data, bytes, 0, bytes.Length);
                if (!wide && IsText(type)) bytes = Encoding.Unicode.GetBytes(Ansi.Decode(bytes));
                values[ReadName(name, wide)] = new Value { Type = type, Data = bytes };
                return Save();
            }
        }

        internal int Query(IntPtr key, IntPtr name, IntPtr reserved, IntPtr type, IntPtr data, IntPtr size, bool wide)
        {
            if (reserved != IntPtr.Zero || (data != IntPtr.Zero && size == IntPtr.Zero)) return 87;
            lock (gate)
            {
                var path = PathOf(key);
                if (path == null || !keys.TryGetValue(path, out var values)) return 6;
                if (!values.TryGetValue(ReadName(name, wide), out var value)) return 2;
                var bytes = !wide && IsText(value.Type) ? Ansi.Encode(Encoding.Unicode.GetString(value.Data)) : value.Data;
                if (type != IntPtr.Zero) Marshal.WriteInt32(type, (int)value.Type);
                if (size == IntPtr.Zero) return 0;
                var capacity = unchecked((uint)Marshal.ReadInt32(size));
                Marshal.WriteInt32(size, bytes.Length);
                if (data == IntPtr.Zero) return 0;
                if (capacity < bytes.Length) return 234;
                Marshal.Copy(bytes, 0, data, bytes.Length);
                return 0;
            }
        }

        internal int Close(IntPtr key)
        {
            lock (gate)
            {
                if (PathOf(key) == null) return 6;
                handles.Remove(key.ToInt64());
                return 0;
            }
        }

        internal int Delete(IntPtr key, IntPtr name, bool value, bool wide)
        {
            lock (gate)
            {
                var path = PathOf(key);
                if (path == null || !keys.TryGetValue(path, out var values)) return 6;
                var text = ReadName(name, wide);
                if (value)
                {
                    if (!values.Remove(text)) return 2;
                }
                else
                {
                    if (text.Length == 0) return 87;
                    path += "\\" + text.Trim('\\');
                    foreach (var existing in keys.Keys)
                        if (existing.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)) return 5;
                    if (!keys.Remove(path)) return 2;
                }
                return Save();
            }
        }

        private void Add(SystemImports imports, string name, Delegate function)
        {
            roots.Add(function);
            var pointer = Marshal.GetFunctionPointerForDelegate(function);
            foreach (var module in new[] { "advapi32.dll", "ADVAPI32.dll", "api-ms-win-core-registry-l1-1-0.dll", "api-ms-win-core-registry-l1-1-1.dll" })
                imports.Overrides[module + "!" + name] = pointer;
        }

        public static void Install(SystemImports imports, string file)
        {
            var registry = new RegistryBridge(file);
            instances.Add(registry);
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var suffix = w ? "W" : "A";
                registry.Add(imports, "RegOpenKeyEx" + suffix, new OpenDelegate((k, n, o, a, r) =>
                    o == 0 ? registry.Open(k, n, r, IntPtr.Zero, false, w) : 87));
                registry.Add(imports, "RegCreateKeyEx" + suffix, new CreateDelegate((k, n, z, c, o, a, s, r, d) =>
                    z == 0 && o == 0 ? registry.Open(k, n, r, d, true, w) : 87));
                registry.Add(imports, "RegOpenKey" + suffix, new LegacyOpenDelegate((k, n, r) => registry.Open(k, n, r, IntPtr.Zero, false, w)));
                registry.Add(imports, "RegCreateKey" + suffix, new LegacyOpenDelegate((k, n, r) => registry.Open(k, n, r, IntPtr.Zero, true, w)));
                registry.Add(imports, "RegSetValueEx" + suffix, new SetDelegate((k, n, r, t, d, s) => registry.Set(k, n, r, t, d, s, w)));
                registry.Add(imports, "RegQueryValueEx" + suffix, new QueryDelegate((k, n, r, t, d, s) => registry.Query(k, n, r, t, d, s, w)));
                registry.Add(imports, "RegDeleteValue" + suffix, new DeleteDelegate((k, n) => registry.Delete(k, n, true, w)));
                registry.Add(imports, "RegDeleteKey" + suffix, new DeleteDelegate((k, n) => registry.Delete(k, n, false, w)));
            }
            registry.Add(imports, "RegCloseKey", new KeyDelegate(registry.Close));
            registry.Add(imports, "RegFlushKey", new KeyDelegate(k =>
            {
                lock (registry.gate) return registry.PathOf(k) == null ? 6 : registry.Save();
            }));
        }
    }
}
