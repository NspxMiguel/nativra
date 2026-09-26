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
        private const int SmallCapacity = 16384;
        private static int smallUsed;
        private static bool largeUsed;

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
                // A template past the ordinary carriers' 16 KB goes to the one
                // large carrier (512 KB): LEGO Jurassic World's is 346 KB.
                string carrier;
                if (template.Length > SmallCapacity || size > SmallCapacity)
                {
                    if (largeUsed) throw new InvalidOperationException("Large TLS carrier already in use");
                    carrier = "NativraTlsLarge0.dll";
                }
                else
                {
                    if (smallUsed >= Carriers) throw new InvalidOperationException("Game TLS capacity exceeded");
                    carrier = "NativraTls" + smallUsed + ".dll";
                }
                var module = LoadPackagedLibrary(carrier, 0);
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
                    if (carrier.StartsWith("NativraTlsLarge", StringComparison.Ordinal)) largeUsed = true;
                    else smallUsed++;
                    return slot;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }

        // Entry callers retain these hooks, but Windows now owns all TLS memory.
        public static void Restore() { }
        public static void Release() { }

        private static SystemImports images;

        /// <summary>
        /// A new thread made ready the way Windows would: static TLS copied,
        /// then every mapped module told DLL_THREAD_ATTACH, in load order.
        /// </summary>
        public static bool AdoptAndAttach()
        {
            if (!Adopt()) return false;
            var all = images?.LoadOrder();
            if (all == null) return true;
            foreach (var image in all)
            {
                try
                {
                    image.ThreadAttach();
                }
                catch
                {
                    // One module's thread hook failing is not the thread failing.
                }
            }
            return true;
        }

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

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr BeginThreadExDelegate(IntPtr security, uint stack, IntPtr start, IntPtr argument, uint flags, IntPtr id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr BeginThreadDelegate(IntPtr start, uint stack, IntPtr argument);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CdeclStartDelegate(IntPtr parameter);

        private static BeginThreadExDelegate realBeginEx, beginEx;
        private static BeginThreadDelegate realBegin, begin;
        private static StartDelegate crtTrampoline;
        private static CdeclStartDelegate crtPlainTrampoline;

        /// <summary>
        /// Threads the C runtime starts (_beginthreadex, and std::thread on top
        /// of it) are made inside ucrtbase, where the CreateThread hook does not
        /// reach: they got neither the game's TLS nor DLL_THREAD_ATTACH.
        /// </summary>
        private static void InstallCrtThreads(SystemImports imports)
        {
            const string Runtime = "api-ms-win-crt-runtime-l1-1-0.dll";
            var exAddress = imports.SystemAddress(Runtime, "_beginthreadex");
            var plainAddress = imports.SystemAddress(Runtime, "_beginthread");
            if (exAddress == IntPtr.Zero) return;
            realBeginEx = Marshal.GetDelegateForFunctionPointer<BeginThreadExDelegate>(exAddress);
            crtTrampoline = parameter =>
            {
                var start = Marshal.ReadIntPtr(parameter);
                var given = Marshal.ReadIntPtr(parameter, IntPtr.Size);
                Marshal.FreeHGlobal(parameter);
                AdoptAndAttach();
                return Marshal.GetDelegateForFunctionPointer<StartDelegate>(start)(given);
            };
            var trampolineAddress = Marshal.GetFunctionPointerForDelegate(crtTrampoline);
            beginEx = (security, stack, start, argument, flags, id) =>
            {
                if (start == IntPtr.Zero) return realBeginEx(security, stack, start, argument, flags, id);
                var carried = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(carried, start);
                Marshal.WriteIntPtr(carried, IntPtr.Size, argument);
                var made = realBeginEx(security, stack, trampolineAddress, carried, flags, id);
                if (made == IntPtr.Zero) Marshal.FreeHGlobal(carried);
                else StackSampler.Track(made, "crt");
                return made;
            };
            if (plainAddress != IntPtr.Zero)
            {
                realBegin = Marshal.GetDelegateForFunctionPointer<BeginThreadDelegate>(plainAddress);
                crtPlainTrampoline = parameter =>
                {
                    var start = Marshal.ReadIntPtr(parameter);
                    var given = Marshal.ReadIntPtr(parameter, IntPtr.Size);
                    Marshal.FreeHGlobal(parameter);
                    AdoptAndAttach();
                    Marshal.GetDelegateForFunctionPointer<CdeclStartDelegate>(start)(given);
                };
                var plainTrampoline = Marshal.GetFunctionPointerForDelegate(crtPlainTrampoline);
                begin = (start, stack, argument) =>
                {
                    if (start == IntPtr.Zero) return realBegin(start, stack, argument);
                    var carried = Marshal.AllocHGlobal(IntPtr.Size * 2);
                    Marshal.WriteIntPtr(carried, start);
                    Marshal.WriteIntPtr(carried, IntPtr.Size, argument);
                    var made = realBegin(plainTrampoline, stack, carried);
                    if (made == new IntPtr(-1)) Marshal.FreeHGlobal(carried);
                    return made;
                };
            }
            foreach (var module in new[] { Runtime, "ucrtbase.dll", "UCRTBASE.dll" })
            {
                imports.Overrides[module + "!_beginthreadex"] = Marshal.GetFunctionPointerForDelegate(beginEx);
                if (begin != null) imports.Overrides[module + "!_beginthread"] = Marshal.GetFunctionPointerForDelegate(begin);
            }
        }

        public static void Install(SystemImports imports)
        {
            images = imports;
            InstallCrtThreads(imports);
            var real = imports.Overrides.TryGetValue("kernel32.dll!CreateThread", out var previous)
                ? previous : imports.SystemAddress("kernel32.dll", "CreateThread");
            if (real == IntPtr.Zero) return;
            realCreateThread = Marshal.GetDelegateForFunctionPointer<CreateThreadDelegate>(real);
            trampoline = parameter =>
            {
                var start = Marshal.ReadIntPtr(parameter);
                var given = Marshal.ReadIntPtr(parameter, IntPtr.Size);
                Marshal.FreeHGlobal(parameter);
                if (!AdoptAndAttach()) return 8;
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
