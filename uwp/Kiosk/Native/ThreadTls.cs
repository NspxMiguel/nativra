using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>Uses packaged native DLLs to reserve Windows-owned static TLS.</summary>
    internal static class ThreadTls
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate IntPtr CreateThreadDelegate(
            IntPtr attributes, IntPtr stackSize, IntPtr start, IntPtr parameter,
            uint flags, IntPtr threadId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint StartDelegate(IntPtr parameter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConfigureDelegate(IntPtr template, uint size, uint total);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnsureDelegate();

        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>NativraTls0..15.dll, built by uwp/TlsCarrier/build.cmd.</summary>
        private const int Carriers = 16;

        private static readonly object Gate = new object();
        private static readonly List<EnsureDelegate> Blocks = new List<EnsureDelegate>();
        private static CreateThreadDelegate createThread;
        private static CreateThreadDelegate realCreateThread;
        private static StartDelegate trampoline;

        public static long Adopted;
        public static long CopiedBlocks;
        public static long Failures;
        public static string LastError;

        public static int Remember(byte[] template, int size)
        {
            lock (Gate)
            {
                // One carrier DLL per game module with static TLS. Hades maps
                // eleven (engine, SDL2, FMOD, Steam, EOS, Discord...), past
                // the eight there used to be.
                if (Blocks.Count >= Carriers) throw new InvalidOperationException("Game TLS capacity exceeded");
                var module = LoadPackagedLibrary("NativraTls" + Blocks.Count + ".dll", 0);
                if (module == IntPtr.Zero)
                    throw new InvalidOperationException("TLS carrier load failed: " + Marshal.GetLastWin32Error());
                var configure = Marshal.GetDelegateForFunctionPointer<ConfigureDelegate>(
                    GetProcAddress(module, "NativraTlsConfigure"));
                var ensure = Marshal.GetDelegateForFunctionPointer<EnsureDelegate>(
                    GetProcAddress(module, "NativraTlsEnsure"));
                var buffer = Marshal.AllocHGlobal(Math.Max(template.Length, 1));
                try
                {
                    Marshal.Copy(template, 0, buffer, template.Length);
                    var slot = configure(buffer, checked((uint)template.Length), checked((uint)size));
                    if (slot < 0) throw new InvalidOperationException("TLS template exceeds carrier capacity");
                    Blocks.Add(ensure);
                    return slot;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }

        // Entry callers retain these hooks, but Windows now owns all TLS memory.
        public static void Restore() { }
        public static void Release() { }

        public static bool Adopt()
        {
            try
            {
                lock (Gate)
                {
                    foreach (var ensure in Blocks)
                    {
                        var copied = ensure();
                        if (copied < 0) throw new InvalidOperationException("Missing Windows TLS block");
                        if (copied > 0) System.Threading.Interlocked.Increment(ref CopiedBlocks);
                    }
                }
                System.Threading.Interlocked.Increment(ref Adopted);
                return true;
            }
            catch (Exception error)
            {
                System.Threading.Interlocked.Increment(ref Failures);
                LastError = error.GetType().Name + ": " + error.Message;
                return false;
            }
        }

        public static void Install(SystemImports imports)
        {
            var real = imports.Overrides.TryGetValue("kernel32.dll!CreateThread", out var previous)
                ? previous : imports.SystemAddress("kernel32.dll", "CreateThread");
            if (real == IntPtr.Zero) return;
            realCreateThread = Marshal.GetDelegateForFunctionPointer<CreateThreadDelegate>(real);
            trampoline = parameter =>
            {
                var start = Marshal.ReadIntPtr(parameter);
                var given = Marshal.ReadIntPtr(parameter, IntPtr.Size);
                Marshal.FreeHGlobal(parameter);
                if (!Adopt()) return 8;
                return Marshal.GetDelegateForFunctionPointer<StartDelegate>(start)(given);
            };
            var trampolineAddress = Marshal.GetFunctionPointerForDelegate(trampoline);
            createThread = (attributes, stackSize, start, parameter, flags, threadId) =>
            {
                if (start == IntPtr.Zero)
                    return realCreateThread(attributes, stackSize, start, parameter, flags, threadId);
                var carried = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(carried, start);
                Marshal.WriteIntPtr(carried, IntPtr.Size, parameter);
                var made = realCreateThread(attributes, stackSize, trampolineAddress, carried, flags, threadId);
                if (made == IntPtr.Zero) Marshal.FreeHGlobal(carried);
                return made;
            };
            foreach (var module in new[]
            {
                "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll", "kernelbase.dll",
                "api-ms-win-core-processthreads-l1-1-0.dll",
                "api-ms-win-core-processthreads-l1-1-1.dll",
                "api-ms-win-core-processthreads-l1-1-2.dll",
                "api-ms-win-core-processthreads-l1-1-3.dll",
            })
                imports.Overrides[module + "!CreateThread"] = Marshal.GetFunctionPointerForDelegate(createThread);
        }
    }
}
