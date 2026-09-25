using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Answers what a loaded binary asks the operating system for.
    ///
    /// Most of what a game imports from kernel32 is ordinary NT: the same
    /// function the console's own runtime already has mapped. The app store
    /// forbids CALLING some of them, but that is a certification rule, not a
    /// kernel one — so the first attempt is always to hand back the real
    /// address, and only what genuinely is not there needs writing by hand.
    /// </summary>
    public sealed class SystemImports
    {
        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", SetLastError = true,
            CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>Winsock is imported by number, not by name.</summary>
        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", EntryPoint = "GetProcAddress",
            SetLastError = true)]
        private static extern IntPtr GetProcAddressByOrdinal(IntPtr module, IntPtr ordinal);

        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);

        private readonly Dictionary<string, IntPtr> modules =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Binaries this loader mapped itself, which resolve each other.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PeImage> loaded =
            new System.Collections.Concurrent.ConcurrentDictionary<string, PeImage>(
                StringComparer.OrdinalIgnoreCase);

        public HashSet<string> MissingModules { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<string> MissingFunctions { get; } = new List<string>();

        // Counted from every thread that resolves an import, so the increment
        // has to be one indivisible step rather than read-add-write.
        private int fromSystem;
        public int FromSystem => fromSystem;
        private int fromImages;
        public int FromImages => fromImages;
        private int fromStubs;
        public int FromStubs => fromStubs;
        private int fromOverrides;
        public int FromOverrides => fromOverrides;

        /// <summary>
        /// Record every call, not just the ones nobody could answer. A program
        /// that dies without reaching a single missing function is dying inside
        /// one it did reach, and only a trace says which.
        /// </summary>
        public bool Trace;

        /// <summary>Functions whose caller is recorded, not just the call.</summary>
        public static readonly HashSet<string> Watched =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Sleep", "SleepEx", "WaitForSingleObjectEx", "WaitForMultipleObjects",
                // Not waits: spins. A thread that calls one of these hundreds
                // of thousands of times in a second is not working, it is
                // turning — and which piece of the engine is turning is the
                // only thing that names the deadlock. Both are what a lock
                // checks when it asks whether this thread already holds it.
                // What is NOT here, and must not come back: GetCurrentThreadId,
                // TlsGetValue, QueryPerformanceCounter and EnterCriticalSection.
                //
                // Watching a function means taking a lock on every call to it,
                // to write down who called. That is affordable for a wait and
                // ruinous for these: they are what a lock itself calls while
                // deciding whether this thread already holds it, hundreds of
                // thousands of times a second, from every thread at once. A
                // lock inside a lock, on the hottest path a program has, while
                // its libraries are still starting.
                //
                // They were put here to name the spin, and they did — it is in
                // baselib. Leaving them cost far more than it gave: the entry
                // point of the engine's own library stopped returning, and it
                // stopped returning more often the faster the machine ran.
                // The last call a blocked thread made, which is the only clue
                // left when a thread goes into something nothing here traces.
                "ReadFile", "LoadLibraryW", "LoadLibraryA",
                "LoadLibraryExW", "LoadLibraryExA",
            };

        /// <summary>Stands in for what the console does not provide.</summary>
        public Win32Shim Shim { get; } = new Win32Shim();

        public void Add(PeImage image)
        {
            loaded[image.Name] = image;
            ImageLookup.Track(image);
        }

        /// <summary>The system's own version of a function, for falling back to.</summary>
        public IntPtr SystemAddress(string module, string function)
        {
            var handle = Module(module);
            return handle == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(handle, function);
        }

        public PeImage Find(string name) =>
            loaded.TryGetValue(name, out var image) ? image : null;

        /// <summary>The program itself, as opposed to the libraries it uses.</summary>
        public PeImage FindExecutable()
        {
            foreach (var pair in loaded)
            {
                if (pair.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Libraries the console has only as shells: most of what they export
        /// answers "not supported". Whether one is already in the process
        /// depends on what ran first — Windows.Gaming.Input pulls in USER32,
        /// SETUPAPI and HID once a pad is connected — and when it was, the game
        /// got the console's MonitorFromWindow, which fails, could not pick a
        /// resolution and quit after its first frame, with its audio still
        /// playing over "Starting the game". These always take the bridge's
        /// answers, the way every run that worked did.
        /// </summary>
        private static readonly HashSet<string> NeverFromSystem =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "USER32.dll", "SETUPAPI.dll", "HID.DLL", "IMM32.dll", "dwmapi.dll",
            };

        public static string AppRuntimeName(string name)
        {
            var lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".dll")) return null;
            var stem = lower.Substring(0, lower.Length - 4);
            if (stem.EndsWith("_app")) return null;
            if (stem.StartsWith("msvcp140") || stem.StartsWith("vcruntime140") ||
                stem.StartsWith("concrt140") || stem == "vccorlib140")
            {
                return stem + "_app.dll";
            }
            return null;
        }

        /// <summary>Whether a library always answers from the bridge.</summary>
        public static bool IsShell(string name) => name != null && NeverFromSystem.Contains(name);

        private IntPtr Module(string name)
        {
            if (modules.TryGetValue(name, out var handle)) return handle;
            if (NeverFromSystem.Contains(name))
            {
                modules[name] = IntPtr.Zero;
                lock (MissingModules) MissingModules.Add(name);
                return IntPtr.Zero;
            }

            handle = GetModuleHandleW(name);
            if (handle == IntPtr.Zero)
            {
                // Not mapped yet. A packaged library can still be brought in;
                // a system one this process never loaded cannot.
                try
                {
                    handle = LoadPackagedLibrary(name, 0);
                }
                catch
                {
                    handle = IntPtr.Zero;
                }
            }
            if (handle == IntPtr.Zero)
            {
                // The desktop C++ runtime (msvcp140.dll and friends) is not on
                // the console, but this package's framework carries the same
                // library built for apps, under the same exports with an _app
                // suffix on the file name. A game that does not ship its own
                // copy gets that one.
                var alias = AppRuntimeName(name);
                if (alias != null)
                {
                    try { handle = LoadPackagedLibrary(alias, 0); }
                    catch { handle = IntPtr.Zero; }
                }
            }
            modules[name] = handle;
            if (handle == IntPtr.Zero) lock (MissingModules) MissingModules.Add(name);
            return handle;
        }

        /// <summary>
        /// Answers we give instead of the system's. A program asks the system
        /// which file it is and where its data sits; the honest answer here is
        /// the host application, which is not what it needs to hear.
        /// </summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> Overrides { get; } =
            new System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What a missing function should answer instead of zero. Zero means
        /// failure for most of a window system, and a caller told its window
        /// was never created stops there.
        /// </summary>
        public Dictionary<string, long> Answers { get; } =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Maps a DLL from the game's folder by name; true when it did.</summary>
        public Func<string, bool> MapMissing;

        private readonly HashSet<string> notShipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Asked once per name: most imports are system libraries.</summary>
        private bool WantsMapping(string module)
        {
            lock (notShipped)
            {
                if (notShipped.Contains(module)) return false;
                notShipped.Add(module);
                return true;
            }
        }

        public IntPtr Resolve(string module, string function)
        {
            if (Overrides.TryGetValue(module + "!" + function, out var ours))
            {
                System.Threading.Interlocked.Increment(ref fromOverrides);
                return ours;
            }

            if (loaded.TryGetValue(module, out var image))
            {
                var own = image.Export(function);
                if (own != IntPtr.Zero)
                {
                    System.Threading.Interlocked.Increment(ref fromImages);
                    return own;
                }
            }

            // A library the game ships, named by an import before anything
            // loaded it: an engine DLL the program opens at run time imports
            // SDL2 and FMOD, which nothing had mapped yet. It is mapped now,
            // its own imports first, as Windows would.
            if (image == null && MapMissing != null && WantsMapping(module) && MapMissing(module)
                && loaded.TryGetValue(module, out image))
            {
                var own = image.Export(function);
                if (own != IntPtr.Zero)
                {
                    System.Threading.Interlocked.Increment(ref fromImages);
                    return own;
                }
            }

            var handle = Module(module);
            if (handle != IntPtr.Zero)
            {
                IntPtr address;
                if (function.StartsWith("#", StringComparison.Ordinal) &&
                    int.TryParse(function.Substring(1), out var ordinal))
                {
                    address = GetProcAddressByOrdinal(handle, (IntPtr)ordinal);
                }
                else
                {
                    address = GetProcAddress(handle, function);
                }
                if (address != IntPtr.Zero)
                {
                    System.Threading.Interlocked.Increment(ref fromSystem);
                    if (!Trace) return address;
                    // The handful a stuck program spends its life in are worth
                    // the extra instruction that says who called them.
                    return Watched.Contains(function)
                        ? Shim.CallerFor(module + "!" + function, address)
                        : Shim.TraceFor(module + "!" + function, address);
                }
            }

            var name = module + "!" + function;
            lock (MissingFunctions) MissingFunctions.Add(name);

            // A stub keeps the import table complete, so the image can run and
            // say which of these it actually needs.
            var stub = Answers.TryGetValue(name, out var answer)
                ? Shim.StubReturning(name, answer)
                : Shim.StubFor(name);
            if (stub != IntPtr.Zero) System.Threading.Interlocked.Increment(ref fromStubs);
            return stub;
        }
    }
}
