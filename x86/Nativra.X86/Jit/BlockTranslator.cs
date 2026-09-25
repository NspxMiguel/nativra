using System.Runtime.InteropServices;
using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    /// <summary>
    /// Translates a run of straight-line 32-bit guest code into one self-
    /// contained x64 function. Guest GPRs are pinned to host GPRs (see
    /// <see cref="Ctx.GuestToHost"/>), so a guest ALU instruction becomes the
    /// same x64 instruction on the mapped registers and its flags fall out of
    /// the host processor unchanged.
    ///
    /// The block stops — and returns to the dispatcher for one interpreter
    /// step — at the first control-flow or untranslated instruction. That makes
    /// the JIT always correct: it is a fast path over the reference interpreter,
    /// never a replacement that could diverge from it.
    /// </summary>
    public sealed class BlockTranslator
    {
        private static readonly int[] G = Ctx.GuestToHost;
        private const int Mem = Ctx.MemBaseReg;
        private const int S1 = Ctx.Scratch1;
        private const int S2 = Ctx.Scratch2;
        private const int Ctxr = Ctx.CtxReg;

        public bool FullyTranslated { get; private set; }
        public int InstructionCount { get; private set; }

        private X64Emit e;

        /// <summary>
        /// Emits a block starting at <paramref name="startEip"/>. If
        /// <paramref name="stopEip"/> is non-zero the block ends there (used by
        /// the differential tests to translate exactly one snippet); otherwise
        /// it runs to a natural boundary or the instruction cap.
        /// </summary>
        public byte[] Translate(ICodeReader code, uint startEip, uint stopEip, int maxInstructions = 256)
        {
            e = new X64Emit();
            FullyTranslated = true;
            InstructionCount = 0;
            EmitPrologue();

            var eip = startEip;
            var terminated = false;
            for (var n = 0; n < maxInstructions; n++)
            {
                if (stopEip != 0 && eip == stopEip) { EmitExit(eip, Ctx.ReasonNext); terminated = true; break; }

                Instruction ins;
                try { ins = Decoder.Decode(code, eip); }
                catch { EmitExit(eip, Ctx.ReasonFallback); FullyTranslated = false; terminated = true; break; }

                if (!ins.Valid || ins.Rep != 0 || ins.Lock || !TryEmit(ins))
                {
                    EmitExit(eip, Ctx.ReasonFallback);
                    FullyTranslated = false;
                    terminated = true;
                    break;
                }
                InstructionCount++;
                eip = ins.Next;
            }
            if (!terminated) EmitExit(eip, Ctx.ReasonNext);

            e.Label("exit");
            EmitEpilogue();
            e.Finish();
            return e.ToArray();
        }

        // ------------------------------------------------------------ prologue

        private static int ArgRegister => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? X64.Rcx : X64.Rdi;

        private void EmitPrologue()
        {
            // Save every register a block uses that either ABI treats as
            // callee-saved (a superset is harmless).
            foreach (var r in new[] { X64.Rbx, X64.Rbp, X64.Rsi, X64.Rdi, X64.R12, X64.R13, X64.R14, X64.R15 })
                e.PushReg(r);

            e.MovRegReg(Ctxr, ArgRegister, wide: true);
            e.LoadMem(Mem, Ctxr, -1, 1, Ctx.MemBase, 64);
            for (var i = 0; i < 8; i++) e.LoadCtx(G[i], Ctxr, Ctx.Regs + i * 4);

            // Guest arithmetic flags into the host flags register.
            e.LoadCtx(S1, Ctxr, Ctx.EFlags);
            e.AluRegImm(4, S1, Ctx.ArithFlags);   // and
            e.PushReg(S1);
            e.Popfq();
            e.ClearDf();
        }

        private void EmitEpilogue()
        {
            for (var i = 0; i < 8; i++) e.StoreCtx(G[i], Ctxr, Ctx.Regs + i * 4);

            // Merge host arithmetic flags back over the kept guest bits.
            e.Pushfq();
            e.PopReg(S1);
            e.AluRegImm(4, S1, Ctx.ArithFlags);            // and s1, arith
            e.LoadCtx(S2, Ctxr, Ctx.EFlags);
            e.AluRegImm(4, S2, ~Ctx.ArithFlags);           // and s2, ~arith
            e.AluRegReg(1, S2, S1);                          // or  s2, s1
            e.StoreCtx(S2, Ctxr, Ctx.EFlags);

            e.LoadCtx(X64.Rax, Ctxr, Ctx.Eip);             // return value = next guest eip
            foreach (var r in new[] { X64.R15, X64.R14, X64.R13, X64.R12, X64.Rdi, X64.Rsi, X64.Rbp, X64.Rbx })
                e.PopReg(r);
            e.Ret();
        }

        private void EmitExit(uint eip, int reason)
        {
            e.MovMemImm(Ctxr, -1, 1, Ctx.Eip, eip);
            e.MovMemImm(Ctxr, -1, 1, Ctx.ExitReason, (uint)reason);
            e.Jmp("exit");
        }

        // ----------------------------------------------------------- addressing

        /// <summary>Loads a memory operand's guest linear address into <see cref="S1"/>.</summary>
        private void EmitAddress(in Instruction ins)
        {
            if (ins.Base >= 0)
            {
                if (ins.Index >= 0) e.Lea(S1, G[ins.Base], G[ins.Index], ins.Scale, (int)ins.Disp);
                else e.Lea(S1, G[ins.Base], -1, 1, (int)ins.Disp);
            }
            else if (ins.Index >= 0)
            {
                e.LeaNoBase(S1, G[ins.Index], ins.Scale, (int)ins.Disp);
            }
            else
            {
                e.MovRegImm32(S1, ins.Disp);
            }

            if (ins.Segment == Seg.Fs || ins.Segment == Seg.Gs)
            {
                // Add the segment base without disturbing the guest flags.
                e.LoadCtx(S2, Ctxr, ins.Segment == Seg.Fs ? Ctx.FsBase : Ctx.GsBase);
                e.Lea(S1, S1, S2, 1, 0);
            }
        }

        private bool IsMem(in Instruction ins) => ins.Mod != 3;

        // --------------------------------------------------------------- emit

        private static int OpSize(in Instruction ins) => ins.OpSize16 ? 16 : 32;

        private bool TryEmit(in Instruction ins)
        {
            var op = ins.Op;
            var size = OpSize(ins);

            // ALU r/m,r and r,r/m and the AL/eAX immediate forms.
            if (op < 0x40 && (op & 7) < 6)
            {
                var alu = op >> 3;
                var width = (op & 1) == 0 ? 8 : size;
                switch (op & 7)
                {
                    case 0: case 1: return EmitAluToRm(alu, ins, width, toReg: false);
                    case 2: case 3: return EmitAluToRm(alu, ins, width, toReg: true);
                    case 4: e.AluRegImm(alu, G[Reg.Eax], ins.Imm, 8); return true;
                    case 5: e.AluRegImm(alu, G[Reg.Eax], ins.Imm, size); return true;
                }
            }

            switch (op)
            {
                case 0x69: case 0x6B:
                    if (IsMem(ins)) { EmitAddress(ins); e.ImulRegMem(G[ins.RegField], Mem, S1, 1, 0, size); }
                    // 3-operand imul: dst = src * imm. Compute into dst directly.
                    else e.MovRegReg(G[ins.RegField], G[ins.Rm], false);
                    e.ImulRegRegImm(G[ins.RegField], G[ins.RegField],
                        op == 0x6B ? (uint)(sbyte)ins.Imm : ins.Imm, size, op == 0x6B);
                    return true;

                case 0x80: case 0x81: case 0x82: case 0x83:
                {
                    var width = op == 0x80 || op == 0x82 ? 8 : size;
                    var imm = op == 0x83 ? (uint)(sbyte)ins.Imm : ins.Imm;
                    if (IsMem(ins)) { EmitAddress(ins); e.AluMemImm(ins.RegField, Mem, S1, 1, 0, imm, width); }
                    else e.AluRegImm(ins.RegField, G[ins.Rm], imm, width);
                    return true;
                }

                case 0x84: case 0x85:
                {
                    var width = op == 0x84 ? 8 : size;
                    if (IsMem(ins)) { EmitAddress(ins); e.TestMemReg(G[ins.RegField], Mem, S1, 1, 0, width); return true; }
                    e.TestRegReg(G[ins.Rm], G[ins.RegField], width);
                    return true;
                }

                case 0x86: case 0x87:   // XCHG r/m, r
                {
                    var width = op == 0x86 ? 8 : size;
                    if (IsMem(ins)) return false; // memory xchg carries an implicit lock; leave it to the interpreter
                    if (ins.Rm != ins.RegField)
                    {
                        e.MovRegReg(S1, G[ins.Rm], true);
                        e.MovRegReg(G[ins.Rm], G[ins.RegField], width == 32);
                        e.MovRegReg(G[ins.RegField], S1, width == 32);
                        if (width != 32) return false; // partial-register swap: rare, leave to interpreter
                    }
                    return true;
                }

                case 0x88: return EmitMovStore(ins, 8);
                case 0x89: return EmitMovStore(ins, size);
                case 0x8A: return EmitMovLoad(ins, 8);
                case 0x8B: return EmitMovLoad(ins, size);
                case 0x8D:  // LEA
                    if (!IsMem(ins)) return false;
                    EmitAddressNoSeg(ins, G[ins.RegField], size);
                    return true;

                case 0x90: return true; // NOP
                case 0xC6: // MOV r/m8, imm8
                    if (IsMem(ins)) { EmitAddress(ins); e.MovMemImm(Mem, S1, 1, 0, ins.Imm, 8); }
                    else e.MovRegImm8(G[ins.Rm], (byte)ins.Imm);
                    return true;
                case 0xC7: // MOV r/m, imm
                    if (IsMem(ins)) { EmitAddress(ins); e.MovMemImm(Mem, S1, 1, 0, ins.Imm, size); }
                    else if (size == 32) e.MovRegImm32(G[ins.Rm], ins.Imm);
                    else return false;
                    return true;

                case 0xF6: case 0xF7:
                    return EmitGroup3(ins, op == 0xF6 ? 8 : size);
                case 0xFE:
                    if (ins.RegField > 1) return false;
                    if (IsMem(ins)) { EmitAddress(ins); e.IncDecMem(ins.RegField, Mem, S1, 1, 0, 8); }
                    else e.IncDecReg(ins.RegField, G[ins.Rm], 8);
                    return true;
                case 0xFF:
                    if (ins.RegField > 1) return false; // call/jmp/push handled as control flow → fallback
                    if (IsMem(ins)) { EmitAddress(ins); e.IncDecMem(ins.RegField, Mem, S1, 1, 0, size); }
                    else e.IncDecReg(ins.RegField, G[ins.Rm], size);
                    return true;

                case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                    return EmitShift(ins, op);

                case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47:
                    e.IncDecReg(0, G[op - 0x40], size); return true;   // single-byte INC (0x40-0x47 are REX in x64)
                case 0x48: case 0x49: case 0x4A: case 0x4B: case 0x4C: case 0x4D: case 0x4E: case 0x4F:
                    e.IncDecReg(1, G[op - 0x48], size); return true;   // single-byte DEC

                case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
                    return EmitPush(G[op - 0x50], size);
                case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                    return EmitPop(op - 0x58, size);
                case 0x68: return EmitPushImm(ins.Imm, size);
                case 0x6A: return EmitPushImm((uint)(sbyte)ins.Imm, size);

                case 0xA8: e.TestRegImm(G[Reg.Eax], ins.Imm, 8); return true;
                case 0xA9: e.TestRegImm(G[Reg.Eax], ins.Imm, size); return true;

                case 0xB0: case 0xB1: case 0xB2: case 0xB3: case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                    // mov r8, imm8 — only the low four map to a clean host low byte.
                    if (op - 0xB0 >= 4) return false;
                    e.MovRegImm8(G[op - 0xB0], (byte)ins.Imm);
                    return true;
                case 0xB8: case 0xB9: case 0xBA: case 0xBB: case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                    if (size != 32) return false;
                    e.MovRegImm32(G[op - 0xB8], ins.Imm);
                    return true;
            }

            // Two-byte map.
            if (op >= 0x0F00 && op <= 0x0FFF)
            {
                var low = op & 0xFF;
                if (low >= 0x90 && low <= 0x9F)  // setcc
                {
                    var cc = low - 0x90;
                    if (IsMem(ins)) { EmitAddress(ins); e.SetccMem(cc, Mem, S1, 1, 0); }
                    else e.Setcc(cc, G[ins.Rm]);
                    return true;
                }
                if (low >= 0x40 && low <= 0x4F)  // cmovcc
                {
                    var cc = low - 0x40;
                    if (IsMem(ins)) { EmitAddress(ins); e.CmovccMem(cc, G[ins.RegField], Mem, S1, 1, 0, size); }
                    else e.Cmovcc(cc, G[ins.RegField], G[ins.Rm], size);
                    return true;
                }
                switch (low)
                {
                    case 0xAF: // imul r, r/m
                        if (IsMem(ins)) { EmitAddress(ins); e.ImulRegMem(G[ins.RegField], Mem, S1, 1, 0, size); }
                        else e.ImulRegReg(G[ins.RegField], G[ins.Rm], size);
                        return true;
                    case 0xB6: case 0xB7: // movzx
                        if (IsMem(ins)) { EmitAddress(ins); e.MovzxMem(G[ins.RegField], Mem, S1, 1, 0, low == 0xB6 ? 8 : 16); }
                        else e.Movzx(G[ins.RegField], G[ins.Rm], low == 0xB6 ? 8 : 16);
                        return true;
                    case 0xBE: case 0xBF: // movsx
                        if (IsMem(ins)) { EmitAddress(ins); e.MovsxMem(G[ins.RegField], Mem, S1, 1, 0, low == 0xBE ? 8 : 16); }
                        else e.Movsx(G[ins.RegField], G[ins.Rm], low == 0xBE ? 8 : 16);
                        return true;
                    case 0xC8: case 0xC9: case 0xCA: case 0xCB: case 0xCC: case 0xCD: case 0xCE: case 0xCF:
                        e.Bswap(G[low - 0xC8]);
                        return true;
                }
            }

            return false;
        }

        // ---------------------------------------------------------- ALU helpers

        private bool EmitAluToRm(int alu, in Instruction ins, int width, bool toReg)
        {
            if (IsMem(ins))
            {
                EmitAddress(ins);
                if (toReg) e.AluRegMem(alu, G[ins.RegField], Mem, S1, 1, 0, width);
                else e.AluMemReg(alu, G[ins.RegField], Mem, S1, 1, 0, width);
            }
            else
            {
                if (toReg) e.AluRegReg(alu, G[ins.RegField], G[ins.Rm], width);
                else e.AluRegReg(alu, G[ins.Rm], G[ins.RegField], width);
            }
            return true;
        }

        private bool EmitMovStore(in Instruction ins, int width)
        {
            if (IsMem(ins)) { EmitAddress(ins); e.StoreMem(G[ins.RegField], Mem, S1, 1, 0, width); }
            else e.MovRegRegSized(G[ins.Rm], G[ins.RegField], width);
            return true;
        }

        private bool EmitMovLoad(in Instruction ins, int width)
        {
            if (IsMem(ins)) { EmitAddress(ins); e.LoadMem(G[ins.RegField], Mem, S1, 1, 0, width); }
            else e.MovRegRegSized(G[ins.RegField], G[ins.Rm], width);
            return true;
        }

        private void EmitAddressNoSeg(in Instruction ins, int dst, int size)
        {
            if (ins.Base >= 0)
            {
                if (ins.Index >= 0) { if (size == 16) e.U8(0x66); e.Lea(dst, G[ins.Base], G[ins.Index], ins.Scale, (int)ins.Disp); }
                else { if (size == 16) e.U8(0x66); e.Lea(dst, G[ins.Base], -1, 1, (int)ins.Disp); }
            }
            else if (ins.Index >= 0)
            {
                if (size == 16) e.U8(0x66);
                e.LeaNoBase(dst, G[ins.Index], ins.Scale, (int)ins.Disp);
            }
            else
            {
                if (size == 16) { e.U8(0x66); e.MovRegImm16(dst, (ushort)ins.Disp); }
                else e.MovRegImm32(dst, ins.Disp);
            }
        }

        private bool EmitGroup3(in Instruction ins, int width)
        {
            switch (ins.RegField)
            {
                case 0: case 1: // TEST r/m, imm
                    if (IsMem(ins)) { EmitAddress(ins); e.TestMemImm(Mem, S1, 1, 0, ins.Imm, width); }
                    else e.TestRegImm(G[ins.Rm], ins.Imm, width);
                    return true;
                case 2: // NOT — flag-neutral, so a register form is a plain host NOT
                    if (IsMem(ins)) { EmitAddress(ins); e.Group3Mem(2, Mem, S1, 1, 0, width); }
                    else e.Group3(2, G[ins.Rm], width);
                    return true;
                case 3: // NEG
                    if (IsMem(ins)) { EmitAddress(ins); e.Group3Mem(3, Mem, S1, 1, 0, width); }
                    else e.Group3(3, G[ins.Rm], width);
                    return true;
                case 4: case 5: case 6: case 7: // MUL/IMUL/DIV/IDIV via EDX:EAX
                    if (IsMem(ins)) { EmitAddress(ins); e.Group3Mem(ins.RegField, Mem, S1, 1, 0, width); }
                    else e.Group3(ins.RegField, G[ins.Rm], width);
                    return true;
            }
            return false;
        }

        private bool EmitShift(in Instruction ins, int op)
        {
            var width = (op & 1) == 0 ? 8 : OpSize(ins);
            var sub = ins.RegField;
            if (op <= 0xC1) // by imm8
            {
                var count = (byte)ins.Imm;
                if (IsMem(ins)) { EmitAddress(ins); e.ShiftMemImm(sub, Mem, S1, 1, 0, count, width); }
                else e.ShiftImm(sub, G[ins.Rm], count, width);
            }
            else if (op <= 0xD1) // by 1
            {
                if (IsMem(ins)) { EmitAddress(ins); e.ShiftMemImm(sub, Mem, S1, 1, 0, 1, width); }
                else e.ShiftImm(sub, G[ins.Rm], 1, width);
            }
            else // by CL (guest ECX == host RCX, so CL is already the guest count)
            {
                if (IsMem(ins)) { EmitAddress(ins); e.ShiftMemCl(sub, Mem, S1, 1, 0, width); }
                else e.ShiftCl(sub, G[ins.Rm], width);
            }
            return true;
        }

        // --------------------------------------------------------------- stack

        private int StackStep(int size) => size == 16 ? 2 : 4;

        private bool EmitPush(int srcHost, int size)
        {
            var step = StackStep(size);
            e.Lea(S1, G[Reg.Esp], -1, 1, -step);           // s1 = esp - step
            e.StoreMem(srcHost, Mem, S1, 1, 0, size);       // store the register's current value
            e.Lea(G[Reg.Esp], G[Reg.Esp], -1, 1, -step);    // esp -= step
            return true;
        }

        private bool EmitPushImm(uint imm, int size)
        {
            var step = StackStep(size);
            e.Lea(S1, G[Reg.Esp], -1, 1, -step);
            e.MovMemImm(Mem, S1, 1, 0, imm, size);
            e.Lea(G[Reg.Esp], G[Reg.Esp], -1, 1, -step);
            return true;
        }

        private bool EmitPop(int reg, int size)
        {
            var step = StackStep(size);
            if (reg == Reg.Esp)
            {
                e.LoadMem(G[Reg.Esp], Mem, G[Reg.Esp], 1, 0, size);   // pop esp: the load wins
                return true;
            }
            e.LoadMem(G[reg], Mem, G[Reg.Esp], 1, 0, size);
            e.Lea(G[Reg.Esp], G[Reg.Esp], -1, 1, step);
            return true;
        }
    }
}
