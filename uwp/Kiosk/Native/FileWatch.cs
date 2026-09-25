using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Writes down which files the game opens, and which it fails to open.
    ///
    /// A game that stops without saying why is usually waiting on something it
    /// asked the file system for. Its own log would say so, but the log is the
    /// first thing that goes missing when the log cannot be opened — which is
    /// exactly the case this was written to settle.
    ///
    /// Opening files is one of the hottest calls a game makes, so almost none
    /// of them are recorded: only the ones that fail, and the handful whose
    /// name says they matter. Recording all of them would change the timing of
    /// the thing being measured, which is how a measurement stops being one.
    /// </summary>
    internal static class FileWatch
    {
        private const int Keep = 120;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr CreateFileDelegate(
            IntPtr name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        private static CreateFileDelegate wide;
        private static CreateFileDelegate real;

        /// <summary>What was opened, and how it went. Read under its own lock.</summary>
        public static readonly List<string> Seen = new List<string>();

        /// <summary>How many opens failed, whether or not each one was kept.</summary>
        public static long Failures;

        private static void Say(string line)
        {
            lock (Seen)
            {
                if (Seen.Count < Keep) Seen.Add(line);
            }
        }

        /// <summary>
        /// A name worth keeping even when the open succeeds: the log the engine
        /// was told to write, and the files it loads its own code from. The
        /// rest — textures, bundles, the thousands of small reads a scene makes
        /// — are only interesting when they fail.
        /// </summary>
        private static bool WorthKeeping(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lower = path.ToLowerInvariant();
            return lower.EndsWith(".log")
                || lower.EndsWith("global-metadata.dat")
                || lower.EndsWith(".dll")
                || lower.EndsWith("boot.config")
                || lower.EndsWith(".xml");
        }

        [DllImport("api-ms-win-core-file-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string name, int level, IntPtr data, int search, IntPtr filter, uint flags);

        [DllImport("api-ms-win-core-file-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr FindFirstDelegate(IntPtr name, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr FindFirstExDelegate(IntPtr name, int level, IntPtr data, int search, IntPtr filter, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate uint AttributesDelegate(IntPtr name);
        private static FindFirstDelegate findFirst;
        private static FindFirstExDelegate findFirstEx;
        private static AttributesDelegate attributes;

        [DllImport("api-ms-win-core-memory-l1-1-1.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileMappingFromApp(IntPtr file, IntPtr attributes, uint protect, ulong maximumSize, string name);

        [DllImport("api-ms-win-core-memory-l1-1-1.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFileFromApp(IntPtr mapping, uint access, ulong offset, IntPtr size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr MappingWDelegate(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate IntPtr MapViewDelegate(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, IntPtr size);
        private static MappingWDelegate mappingW, mappingA;
        private static MapViewDelegate mapView;

        /// <summary>
        /// File mappings, through the calls an app container is allowed to
        /// make. CreateFileMappingW and MapViewOfFile are desktop calls; the
        /// FromApp pair does the same for a packaged app. Adobe AIR maps its
        /// application descriptor to read it.
        /// </summary>
        private static void BridgeMappings(SystemImports imports)
        {
            mappingW = (file, attributes, protect, high, low, name) =>
            {
                var made = CreateFileMappingFromApp(file, attributes, protect,
                    ((ulong)high << 32) | low, name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name));
                if (made == IntPtr.Zero) Say("mapping failed (" + Marshal.GetLastWin32Error() + ")");
                return made;
            };
            mappingA = (file, attributes, protect, high, low, name) =>
            {
                var made = CreateFileMappingFromApp(file, attributes, protect,
                    ((ulong)high << 32) | low, name == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(name));
                if (made == IntPtr.Zero) Say("mapping failed (" + Marshal.GetLastWin32Error() + ")");
                return made;
            };
            mapView = (mapping, access, high, low, size) =>
            {
                var view = MapViewOfFileFromApp(mapping, access, ((ulong)high << 32) | low, size);
                if (view == IntPtr.Zero) Say("map view failed (" + Marshal.GetLastWin32Error() + ")");
                return view;
            };
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "api-ms-win-core-memory-l1-1-0.dll" })
            {
                imports.Overrides[module + "!CreateFileMappingW"] = Marshal.GetFunctionPointerForDelegate(mappingW);
                imports.Overrides[module + "!CreateFileMappingA"] = Marshal.GetFunctionPointerForDelegate(mappingA);
                imports.Overrides[module + "!MapViewOfFile"] = Marshal.GetFunctionPointerForDelegate(mapView);
            }
        }

        /// <summary>
        /// The lookups that decide whether a program believes a file exists.
        /// Watched for the same reason as opens: a failed one, with its path,
        /// says what the program looked for and where.
        /// </summary>
        private static void WatchLookups(SystemImports imports)
        {
            findFirstEx = (name, level, data, search, filter, flags) =>
            {
                var path = name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
                var found = FindFirstFileExW(path, level, data, search, filter, flags);
                if (found == InvalidHandle) Say("find failed " + (path ?? "?") + " (" + Marshal.GetLastWin32Error() + ")");
                else Say("find " + (path ?? "?"));
                return found;
            };
            findFirst = (name, data) => findFirstEx(name, 0, data, 0, IntPtr.Zero, 0);
            attributes = name =>
            {
                var path = name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
                var result = GetFileAttributesW(path);
                if (result == uint.MaxValue) Say("attributes failed " + (path ?? "?") + " (" + Marshal.GetLastWin32Error() + ")");
                return result;
            };
            foreach (var module in new[] { "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "api-ms-win-core-file-l1-1-0.dll" })
            {
                imports.Overrides[module + "!FindFirstFileW"] = Marshal.GetFunctionPointerForDelegate(findFirst);
                imports.Overrides[module + "!FindFirstFileExW"] = Marshal.GetFunctionPointerForDelegate(findFirstEx);
                imports.Overrides[module + "!GetFileAttributesW"] = Marshal.GetFunctionPointerForDelegate(attributes);
            }
        }

        /// <summary>Answers an open by name, or zero to let it through.</summary>
        public static Func<IntPtr, IntPtr> Intercept;

        public static void Install(SystemImports imports)
        {
            WatchLookups(imports);
            BridgeMappings(imports);
            real = null;
            var address = imports.SystemAddress("kernel32.dll", "CreateFileW");
            if (address != IntPtr.Zero)
            {
                real = Marshal.GetDelegateForFunctionPointer<CreateFileDelegate>(address);
            }

            wide = (name, access, share, security, disposition, flags, template) =>
            {
                // Paths that name something the bridge provides, such as the
                // listed controller, are answered before the file system.
                var ours = Intercept == null ? IntPtr.Zero : Intercept(name);
                if (ours != IntPtr.Zero) return ours;
                if (real == null) return InvalidHandle;

                var handle = real(
                    name, access, share, security, disposition, flags, template);

                // Reading the name costs a copy, so it is only read when there
                // is a reason to: the open failed, or there is still room and
                // the name might be one of the few that matter.
                var failed = handle == InvalidHandle;
                if (!failed && Seen.Count >= Keep) return handle;

                string path = null;
                try
                {
                    path = name == IntPtr.Zero ? null : Marshal.PtrToStringUni(name);
                }
                catch
                {
                    // An unreadable name is not worth failing the open over.
                }

                if (failed)
                {
                    Failures++;
                    Say("failed " + (path ?? "?"));
                }
                else if (WorthKeeping(path))
                {
                    Say("opened " + path);
                }
                return handle;
            };

            foreach (var module in new[]
                     {
                         "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll",
                         "api-ms-win-core-file-l1-1-0.dll",
                         "api-ms-win-core-file-l1-2-0.dll",
                         "api-ms-win-core-file-l1-2-1.dll",
                     })
            {
                imports.Overrides[module + "!CreateFileW"] =
                    Marshal.GetFunctionPointerForDelegate(wide);
            }
        }
    }
}
