using System;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    /// <summary>
    /// The native block of guest state a compiled block reads and writes, and
    /// the fixed host-register assignment the whole JIT agrees on.
    ///
    /// Guest registers are pinned to host registers for the length of a block,
    /// so most guest ALU instructions become the identical x64 instruction on
    /// the mapped registers — and the guest's EFLAGS then come straight from
    /// the host processor, which shares x86's flag semantics. A block is a
    /// self-contained function: it loads the guest registers and flags from
    /// this context, runs, writes them back, and returns the next guest EIP.
    /// </summary>
    public static class Ctx
    {
        // Byte offsets into the context block.
        public const int Regs = 0;         // 8 * 4: guest eax..edi
        public const int Eip = 32;
        public const int EFlags = 36;
        public const int FsBase = 40;
        public const int GsBase = 44;
        public const int MemBase = 48;     // 8 bytes: host pointer to guest address 0
        public const int ExitReason = 56;
        public const int ExitData = 60;    // fault address, or the imm a helper needs
        public const int Scratch = 64;     // 8 bytes of spill room for helper glue
        public const int Size = 128;

        /// <summary>Guest register N lives in this host register during a block.</summary>
        public static readonly int[] GuestToHost =
        {
            X64.Rax, X64.Rcx, X64.Rdx, X64.Rbx, X64.Rdi, X64.R12, X64.R13, X64.R14,
        };

        public const int MemBaseReg = X64.Rsi;
        public const int CtxReg = X64.Rbp;
        public const int Scratch1 = X64.R15;
        public const int Scratch2 = X64.R11;
        public const int Scratch3 = X64.R10;

        /// <summary>Only the arithmetic flags travel in the host flags register.</summary>
        public const uint ArithFlags = Flag.Arith;

        // Why a block returned to the dispatcher.
        public const int ReasonNext = 0;       // fell through / took a branch; continue at Eip
        public const int ReasonFallback = 1;   // hit an untranslated instruction at Eip
        public const int ReasonHalt = 2;       // hlt / explicit stop
    }

    /// <summary>Owns the native context block and mirrors it to/from a managed <see cref="CpuState"/>.</summary>
    public sealed unsafe class JitContext : IDisposable
    {
        private readonly byte* block;

        public JitContext(GuestMemory memory)
        {
            block = (byte*)Marshal.AllocHGlobal(Ctx.Size);
            for (var i = 0; i < Ctx.Size; i++) block[i] = 0;
            *(ulong*)(block + Ctx.MemBase) = (ulong)memory.HostBase.ToInt64();
        }

        public IntPtr Pointer => (IntPtr)block;

        public uint GetReg(int i) => *(uint*)(block + Ctx.Regs + i * 4);
        public void SetReg(int i, uint v) => *(uint*)(block + Ctx.Regs + i * 4) = v;
        public uint Eip { get => *(uint*)(block + Ctx.Eip); set => *(uint*)(block + Ctx.Eip) = value; }
        public uint EFlags { get => *(uint*)(block + Ctx.EFlags); set => *(uint*)(block + Ctx.EFlags) = value; }
        public uint FsBase { get => *(uint*)(block + Ctx.FsBase); set => *(uint*)(block + Ctx.FsBase) = value; }
        public uint GsBase { get => *(uint*)(block + Ctx.GsBase); set => *(uint*)(block + Ctx.GsBase) = value; }
        public int ExitReason { get => *(int*)(block + Ctx.ExitReason); set => *(int*)(block + Ctx.ExitReason) = value; }
        public uint ExitData { get => *(uint*)(block + Ctx.ExitData); set => *(uint*)(block + Ctx.ExitData) = value; }

        public void Load(CpuState cpu)
        {
            for (var i = 0; i < 8; i++) SetReg(i, cpu.R[i]);
            Eip = cpu.Eip;
            EFlags = cpu.EFlags;
            FsBase = cpu.FsBase;
        }

        public void Store(CpuState cpu)
        {
            for (var i = 0; i < 8; i++) cpu.R[i] = GetReg(i);
            cpu.Eip = Eip;
            cpu.EFlags = EFlags;
        }

        public void Dispose()
        {
            if (block != null) Marshal.FreeHGlobal((IntPtr)block);
        }
    }
}
