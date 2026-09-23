using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Gives every thread its own copy of a library's thread-local variables.
    ///
    /// A program that declares a variable thread-local expects one of them per
    /// thread. Windows arranges that when it loads a library: it keeps a
    /// template of the initial values and hands each thread a fresh copy at
    /// the slot the library was given.
    ///
    /// This loader maps its libraries itself, so nothing arranges it. What was
    /// happening instead: the copy was made once, on whichever thread did the
    /// loading, and written into that thread's table alone. The main thread
    /// worked. Every thread the game created afterwards — and a game of this
    /// size creates thirty — found nothing at its slot and read whatever was
    /// there, which is a crash or a hang depending on what it found.
    ///
    /// That is the shape of a fault that comes and goes: it depends entirely
    /// on which thread touches a thread-local variable first, and on whether
    /// the memory it lands on happened to be zero.
    /// </summary>
    internal static class ThreadTls
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate IntPtr CreateThreadDelegate(
            IntPtr attributes, IntPtr stackSize, IntPtr start, IntPtr parameter,
            uint flags, IntPtr threadId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint StartDelegate(IntPtr parameter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ExitDelegate(uint code);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void FreeAndExitDelegate(IntPtr module, uint code);

        /// <summary>One library's thread-local template and the slot it uses.</summary>
        private struct Block
        {
            public int Slot;
            public byte[] Template;
            public int Size;
        }

        private static readonly List<Block> Blocks = new List<Block>();
        private static readonly object Gate = new object();
        // Dynamic TlsAlloc indexes do not reserve entries in gs:[0x58]. Keep
        // game indexes in a private vector tail, beyond the native prefix.
        private const int NativeSlots = 4096;
        private const int GameSlots = 64;
        private sealed class ThreadState
        {
            public IntPtr Original;
            public IntPtr Table;
            public readonly Dictionary<int, IntPtr> Copies = new Dictionary<int, IntPtr>();
        }
        private static readonly Dictionary<long, ThreadState> Threads =
            new Dictionary<long, ThreadState>();

        private static CreateThreadDelegate createThread;
        private static CreateThreadDelegate realCreateThread;
        private static StartDelegate trampoline;
        private static ExitDelegate exitThread;
        private static ExitDelegate realExitThread;
        private static FreeAndExitDelegate freeAndExitThread;
        private static FreeAndExitDelegate realFreeAndExitThread;

        /// <summary>Threads that were given their own copies.</summary>
        public static long Adopted;
        public static long CopiedBlocks;
        public static long Failures;
        public static string LastError;

        /// <summary>
        /// Records a library's template so every thread made after this gets a
        /// copy of it. Called once per mapped library that has one.
        /// </summary>
        public static int Remember(byte[] template, int size)
        {
            lock (Gate)
            {
                if (Blocks.Count >= GameSlots) throw new InvalidOperationException("Game TLS capacity exceeded");
                var slot = NativeSlots + Blocks.Count;
                Blocks.Add(new Block { Slot = slot, Template = template, Size = size });
                return slot;
            }
        }

        public static void Restore()
        {
            var teb = PeImage.CurrentTeb();
            lock (Gate)
            {
                if (Threads.TryGetValue(teb.ToInt64(), out var state) &&
                    Marshal.ReadIntPtr(teb + 0x58) == state.Table)
                    Marshal.WriteIntPtr(teb + 0x58, state.Original);
            }
        }

        public static void Release()
        {
            Restore();
            var key = PeImage.CurrentTeb().ToInt64();
            lock (Gate)
            {
                if (!Threads.TryGetValue(key, out var state)) return;
                Threads.Remove(key);
                foreach (var block in state.Copies.Values) Marshal.FreeHGlobal(block);
                Marshal.FreeHGlobal(state.Table);
            }
        }

        /// <summary>
        /// Gives the calling thread its own copies. Safe to call more than
        /// once: only copies owned by this bridge count as initialized. A
        /// nonzero value in the system heap is not evidence of a TLS block.
        /// </summary>
        public static bool Adopt()
        {
            try
            {
                // Reuse the reader already initialized by PE mapping. Its
                // app-memory API contract has been measured on this console.
                var teb = PeImage.CurrentTeb();
                if (teb == IntPtr.Zero) throw new InvalidOperationException("No thread environment block");

                var table = Marshal.ReadIntPtr(teb + 0x58);
                if (table == IntPtr.Zero) throw new InvalidOperationException("No static TLS table");

                lock (Gate)
                {
                    if (!Threads.TryGetValue(teb.ToInt64(), out var state))
                    {
                        state = new ThreadState
                        {
                            Table = Marshal.AllocHGlobal((NativeSlots + GameSlots) * IntPtr.Size),
                        };
                        var empty = new byte[(NativeSlots + GameSlots) * IntPtr.Size];
                        Marshal.Copy(empty, 0, state.Table, empty.Length);
                        Threads.Add(teb.ToInt64(), state);
                    }
                    if (table != state.Table)
                    {
                        var readable = PeImage.ReadableBytes(table);
                        if (readable < IntPtr.Size) throw new InvalidOperationException("Unreadable native TLS vector");
                        var prefix = new byte[Math.Min(readable, NativeSlots * IntPtr.Size)];
                        Marshal.Copy(table, prefix, 0, prefix.Length);
                        Marshal.Copy(prefix, 0, state.Table, prefix.Length);
                        state.Original = table;
                    }
                    foreach (var one in Blocks)
                    {
                        if (state.Copies.ContainsKey(one.Slot)) continue;

                        var block = Marshal.AllocHGlobal(Math.Max(one.Size, 8));
                        for (var i = 0; i < one.Size; i++) Marshal.WriteByte(block, i, 0);
                        if (one.Template != null && one.Template.Length > 0)
                        {
                            Marshal.Copy(one.Template, 0, block, one.Template.Length);
                        }
                        Marshal.WriteIntPtr(state.Table, one.Slot * IntPtr.Size, block);
                        state.Copies.Add(one.Slot, block);
                        System.Threading.Interlocked.Increment(ref CopiedBlocks);
                    }
                    Marshal.WriteIntPtr(teb + 0x58, state.Table);
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

        /// <summary>
        /// Makes every thread the program starts adopt its own copies before
        /// it runs a line of its own code.
        /// </summary>
        public static void Install(SystemImports imports)
        {
            // Preserve the existing priority hook. Resolving the system export
            // here would silently bypass it when installing the TLS wrapper.
            var real = imports.Overrides.TryGetValue("kernel32.dll!CreateThread", out var previous)
                ? previous
                : imports.SystemAddress("kernel32.dll", "CreateThread");
            if (real == IntPtr.Zero) return;
            realCreateThread = Marshal.GetDelegateForFunctionPointer<CreateThreadDelegate>(real);

            // Native CRT thread wrappers can call ExitThread without returning
            // through our trampoline. Windows must see its original vector
            // before running thread detach callbacks and freeing loader data.
            var exitAddress = imports.SystemAddress("kernel32.dll", "ExitThread");
            if (exitAddress != IntPtr.Zero)
            {
                realExitThread = Marshal.GetDelegateForFunctionPointer<ExitDelegate>(exitAddress);
                exitThread = code =>
                {
                    try { Release(); }
                    finally { realExitThread(code); }
                };
            }
            var freeExitAddress = imports.SystemAddress("kernel32.dll", "FreeLibraryAndExitThread");
            if (freeExitAddress != IntPtr.Zero)
            {
                realFreeAndExitThread = Marshal.GetDelegateForFunctionPointer<FreeAndExitDelegate>(freeExitAddress);
                freeAndExitThread = (module, code) =>
                {
                    try { Release(); }
                    finally { realFreeAndExitThread(module, code); }
                };
            }

            // The thread starts in here, takes its copies, and only then goes
            // where it was asked to go. Doing it any later means the thread's
            // own start-up code reads a slot nobody has filled.
            trampoline = parameter =>
            {
                var start = Marshal.ReadIntPtr(parameter, 0);
                var given = Marshal.ReadIntPtr(parameter, IntPtr.Size);
                Marshal.FreeHGlobal(parameter);

                if (!Adopt()) return 8;

                try
                {
                    return Marshal.GetDelegateForFunctionPointer<StartDelegate>(start)(given);
                }
                finally
                {
                    Release();
                }
            };
            var trampolineAddress = Marshal.GetFunctionPointerForDelegate(trampoline);

            createThread = (attributes, stackSize, start, parameter, flags, threadId) =>
            {
                if (start == IntPtr.Zero)
                {
                    return realCreateThread(
                        attributes, stackSize, start, parameter, flags, threadId);
                }

                // Where the thread was really going, carried across in memory
                // the new thread frees as soon as it has read it.
                var carried = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(carried, 0, start);
                Marshal.WriteIntPtr(carried, IntPtr.Size, parameter);

                var made = realCreateThread(
                    attributes, stackSize, trampolineAddress, carried, flags, threadId);
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
            {
                imports.Overrides[module + "!CreateThread"] =
                    Marshal.GetFunctionPointerForDelegate(createThread);
                if (exitThread != null)
                    imports.Overrides[module + "!ExitThread"] = Marshal.GetFunctionPointerForDelegate(exitThread);
                if (freeAndExitThread != null)
                    imports.Overrides[module + "!FreeLibraryAndExitThread"] = Marshal.GetFunctionPointerForDelegate(freeAndExitThread);
            }
        }
    }
}
