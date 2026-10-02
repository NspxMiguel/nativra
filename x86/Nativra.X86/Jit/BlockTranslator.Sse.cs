using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    public sealed partial class BlockTranslator
    {
        /// <summary>
        /// SSE/SSE2 forms whose host instruction is the guest's own: conversions,
        /// the packed-integer map, shifts by immediate, min/max/sqrt/compare and
        /// the remaining moves. The interpreter spells these out lane by lane;
        /// every one here is an operation whose result the host computes
        /// identically (the oracle and differential tests hold both to that).
        /// </summary>
        private bool EmitSseExtra(in Instruction ins)
        {
            if (ins.Op < 0x0F00 || ins.Op > 0x0FFF) return false;
            var op = ins.Op & 0xFF;
            var prefix = ins.Rep != 0 ? ins.Rep : ins.OpSize16 ? 0x66 : 0;
            var mem = IsMem(ins);

            // What the instruction does with its operands.
            //   Load:    xmm <- xmm/m          Store:   xmm/m <- xmm (mem only unless noted)
            //   ToGpr:   r32 <- xmm            FromGpr: xmm <- r/m32
            var kind = 0;
            const int Load = 1, Store = 2, ToGpr = 3, FromGpr = 4, StoreRm = 5;
            var immediate = false;
            var needsRound = false;

            switch (op)
            {
                // ---- conversions
                case 0x5A:
                    kind = Load; break;
                case 0x5B:
                    if (prefix == 0xF2) return false;
                    kind = Load;
                    needsRound = prefix == 0x66;
                    break;
                case 0xE6:
                    if (prefix == 0) return false;
                    kind = Load;
                    needsRound = prefix == 0xF2;
                    break;
                case 0x2A:
                    if (prefix != 0xF3 && prefix != 0xF2) return false;   // the MMX source forms stay interpreted
                    kind = FromGpr;
                    break;
                case 0x2C:
                case 0x2D:
                    if (prefix != 0xF3 && prefix != 0xF2) return false;
                    kind = ToGpr;
                    needsRound = op == 0x2D;
                    break;

                // ---- packed integer, 66-prefixed (the MMX forms are in the MMX translator)
                case 0x60: case 0x61: case 0x62: case 0x63: case 0x64: case 0x65: case 0x66: case 0x67:
                case 0x68: case 0x69: case 0x6A: case 0x6B: case 0x6C: case 0x6D:
                case 0x74: case 0x75: case 0x76:
                case 0xD1: case 0xD2: case 0xD3: case 0xD4: case 0xD5:
                case 0xD8: case 0xD9: case 0xDA: case 0xDB: case 0xDC: case 0xDD: case 0xDE: case 0xDF:
                case 0xE0: case 0xE1: case 0xE2: case 0xE3: case 0xE4: case 0xE5:
                case 0xE8: case 0xE9: case 0xEA: case 0xEB: case 0xEC: case 0xED: case 0xEE: case 0xEF:
                case 0xF1: case 0xF2: case 0xF3: case 0xF4: case 0xF5: case 0xF6:
                case 0xF8: case 0xF9: case 0xFA: case 0xFB: case 0xFC: case 0xFD: case 0xFE:
                    if (prefix != 0x66) return false;
                    kind = Load;
                    break;
                case 0x70:   // pshufd / pshufhw / pshuflw
                    if (prefix != 0x66 && prefix != 0xF3 && prefix != 0xF2) return false;
                    kind = Load; immediate = true;
                    break;
                case 0x71:
                case 0x72:
                case 0x73:
                    if (prefix != 0x66 || mem) return false;
                    if (op == 0x73 ? (ins.RegField != 2 && ins.RegField != 3 && ins.RegField != 6 && ins.RegField != 7)
                                   : (ins.RegField != 2 && ins.RegField != 4 && ins.RegField != 6)) return false;
                    EnsureXmm();
                    e.SseRegReg(0x66, op, ins.RegField, ins.Rm);
                    e.U8((byte)ins.Imm);
                    return true;
                case 0xD7:
                    if (prefix != 0x66 || mem) return false;
                    kind = ToGpr;
                    break;
                case 0xE7:   // movntdq
                    if (prefix != 0x66 || !mem) return false;
                    kind = Store;
                    break;

                // ---- moves
                case 0x7E:
                    if (prefix == 0xF3) { kind = Load; break; }               // movq xmm, xmm/m64
                    if (prefix != 0x66) return false;                         // movd r/m32, xmm
                    kind = StoreRm;
                    break;
                case 0xD6:
                    if (prefix != 0x66) return false;                         // movq xmm/m64, xmm
                    kind = StoreRm;
                    break;
                case 0x12:
                case 0x16:
                    if (prefix != 0 && prefix != 0x66) return false;
                    if (!mem && prefix != 0) return false;                    // movhlps / movlhps only without a prefix
                    kind = Load;
                    break;
                case 0x13:
                case 0x17:
                case 0x2B:
                    if ((prefix != 0 && prefix != 0x66) || !mem) return false;
                    kind = Store;
                    break;
                case 0x50:
                    if ((prefix != 0 && prefix != 0x66) || mem) return false;
                    kind = ToGpr;
                    break;

                // ---- floating point
                case 0x51:   // sqrt
                case 0x5D:   // min
                case 0x5F:   // max
                    kind = Load;
                    break;
                case 0x15:   // unpckh
                case 0x2E:   // ucomis
                    if (prefix != 0 && prefix != 0x66) return false;
                    kind = Load;
                    break;
                case 0xC2:
                    if (ins.Imm > 7) return false;
                    kind = Load; immediate = true;
                    break;
                case 0xC6:
                    if (prefix != 0 && prefix != 0x66) return false;
                    kind = Load; immediate = true;
                    break;
                default:
                    return false;
            }

            if (mem) EmitAddress(ins);
            EnsureXmm();
            var done = "sse_done_" + labelCounter;
            var bad = "sse_bad_" + labelCounter++;
            if (needsRound)
            {
                // The interpreter rounds by MXCSR.RC and the host runs round-to-nearest, so
                // only the default mode runs natively; the others go to the interpreter.
                e.Pushfq();
                e.TestMemImm(Ctxr, -1, 1, Ctx.Mxcsr, 0x6000);
                e.Jcc(5, bad);
                e.Popfq();
            }

            switch (kind)
            {
                case Load:
                    if (mem) e.SseRegMem(prefix, op, ins.RegField, Mem, S1, 1, 0);
                    else e.SseRegReg(prefix, op, ins.RegField, ins.Rm);
                    break;
                case Store:
                    e.SseMemReg(prefix, op, Mem, S1, 1, 0, ins.RegField);
                    break;
                case StoreRm:
                    if (mem) e.SseMemReg(prefix, op, Mem, S1, 1, 0, ins.RegField);
                    else if (op == 0x7E) e.SseRegReg(prefix, op, ins.RegField, G[ins.Rm]);
                    else e.SseRegReg(prefix, op, ins.RegField, ins.Rm);
                    break;
                case ToGpr:
                    if (mem) e.SseRegMem(prefix, op, G[ins.RegField], Mem, S1, 1, 0);
                    else e.SseRegReg(prefix, op, G[ins.RegField], ins.Rm);
                    break;
                case FromGpr:
                    if (mem) e.SseRegMem(prefix, op, ins.RegField, Mem, S1, 1, 0);
                    else e.SseRegReg(prefix, op, ins.RegField, G[ins.Rm]);
                    break;
            }
            if (immediate) e.U8((byte)ins.Imm);

            if (needsRound)
            {
                e.Jmp(done);
                e.Label(bad);
                e.Popfq();
                EmitExit(ins.Address, Ctx.ReasonFallback);
                e.Label(done);
            }
            return true;
        }
    }
}
