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
        private readonly Dictionary<string, PeImage> loaded =
            new Dictionary<string, PeImage>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> MissingModules { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<string> MissingFunctions { get; } = new List<string>();

        public int FromSystem { get; private set; }
        public int FromImages { get; private set; }
        public int FromStubs { get; private set; }
        public int FromOverrides { get; private set; }

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
                "GetCurrentThreadId", "TlsGetValue",
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

        private IntPtr Module(string name)
        {
            if (modules.TryGetValue(name, out var handle)) return handle;

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
            modules[name] = handle;
            if (handle == IntPtr.Zero) MissingModules.Add(name);
            return handle;
        }

        /// <summary>
        /// Answers we give instead of the system's. A program asks the system
        /// which file it is and where its data sits; the honest answer here is
        /// the host application, which is not what it needs to hear.
        /// </summary>
        public Dictionary<string, IntPtr> Overrides { get; } =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What a missing function should answer instead of zero. Zero means
        /// failure for most of a window system, and a caller told its window
        /// was never created stops there.
        /// </summary>
        public Dictionary<string, long> Answers { get; } =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public IntPtr Resolve(string module, string function)
        {
            if (Overrides.TryGetValue(module + "!" + function, out var ours))
            {
                FromOverrides++;
                return ours;
            }

            if (loaded.TryGetValue(module, out var image))
            {
                var own = image.Export(function);
                if (own != IntPtr.Zero)
                {
                    FromImages++;
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
                    FromSystem++;
                    if (!Trace) return address;
                    // The handful a stuck program spends its life in are worth
                    // the extra instruction that says who called them.
                    return Watched.Contains(function)
                        ? Shim.CallerFor(module + "!" + function, address)
                        : Shim.TraceFor(module + "!" + function, address);
                }
            }

            var name = module + "!" + function;
            MissingFunctions.Add(name);

            // A stub keeps the import table complete, so the image can run and
            // say which of these it actually needs.
            var stub = Answers.TryGetValue(name, out var answer)
                ? Shim.StubReturning(name, answer)
                : Shim.StubFor(name);
            if (stub != IntPtr.Zero) FromStubs++;
            return stub;
        }
    }
}
