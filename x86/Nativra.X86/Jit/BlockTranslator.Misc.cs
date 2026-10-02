using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    public sealed partial class BlockTranslator
    {
        private static bool IsStringOp(int op) =>
            op == 0xA4 || op == 0xA5 || op == 0xAA || op == 0xAB || op == 0xAC || op == 0xAD;

        /// <summary>The locked forms that are emitted with their own LOCK prefix.</summary>
        private static bool LockIsAtomicHere(in Instruction ins) =>
            ins.Mod != 3 && (ins.Op == 0x0FB0 || ins.Op == 0x0FB1 || (ins.Op == 0x0FC7 && ins.RegField == 1) ||
                             ins.Op == 0x0FAB || ins.Op == 0x0FB3 || ins.Op == 0x0FBB ||
                             (ins.Op == 0x0FBA && ins.RegField >= 5));

        /// <summary>Opcodes below 0F00 that ignore a REP/REPNE prefix (PAUSE, REP RET, a prefixed FWAIT).</summary>
        private static bool RepIsIgnored(int op) => op == 0x90 || op == 0x9B || op == 0xC2 || op == 0xC3;

        /// <summary>
        /// MOVS/STOS/LODS, with and without REP. Each iteration works on the guest
        /// registers in place, so a fault part way leaves ESI/EDI/ECX as the
        /// interpreter would. A set direction flag, which only the context holds, is
        /// left to the interpreter.
        /// </summary>
        private bool EmitString(in Instruction ins, int op, int size)
        {
            if (ins.Segment != Seg.None || ins.AddrSize16) return false;
            if (op == 0xA4 || op == 0xAA || op == 0xAC) size = 8;
            var step = size / 8;
            var bad = NewLabel("str_bad");
            var done = NewLabel("str_done");
            var top = NewLabel("str_top");
            var end = NewLabel("str_end");

            e.Pushfq();
            e.TestMemImm(Ctxr, -1, 1, Ctx.EFlags, Flag.DF);
            e.Jcc(5, bad);
            e.Popfq();

            var rep = ins.Rep != 0;
            if (rep)
            {
                e.Label(top);
                e.Jrcxz(end);
            }
            switch (op)
            {
                case 0xA4:
                case 0xA5:
                    e.LoadMem(S2, Mem, G[Reg.Esi], 1, 0, size);
                    e.StoreMem(S2, Mem, G[Reg.Edi], 1, 0, size);
                    e.Lea(G[Reg.Esi], G[Reg.Esi], -1, 1, step);
                    e.Lea(G[Reg.Edi], G[Reg.Edi], -1, 1, step);
                    break;
                case 0xAA:
                case 0xAB:
                    e.StoreMem(G[Reg.Eax], Mem, G[Reg.Edi], 1, 0, size);
                    e.Lea(G[Reg.Edi], G[Reg.Edi], -1, 1, step);
                    break;
                default:
                    e.LoadMem(G[Reg.Eax], Mem, G[Reg.Esi], 1, 0, size);
                    e.Lea(G[Reg.Esi], G[Reg.Esi], -1, 1, step);
                    break;
            }
            if (rep)
            {
                e.Lea(G[Reg.Ecx], G[Reg.Ecx], -1, 1, -1);
                e.Jmp(top);
                e.Label(end);
            }
            e.Jmp(done);
            e.Label(bad);
            e.Popfq();
            EmitExit(ins.Address, Ctx.ReasonFallback);
            e.Label(done);
            return true;
        }

        private bool EmitCmpxchg(in Instruction ins, int width)
        {
            if (ins.Lock && !IsMem(ins)) return false;
            var prefix = width == 16 ? 0x66 : 0;
            var opcode = width == 8 ? 0xB0 : 0xB1;
            var equal = NewLabel("cx_equal");
            var end = NewLabel("cx_end");
            // The host sets the flags as CMP accumulator, destination would; they are
            // rebuilt from that definition rather than trusted from the instruction
            // (an emulating host does not always get them right).
            e.MovRegReg(S3, G[Reg.Eax]);
            if (IsMem(ins))
            {
                EmitAddress(ins);
                if (ins.Lock) e.U8(0xF0);
                e.Rm(prefix, false, 0x0F, opcode, G[ins.RegField], Mem, S1, 1, 0);
            }
            else e.Rr(prefix, false, 0x0F, opcode, G[ins.RegField], G[ins.Rm]);
            e.Jcc(4, equal);
            e.AluRegReg(7, S3, G[Reg.Eax], width);
            e.Jmp(end);
            e.Label(equal);
            e.AluRegReg(7, S3, S3, width);
            e.Label(end);
            return true;
        }

        private bool EmitCmpxchg8b(in Instruction ins)
        {
            if (ins.RegField != 1 || !IsMem(ins)) return false;
            EmitAddress(ins);
            if (ins.Lock) e.U8(0xF0);
            e.Rm(0, false, 0x0F, 0xC7, 1, Mem, S1, 1, 0);
            return true;
        }

        private bool EmitMxcsr(in Instruction ins)
        {
            if (!IsMem(ins) || (ins.RegField != 2 && ins.RegField != 3)) return false;
            if (ins.Rep != 0 || ins.OpSize16) return false;
            EmitAddress(ins);
            EnsureXmm();   // the guest's MXCSR travels with the XMM state
            if (ins.RegField == 2)
            {
                e.LoadMem(S2, Mem, S1, 1, 0, 32);
                e.StoreCtx(S2, Ctxr, Ctx.Mxcsr);
            }
            else
            {
                e.LoadCtx(S2, Ctxr, Ctx.Mxcsr);
                e.StoreMem(S2, Mem, S1, 1, 0, 32);
            }
            return true;
        }
    }
}
