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

        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern void SetLastError(uint error);

        private static IntPtr MissingProcedure()
        {
            SetLastError(127); // ERROR_PROC_NOT_FOUND
            return IntPtr.Zero;
        }

        [DllImport("api-ms-win-core-processthreads-l1-1-0.dll", SetLastError = true)]
        private static extern IntPtr CreateThread(
            IntPtr security, IntPtr stack, IntPtr start, IntPtr argument,
            uint flags, IntPtr id);

        [DllImport("api-ms-win-core-processthreads-l1-1-0.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        // A managed function pointer round-trip preserves its delegate type.
        // The TLS wrapper must recover this exact type, not a look-alike.
        private static ThreadTls.CreateThreadDelegate makeThread;

        /// <summary>How many of the game's threads were told to stand back.</summary>
        public static long Calmed;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SayDelegate(IntPtr text);

        private static SayDelegate says;
        private static SayDelegate saysWide;

        /// <summary>What the engine said on its way up.</summary>
        public static readonly List<string> Said = new List<string>();

        /// <summary>Which library each handle we handed out came from.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> named =
            new System.Collections.Concurrent.ConcurrentDictionary<long, string>();

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

        // Libraries the console does not have and the bridge does. Kept so the
        // same name always answers with the same handle, the way a real loader
        // behaves.
        private static readonly
            System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> Invented =
            new System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr>(
                StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Hands back a handle for a library the bridge can answer for.
        ///
        /// The controller is the case this was written for. A game asks the
        /// loader for XInput by name at run time, and on this console no file
        /// by that name exists, so it is told no — and it falls back to
        /// reading raw devices, which is a road that ends nowhere here. Every
        /// XInput function it would have called is already implemented, over
        /// the console's own pad; the only thing missing was a handle to hang
        /// them on.
        ///
        /// The handle is real memory rather than an invented number, so that
        /// anything which pokes at it instead of merely passing it around
        /// finds something readable there.
        /// </summary>
        private static IntPtr Invent(string name)
        {
            {
                if (Invented.TryGetValue(name, out var already)) return already;

                var prefix = name + "!";
                var serves = false;
                try
                {
                    foreach (var key in new List<string>(imports.Overrides.Keys))
                    {
                        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        serves = true;
                        break;
                    }
                }
                catch
                {
                    return IntPtr.Zero;
                }
                if (!serves) return IntPtr.Zero;

                var handle = Marshal.AllocHGlobal(64);
                for (var i = 0; i < 64; i++) Marshal.WriteByte(handle, i, 0);
                Invented[name] = handle;
                named[handle.ToInt64()] = name;
                Remember("invented " + name);
                return handle;
            }
        }

        private static IntPtr Open(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return IntPtr.Zero;
            var name = BaseName(requested);
            // LoadLibrary appends .dll when the caller supplies no extension.
            if (name.IndexOf('.') < 0) name += ".dll";
            Remember(name);

            // One the loader mapped itself. Handing back its base address is
            // not a trick: that is exactly what a module handle is.
            var mine = imports.Find(name);
            if (mine != null)
            {
                named[mine.BaseAddress.ToInt64()] = name;
                return mine.BaseAddress;
            }

            // A library shipped with the game, asked for after startup: by
            // full path (Adobe AIR's runtime lives in a subfolder) or by name
            // from the game's folder, which Windows searches first.
            var shipped = MapFromGame(requested, name);
            if (shipped != IntPtr.Zero) return shipped;

            // A console shell library answers from the bridge even when
            // something else already loaded the real one into this process.
            if (SystemImports.IsShell(name)) return Invent(name);

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
            if (handle == IntPtr.Zero && SystemImports.AppRuntimeName(name) is string alias)
            {
                // The desktop C++ runtime, served by the framework's _app build.
                try { handle = LoadPackagedLibrary(alias, 0); }
                catch { handle = IntPtr.Zero; }
            }
            if (handle != IntPtr.Zero)
            {
                named[handle.ToInt64()] = name;
                return handle;
            }

            // Nothing on the console carries this one — but the bridge might.
            // A library every one of whose functions we already answer is a
            // library that exists, as far as the game has any way to tell.
            return Invent(name);
        }

        /// <summary>The game's folder, where a library it asks for by name is looked for first.</summary>
        public static string GameFolder;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ModuleMainDelegate(IntPtr instance, uint reason, IntPtr reserved);

        private static readonly object mapping = new object();

        /// <summary>
        /// Maps a DLL from the game's own files the way the startup pass does,
        /// runs its DllMain, and hands back its base as the module handle.
        /// Zero when the file is not the game's or cannot be mapped (a 32-bit
        /// image, for one), so the caller falls through to the system.
        /// </summary>
        private static IntPtr MapFromGame(string requested, string name)
        {
            string path = null;
            try
            {
                if (requested.IndexOf('\\') >= 0 || requested.IndexOf('/') >= 0)
                {
                    var full = requested.Replace('/', '\\');
                    if (GameFolder != null && full.StartsWith(GameFolder, StringComparison.OrdinalIgnoreCase)
                        && System.IO.File.Exists(full))
                        path = full;
                }
                else if (GameFolder != null)
                {
                    var local = System.IO.Path.Combine(GameFolder, name);
                    if (System.IO.File.Exists(local)) path = local;
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
            if (path == null) return IntPtr.Zero;

            lock (mapping)
            {
                var already = imports.Find(name);
                if (already != null) return already.BaseAddress;
                try
                {
                    var image = PeImage.Load(name, System.IO.File.ReadAllBytes(path), imports.Resolve);
                    imports.Add(image);
                    named[image.BaseAddress.ToInt64()] = name;
                    ModuleFileName.Register(image.BaseAddress, path);
                    Remember("mapped " + name);
                    if (image.EntryPoint != IntPtr.Zero)
                    {
                        ThreadTls.Adopt();
                        var main = Marshal.GetDelegateForFunctionPointer<ModuleMainDelegate>(image.EntryPoint);
                        main(image.BaseAddress, 1, IntPtr.Zero);
                    }
                    return image.BaseAddress;
                }
                catch (Exception error)
                {
                    Remember("could not map " + name + ": " + error.GetType().Name);
                    return IntPtr.Zero;
                }
            }
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

                string from;
                named.TryGetValue(module.ToInt64(), out from);
                if (from != null)
                {
                    Remember(from + "!" + wanted);

                    // Steamworks, answered with the signed-in account.
                    if (SteamBridge.Serves(from))
                    {
                        var bridged = SteamBridge.Resolve(wanted);
                        if (bridged != IntPtr.Zero) return bridged;
                    }

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
                        // An optional plugin export is a capability probe, not
                        // an import to fill. A fabricated pointer makes Unity
                        // register rendering callbacks the plugin never had.
                        return own != IntPtr.Zero ? own : MissingProcedure();
                    }
                }

                var invented = from != null && Invented.TryGetValue(from, out var fake)
                    && fake == module;
                var real = invented ? IntPtr.Zero : GetProcAddress(module, wanted);
                if (real != IntPtr.Zero) return real;
                if (from == null) return IntPtr.Zero;

                // Only explicitly implemented bridge answers can stand in for
                // an absent export. Generic import stubs must not advertise
                // optional features that the platform does not implement.
                var key = from + "!" + wanted;
                if (!imports.Answers.ContainsKey(key)) return MissingProcedure();
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

            // Every thread the game starts, started one step below normal.
            //
            // The engine sizes itself to the machine and takes it: sixteen
            // hardware threads become thirty-odd of its own, all at normal
            // priority, and the one thread that draws this application's screen
            // is left with almost nothing. Measured: fifteen screen updates in
            // a minute. A console is not a desktop — there is one thing running
            // and it still has to hand back a frame — so the game runs a step
            // below the screen, which costs it nothing it can feel.
            makeThread = (security, stack, start, argument, flags, id) =>
            {
                var thread = CreateThread(security, stack, start, argument, flags, id);
                if (thread != IntPtr.Zero)
                {
                    try
                    {
                        SetThreadPriority(thread, -1);   // below normal
                        Calmed++;
                    }
                    catch
                    {
                        // A thread that keeps its priority is not a failure.
                    }
                }
                return thread;
            };

            var answers = new Dictionary<string, IntPtr>
            {
                { "CreateThread", Marshal.GetFunctionPointerForDelegate(makeThread) },
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
