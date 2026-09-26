using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Where each game thread is, sampled from inside the process.
    ///
    /// The console takes no live dump of an app (the Device Portal answers
    /// 500), and a game that stops with the CPU idle says nothing about what
    /// it waits for. Each tracked thread is suspended for an instant: its
    /// instruction pointer and the top of its stack are copied into buffers
    /// allocated up front, and it is resumed before anything else happens,
    /// since a suspended thread may hold the heap lock. The stack words that
    /// fall inside the game's own images are return-address candidates, and
    /// module+offset is something a disassembler can answer.
    /// </summary>
    internal static class StackSampler
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint CountDelegate(IntPtr thread);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ContextDelegate(IntPtr thread, IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr PseudoDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint IdDelegate(IntPtr thread);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DuplicateDelegate(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, int inherit, uint options);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadDelegate(IntPtr process, IntPtr address, IntPtr buffer, IntPtr size, IntPtr read);

        private const uint ContextFull = 0x10000B;
        private const int ContextSize = 1232;
        private const int RipOffset = 0xF8;
        private const int RspOffset = 0x98;
        private const int StackBytes = 4096;
        private const int MaxThreads = 48;

        private sealed class Tracked
        {
            public string Name;
            public IntPtr Handle;
            public uint Id;
        }

        private static readonly List<Tracked> threads = new List<Tracked>();
        private static CountDelegate suspend, resume;
        private static ContextDelegate getContext;
        private static PseudoDelegate currentProcess, currentThread;
        private static IdDelegate threadId;
        private static DuplicateDelegate duplicate;
        private static ReadDelegate read;
        private static IntPtr contextBlock, context, stack, readCount;

        /// <summary>Set when stacks.txt asks for sampling.</summary>
        public static bool Enabled;

        private static T Get<T>(SystemImports imports, string name) where T : class
        {
            var address = imports.SystemAddress("kernel32.dll", name);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        public static void Install(SystemImports imports)
        {
            suspend = Get<CountDelegate>(imports, "SuspendThread");
            resume = Get<CountDelegate>(imports, "ResumeThread");
            getContext = Get<ContextDelegate>(imports, "GetThreadContext");
            currentProcess = Get<PseudoDelegate>(imports, "GetCurrentProcess");
            currentThread = Get<PseudoDelegate>(imports, "GetCurrentThread");
            threadId = Get<IdDelegate>(imports, "GetThreadId");
            duplicate = Get<DuplicateDelegate>(imports, "DuplicateHandle");
            read = Get<ReadDelegate>(imports, "ReadProcessMemory");
            // CONTEXT must be 16-byte aligned; everything the sample touches
            // while a thread is suspended exists before it is suspended.
            contextBlock = Marshal.AllocHGlobal(ContextSize + 16);
            context = new IntPtr((contextBlock.ToInt64() + 15) & ~15L);
            stack = Marshal.AllocHGlobal(StackBytes);
            readCount = Marshal.AllocHGlobal(IntPtr.Size);
        }

        private static bool Ready =>
            Enabled && suspend != null && resume != null && getContext != null && duplicate != null
            && currentProcess != null && read != null && context != IntPtr.Zero;

        /// <summary>Keeps a handle of our own to a thread the game made.</summary>
        public static void Track(IntPtr thread, string name)
        {
            if (!Ready || thread == IntPtr.Zero) return;
            var process = currentProcess();
            if (duplicate(process, thread, process, out var own, 0, 0, 2) == 0) return;
            lock (threads)
            {
                if (threads.Count >= MaxThreads) threads.RemoveAt(0);
                threads.Add(new Tracked { Name = name, Handle = own, Id = threadId?.Invoke(own) ?? 0 });
            }
        }

        /// <summary>Tracks the calling thread, such as the one that runs the game's entry point.</summary>
        public static void TrackCurrent(string name)
        {
            if (!Ready || currentThread == null) return;
            Track(currentThread(), name);
        }

        /// <summary>One line per thread: where it is and the game frames on its stack.</summary>
        public static List<string> Sample()
        {
            var lines = new List<string>();
            if (!Ready) return lines;
            Tracked[] list;
            lock (threads) list = threads.ToArray();
            var words = new long[StackBytes / 8];
            foreach (var thread in list)
            {
                long rip, rsp;
                var got = 0L;
                if (suspend(thread.Handle) == uint.MaxValue) continue;
                try
                {
                    Marshal.WriteInt32(context, 0x30, unchecked((int)ContextFull));
                    if (getContext(thread.Handle, context) == 0) continue;
                    rip = Marshal.ReadInt64(context, RipOffset);
                    rsp = Marshal.ReadInt64(context, RspOffset);
                    if (read(currentProcess(), new IntPtr(rsp), stack, new IntPtr(StackBytes), readCount) != 0)
                        got = Marshal.ReadInt64(readCount);
                    else if (read(currentProcess(), new IntPtr(rsp), stack, new IntPtr(512), readCount) != 0)
                        got = Marshal.ReadInt64(readCount);
                }
                finally
                {
                    resume(thread.Handle);
                }

                if (got > 0) Marshal.Copy(stack, words, 0, (int)(got / 8));
                var frames = new List<string>();
                for (var i = 0; i < got / 8 && frames.Count < 10; i++)
                {
                    var described = ImageLookup.Describe(words[i]);
                    if (described.StartsWith("0x", StringComparison.Ordinal)) continue;
                    if (frames.Count > 0 && frames[frames.Count - 1] == described) continue;
                    frames.Add(described);
                }
                lines.Add("stack " + thread.Name + " tid=" + thread.Id + " at " + ImageLookup.Describe(rip)
                    + " | " + string.Join(" < ", frames));
            }
            return lines;
        }
    }
}
