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

        public void Add(PeImage image) => loaded[image.Name] = image;

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

        public IntPtr Resolve(string module, string function)
        {
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
                    return address;
                }
            }

            MissingFunctions.Add(module + "!" + function);
            return IntPtr.Zero;
        }
    }
}
