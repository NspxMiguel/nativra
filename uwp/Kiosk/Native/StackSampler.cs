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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr HeaderDelegate(IntPtr pc, out IntPtr imageBase);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder name, uint size);

        private static HeaderDelegate header;
        private static readonly Dictionary<long, string> moduleNames = new Dictionary<long, string>();

        /// <summary>
        /// A game image as module+offset, and failing that any module in the
        /// process, the system's and this app's own: a thread stuck inside the
        /// bridge shows up as Kiosk or SharedLibrary rather than as a number.
        /// </summary>
        private static string Describe(long address)
        {
            var mine = ImageLookup.Describe(address);
            if (!mine.StartsWith("0x", StringComparison.Ordinal) || header == null) return mine;
            try
            {
                if (header(new IntPtr(address), out var imageBase) == IntPtr.Zero || imageBase == IntPtr.Zero) return mine;
                var key = imageBase.ToInt64();
                if (!moduleNames.TryGetValue(key, out var name))
                {
                    var text = new System.Text.StringBuilder(260);
                    GetModuleFileNameW(imageBase, text, 260);
                    name = System.IO.Path.GetFileName(text.ToString());
                    if (string.IsNullOrEmpty(name)) name = "module@" + key.ToString("X");
                    moduleNames[key] = name;
                }
                return "~" + name + "+0x" + (address - key).ToString("X");
            }
            catch
            {
                return mine;
            }
        }

        private const uint ContextFull = 0x10000B;
        private const int ContextSize = 1232;
        private const int RipOffset = 0xF8;
        private const int RspOffset = 0x98;
        private const int StackBytes = 65536;
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
            header = Get<HeaderDelegate>(imports, "RtlPcToFileHeader");
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

        private static bool Plumbing(string frame) =>
            frame.StartsWith("~ntdll.dll", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("~RPCRT4.dll", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("~combase.dll", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("~OneCoreCommonProxyStub.dll", StringComparison.OrdinalIgnoreCase)
            || frame.StartsWith("~KERNELBASE.dll", StringComparison.OrdinalIgnoreCase);

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
                    // Page by page up to the top of the stack: one read that
                    // runs past it fails whole.
                    var at = rsp;
                    while (got < StackBytes)
                    {
                        var toPage = 4096 - (at & 4095);
                        var size = Math.Min(toPage, StackBytes - got);
                        if (read(currentProcess(), new IntPtr(at), stack + (int)got, new IntPtr(size), readCount) == 0) break;
                        got += size;
                        at += size;
                    }
                }
                finally
                {
                    resume(thread.Handle);
                }

                if (got > 0) Marshal.Copy(stack, words, 0, (int)(got / 8));
                var frames = new List<string>();
                // Every word that lands in a module; the ~ ones are the
                // system's or this app's, the rest are the game's.
                for (var i = 0; i < got / 8 && frames.Count < 40; i++)
                {
                    var described = Describe(words[i]);
                    if (described.StartsWith("0x", StringComparison.Ordinal)) continue;
                    // Past the first few, the COM and RPC plumbing is noise:
                    // what matters is who made the call.
                    if (frames.Count >= 4 && Plumbing(described)) continue;
                    if (frames.Count > 0 && frames[frames.Count - 1] == described) continue;
                    frames.Add(described);
                }
                lines.Add("stack " + thread.Name + " tid=" + thread.Id + " at " + Describe(rip)
                    + " | " + string.Join(" < ", frames));
            }
            return lines;
        }
    }
}
