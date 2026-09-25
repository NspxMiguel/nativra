using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    /// <summary>
    /// Runs 32-bit guest code JIT-first: it translates a block starting at the
    /// current guest EIP, executes the native code, and — when the block stops
    /// at an instruction the translator does not cover — hands that one
    /// instruction to the reference interpreter and carries on. Because the
    /// interpreter is proven against real silicon and the pinned-register JIT
    /// borrows the host's own flags, the two never disagree; the JIT is only
    /// ever faster, never a second source of truth.
    /// </summary>
    public sealed class JitEngine : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong BlockFn(IntPtr context);

        public CpuState Cpu { get; }
        public GuestMemory Memory { get; }
        public Interpreter Interpreter { get; }

        private readonly JitContext ctx;
        private readonly CodeCache cache = new CodeCache();
        private readonly Dictionary<uint, IntPtr> blocks = new Dictionary<uint, IntPtr>();
        private readonly Dictionary<IntPtr, BlockFn> delegates = new Dictionary<IntPtr, BlockFn>();

        public long BlocksCompiled { get; private set; }
        public long BlocksExecuted { get; private set; }
        public long InterpreterFallbacks { get; private set; }

        public JitEngine(CpuState cpu, GuestMemory memory)
        {
            if (!memory.IsNative)
                throw new ArgumentException("the JIT needs a native-backed guest memory (guest address + host base = pointer)");
            Cpu = cpu;
            Memory = memory;
            Interpreter = new Interpreter(cpu, memory);
            ctx = new JitContext(memory);
        }

        /// <summary>
        /// Translate and run one natural block at the current EIP, then, if it
        /// stopped on an untranslated instruction, interpret that one. Blocks
        /// are cached and reused, so a hot loop compiles once. Returns why the
        /// block stopped.
        /// </summary>
        public int RunBlock()
        {
            ctx.Load(Cpu);
            var block = GetBlock(Cpu.Eip, 0);
            DelegateFor(block)(ctx.Pointer);
            BlocksExecuted++;
            ctx.Store(Cpu);
            if (ctx.ExitReason == Ctx.ReasonFallback)
            {
                Interpreter.Step();
                InterpreterFallbacks++;
            }
            return ctx.ExitReason;
        }

        /// <summary>
        /// Runs until EIP reaches <paramref name="stop"/>. Real code reaches its
        /// stop through its own control flow (a ret, a jump), which ends a block
        /// anyway, so natural cached blocks never overrun it.
        /// </summary>
        public bool RunUntil(uint stop, long maxBlocks = 10_000_000)
        {
            for (long i = 0; i < maxBlocks; i++)
            {
                if (Cpu.Eip == stop) return true;
                RunBlock();
            }
            return Cpu.Eip == stop;
        }

        /// <summary>
        /// Runs a straight-line region that carries no trailing control flow,
        /// translating each block up to <paramref name="stop"/> exactly. Used by
        /// the differential tests, where a snippet's bytes are followed by
        /// unrelated memory. One-shot: these blocks are not cached.
        /// </summary>
        public bool RunToStop(uint stop, long maxBlocks = 1_000_000)
        {
            for (long i = 0; i < maxBlocks; i++)
            {
                if (Cpu.Eip == stop) return true;
                ctx.Load(Cpu);
                var translator = new BlockTranslator();
                var code = translator.Translate(Memory, Cpu.Eip, stop);
                LastFullyTranslated = translator.FullyTranslated;
                LastInstructionCount = translator.InstructionCount;
                DelegateFor(cache.Publish(code))(ctx.Pointer);
                BlocksCompiled++;
                BlocksExecuted++;
                ctx.Store(Cpu);
                if (ctx.ExitReason == Ctx.ReasonFallback)
                {
                    Interpreter.Step();
                    InterpreterFallbacks++;
                }
            }
            return Cpu.Eip == stop;
        }

        private IntPtr GetBlock(uint eip, uint stopEip)
        {
            if (blocks.TryGetValue(eip, out var cached)) return cached;

            var translator = new BlockTranslator();
            var code = translator.Translate(Memory, eip, stopEip);
            LastFullyTranslated = translator.FullyTranslated;
            LastInstructionCount = translator.InstructionCount;
            var published = cache.Publish(code);
            BlocksCompiled++;
            blocks[eip] = published;
            return published;
        }

        public bool LastFullyTranslated { get; private set; }
        public int LastInstructionCount { get; private set; }

        private BlockFn DelegateFor(IntPtr block)
        {
            if (!delegates.TryGetValue(block, out var fn))
            {
                fn = Marshal.GetDelegateForFunctionPointer<BlockFn>(block);
                delegates[block] = fn;
            }
            return fn;
        }

        public void Dispose()
        {
            cache.Dispose();
            ctx.Dispose();
        }
    }
}
