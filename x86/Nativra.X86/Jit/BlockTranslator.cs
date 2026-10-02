using System.Collections.Generic;
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
    /// A translated control-flow instruction ends the block at its target.
    /// Untranslated instructions still return to the reference interpreter.
    /// </summary>
    public sealed partial class BlockTranslator
    {
        private static readonly int[] G = Ctx.GuestToHost;
        private const int Mem = Ctx.MemBaseReg;
        private const int S1 = Ctx.Scratch1;
        private const int S2 = Ctx.Scratch2;
        private const int S3 = Ctx.Scratch3;
        private const int Ctxr = Ctx.CtxReg;

        public bool FullyTranslated { get; private set; }
        public int InstructionCount { get; private set; }
        public bool UsesSse { get; private set; }
        /// <summary>The block touches the x87/MMX register file, which then travels in the context.</summary>
        public bool UsesX87 { get; private set; }
        /// <summary>The block borrows xmm14/xmm15 as temporaries (callee-saved on Windows).</summary>
        private bool usesTemps;

        /// <summary>Host code offset where each guest instruction's translation starts, in order.</summary>
        public List<int> HostOffsets { get; } = new List<int>();
        /// <summary>The guest EIP of each entry in <see cref="HostOffsets"/>.</summary>
        public List<uint> GuestEips { get; } = new List<uint>();
        /// <summary>Offset of the fault exit: resuming there ends the block with <see cref="Ctx.ReasonFault"/>.</summary>
        public int FaultExitOffset { get; private set; }

        private X64Emit e;

        /// <summary>
        /// Emits a block starting at <paramref name="startEip"/>. If
        /// <paramref name="stopEip"/> is non-zero the block ends there (used by
        /// the differential tests to translate exactly one snippet); otherwise
        /// it runs to a natural boundary or the instruction cap.
        /// </summary>
        public byte[] Translate(ICodeReader code, uint startEip, uint stopEip, int maxInstructions = 256)
        {
            // Whether a block needs the XMM registers (or the borrowed temporaries)
            // is only known after translating it, but the registers must be set up
            // before any early exit can reach the epilogue that stores them back. So
            // a block that turns out to need them is translated once more with the
            // setup in its prologue.
            preload = false;
            var bytes = TranslatePass(code, startEip, stopEip, maxInstructions);
            if (!UsesSse && !usesTemps) return bytes;
            preload = true;
            return TranslatePass(code, startEip, stopEip, maxInstructions);
        }

        private bool preload;
        private int labelCounter;

        private byte[] TranslatePass(ICodeReader code, uint startEip, uint stopEip, int maxInstructions)
        {
            e = new X64Emit();
            FullyTranslated = true;
            InstructionCount = 0;
            var wantSse = preload && UsesSse;
            var wantTemps = preload && usesTemps;
            UsesSse = wantSse;
            usesTemps = wantTemps;
            UsesX87 = false;
            labelCounter = 0;
            HostOffsets.Clear();
            GuestEips.Clear();
            EmitPrologue();
            if (wantSse) LoadGuestXmm();
            if (wantTemps) SaveTemps();

            var eip = startEip;
            var terminated = false;
            for (var n = 0; n < maxInstructions; n++)
            {
                if (stopEip != 0 && eip == stopEip) { EmitExit(eip, Ctx.ReasonNext); terminated = true; break; }

                Instruction ins;
                try { ins = Decoder.Decode(code, eip); }
                catch { EmitExit(eip, Ctx.ReasonFallback); FullyTranslated = false; terminated = true; break; }

                HostOffsets.Add(e.Here);
                GuestEips.Add(eip);
                if (ins.Op >= 0xA0 && ins.Op <= 0xA3 && ins.Segment == Seg.None &&
                    code is GuestMemory guestMemory && !guestMemory.IsMapped(ins.Disp))
                {
                    EmitExit(eip, Ctx.ReasonFallback);
                    FullyTranslated = false;
                    terminated = true;
                    break;
                }
                if (!ins.Valid || ins.Lock || (ins.Rep != 0 && ins.Op < 0x0F00) || !TryEmit(ins))
                {
                    EmitExit(eip, Ctx.ReasonFallback);
                    FullyTranslated = false;
                    terminated = true;
                    break;
                }
                InstructionCount++;
                if (IsBranch(ins)) { terminated = true; break; }
                eip = ins.Next;
            }
            if (!terminated) EmitExit(eip, Ctx.ReasonNext);

            // The fault exit: a guest memory access that faulted resumes here
            // (see JitFaults) with the guest registers still in their host
            // registers, and leaves through the normal epilogue.
            FaultExitOffset = e.Here;
            e.MovMemImm(Ctxr, -1, 1, Ctx.ExitReason, (uint)Ctx.ReasonFault);

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
            if (UsesSse)
            {
                for (var i = 0; i < 8; i++) e.SseMemReg(0, 0x11, Ctxr, -1, 1, Ctx.Xmm + i * 16, i);
                e.SseRegMem(0, 0x10, 6, Ctxr, -1, 1, 96);
                e.SseRegMem(0, 0x10, 7, Ctxr, -1, 1, 112);
            }
            if (usesTemps)
            {
                e.SseRegMem(0, 0x10, 14, Ctxr, -1, 1, Ctx.SaveXmm14);
                e.SseRegMem(0, 0x10, 15, Ctxr, -1, 1, Ctx.SaveXmm15);
            }
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

        private void EmitExitReg(int target)
        {
            e.StoreCtx(target, Ctxr, Ctx.Eip);
            e.MovMemImm(Ctxr, -1, 1, Ctx.ExitReason, Ctx.ReasonNext);
            e.Jmp("exit");
        }

        private static bool IsBranch(in Instruction ins) =>
            (ins.Op >= 0x70 && ins.Op <= 0x7F) ||
            (ins.Op >= 0x0F80 && ins.Op <= 0x0F8F) ||
            ins.Op == 0xE8 || ins.Op == 0xE9 || ins.Op == 0xEB ||
            ins.Op == 0xC2 || ins.Op == 0xC3 ||
            (ins.Op == 0xFF && (ins.RegField == 2 || ins.RegField == 4));

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

            // Byte registers 4-7 are AH, CH, DH and BH. Their host homes (rdi,
            // r12-r14) have no high byte, and AH-style encodings cannot sit in
            // an instruction that needs a REX prefix, which every memory
            // operand here does. Leave those forms to the interpreter.
            if (UsesHighByteRegister(ins)) return EmitHighByte(ins);

            if (op >= 0x70 && op <= 0x7F)
                return EmitConditional(ins, op - 0x70, (uint)(sbyte)ins.Imm);
            if (op >= 0x0F80 && op <= 0x0F8F)
                return EmitConditional(ins, op - 0x0F80, ins.Imm);

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

            if (op >= 0xD8 && op <= 0xDF) return EmitX87(ins);

            switch (op)
            {
                case 0x69:
                case 0x6B:
                    // 3-operand imul: dst = src * imm. The source goes into dst
                    // first (a load for a memory source, not dst *= mem, which
                    // multiplied dst's old value in), then dst *= imm.
                    if (IsMem(ins)) { EmitAddress(ins); e.LoadMem(G[ins.RegField], Mem, S1, 1, 0, size); }
                    else e.MovRegReg(G[ins.RegField], G[ins.Rm], false);
                    e.ImulRegRegImm(G[ins.RegField], G[ins.RegField],
                        op == 0x6B ? (uint)(sbyte)ins.Imm : ins.Imm, size, op == 0x6B);
                    return true;

                case 0x80:
                case 0x81:
                case 0x82:
                case 0x83:
                    {
                        var width = op == 0x80 || op == 0x82 ? 8 : size;
                        var imm = op == 0x83 ? (uint)(sbyte)ins.Imm : ins.Imm;
                        if (IsMem(ins)) { EmitAddress(ins); e.AluMemImm(ins.RegField, Mem, S1, 1, 0, imm, width); }
                        else e.AluRegImm(ins.RegField, G[ins.Rm], imm, width);
                        return true;
                    }

                case 0x84:
                case 0x85:
                    {
                        var width = op == 0x84 ? 8 : size;
                        if (IsMem(ins)) { EmitAddress(ins); e.TestMemReg(G[ins.RegField], Mem, S1, 1, 0, width); return true; }
                        e.TestRegReg(G[ins.Rm], G[ins.RegField], width);
                        return true;
                    }

                case 0x86:
                case 0x87:   // XCHG r/m, r
                    {
                        var width = op == 0x86 ? 8 : size;
                        if (IsMem(ins)) return false; // memory xchg carries an implicit lock; leave it to the interpreter
                                                      // Partial-register swaps are rare; decided before anything is
                                                      // emitted, or the interpreter would swap them a second time.
                        if (width != 32) return false;
                        if (ins.Rm != ins.RegField)
                        {
                            e.MovRegReg(S1, G[ins.Rm], true);
                            e.MovRegReg(G[ins.Rm], G[ins.RegField], true);
                            e.MovRegReg(G[ins.RegField], S1, true);
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
                case 0x8F: // POP r/m
                    if (ins.RegField != 0) return false;
                    if (!IsMem(ins)) return EmitPop(ins.Rm, size);
                    e.LoadMem(S3, Mem, G[Reg.Esp], 1, 0, size);
                    e.Lea(G[Reg.Esp], G[Reg.Esp], -1, 1, StackStep(size));
                    EmitAddress(ins);
                    e.StoreMem(S3, Mem, S1, 1, 0, size);
                    return true;

                case 0x90: return true; // NOP
                case 0x98:
                    if (size == 16)
                    {
                        e.Movsx(S2, G[Reg.Eax], 8);
                        e.MovRegRegSized(G[Reg.Eax], S2, 16);
                    }
                    else e.Movsx(G[Reg.Eax], G[Reg.Eax], 16);
                    return true;
                case 0x99:
                    if (size == 16) e.U8(0x66);
                    e.Cdq();
                    return true;
                case 0xA0:
                case 0xA1:
                case 0xA2:
                case 0xA3:
                    EmitAddress(ins);
                    if (op == 0xA0 || op == 0xA1)
                        e.LoadMem(G[Reg.Eax], Mem, S1, 1, 0, op == 0xA0 ? 8 : size);
                    else e.StoreMem(G[Reg.Eax], Mem, S1, 1, 0, op == 0xA2 ? 8 : size);
                    return true;
                case 0xC2:
                case 0xC3:
                    e.LoadMem(S2, Mem, G[Reg.Esp], 1, 0);
                    e.Lea(G[Reg.Esp], G[Reg.Esp], -1, 1, op == 0xC2 ? 4 + (int)ins.Imm : 4);
                    EmitExitReg(S2);
                    return true;
                case 0xE8:
                    EmitPushImm(ins.Next, 32);
                    EmitExit(unchecked(ins.Next + ins.Imm), Ctx.ReasonNext);
                    return true;
                case 0xE9:
                    EmitExit(unchecked(ins.Next + ins.Imm), Ctx.ReasonNext);
                    return true;
                case 0xEB:
                    EmitExit(unchecked(ins.Next + (uint)(sbyte)ins.Imm), Ctx.ReasonNext);
                    return true;
                case 0xC6: // MOV r/m8, imm8
                    if (IsMem(ins)) { EmitAddress(ins); e.MovMemImm(Mem, S1, 1, 0, ins.Imm, 8); }
                    else e.MovRegImm8(G[ins.Rm], (byte)ins.Imm);
                    return true;
                case 0xC7: // MOV r/m, imm
                    if (IsMem(ins)) { EmitAddress(ins); e.MovMemImm(Mem, S1, 1, 0, ins.Imm, size); }
                    else if (size == 32) e.MovRegImm32(G[ins.Rm], ins.Imm);
                    else return false;
                    return true;
                case 0xC9: // LEAVE
                    e.MovRegReg(G[Reg.Esp], G[Reg.Ebp]);
                    e.LoadMem(G[Reg.Ebp], Mem, G[Reg.Esp], 1, 0, size);
                    e.Lea(G[Reg.Esp], G[Reg.Esp], -1, 1, StackStep(size));
                    return true;

                case 0xF6:
                case 0xF7:
                    return EmitGroup3(ins, op == 0xF6 ? 8 : size);
                case 0xFE:
                    if (ins.RegField > 1) return false;
                    if (IsMem(ins)) { EmitAddress(ins); e.IncDecMem(ins.RegField, Mem, S1, 1, 0, 8); }
                    else e.IncDecReg(ins.RegField, G[ins.Rm], 8);
                    return true;
                case 0xFF:
                    if (ins.RegField == 6)
                    {
                        if (IsMem(ins))
                        {
                            EmitAddress(ins);
                            e.LoadMem(S2, Mem, S1, 1, 0, size);
                            return EmitPush(S2, size);
                        }
                        return EmitPush(G[ins.Rm], size);
                    }
                    if (ins.RegField == 2 || ins.RegField == 4)
                    {
                        if (IsMem(ins)) { EmitAddress(ins); e.LoadMem(S2, Mem, S1, 1, 0); }
                        else e.MovRegReg(S2, G[ins.Rm]);
                        if (ins.RegField == 2) EmitPushImm(ins.Next, 32);
                        EmitExitReg(S2);
                        return true;
                    }
                    if (ins.RegField > 1) return false;
                    if (IsMem(ins)) { EmitAddress(ins); e.IncDecMem(ins.RegField, Mem, S1, 1, 0, size); }
                    else e.IncDecReg(ins.RegField, G[ins.Rm], size);
                    return true;

                case 0xC0:
                case 0xC1:
                case 0xD0:
                case 0xD1:
                case 0xD2:
                case 0xD3:
                    return EmitShift(ins, op);

                case 0x40:
                case 0x41:
                case 0x42:
                case 0x43:
                case 0x44:
                case 0x45:
                case 0x46:
                case 0x47:
                    e.IncDecReg(0, G[op - 0x40], size); return true;   // single-byte INC (0x40-0x47 are REX in x64)
                case 0x48:
                case 0x49:
                case 0x4A:
                case 0x4B:
                case 0x4C:
                case 0x4D:
                case 0x4E:
                case 0x4F:
                    e.IncDecReg(1, G[op - 0x48], size); return true;   // single-byte DEC

                case 0x50:
                case 0x51:
                case 0x52:
                case 0x53:
                case 0x54:
                case 0x55:
                case 0x56:
                case 0x57:
                    return EmitPush(G[op - 0x50], size);
                case 0x58:
                case 0x59:
                case 0x5A:
                case 0x5B:
                case 0x5C:
                case 0x5D:
                case 0x5E:
                case 0x5F:
                    return EmitPop(op - 0x58, size);
                case 0x68: return EmitPushImm(ins.Imm, size);
                case 0x6A: return EmitPushImm((uint)(sbyte)ins.Imm, size);

                case 0xA8: e.TestRegImm(G[Reg.Eax], ins.Imm, 8); return true;
                case 0xA9: e.TestRegImm(G[Reg.Eax], ins.Imm, size); return true;

                case 0xB0:
                case 0xB1:
                case 0xB2:
                case 0xB3:
                case 0xB4:
                case 0xB5:
                case 0xB6:
                case 0xB7:
                    // mov r8, imm8 — only the low four map to a clean host low byte.
                    if (op - 0xB0 >= 4) return false;
                    e.MovRegImm8(G[op - 0xB0], (byte)ins.Imm);
                    return true;
                case 0xB8:
                case 0xB9:
                case 0xBA:
                case 0xBB:
                case 0xBC:
                case 0xBD:
                case 0xBE:
                case 0xBF:
                    if (size != 32) return false;
                    e.MovRegImm32(G[op - 0xB8], ins.Imm);
                    return true;
            }

            // Two-byte map.
            if (op >= 0x0F00 && op <= 0x0FFF)
            {
                if (EmitSse(ins)) return true;
                if (ins.Rep != 0) return false;
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
                    case 0xB6:
                    case 0xB7: // movzx
                        if (size == 16 && low == 0xB7) return false;
                        if (IsMem(ins)) { EmitAddress(ins); e.MovzxMem(G[ins.RegField], Mem, S1, 1, 0, low == 0xB6 ? 8 : 16, size); }
                        else e.Movzx(G[ins.RegField], G[ins.Rm], low == 0xB6 ? 8 : 16, size);
                        return true;
                    case 0xBE:
                    case 0xBF: // movsx
                        if (size == 16 && low == 0xBF) return false;
                        if (IsMem(ins)) { EmitAddress(ins); e.MovsxMem(G[ins.RegField], Mem, S1, 1, 0, low == 0xBE ? 8 : 16, size); }
                        else e.Movsx(G[ins.RegField], G[ins.Rm], low == 0xBE ? 8 : 16, size);
                        return true;
                    case 0xC8:
                    case 0xC9:
                    case 0xCA:
                    case 0xCB:
                    case 0xCC:
                    case 0xCD:
                    case 0xCE:
                    case 0xCF:
                        e.Bswap(G[low - 0xC8]);
                        return true;
                }
            }

            return false;
        }

        private void EnsureXmm()
        {
            if (UsesSse) return;
            UsesSse = true;   // the first pass only records the need; see Translate
        }

        private void LoadGuestXmm()
        {
            // XMM6/7 belong to the caller on Windows; guest XMM state lives
            // in the context so each block can enter and leave independently.
            e.SseMemReg(0, 0x11, Ctxr, -1, 1, 96, 6);
            e.SseMemReg(0, 0x11, Ctxr, -1, 1, 112, 7);
            for (var i = 0; i < 8; i++) e.SseRegMem(0, 0x10, i, Ctxr, -1, 1, Ctx.Xmm + i * 16);
        }

        /// <summary>xmm14 and xmm15 are scratch for the x87 and MMX paths; they are callee-saved on Windows.</summary>
        private void EnsureTemps()
        {
            usesTemps = true;
        }

        private void SaveTemps()
        {
            e.SseMemReg(0, 0x11, Ctxr, -1, 1, Ctx.SaveXmm14, 14);
            e.SseMemReg(0, 0x11, Ctxr, -1, 1, Ctx.SaveXmm15, 15);
        }

        private bool EmitSse(in Instruction ins)
        {
            if (EmitSseExtra(ins)) return true;
            var op = ins.Op & 0xFF;
            var prefix = ins.Rep != 0 ? ins.Rep : ins.OpSize16 ? 0x66 : 0;
            var ordinary = prefix == 0 || prefix == 0x66;
            var scalar = prefix == 0xF2 || prefix == 0xF3;
            var load = op == 0x10 || op == 0x12 || op == 0x28 || op == 0x6F;
            var store = op == 0x11 || op == 0x13 || op == 0x29 || op == 0x7F;
            var arithmetic = op == 0x58 || op == 0x59 || op == 0x5C || op == 0x5E;
            var packed = op == 0x14 || op == 0x54 || op == 0x55 || op == 0x56 ||
                         op == 0x57 || op == 0xFE || op == 0xFB;
            if (ins.Op < 0x0F00 || ins.Op > 0x0FFF ||
                !(load || store || arithmetic || packed || op == 0x70 || op == 0x73 ||
                  op == 0x6E || op == 0xC5 || op == 0xC4 || op == 0x2F)) return false;
            if ((op == 0x12 || op == 0x13) && (!ordinary || (op == 0x13 && !IsMem(ins)) ||
                (op == 0x12 && !IsMem(ins)))) return false;
            if ((op == 0x28 || op == 0x29 || op == 0x14 || op == 0x54 || op == 0x55 ||
                 op == 0x56 || op == 0x57 || op == 0x2F) && !ordinary) return false;
            if ((op == 0xFE || op == 0xFB) && prefix != 0x66) return false;
            if ((op == 0x6F || op == 0x7F) && prefix != 0x66 && prefix != 0xF3) return false;
            if ((op == 0x70 || op == 0x73 || op == 0x6E || op == 0xC5 || op == 0xC4) && prefix != 0x66) return false;
            if (op == 0x73 && (ins.Mod != 3 || (ins.RegField != 3 && ins.RegField != 7))) return false;
            if (op == 0xC5 && ins.Mod != 3) return false;
            if (op == 0xC4 && !IsMem(ins) && ins.Rm >= 4) return false;
            if (op == 0x2F && scalar) return false;
            if (IsMem(ins)) EmitAddress(ins);
            EnsureXmm();
            if (op == 0x6E || op == 0xC4)
            {
                if (IsMem(ins)) e.SseRegMem(prefix, op, ins.RegField, Mem, S1, 1, 0);
                else e.SseRegReg(prefix, op, ins.RegField, G[ins.Rm]);
                if (op == 0xC4) e.U8((byte)ins.Imm);
            }
            else if (op == 0xC5)
            {
                e.SseRegReg(prefix, op, G[ins.RegField], ins.Rm);
                e.U8((byte)ins.Imm);
            }
            else if (op == 0x73)
            {
                e.SseRegReg(prefix, op, ins.RegField, ins.Rm);
                e.U8((byte)ins.Imm);
            }
            else if (store)
            {
                var nativeOp = op == 0x29 ? 0x11 : op;
                var nativePrefix = op == 0x7F ? 0xF3 : prefix;
                if (IsMem(ins)) e.SseMemReg(nativePrefix, nativeOp, Mem, S1, 1, 0, ins.RegField);
                else e.SseRegReg(nativePrefix, nativeOp, ins.RegField, ins.Rm);
            }
            else
            {
                var nativeOp = op == 0x28 ? 0x10 : op;
                var nativePrefix = op == 0x6F ? 0xF3 : prefix;
                if (IsMem(ins)) e.SseRegMem(nativePrefix, nativeOp, ins.RegField, Mem, S1, 1, 0);
                else e.SseRegReg(nativePrefix, nativeOp, ins.RegField, ins.Rm);
                if (op == 0x70) e.U8((byte)ins.Imm);
            }
            return true;
        }

        private bool EmitConditional(in Instruction ins, int cc, uint displacement)
        {
            var taken = "taken";
            e.Jcc(cc, taken);
            EmitExit(ins.Next, Ctx.ReasonNext);
            e.Label(taken);
            EmitExit(unchecked(ins.Next + displacement), Ctx.ReasonNext);
            return true;
        }

        /// <summary>An 8-bit operand that names AH, CH, DH or BH (register numbers 4-7).</summary>
        private bool UsesHighByteRegister(in Instruction ins)
        {
            var op = ins.Op;
            bool rmIsByte, regIsByte;
            if (op < 0x40 && (op & 7) < 4)
            {
                rmIsByte = regIsByte = (op & 1) == 0;               // ALU r/m8,r8 and r8,r/m8
            }
            else
            {
                switch (op)
                {
                    case 0x84:
                    case 0x86:
                    case 0x88:
                    case 0x8A:     // test/xchg/mov with r8
                        rmIsByte = regIsByte = true; break;
                    case 0x80:
                    case 0x82:
                    case 0xC6:
                    case 0xF6:
                    case 0xFE:
                    case 0xC0:
                    case 0xD0:
                    case 0xD2:                 // r/m8 with an opcode extension
                        rmIsByte = true; regIsByte = false; break;
                    case 0x0FB6:
                    case 0x0FBE:                        // movzx/movsx from r/m8
                        rmIsByte = true; regIsByte = false; break;
                    default:
                        rmIsByte = op >= 0x0F90 && op <= 0x0F9F;     // setcc r/m8
                        regIsByte = false;
                        break;
                }
            }
            return (rmIsByte && ins.Mod == 3 && ins.Rm >= 4) || (regIsByte && ins.RegField >= 4);
        }

        private int ReadByteRegister(int guest, int scratch)
        {
            if (guest < 4) return G[guest];
            // AH/CH/DH/BH are bits 8-15 of the first four guest registers.
            // Extracting them must not change the flags seen by the guest op.
            e.Pushfq();
            e.MovRegReg(scratch, G[guest - 4]);
            e.ShiftImm(5, scratch, 8);
            e.Popfq();
            return scratch;
        }

        private void WriteByteRegister(int guest, int value)
        {
            if (guest < 4)
            {
                e.MovRegRegSized(G[guest], value, 8);
                return;
            }
            if (value != S2 && value != S3)
            {
                e.MovRegReg(S3, value);
                value = S3;
            }
            e.Pushfq();
            e.AluRegImm(4, G[guest - 4], 0xFFFF00FF);
            e.AluRegImm(4, value, 0xFF);
            e.ShiftImm(4, value, 8);
            e.AluRegReg(1, G[guest - 4], value);
            e.Popfq();
        }

        private bool EmitHighByte(in Instruction ins)
        {
            var op = ins.Op;
            if (op < 0x40 && (op & 7) < 4)
            {
                var alu = op >> 3;
                var toReg = (op & 2) != 0;
                if (IsMem(ins))
                {
                    EmitAddress(ins);
                    var reg = ReadByteRegister(ins.RegField, S2);
                    if (toReg)
                    {
                        e.AluRegMem(alu, reg, Mem, S1, 1, 0, 8);
                        if (alu != 7 && ins.RegField >= 4) WriteByteRegister(ins.RegField, reg);
                    }
                    else e.AluMemReg(alu, reg, Mem, S1, 1, 0, 8);
                }
                else
                {
                    var dstGuest = toReg ? ins.RegField : ins.Rm;
                    var srcGuest = toReg ? ins.Rm : ins.RegField;
                    var dst = ReadByteRegister(dstGuest, S2);
                    var src = ReadByteRegister(srcGuest, S3);
                    e.AluRegReg(alu, dst, src, 8);
                    if (alu != 7 && dstGuest >= 4) WriteByteRegister(dstGuest, dst);
                }
                return true;
            }
            if (op == 0x80 || op == 0x82)
            {
                var dst = ReadByteRegister(ins.Rm, S2);
                e.AluRegImm(ins.RegField, dst, ins.Imm, 8);
                if (ins.RegField != 7) WriteByteRegister(ins.Rm, dst);
                return true;
            }
            if (op == 0x84)
            {
                if (IsMem(ins))
                {
                    EmitAddress(ins);
                    e.TestMemReg(ReadByteRegister(ins.RegField, S2), Mem, S1, 1, 0, 8);
                }
                else e.TestRegReg(ReadByteRegister(ins.Rm, S2), ReadByteRegister(ins.RegField, S3), 8);
                return true;
            }
            if (op == 0x88 || op == 0x8A)
            {
                if (IsMem(ins))
                {
                    EmitAddress(ins);
                    if (op == 0x88) e.StoreMem(ReadByteRegister(ins.RegField, S2), Mem, S1, 1, 0, 8);
                    else
                    {
                        var dst = ins.RegField >= 4 ? S2 : G[ins.RegField];
                        e.LoadMem(dst, Mem, S1, 1, 0, 8);
                        if (ins.RegField >= 4) WriteByteRegister(ins.RegField, dst);
                    }
                }
                else
                {
                    var dst = op == 0x88 ? ins.Rm : ins.RegField;
                    var src = op == 0x88 ? ins.RegField : ins.Rm;
                    WriteByteRegister(dst, ReadByteRegister(src, S2));
                }
                return true;
            }
            if (op == 0xC6)
            {
                e.MovRegImm8(S2, (byte)ins.Imm);
                WriteByteRegister(ins.Rm, S2);
                return true;
            }
            if (op == 0xFE && ins.RegField <= 1)
            {
                var dst = ReadByteRegister(ins.Rm, S2);
                e.IncDecReg(ins.RegField, dst, 8);
                WriteByteRegister(ins.Rm, dst);
                return true;
            }
            if (op == 0x0FB6 || op == 0x0FBE)
            {
                var src = ReadByteRegister(ins.Rm, S2);
                if (op == 0x0FB6) e.Movzx(G[ins.RegField], src, 8, OpSize(ins));
                else e.Movsx(G[ins.RegField], src, 8, OpSize(ins));
                return true;
            }
            if (op >= 0x0F90 && op <= 0x0F9F)
            {
                e.Setcc(op - 0x0F90, S2);
                WriteByteRegister(ins.Rm, S2);
                return true;
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
                case 0:
                case 1: // TEST r/m, imm
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
                case 4:
                case 5:
                    // MUL/IMUL via EDX:EAX
                    if (IsMem(ins)) { EmitAddress(ins); e.Group3Mem(ins.RegField, Mem, S1, 1, 0, width); }
                    else e.Group3(ins.RegField, G[ins.Rm], width);
                    return true;
                case 6:
                case 7: // Check faulting divides before executing them on the host.
                    if (width != 32) return false;
                    if (IsMem(ins)) { EmitAddress(ins); e.LoadMem(S2, Mem, S1, 1, 0); }
                    else e.MovRegReg(S2, G[ins.Rm]);
                    var bad = "divide_bad_" + e.Here;
                    var ready = "divide_ready_" + e.Here;
                    var done = "divide_done_" + e.Here;
                    e.Pushfq();
                    e.TestRegReg(S2, S2);
                    e.Jcc(4, bad);
                    if (ins.RegField == 6)
                    {
                        e.AluRegReg(7, G[Reg.Edx], S2);
                        e.Jcc(3, bad);
                    }
                    else
                    {
                        e.MovRegReg(S3, G[Reg.Eax]);
                        e.ShiftImm(7, S3, 31);
                        e.AluRegReg(7, G[Reg.Edx], S3);
                        e.Jcc(5, bad);
                        e.AluRegImm(7, S2, 0xFFFFFFFF);
                        e.Jcc(5, ready);
                        e.AluRegImm(7, G[Reg.Eax], 0x80000000);
                        e.Jcc(4, bad);
                    }
                    e.Label(ready);
                    e.Popfq();
                    e.Group3(ins.RegField, S2, 32);
                    e.Jmp(done);
                    e.Label(bad);
                    e.Popfq();
                    EmitExit(ins.Address, Ctx.ReasonFallback);
                    e.Label(done);
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
            else if (op <= 0xD1) // by 1 — emit the D0/D1 form, not the C0/C1 imm form
            {
                if (IsMem(ins)) { EmitAddress(ins); e.ShiftMemOne(sub, Mem, S1, 1, 0, width); }
                else e.ShiftOne(sub, G[ins.Rm], width);
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
