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
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;

        [DllImport("api-ms-win-core-memory-l1-1-0.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocFromApp(
            IntPtr address, UIntPtr size, uint type, uint protect);

        [DllImport("api-ms-win-core-memory-l1-1-0.dll", SetLastError = true)]
        private static extern bool VirtualProtectFromApp(
            IntPtr address, UIntPtr size, uint protect, out uint old);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr TebDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr CreateThreadDelegate(
            IntPtr attributes, IntPtr stackSize, IntPtr start, IntPtr parameter,
            uint flags, IntPtr threadId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint StartDelegate(IntPtr parameter);

        /// <summary>One library's thread-local template and the slot it uses.</summary>
        private struct Block
        {
            public int Slot;
            public byte[] Template;
            public int Size;
        }

        private static readonly List<Block> Blocks = new List<Block>();
        private static readonly object Gate = new object();

        private static IntPtr tebReader;
        private static CreateThreadDelegate createThread;
        private static CreateThreadDelegate realCreateThread;
        private static StartDelegate trampoline;

        /// <summary>Threads that were given their own copies.</summary>
        public static long Adopted;

        /// <summary>
        /// Records a library's template so every thread made after this gets a
        /// copy of it. Called once per mapped library that has one.
        /// </summary>
        public static void Remember(int slot, byte[] template, int size)
        {
            lock (Gate)
            {
                Blocks.Add(new Block { Slot = slot, Template = template, Size = size });
            }
        }

        private static IntPtr CurrentTeb()
        {
            if (tebReader == IntPtr.Zero)
            {
                // The thread's own block, read from where the processor keeps
                // it. There is no call for this and no way to ask for it from
                // managed code, so it is two instructions written by hand.
                var code = new byte[]
                {
                    0x65, 0x48, 0x8B, 0x04, 0x25, 0x30, 0x00, 0x00, 0x00, // mov rax, gs:[0x30]
                    0xC3,                                                 // ret
                };
                var page = VirtualAllocFromApp(
                    IntPtr.Zero, (UIntPtr)64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (page == IntPtr.Zero) return IntPtr.Zero;
                Marshal.Copy(code, 0, page, code.Length);
                VirtualProtectFromApp(page, (UIntPtr)64, PAGE_EXECUTE_READ, out _);
                tebReader = page;
            }
            return Marshal.GetDelegateForFunctionPointer<TebDelegate>(tebReader)();
        }

        /// <summary>
        /// Gives the calling thread its own copies. Safe to call more than
        /// once: a slot that already holds something is left alone, because
        /// overwriting it would throw away whatever the thread had put there.
        /// </summary>
        public static void Adopt()
        {
            try
            {
                var teb = CurrentTeb();
                if (teb == IntPtr.Zero) return;

                var table = Marshal.ReadIntPtr(teb + 0x58);
                if (table == IntPtr.Zero) return;

                lock (Gate)
                {
                    foreach (var one in Blocks)
                    {
                        if (Marshal.ReadIntPtr(table, one.Slot * 8) != IntPtr.Zero) continue;

                        var block = Marshal.AllocHGlobal(Math.Max(one.Size, 8));
                        for (var i = 0; i < one.Size; i++) Marshal.WriteByte(block, i, 0);
                        if (one.Template != null && one.Template.Length > 0)
                        {
                            Marshal.Copy(one.Template, 0, block, one.Template.Length);
                        }
                        Marshal.WriteIntPtr(table, one.Slot * 8, block);
                    }
                }
                System.Threading.Interlocked.Increment(ref Adopted);
            }
            catch
            {
                // A thread without a table is a thread that will not read one.
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

            // The thread starts in here, takes its copies, and only then goes
            // where it was asked to go. Doing it any later means the thread's
            // own start-up code reads a slot nobody has filled.
            trampoline = parameter =>
            {
                var start = Marshal.ReadIntPtr(parameter, 0);
                var given = Marshal.ReadIntPtr(parameter, IntPtr.Size);
                Marshal.FreeHGlobal(parameter);

                Adopt();

                return Marshal.GetDelegateForFunctionPointer<StartDelegate>(start)(given);
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
            }
        }
    }
}
