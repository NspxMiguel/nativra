using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// The other half of resolving: what a program asks for while it runs.
    ///
    /// An import table is settled once, before anything executes, and that is
    /// where most of a game's functions come from. Graphics do not. A game
    /// chooses its renderer at startup and reaches for the library by name —
    /// LoadLibrary, then GetProcAddress — so nothing decided at load time ever
    /// touches it. Every stand-in this app installs would be walked straight
    /// past.
    ///
    /// So both are answered here: a library the loader already mapped is handed
    /// back as itself, and a function the app answers differently is answered
    /// the same way it would have been in the import table.
    /// </summary>
    public static class LoaderStubs
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr LoadDelegate(IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr LoadExDelegate(IntPtr name, IntPtr file, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr ProcDelegate(IntPtr module, IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FreeDelegate(IntPtr module);

        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", SetLastError = true,
            CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", EntryPoint = "GetProcAddress",
            SetLastError = true)]
        private static extern IntPtr GetProcAddressByOrdinal(IntPtr module, IntPtr ordinal);

        private static LoadDelegate loadW;
        private static LoadDelegate loadA;
        private static LoadExDelegate loadExW;
        private static LoadExDelegate loadExA;
        private static ProcDelegate proc;
        private static FreeDelegate free;
        private static LoadDelegate handleW;
        private static LoadDelegate handleA;

        private static SystemImports imports;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SayDelegate(IntPtr text);

        private static SayDelegate says;
        private static SayDelegate saysWide;

        /// <summary>What the engine said on its way up.</summary>
        public static readonly List<string> Said = new List<string>();

        /// <summary>Which library each handle we handed out came from.</summary>
        private static readonly Dictionary<long, string> named =
            new Dictionary<long, string>();

        /// <summary>Stand-ins already written, so a loop does not write a thousand.</summary>
        private static readonly Dictionary<string, IntPtr> made =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every library a game asked for by name, for the report.</summary>
        public static readonly List<string> Asked = new List<string>();

        private static void Remember(string name)
        {
            lock (Asked)
            {
                if (!Asked.Contains(name) && Asked.Count < 80) Asked.Add(name);
            }
        }

        /// <summary>A file name without its folder, which is how modules are keyed.</summary>
        private static string BaseName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var cut = path.LastIndexOfAny(new[] { '\\', '/' });
            return cut >= 0 ? path.Substring(cut + 1) : path;
        }

        private static IntPtr Open(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return IntPtr.Zero;
            var name = BaseName(requested);
            Remember(name);

            // One the loader mapped itself. Handing back its base address is
            // not a trick: that is exactly what a module handle is.
            var mine = imports.Find(name);
            if (mine != null)
            {
                named[mine.BaseAddress.ToInt64()] = name;
                return mine.BaseAddress;
            }

            var handle = GetModuleHandleW(name);
            if (handle == IntPtr.Zero)
            {
                try
                {
                    handle = LoadPackagedLibrary(name, 0);
                }
                catch
                {
                    handle = IntPtr.Zero;
                }
            }
            if (handle != IntPtr.Zero) named[handle.ToInt64()] = name;
            return handle;
        }

        public static void Install(SystemImports system)
        {
            imports = system;

            loadW = name => Open(Marshal.PtrToStringUni(name));
            loadA = name => Open(Marshal.PtrToStringAnsi(name));
            loadExW = (name, file, flags) => Open(Marshal.PtrToStringUni(name));
            loadExA = (name, file, flags) => Open(Marshal.PtrToStringAnsi(name));
            handleW = name => name == IntPtr.Zero
                ? (imports.FindExecutable()?.BaseAddress ?? IntPtr.Zero)
                : Open(Marshal.PtrToStringUni(name));
            handleA = name => name == IntPtr.Zero
                ? (imports.FindExecutable()?.BaseAddress ?? IntPtr.Zero)
                : Open(Marshal.PtrToStringAnsi(name));
            free = module => 1;

            proc = (module, name) =>
            {
                // Imported by number rather than by name: anything under the
                // first page is an ordinal, not a pointer to a string.
                if (name.ToInt64() > 0 && name.ToInt64() < 0x10000)
                {
                    return GetProcAddressByOrdinal(module, name);
                }

                var wanted = Marshal.PtrToStringAnsi(name);
                if (string.IsNullOrEmpty(wanted)) return IntPtr.Zero;

                named.TryGetValue(module.ToInt64(), out var from);
                if (from != null)
                {
                    Remember(from + "!" + wanted);

                    // The same answer the import table would have given, so a
                    // game that looks a function up at runtime and a game that
                    // links it end up in the same place.
                    if (imports.Overrides.TryGetValue(from + "!" + wanted, out var ours))
                    {
                        return ours;
                    }

                    var mine = imports.Find(from);
                    if (mine != null)
                    {
                        var own = mine.Export(wanted);
                        if (own != IntPtr.Zero) return own;
                    }
                }

                var real = GetProcAddress(module, wanted);
                if (real != IntPtr.Zero) return real;
                if (from == null) return IntPtr.Zero;

                // Missing here means missing in the import table too, and the
                // same stand-in is the right answer: it records what was
                // wanted. Cached, because a game that looks a function up in a
                // loop would otherwise get a new stand-in every time and use
                // up the page they are written into.
                var key = from + "!" + wanted;
                lock (made)
                {
                    if (made.TryGetValue(key, out var already)) return already;
                    var fresh = imports.Resolve(from, wanted);
                    made[key] = fresh;
                    return fresh;
                }
            };

            // The engine narrates what it is doing to a debugger that is not
            // attached, and that narration is the most useful text there is:
            // it is the engine's own account of its startup, in its own words.
            says = text =>
            {
                var line = Marshal.PtrToStringAnsi(text);
                if (line == null) return;
                lock (Said)
                {
                    if (Said.Count < 300) Said.Add(line.TrimEnd());
                }
            };
            saysWide = text =>
            {
                var line = Marshal.PtrToStringUni(text);
                if (line == null) return;
                lock (Said)
                {
                    if (Said.Count < 300) Said.Add(line.TrimEnd());
                }
            };

            var answers = new Dictionary<string, IntPtr>
            {
                { "OutputDebugStringA", Marshal.GetFunctionPointerForDelegate(says) },
                { "OutputDebugStringW", Marshal.GetFunctionPointerForDelegate(saysWide) },
                { "LoadLibraryW", Marshal.GetFunctionPointerForDelegate(loadW) },
                { "LoadLibraryA", Marshal.GetFunctionPointerForDelegate(loadA) },
                { "LoadLibraryExW", Marshal.GetFunctionPointerForDelegate(loadExW) },
                { "LoadLibraryExA", Marshal.GetFunctionPointerForDelegate(loadExA) },
                { "GetModuleHandleW", Marshal.GetFunctionPointerForDelegate(handleW) },
                { "GetModuleHandleA", Marshal.GetFunctionPointerForDelegate(handleA) },
                { "GetProcAddress", Marshal.GetFunctionPointerForDelegate(proc) },
                { "FreeLibrary", Marshal.GetFunctionPointerForDelegate(free) },
            };

            foreach (var module in new[]
            {
                "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "kernelbase.dll",
                "api-ms-win-core-libraryloader-l1-2-0.dll",
                "api-ms-win-core-libraryloader-l1-1-0.dll",
                // A game often imports the debug functions from the api set
                // rather than from kernel32, and the narration is worth having.
                "api-ms-win-core-debug-l1-1-0.dll",
                "api-ms-win-core-debug-l1-1-1.dll",
            })
            {
                foreach (var pair in answers)
                {
                    system.Overrides[module + "!" + pair.Key] = pair.Value;
                }
            }
        }
    }
}
