using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    public sealed partial class BlockTranslator
    {
        /// <summary>
        /// MMX. The registers are the x87 significands in the context (see Ctx.Mm),
        /// so an MMX instruction loads its operands from there into xmm14/xmm15,
        /// runs the SSE2 form of the same operation on the low quadword, and stores
        /// the result back with the interpreter's side effects: the register's
        /// exponent word becomes 0xFFFF, TOP becomes 0 and every register is marked
        /// in use (EMMS marks them empty again). Nothing here touches the flags.
        /// </summary>
        private static bool IsMmxForm(in Instruction ins)
        {
            if (ins.Op < 0x0F00 || ins.Op > 0x0FFF || ins.Rep != 0 || ins.OpSize16) return false;
            switch (ins.Op & 0xFF)
            {
                case 0x77: case 0x6E: case 0x7E: case 0x6F: case 0x7F: case 0xE7:
                case 0x70: case 0x71: case 0x72: case 0x73: case 0xC4: case 0xC5: case 0xD7:
                case 0xFC: case 0xFD: case 0xFE: case 0xD4: case 0xF8: case 0xF9: case 0xFA: case 0xFB:
                case 0xEC: case 0xED: case 0xE8: case 0xE9: case 0xDC: case 0xDD: case 0xD8: case 0xD9:
                case 0xD5: case 0xE5: case 0xE4: case 0xF4: case 0xF5: case 0xF6: case 0xE0: case 0xE3:
                case 0xDA: case 0xDE: case 0xEA: case 0xEE: case 0xDB: case 0xDF: case 0xEB: case 0xEF:
                case 0x74: case 0x75: case 0x76: case 0x64: case 0x65: case 0x66:
                case 0xD1: case 0xD2: case 0xD3: case 0xE1: case 0xE2: case 0xF1: case 0xF2: case 0xF3:
                case 0x60: case 0x61: case 0x62: case 0x63: case 0x67: case 0x6B:
                case 0x68: case 0x69: case 0x6A:
                    return true;
                default: return false;
            }
        }

        private bool EmitMmx(in Instruction ins)
        {
            if (ins.Op < 0x0F00 || ins.Op > 0x0FFF) return false;
            if (ins.Rep != 0 || ins.OpSize16) return false;
            var op = ins.Op & 0xFF;
            var d = ins.RegField;
            var mem = IsMem(ins);

            switch (op)
            {
                case 0x77:   // EMMS
                    UsesX87 = true;
                    e.MovMemImm(Ctxr, -1, 1, Ctx.Empty, 0x01010101);
                    e.MovMemImm(Ctxr, -1, 1, Ctx.Empty + 4, 0x01010101);
                    return true;

                case 0x6E:   // MOVD mm, r/m32
                    if (mem) EmitAddress(ins);
                    UsesX87 = true; EnsureTemps();
                    if (mem) e.SseRegMem(0x66, 0x6E, 14, Mem, S1, 1, 0);
                    else e.SseRegReg(0x66, 0x6E, 14, G[ins.Rm]);
                    StoreMmFromXmm14(d);
                    return true;

                case 0x7E:   // MOVD r/m32, mm
                    if (mem) EmitAddress(ins);
                    UsesX87 = true; EnsureTemps();
                    e.LoadCtx(S2, Ctxr, Ctx.Mm + d * 8);
                    if (mem) e.StoreMem(S2, Mem, S1, 1, 0, 32);
                    else e.MovRegReg(G[ins.Rm], S2);
                    EnterMmxState();
                    return true;

                case 0x6F:   // MOVQ mm, mm/m64
                    if (mem) EmitAddress(ins);
                    UsesX87 = true; EnsureTemps();
                    LoadMmOperand(ins, 14);
                    StoreMmFromXmm14(d);
                    return true;

                case 0x7F:   // MOVQ mm/m64, mm
                case 0xE7:   // MOVNTQ m64, mm
                    if (op == 0xE7 && !mem) return false;
                    if (mem) EmitAddress(ins);
                    UsesX87 = true; EnsureTemps();
                    e.SseRegMem(0xF3, 0x7E, 14, Ctxr, -1, 1, Ctx.Mm + d * 8);
                    if (mem)
                    {
                        e.SseMemReg(0x66, 0xD6, Mem, S1, 1, 0, 14);
                        EnterMmxState();
                    }
                    else StoreMmFromXmm14(ins.Rm);
                    return true;

                case 0x70:   // PSHUFW
                    if (mem) EmitAddress(ins);
                    UsesX87 = true; EnsureTemps();
                    LoadMmOperand(ins, 15);
                    e.SseRegReg(0xF2, 0x70, 14, 15);
                    e.U8((byte)ins.Imm);
                    StoreMmFromXmm14(d);
                    return true;

                case 0x71:
                case 0x72:
                case 0x73:
                    {
                        if (mem) return false;
                        var sub = ins.RegField;
                        if (sub != 2 && sub != 6 && !(sub == 4 && op != 0x73)) return false;
                        UsesX87 = true; EnsureTemps();
                        var target = ins.Rm;
                        e.SseRegMem(0xF3, 0x7E, 14, Ctxr, -1, 1, Ctx.Mm + target * 8);
                        e.SseRegReg(0x66, op, sub, 14);
                        e.U8((byte)ins.Imm);
                        StoreMmFromXmm14(target);
                        return true;
                    }

                case 0xC4:   // PINSRW mm, r32/m16, imm8
                    if (mem) EmitAddress(ins);
                    UsesX87 = true; EnsureTemps();
                    e.SseRegMem(0xF3, 0x7E, 14, Ctxr, -1, 1, Ctx.Mm + d * 8);
                    if (mem) e.SseRegMem(0x66, 0xC4, 14, Mem, S1, 1, 0);
                    else e.SseRegReg(0x66, 0xC4, 14, G[ins.Rm]);
                    e.U8((byte)(ins.Imm & 3));
                    StoreMmFromXmm14(d);
                    return true;

                case 0xC5:   // PEXTRW r32, mm, imm8
                case 0xD7:   // PMOVMSKB r32, mm
                    if (mem) return false;
                    UsesX87 = true; EnsureTemps();
                    e.SseRegMem(0xF3, 0x7E, 14, Ctxr, -1, 1, Ctx.Mm + ins.Rm * 8);
                    e.SseRegReg(0x66, op, G[d], 14);
                    if (op == 0xC5) e.U8((byte)(ins.Imm & 3));
                    EnterMmxState();
                    return true;
            }

            // dest = op(dest, src) on the low quadword.
            int sseOp;
            var pack = false;
            var high = false;
            switch (op)
            {
                case 0xFC: case 0xFD: case 0xFE: case 0xD4: case 0xF8: case 0xF9: case 0xFA: case 0xFB:
                case 0xEC: case 0xED: case 0xE8: case 0xE9: case 0xDC: case 0xDD: case 0xD8: case 0xD9:
                case 0xD5: case 0xE5: case 0xE4: case 0xF4: case 0xF5: case 0xF6: case 0xE0: case 0xE3:
                case 0xDA: case 0xDE: case 0xEA: case 0xEE: case 0xDB: case 0xDF: case 0xEB: case 0xEF:
                case 0x74: case 0x75: case 0x76: case 0x64: case 0x65: case 0x66:
                case 0xD1: case 0xD2: case 0xD3: case 0xE1: case 0xE2: case 0xF1: case 0xF2: case 0xF3:
                case 0x60: case 0x61: case 0x62:
                    sseOp = op; break;
                case 0x63: case 0x67: case 0x6B:
                    sseOp = op; pack = true; break;
                case 0x68: case 0x69: case 0x6A:
                    sseOp = op - 8; high = true; break;
                default:
                    return false;
            }

            if (mem) EmitAddress(ins);
            UsesX87 = true; EnsureTemps();
            e.SseRegMem(0xF3, 0x7E, 14, Ctxr, -1, 1, Ctx.Mm + d * 8);
            LoadMmOperand(ins, 15);
            if (pack)
            {
                e.SseRegReg(0x66, 0x6C, 14, 15);       // punpcklqdq: both operands side by side
                e.SseRegReg(0x66, sseOp, 14, 14);
            }
            else
            {
                e.SseRegReg(0x66, sseOp, 14, 15);
                if (high)
                {
                    e.SseRegReg(0x66, 0x73, 3, 14);    // psrldq xmm14, 8: the high half is what MMX returns
                    e.U8(8);
                }
            }
            StoreMmFromXmm14(d);
            return true;
        }

        /// <summary>The source operand (mm register or m64) into the low quadword of an xmm temporary.</summary>
        private void LoadMmOperand(in Instruction ins, int xmm)
        {
            if (IsMem(ins)) e.SseRegMem(0xF3, 0x7E, xmm, Mem, S1, 1, 0);
            else e.SseRegMem(0xF3, 0x7E, xmm, Ctxr, -1, 1, Ctx.Mm + ins.Rm * 8);
        }

        /// <summary>FpuUnit.SetMm: write the register, tag it MMX data, and enter MMX state.</summary>
        private void StoreMmFromXmm14(int reg)
        {
            e.SseMemReg(0x66, 0xD6, Ctxr, -1, 1, Ctx.Mm + reg * 8, 14);
            e.MovMemImm(Ctxr, -1, 1, Ctx.Sexp + reg * 2, 0xFFFF, 16);
            EnterMmxState();
        }

        private void EnterMmxState()
        {
            e.MovMemImm(Ctxr, -1, 1, Ctx.Top, 0);
            e.MovMemImm(Ctxr, -1, 1, Ctx.Empty, 0);
            e.MovMemImm(Ctxr, -1, 1, Ctx.Empty + 4, 0);
        }
    }
}
