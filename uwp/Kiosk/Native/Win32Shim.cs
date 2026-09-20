using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Stands in for the Windows functions this console does not hand out.
    ///
    /// A game imports far more than it calls: of the hundred and seventy-eight
    /// the loader could not answer, only some are reached on the way to a first
    /// frame. Guessing which is wasted effort, so every unanswered import gets
    /// a small piece of generated code that records its own name and returns
    /// zero. Running the game then says exactly which ones to write, in the
    /// order it needs them.
    ///
    /// Returning zero from anything is safe on x64 because the caller cleans
    /// the stack, so a stub does not need to know the signature it is standing
    /// in for.
    /// </summary>
    public sealed class Win32Shim
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocFromApp(
            IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern bool VirtualProtectFromApp(
            IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long RecorderDelegate(long index);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long CallerDelegate(long index, long from);

        private const int ThunkSize = 96;
        // Measured: 845 system functions traced plus 180 stubs is 1025 on one
        // game, before anything resolved at run time is counted. The old
        // ceiling of 1200 left no room for those, and running out is not a
        // clean failure — a stub that cannot be built is a null in the import
        // table, which the program calls and dies on. Address space is free
        // until it is written to, so the ceiling is now far above the need.
        private const int Capacity = 4096;

        private readonly List<string> names = new List<string>();

        // A flat array, not a set: this is read from every thread the engine
        // starts while another thread is still adding to it, and a hash set
        // read during someone else's insert is how the whole process dies.
        private readonly bool[] seen = new bool[Capacity];

        /// <summary>Thunks that could not be built because the page was full.</summary>
        public long Overflowed;
        private readonly RecorderDelegate recorder;
        private readonly IntPtr recorderPointer;
        private readonly CallerDelegate noting;
        private readonly IntPtr notingPointer;

        /// <summary>Where each watched function was called from, and how often.</summary>
        public readonly Dictionary<string, long> Callers = new Dictionary<string, long>();

        private IntPtr page;
        private int used;

        /// <summary>Names of the stubs the program actually reached, in order.</summary>
        public List<string> Called { get; } = new List<string>();

        /// <summary>
        /// The last few calls including repeats. The list above records each
        /// name once, which hides the one a program dies on when it has been
        /// called before.
        /// </summary>
        private readonly int[] recent = new int[64];
        private int recentAt;

        /// <summary>
        /// How many times each stub was entered. A program that is stuck and a
        /// program that is busy look identical in a list of names; they look
        /// nothing alike in a count taken twice.
        /// </summary>
        private readonly long[] counts = new long[Capacity];

        /// <summary>Total calls, which is the cheapest sign of life there is.</summary>
        public long Total;

        // Where each thread was last seen. A engine whose worker threads are
        // busy while the main thread has not called anything for seconds is not
        // slow, it is blocked — and only a per-thread mark shows the difference.
        private const int Slots = 64;
        private readonly int[] threadId = new int[Slots];
        private readonly int[] threadWhere = new int[Slots];
        private readonly long[] threadCount = new long[Slots];
        private readonly int[] threadWhen = new int[Slots];

        public List<string> Recent()
        {
            var out_ = new List<string>();
            for (var i = 0; i < recent.Length; i++)
            {
                var slot = recent[(recentAt + i) % recent.Length];
                if (slot > 0 && slot - 1 < names.Count) out_.Add(names[slot - 1]);
            }
            return out_;
        }

        /// <summary>The busiest calls, which is what the program is doing.</summary>
        public List<string> Busiest(int take)
        {
            var pairs = new List<KeyValuePair<long, string>>();
            for (var i = 0; i < names.Count && i < counts.Length; i++)
            {
                if (counts[i] > 0) pairs.Add(new KeyValuePair<long, string>(counts[i], names[i]));
            }
            pairs.Sort((a, b) => b.Key.CompareTo(a.Key));
            var out_ = new List<string>();
            for (var i = 0; i < pairs.Count && i < take; i++)
            {
                out_.Add(pairs[i].Key + "x " + pairs[i].Value);
            }
            return out_;
        }

        /// <summary>Each live thread, where it was last, and how long ago.</summary>
        public List<string> Threads()
        {
            var now = Environment.TickCount;
            var out_ = new List<string>();
            for (var i = 0; i < Slots; i++)
            {
                if (threadId[i] == 0) continue;
                var where = threadWhere[i] > 0 && threadWhere[i] - 1 < names.Count
                    ? names[threadWhere[i] - 1]
                    : "?";
                out_.Add($"thread {threadId[i]} calls={threadCount[i]} " +
                         $"idle={now - threadWhen[i]}ms at {where}");
            }
            return out_;
        }

        public Win32Shim()
        {
            // Held in a field so the garbage collector cannot take the delegate
            // while native code still holds its address.
            recorder = Record;
            recorderPointer = Marshal.GetFunctionPointerForDelegate(recorder);
            noting = NoteCaller;
            notingPointer = Marshal.GetFunctionPointerForDelegate(noting);
        }

        private long Record(long index)
        {
            var slot = (int)index;
            if (slot < 0 || slot >= names.Count) return 0;

            // No lock here on purpose: this runs on every call the engine makes,
            // millions of them, and a lock would make the measurement the
            // slowest thing in the process. A lost increment costs nothing.
            counts[slot]++;
            Total++;
            recent[recentAt] = slot + 1;
            recentAt = (recentAt + 1) % recent.Length;

            var id = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var bucket = id & (Slots - 1);
            threadId[bucket] = id;
            threadWhere[bucket] = slot + 1;
            threadCount[bucket]++;
            threadWhen[bucket] = Environment.TickCount;

            if (!seen[slot])
            {
                seen[slot] = true;
                lock (Called) Called.Add(names[slot]);
            }
            return 0;
        }


        private long NoteCaller(long index, long from)
        {
            var slot = (int)index;
            if (slot < 0 || slot >= names.Count) return 0;
            var key = names[slot] + " from " + ImageLookup.Describe(from);
            lock (Callers)
            {
                if (Callers.Count < 40 || Callers.ContainsKey(key))
                {
                    Callers.TryGetValue(key, out var seen);
                    Callers[key] = seen + 1;
                }
            }
            return Record(index);
        }

        /// <summary>
        /// Like a trace, but it also writes down where the call came from.
        ///
        /// A program stuck in a loop names the same three functions forever,
        /// and none of them is the answer — the answer is which piece of the
        /// program is calling them. The return address is sitting on the stack
        /// at the moment of the call, so it costs one instruction to take it.
        /// </summary>
        /// <summary>
        /// Takes the next slot in the code page, for one caller at a time.
        ///
        /// Every generator below used to do this in the open: read the count,
        /// append the name, work out the address, bump the counter. Two threads
        /// resolving imports at the same moment — which is what a game with
        /// forty threads does on the way up — could take the same index and the
        /// same address, and the second one wrote its thunk over the first.
        /// What the program jumped into afterwards was half of one function and
        /// half of another. It showed up as a hang in a different place on
        /// every run, which is exactly what it looked like.
        /// </summary>
        private bool Reserve(string name, out int index, out IntPtr at)
        {
            lock (names)
            {
                index = -1;
                at = IntPtr.Zero;

                if (page == IntPtr.Zero)
                {
                    page = VirtualAllocFromApp(
                        IntPtr.Zero, (UIntPtr)(ThunkSize * Capacity),
                        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    if (page == IntPtr.Zero) return false;
                }
                if (used >= Capacity)
                {
                    // Worth saying out loud: the caller turns this into a null
                    // import, and a program that calls a null import dies
                    // somewhere that looks nothing like the cause.
                    Overflowed++;
                    return false;
                }

                index = names.Count;
                names.Add(name);
                at = page + used * ThunkSize;
                return true;
            }
        }

        public IntPtr CallerFor(string name, IntPtr target)
        {
            int index;
            IntPtr at;
            if (!Reserve(name, out index, out at)) return target;

            var code = new List<byte>();
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x48 });       // sub rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0x89, 0x4C, 0x24, 0x20 }); // mov [rsp+20], rcx
            code.AddRange(new byte[] { 0x48, 0x89, 0x54, 0x24, 0x28 }); // mov [rsp+28], rdx
            code.AddRange(new byte[] { 0x4C, 0x89, 0x44, 0x24, 0x30 }); // mov [rsp+30], r8
            code.AddRange(new byte[] { 0x4C, 0x89, 0x4C, 0x24, 0x38 }); // mov [rsp+38], r9
            code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x48 }); // mov rdx, [rsp+48]
            code.AddRange(new byte[] { 0x48, 0xB9 });                   // mov rcx, index
            code.AddRange(BitConverter.GetBytes((long)index));
            code.AddRange(new byte[] { 0x48, 0xB8 });                   // mov rax, noting
            code.AddRange(BitConverter.GetBytes(notingPointer.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xD0 });                   // call rax
            code.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x20 });
            code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x28 });
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x30 });
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x4C, 0x24, 0x38 });
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x48 });       // add rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0xB8 });                   // mov rax, target
            code.AddRange(BitConverter.GetBytes(target.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });                   // jmp rax

            Write(at, code.ToArray());
            return at;
        }

        /// <summary>
        /// Emits: mov rcx, index ; mov rax, recorder ; jmp rax.
        /// A tail jump keeps the caller's return address, so the stub answers
        /// with whatever the recorder returns.
        /// </summary>
        public IntPtr StubFor(string name)
        {
            int index;
            IntPtr at;
            if (!Reserve(name, out index, out at)) return IntPtr.Zero;

            var code = new List<byte> { 0x48, 0xB9 };            // mov rcx, imm64
            code.AddRange(BitConverter.GetBytes((long)index));
            code.AddRange(new byte[] { 0x48, 0xB8 });            // mov rax, imm64
            code.AddRange(BitConverter.GetBytes(recorderPointer.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });            // jmp rax
            Write(at, code.ToArray());

            return at;
        }

        /// <summary>
        /// Wraps a real function so the call is recorded and then made anyway.
        ///
        /// The four argument registers are put on the stack, the recorder is
        /// called with the index, they are put back, and the jump to the real
        /// address leaves the caller's return address in place — so the
        /// function returns straight to whoever called it, none the wiser.
        /// </summary>
        public IntPtr TraceFor(string name, IntPtr target)
        {
            int index;
            IntPtr at;
            if (!Reserve(name, out index, out at)) return target;

            var code = new List<byte>();
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x48 });             // sub rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0x89, 0x4C, 0x24, 0x20 });       // mov [rsp+20], rcx
            code.AddRange(new byte[] { 0x48, 0x89, 0x54, 0x24, 0x28 });       // mov [rsp+28], rdx
            code.AddRange(new byte[] { 0x4C, 0x89, 0x44, 0x24, 0x30 });       // mov [rsp+30], r8
            code.AddRange(new byte[] { 0x4C, 0x89, 0x4C, 0x24, 0x38 });       // mov [rsp+38], r9
            code.AddRange(new byte[] { 0x48, 0xB9 });                         // mov rcx, index
            code.AddRange(BitConverter.GetBytes((long)index));
            code.AddRange(new byte[] { 0x48, 0xB8 });                         // mov rax, recorder
            code.AddRange(BitConverter.GetBytes(recorderPointer.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xD0 });                         // call rax
            code.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x20 });       // mov rcx, [rsp+20]
            code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x28 });       // mov rdx, [rsp+28]
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x30 });       // mov r8, [rsp+30]
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x4C, 0x24, 0x38 });       // mov r9, [rsp+38]
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x48 });             // add rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0xB8 });                         // mov rax, target
            code.AddRange(BitConverter.GetBytes(target.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });                         // jmp rax

            Write(at, code.ToArray());
            return at;
        }

        /// <summary>
        /// A stub that answers with something other than zero.
        ///
        /// Zero is the wrong answer for most of what a program asks a window
        /// system: a window handle of zero means the window was never created,
        /// and the caller gives up or walks into it. Registering a class,
        /// creating a window, showing it — each has a value that means "fine",
        /// and this returns that value after recording the call.
        /// </summary>
        public IntPtr StubReturning(string name, long value)
        {
            int index;
            IntPtr at;
            if (!Reserve(name, out index, out at)) return IntPtr.Zero;

            var code = new List<byte>();
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });       // sub rsp, 0x28
            code.AddRange(new byte[] { 0x48, 0xB9 });                   // mov rcx, index
            code.AddRange(BitConverter.GetBytes((long)index));
            code.AddRange(new byte[] { 0x48, 0xB8 });                   // mov rax, recorder
            code.AddRange(BitConverter.GetBytes(recorderPointer.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xD0 });                   // call rax
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });       // add rsp, 0x28
            code.AddRange(new byte[] { 0x48, 0xB8 });                   // mov rax, value
            code.AddRange(BitConverter.GetBytes(value));
            code.AddRange(new byte[] { 0xC3 });                         // ret

            Write(at, code.ToArray());
            return at;
        }

        /// <summary>
        /// Puts a stub in the page. A page cannot be written and executed at
        /// the same time, and stubs keep being made after the first seal —
        /// a game that looks a function up while running asks for one — so
        /// each write opens the page and closes it again.
        /// </summary>
        private void Write(IntPtr at, byte[] code)
        {
            lock (names)
            {
                VirtualProtectFromApp(
                    page, (UIntPtr)(ThunkSize * Capacity), PAGE_READWRITE, out _);
                Marshal.Copy(code, 0, at, code.Length);
                VirtualProtectFromApp(
                    page, (UIntPtr)(ThunkSize * Capacity), PAGE_EXECUTE_READ, out _);
            }
        }

        /// <summary>Marks the end of setup; each write seals the page itself.</summary>
        public void Seal()
        {
            if (page == IntPtr.Zero) return;
            VirtualProtectFromApp(
                page, (UIntPtr)(ThunkSize * Capacity), PAGE_EXECUTE_READ, out _);
        }
    }
}
