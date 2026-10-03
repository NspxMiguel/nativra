using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    /// <summary>
    /// x87 translation. The interpreter is the reference, and it models the x87
    /// as raw 80-bit registers whose arithmetic runs in double precision. The
    /// register file travels in the JIT context in that same layout (see Ctx), and
    /// each translated instruction does what the interpreter does on it, in x64
    /// code, for the ordinary case: operands present, finite and normal, results
    /// that fit a normal double. Anything else (an empty register, a NaN or
    /// infinity, a denormal, a rounding mode the host cannot reproduce) leaves the
    /// block before the instruction changes anything and the interpreter runs it.
    ///
    /// The guest's arithmetic flags live in the host flags register, so an
    /// instruction that does arithmetic brackets itself with pushfq/popfq. Guest
    /// memory is only touched outside that bracket, where a fault still sees the
    /// guest's flags and a balanced stack.
    /// </summary>
    public sealed partial class BlockTranslator
    {
        private const int R8 = X64.R8;
        private const int R9 = X64.R9;

        private string NewLabel(string prefix) => prefix + "_" + (labelCounter++);

        // ----------------------------------------------------------- primitives

        /// <summary>Starts a bracketed instruction: flags saved, TOP in r8.</summary>
        private void X87Begin()
        {
            EnsureTemps();
            UsesX87 = true;
            e.Pushfq();
            e.LoadCtx(R8, Ctxr, Ctx.Top);
        }

        /// <summary>Physical register of ST(i) into <paramref name="dst"/>; needs TOP in r8.</summary>
        private void PhysOf(int dst, int i) => e.MovzxMem(dst, Ctxr, R8, 1, Ctx.PhysTab + i, 8);

        private void RequireFull(int phys, string bad)
        {
            e.AluMemImm(7, Ctxr, phys, 1, Ctx.Empty, 0, 8);
            e.Jcc(5, bad);
        }

        private void RequireEmpty(int phys, string bad)
        {
            e.AluMemImm(7, Ctxr, phys, 1, Ctx.Empty, 0, 8);
            e.Jcc(4, bad);
        }

        private void MovqToXmm(int xmm, int gpr) => e.Rr(0x66, true, 0x0F, 0x6E, xmm, gpr);
        private void MovqFromXmm(int gpr, int xmm) => e.Rr(0x66, true, 0x0F, 0x7E, xmm, gpr);
        private void Shl64(int reg, int count) { e.Rr(0, true, 0xC1, -1, 4, reg); e.U8(count); }
        private void Shr64(int reg, int count) { e.Rr(0, true, 0xC1, -1, 5, reg); e.U8(count); }
        private void Test64(int a, int b) => e.Rr(0, true, 0x85, -1, b, a);

        /// <summary>
        /// ST(i)'s register (significand in S2, sign/exponent in S3) to a double's
        /// bits in S2, as FpuUnit.ToDouble would for a normal value. Zero is handled;
        /// denormals, infinities, NaNs, unnormals and values outside the double range
        /// leave through <paramref name="bad"/>. Uses r9.
        /// </summary>
        private void EmitToDouble(string bad)
        {
            var zero = NewLabel("td_zero");
            var up = NewLabel("td_up");
            var rounded = NewLabel("td_rounded");
            var end = NewLabel("td_end");
            e.MovRegReg(R9, S3);
            e.AluRegImm(4, R9, 0x7FFF);
            e.Jcc(4, zero);
            e.AluRegImm(5, R9, 15361);
            e.AluRegImm(7, R9, 2045);
            e.Jcc(7, bad);
            Test64(S2, S2);
            e.Jcc(9, bad);                       // the integer bit must be set
            Shl64(R9, 52);
            e.AluRegImm(4, S3, 0x8000);
            Shl64(S3, 48);
            e.AluRegReg(1, R9, S3, 64);          // exponent and sign
            e.MovRegReg(S3, S2, true);
            e.AluRegImm(4, S3, 0x7FF);           // the 11 bits that round away
            Shr64(S2, 11);
            e.AluRegImm(7, S3, 0x400);
            e.Jcc(7, up);
            e.Jcc(2, rounded);
            e.TestRegImm(S2, 1);
            e.Jcc(4, rounded);
            e.Label(up);
            e.AluRegImm(0, S2, 1, 64);
            e.Label(rounded);
            e.AluRegReg(0, S2, R9, 64);          // the carry out of the significand lands in the exponent
            e.Jmp(end);
            e.Label(zero);
            Test64(S2, S2);
            e.Jcc(5, bad);
            e.MovRegReg(S2, S3);
            e.AluRegImm(4, S2, 0x8000);
            Shl64(S2, 48);
            e.Label(end);
        }

        /// <summary>
        /// The double whose bits are in S2 into register <paramref name="dst"/> (a
        /// physical index), as FpuUnit.FromDouble would. Zero and normal doubles
        /// only. Uses S3 and r8; marks nothing empty or full.
        /// </summary>
        private void EmitFromDouble(int dst, string bad)
        {
            var zero = NewLabel("fd_zero");
            var store = NewLabel("fd_store");
            e.MovRegReg(S3, S2, true);
            Shr64(S3, 52);                       // sign and biased exponent
            e.MovRegReg(R8, S3);
            e.AluRegImm(4, R8, 0x7FF);
            e.Jcc(4, zero);
            e.AluRegImm(7, R8, 0x7FF);
            e.Jcc(4, bad);
            e.AluRegImm(0, R8, 15360);
            e.AluRegImm(4, S3, 0x800);
            e.ShiftImm(4, S3, 4);
            e.AluRegReg(1, R8, S3);              // sign and rebiased exponent
            Shl64(S2, 12);
            Shr64(S2, 1);
            e.Rr(0, true, 0x0F, 0xBA, 5, S2); e.U8(63);   // the explicit integer bit
            e.Jmp(store);
            e.Label(zero);
            e.MovRegReg(R8, S2, true);
            Shl64(R8, 1);
            e.Jcc(5, bad);                       // a denormal double
            e.MovRegReg(R8, S3);
            e.ShiftImm(4, R8, 4);
            e.AluRegReg(6, S2, S2);
            e.Label(store);
            e.StoreMem(S2, Ctxr, dst, 8, Ctx.Mm, 64);
            e.StoreMem(R8, Ctxr, dst, 2, Ctx.Sexp, 16);
        }

        /// <summary>ST(i) as a double in <paramref name="xmm"/>; checks the register is not empty.</summary>
        private void EmitLoadSt(int i, int xmm, string bad)
        {
            PhysOf(R9, i);
            RequireFull(R9, bad);
            e.LoadMem(S2, Ctxr, R9, 8, Ctx.Mm, 64);
            e.MovzxMem(S3, Ctxr, R9, 2, Ctx.Sexp, 16);
            EmitToDouble(bad);
            MovqToXmm(xmm, S2);
        }

        /// <summary>Single-precision control narrows a result to a float, as FpuUnit.RoundToPrecision does.</summary>
        private void EmitPrecisionRound(int xmm)
        {
            var skip = NewLabel("pc_skip");
            e.TestMemImm(Ctxr, -1, 1, Ctx.Control, 0x300);
            e.Jcc(5, skip);
            e.SseRegReg(0xF2, 0x5A, xmm, xmm);   // cvtsd2ss
            e.SseRegReg(0xF3, 0x5A, xmm, xmm);   // cvtss2sd
            e.Label(skip);
        }

        /// <summary>Pop: mark TOP empty and advance it. Flag-free.</summary>
        private void EmitPopCommit()
        {
            e.LoadCtx(R8, Ctxr, Ctx.Top);
            e.MovMemImm(Ctxr, R8, 1, Ctx.Empty, 1, 8);
            e.MovzxMem(R8, Ctxr, R8, 1, Ctx.PhysTab + 1, 8);
            e.StoreCtx(R8, Ctxr, Ctx.Top);
        }

        /// <summary>The comparison of xmm14 with xmm15 into C3/C2/C0 (no NaN reaches here).</summary>
        private void EmitSetCompare()
        {
            var less = NewLabel("cmp_less");
            var store = NewLabel("cmp_store");
            e.LoadCtx(S2, Ctxr, Ctx.Status);
            e.AluRegImm(4, S2, 0xFFFFBAFF);
            e.SseRegReg(0x66, 0x2F, 14, 15);     // comisd
            e.Jcc(2, less);
            e.Jcc(5, store);
            e.AluRegImm(1, S2, 0x4000);
            e.Jmp(store);
            e.Label(less);
            e.AluRegImm(1, S2, 0x0100);
            e.Label(store);
            e.StoreCtx(S2, Ctxr, Ctx.Status);
        }

        /// <summary>Ends a bracketed instruction on its success path, then lays out the exit to the interpreter.</summary>
        private void X87Finish(in Instruction ins, string bad, string done, System.Action afterRestore = null)
        {
            e.Popfq();
            afterRestore?.Invoke();
            e.Jmp(done);
            e.Label(bad);
            e.Popfq();
            EmitExit(ins.Address, Ctx.ReasonFallback);
            e.Label(done);
        }

        // -------------------------------------------------------------- dispatch

        private bool EmitX87(in Instruction ins)
        {
            var escape = ins.Op - 0xD8;
            var reg = ins.RegField;
            var rm = ins.Rm;
            if (ins.Mod != 3)
            {
                switch (escape)
                {
                    case 0:
                    case 2:
                    case 4:
                    case 6:
                        return EmitX87MemArith(ins, escape);
                    case 1:
                        switch (reg)
                        {
                            case 0: return EmitX87LoadFloat(ins, 32);
                            case 2: case 3: return EmitX87StoreFloat(ins, 32, reg == 3);
                            case 4: if (DisableX87Environment) return false; EmitFldenv(ins); return true;
                            case 5: EmitFldcw(ins); return true;
                            case 6: if (DisableX87Environment) return false; EmitFnstenv(ins); return true;
                            case 7: EmitFnstcw(ins); return true;
                        }
                        return false;
                    case 3:
                        switch (reg)
                        {
                            case 0: return EmitX87LoadInt(ins, 32);
                            case 1: return EmitX87StoreInt(ins, 32, true, true);
                            case 2: case 3: return EmitX87StoreInt(ins, 32, false, reg == 3);
                            case 5: EmitFld80(ins); return true;
                            case 7: EmitFstp80(ins); return true;
                        }
                        return false;
                    case 5:
                        switch (reg)
                        {
                            case 0: return EmitX87LoadFloat(ins, 64);
                            case 1: return EmitX87StoreInt(ins, 64, true, true);
                            case 2: case 3: return EmitX87StoreFloat(ins, 64, reg == 3);
                            case 7: EmitFnstswMem(ins); return true;
                        }
                        return false;
                    case 7:
                        switch (reg)
                        {
                            case 0: return EmitX87LoadInt(ins, 16);
                            case 1: return EmitX87StoreInt(ins, 16, true, true);
                            case 2: case 3: return EmitX87StoreInt(ins, 16, false, reg == 3);
                            case 5: return EmitX87LoadInt(ins, 64);
                            case 7: return EmitX87StoreInt(ins, 64, false, true);
                        }
                        return false;
                }
                return false;
            }

            switch (escape)
            {
                case 0:
                    if (reg == 2 || reg == 3) return EmitX87Compare(ins, rm, reg == 3 ? 1 : 0);
                    return EmitX87RegArith(ins, rm, reg, toSt0: true, pop: false);
                case 1:
                    switch (reg)
                    {
                        case 0: return EmitFldSt(ins, rm);
                        case 1: EmitFxch(rm); return true;
                        case 4:
                            if (rm == 0) { EmitSignBit(false); return true; }
                            if (rm == 1) { EmitSignBit(true); return true; }
                            return false;
                        case 5:
                            if (rm == 0 || rm == 6) return EmitFldConstant(ins, rm == 0);
                            return false;
                        case 7:
                            if (rm == 2) return EmitSqrt(ins);
                            if (rm == 4) return EmitRndint(ins);
                            return false;
                    }
                    return false;
                case 2:
                    if (reg == 5 && rm == 1) return EmitX87Compare(ins, 1, 2, st1: true);
                    return false;
                case 3:
                    if (reg == 5 || reg == 6) return EmitCompareToFlags(ins, rm, false);
                    return false;
                case 4:
                    if (reg == 2 || reg == 3) return EmitX87Compare(ins, rm, reg == 3 ? 1 : 0);
                    return EmitX87RegArith(ins, rm, reg, toSt0: false, pop: false);
                case 5:
                    switch (reg)
                    {
                        case 1: EmitFxch(rm); return true;
                        case 2: return EmitFstSt(ins, rm, false);
                        case 3: return EmitFstSt(ins, rm, true);
                        case 4: return EmitX87Compare(ins, rm, 0);
                        case 5: return EmitX87Compare(ins, rm, 1);
                    }
                    return false;
                case 6:
                    if (reg == 3) return rm == 1 ? EmitX87Compare(ins, 1, 2, st1: true) : false;
                    if (reg == 2) return false;
                    return EmitX87RegArith(ins, rm, reg, toSt0: false, pop: true);
                case 7:
                    if (reg == 4 && rm == 0) { EmitFnstswAx(); return true; }
                    if (reg == 5 || reg == 6) return EmitCompareToFlags(ins, rm, true);
                    return false;
            }
            return false;
        }

        // ------------------------------------------------------- memory arithmetic

        private static int ArithOpcode(int kind) =>
            kind == 0 ? 0x58 : kind == 1 ? 0x59 : (kind == 4 || kind == 5) ? 0x5C : 0x5E;

        /// <summary>Result of X op Y into xmm14: kinds 0,1 add/mul; 4,6 X-Y, X/Y; 5,7 Y-X, Y/X.</summary>
        private void EmitArithCompute(int kind)
        {
            var op = ArithOpcode(kind);
            if (kind == 5 || kind == 7)
            {
                e.SseRegReg(0xF2, op, 15, 14);
                e.SseRegReg(0x66, 0x28, 14, 15);
            }
            else e.SseRegReg(0xF2, op, 14, 15);
        }

        private bool EmitX87MemArith(in Instruction ins, int escape)
        {
            var kind = ins.RegField;
            EmitAddress(ins);
            if (escape == 0 || escape == 2) e.LoadMem(S2, Mem, S1, 1, 0, 32);
            else if (escape == 4) e.LoadMem(S2, Mem, S1, 1, 0, 64);
            else e.MovsxMem(S2, Mem, S1, 1, 0, 16, 32);

            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            if (escape == 0)
            {
                e.MovRegReg(S3, S2);
                e.AluRegImm(4, S3, 0x7F800000);
                e.AluRegImm(7, S3, 0x7F800000);
                e.Jcc(4, bad);                   // NaN or infinity
                e.Rr(0x66, false, 0x0F, 0x6E, 15, S2);
                e.SseRegReg(0xF3, 0x5A, 15, 15);
            }
            else if (escape == 4)
            {
                e.MovRegReg(S3, S2, true);
                Shr64(S3, 52);
                e.AluRegImm(4, S3, 0x7FF);
                e.AluRegImm(7, S3, 0x7FF);
                e.Jcc(4, bad);
                MovqToXmm(15, S2);
            }
            else e.SseRegReg(0xF2, 0x2A, 15, S2);   // cvtsi2sd of the integer operand

            EmitLoadSt(0, 14, bad);
            if (kind == 2 || kind == 3)
            {
                EmitSetCompare();
                X87Finish(ins, bad, done, kind == 3 ? (System.Action)EmitPopCommit : null);
                return true;
            }
            EmitArithCompute(kind);
            EmitPrecisionRound(14);
            MovqFromXmm(S2, 14);
            PhysOf(R9, 0);
            EmitFromDouble(R9, bad);
            X87Finish(ins, bad, done);
            return true;
        }

        private bool EmitX87RegArith(in Instruction ins, int rm, int kind, bool toSt0, bool pop)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            EmitLoadSt(0, 14, bad);
            EmitLoadSt(rm, 15, bad);
            EmitArithCompute(kind);
            EmitPrecisionRound(14);
            MovqFromXmm(S2, 14);
            PhysOf(R9, toSt0 ? 0 : rm);
            EmitFromDouble(R9, bad);
            X87Finish(ins, bad, done, pop ? (System.Action)EmitPopCommit : null);
            return true;
        }

        /// <summary>FCOM/FCOMP/FUCOM/FUCOMP ST(i) and the double-pop compares. <paramref name="pops"/> is 0, 1 or 2.</summary>
        private bool EmitX87Compare(in Instruction ins, int rm, int pops, bool st1 = false)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            EmitLoadSt(0, 14, bad);
            EmitLoadSt(st1 ? 1 : rm, 15, bad);
            EmitSetCompare();
            X87Finish(ins, bad, done, () =>
            {
                for (var i = 0; i < pops; i++) EmitPopCommit();
            });
            return true;
        }

        /// <summary>FCOMI/FUCOMI and the popping forms: the host's COMISD leaves exactly the flags the interpreter sets.</summary>
        private bool EmitCompareToFlags(in Instruction ins, int rm, bool pop)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            EmitLoadSt(0, 14, bad);
            EmitLoadSt(rm, 15, bad);
            e.SseRegReg(0x66, 0x2F, 14, 15);
            e.LeaWide(X64.Rsp, X64.Rsp, -1, 1, 8);   // keep the new flags: drop the saved copy
            if (pop) EmitPopCommit();
            e.Jmp(done);
            e.Label(bad);
            e.Popfq();
            EmitExit(ins.Address, Ctx.ReasonFallback);
            e.Label(done);
            return true;
        }

        // ------------------------------------------------------------ loads/stores

        private bool EmitX87LoadFloat(in Instruction ins, int bits)
        {
            EmitAddress(ins);
            e.LoadMem(S2, Mem, S1, 1, 0, bits);
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            if (bits == 32)
            {
                e.Rr(0x66, false, 0x0F, 0x6E, 14, S2);
                e.SseRegReg(0xF3, 0x5A, 14, 14);
            }
            else
            {
                MovqToXmm(14, S2);
                EmitPrecisionRound(14);
            }
            MovqFromXmm(S2, 14);
            EmitPushDouble(bad);
            X87Finish(ins, bad, done);
            return true;
        }

        /// <summary>Push the double in S2: needs TOP in r8; ends with the new TOP in r9.</summary>
        private void EmitPushDouble(string bad)
        {
            PhysOf(R9, 7);
            RequireEmpty(R9, bad);                // a full slot is a stack overflow
            EmitFromDouble(R9, bad);
            e.MovMemImm(Ctxr, R9, 1, Ctx.Empty, 0, 8);
            e.StoreCtx(R9, Ctxr, Ctx.Top);
        }

        private bool EmitX87LoadInt(in Instruction ins, int bits)
        {
            EmitAddress(ins);
            if (bits == 16) e.MovsxMem(S2, Mem, S1, 1, 0, 16, 32);
            else e.LoadMem(S2, Mem, S1, 1, 0, bits);
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            e.Rr(0xF2, bits == 64, 0x0F, 0x2A, 14, S2);   // cvtsi2sd
            EmitPrecisionRound(14);
            MovqFromXmm(S2, 14);
            EmitPushDouble(bad);
            X87Finish(ins, bad, done);
            return true;
        }

        private bool EmitX87StoreFloat(in Instruction ins, int bits, bool pop)
        {
            EmitAddress(ins);
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            EmitLoadSt(0, 14, bad);
            if (bits == 32)
            {
                e.SseRegReg(0xF2, 0x5A, 14, 14);          // (float)St(0)
                e.Rr(0x66, false, 0x0F, 0x7E, 14, S2);
            }
            X87Finish(ins, bad, done, () =>
            {
                e.StoreMem(S2, Mem, S1, 1, 0, bits);
                if (pop) EmitPopCommit();
            });
            return true;
        }

        private bool EmitX87StoreInt(in Instruction ins, int bits, bool truncateAlways, bool pop)
        {
            EmitAddress(ins);
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            var nearest = NewLabel("fi_nearest");
            var converted = NewLabel("fi_converted");
            EmitLoadSt(0, 14, bad);
            if (truncateAlways) e.Rr(0xF2, true, 0x0F, 0x2C, S2, 14);
            else
            {
                e.LoadCtx(S3, Ctxr, Ctx.Control);
                e.AluRegImm(4, S3, 0xC00);
                e.Jcc(4, nearest);
                e.AluRegImm(7, S3, 0xC00);
                e.Jcc(5, bad);                   // round down and up stay with the interpreter
                e.Rr(0xF2, true, 0x0F, 0x2C, S2, 14);
                e.Jmp(converted);
                e.Label(nearest);
                e.Rr(0xF2, true, 0x0F, 0x2D, S2, 14);
                e.Label(converted);
            }
            if (bits == 32) e.MovsxdRegReg(S3, S2);
            else if (bits == 16) e.Rr(0, true, 0x0F, 0xBF, S3, S2);
            else { e.MovRegReg(S3, S2, true); e.Rr(0, true, 0xF7, -1, 3, S3); e.Jcc(0, bad); }
            if (bits != 64) { e.AluRegReg(7, S3, S2, 64); e.Jcc(5, bad); }   // out of range: the integer indefinite
            X87Finish(ins, bad, done, () =>
            {
                e.StoreMem(S2, Mem, S1, 1, 0, bits);
                if (pop) EmitPopCommit();
            });
            return true;
        }

        private void EmitFldcw(in Instruction ins)
        {
            EmitAddress(ins);
            UsesX87 = true;
            e.MovzxMem(S2, Mem, S1, 1, 0, 16);
            e.StoreCtx(S2, Ctxr, Ctx.Control);
        }

        private void EmitFldenv(in Instruction ins)
        {
            EmitAddress(ins);
            UsesX87 = true;
            e.MovzxMem(S2, Mem, S1, 1, 0, 16);
            e.MovzxMem(S3, Mem, S1, 1, 4, 16);
            e.MovzxMem(R8, Mem, S1, 1, 8, 16);
            e.Pushfq();
            e.StoreCtx(S2, Ctxr, Ctx.Control);
            e.StoreCtx(S3, Ctxr, Ctx.Status);
            e.ShiftImm(5, S3, 11);
            e.AluRegImm(4, S3, 7);
            e.StoreCtx(S3, Ctxr, Ctx.Top);
            for (var p = 0; p < 8; p++)
            {
                var empty = NewLabel("env_empty");
                var done = NewLabel("env_done");
                e.MovRegReg(S3, R8);
                if (p != 0) e.ShiftImm(5, S3, (byte)(p * 2));
                e.AluRegImm(4, S3, 3);
                e.AluRegImm(7, S3, 3);
                e.Jcc(4, empty);
                e.MovMemImm(Ctxr, -1, 1, Ctx.Empty + p, 0, 8);
                e.Jmp(done);
                e.Label(empty);
                e.MovMemImm(Ctxr, -1, 1, Ctx.Empty + p, 1, 8);
                e.Label(done);
            }
            e.Popfq();
        }

        private void EmitFnstenv(in Instruction ins)
        {
            EmitAddress(ins);
            UsesX87 = true;
            e.Pushfq();
            e.MovRegImm32(S2, 0);
            for (var p = 0; p < 8; p++)
            {
                var full = NewLabel("tag_full");
                var zero = NewLabel("tag_zero");
                var special = NewLabel("tag_special");
                var done = NewLabel("tag_done");
                e.MovzxMem(R8, Ctxr, -1, 1, Ctx.Empty + p, 8);
                e.TestRegReg(R8, R8);
                e.Jcc(4, full);
                e.AluRegImm(1, S2, (uint)(3 << (p * 2)));
                e.Jmp(done);
                e.Label(full);
                e.MovzxMem(R9, Ctxr, -1, 1, Ctx.Sexp + p * 2, 16);
                e.LoadMem(R8, Ctxr, -1, 1, Ctx.Mm + p * 8, 64);
                e.AluRegImm(4, R9, 0x7FFF);
                e.Jcc(4, zero);
                e.AluRegImm(7, R9, 0x7FFF);
                e.Jcc(4, special);
                Test64(R8, R8);
                e.Jcc(9, special);
                e.Jmp(done);
                e.Label(zero);
                Test64(R8, R8);
                e.Jcc(5, special);
                e.AluRegImm(1, S2, (uint)(1 << (p * 2)));
                e.Jmp(done);
                e.Label(special);
                e.AluRegImm(1, S2, (uint)(2 << (p * 2)));
                e.Label(done);
            }
            e.AluRegImm(1, S2, 0xFFFF0000);
            e.Popfq();
            e.StoreCtx(S2, Ctxr, Ctx.Scratch);

            e.Pushfq();
            e.LoadCtx(S3, Ctxr, Ctx.Control);
            e.AluRegImm(1, S3, 0xFFFF0000);
            e.Popfq();
            e.StoreMem(S3, Mem, S1, 1, 0);
            e.Pushfq();
            EmitStatusWord();
            e.AluRegImm(1, S2, 0xFFFF0000);
            e.Popfq();
            e.StoreMem(S2, Mem, S1, 1, 4);
            // Keep the tag in the JIT scratch slot while EmitStatusWord borrows S2.
            e.LoadCtx(S2, Ctxr, Ctx.Scratch);
            e.StoreMem(S2, Mem, S1, 1, 8);
            e.MovMemImm(Mem, S1, 1, 12, 0);
            e.MovMemImm(Mem, S1, 1, 16, 0x23);
            e.MovMemImm(Mem, S1, 1, 20, 0);
            e.MovMemImm(Mem, S1, 1, 24, 0x2B);
            e.Pushfq();
            e.LoadCtx(S2, Ctxr, Ctx.Control);
            e.AluRegImm(1, S2, 0x3F);
            e.StoreCtx(S2, Ctxr, Ctx.Control);
            e.Popfq();
        }

        private void EmitFnstcw(in Instruction ins)
        {
            EmitAddress(ins);
            UsesX87 = true;
            e.LoadCtx(S2, Ctxr, Ctx.Control);
            e.StoreMem(S2, Mem, S1, 1, 0, 16);
        }

        private void EmitStatusWord()
        {
            e.LoadCtx(S2, Ctxr, Ctx.Status);
            e.AluRegImm(4, S2, 0xFFFFC7FF);
            e.LoadCtx(S3, Ctxr, Ctx.Top);
            e.ShiftImm(4, S3, 11);
            e.AluRegReg(1, S2, S3);
        }

        private void EmitFnstswMem(in Instruction ins)
        {
            EmitAddress(ins);
            UsesX87 = true;
            e.Pushfq();
            EmitStatusWord();
            e.Popfq();
            e.StoreMem(S2, Mem, S1, 1, 0, 16);
        }

        private void EmitFnstswAx()
        {
            UsesX87 = true;
            e.Pushfq();
            EmitStatusWord();
            e.Popfq();
            e.MovRegRegSized(G[Reg.Eax], S2, 16);
        }

        private void EmitFld80(in Instruction ins)
        {
            EmitAddress(ins);
            UsesX87 = true;
            e.LoadMem(S2, Mem, S1, 1, 0, 64);
            e.MovzxMem(S3, Mem, S1, 1, 8, 16);
            e.LoadCtx(R8, Ctxr, Ctx.Top);
            e.MovzxMem(R8, Ctxr, R8, 1, Ctx.PhysTab + 7, 8);
            e.StoreMem(S2, Ctxr, R8, 8, Ctx.Mm, 64);
            e.StoreMem(S3, Ctxr, R8, 2, Ctx.Sexp, 16);
            e.MovMemImm(Ctxr, R8, 1, Ctx.Empty, 0, 8);
            e.StoreCtx(R8, Ctxr, Ctx.Top);
        }

        private void EmitFstp80(in Instruction ins)
        {
            EmitAddress(ins);
            UsesX87 = true;
            e.LoadCtx(R8, Ctxr, Ctx.Top);
            e.MovzxMem(R9, Ctxr, R8, 1, Ctx.PhysTab, 8);
            e.LoadMem(S2, Ctxr, R9, 8, Ctx.Mm, 64);
            e.MovzxMem(S3, Ctxr, R9, 2, Ctx.Sexp, 16);
            e.StoreMem(S2, Mem, S1, 1, 0, 64);
            e.StoreMem(S3, Mem, S1, 1, 8, 16);
            EmitPopCommit();
        }

        // ------------------------------------------------------ register moves

        private void EmitFxch(int rm)
        {
            EnsureTemps();
            UsesX87 = true;
            e.LoadCtx(R8, Ctxr, Ctx.Top);
            e.MovzxMem(R9, Ctxr, R8, 1, Ctx.PhysTab, 8);
            e.MovzxMem(R8, Ctxr, R8, 1, Ctx.PhysTab + rm, 8);
            e.LoadMem(S2, Ctxr, R9, 8, Ctx.Mm, 64);
            e.LoadMem(S3, Ctxr, R8, 8, Ctx.Mm, 64);
            e.StoreMem(S3, Ctxr, R9, 8, Ctx.Mm, 64);
            e.StoreMem(S2, Ctxr, R8, 8, Ctx.Mm, 64);
            e.MovzxMem(S2, Ctxr, R9, 2, Ctx.Sexp, 16);
            e.MovzxMem(S3, Ctxr, R8, 2, Ctx.Sexp, 16);
            e.StoreMem(S3, Ctxr, R9, 2, Ctx.Sexp, 16);
            e.StoreMem(S2, Ctxr, R8, 2, Ctx.Sexp, 16);
            e.MovzxMem(S2, Ctxr, R9, 1, Ctx.Empty, 8);
            e.MovzxMem(S3, Ctxr, R8, 1, Ctx.Empty, 8);
            e.StoreMem(S3, Ctxr, R9, 1, Ctx.Empty, 8);
            e.StoreMem(S2, Ctxr, R8, 1, Ctx.Empty, 8);
        }

        private void EmitSignBit(bool clear)
        {
            UsesX87 = true;
            if (clear) e.Pushfq();
            e.LoadCtx(R8, Ctxr, Ctx.Top);
            e.MovzxMem(R9, Ctxr, R8, 1, Ctx.PhysTab, 8);
            e.MovzxMem(S2, Ctxr, R9, 2, Ctx.Sexp, 16);
            if (clear) e.AluRegImm(4, S2, 0x7FFF);
            else e.Lea(S2, S2, -1, 1, 0x8000);   // adding 0x8000 flips bit 15 in a 16-bit store, and leaves the flags alone
            e.StoreMem(S2, Ctxr, R9, 2, Ctx.Sexp, 16);
            if (clear) e.Popfq();
        }

        private bool EmitFldSt(in Instruction ins, int rm)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            PhysOf(R9, rm);
            RequireFull(R9, bad);
            e.LoadMem(S2, Ctxr, R9, 8, Ctx.Mm, 64);
            e.MovzxMem(S3, Ctxr, R9, 2, Ctx.Sexp, 16);
            e.MovzxMem(R8, Ctxr, R8, 1, Ctx.PhysTab + 7, 8);
            e.StoreMem(S2, Ctxr, R8, 8, Ctx.Mm, 64);
            e.StoreMem(S3, Ctxr, R8, 2, Ctx.Sexp, 16);
            e.MovMemImm(Ctxr, R8, 1, Ctx.Empty, 0, 8);
            e.StoreCtx(R8, Ctxr, Ctx.Top);
            X87Finish(ins, bad, done);
            return true;
        }

        private bool EmitFstSt(in Instruction ins, int rm, bool pop)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            PhysOf(R9, 0);
            RequireFull(R9, bad);
            e.LoadMem(S2, Ctxr, R9, 8, Ctx.Mm, 64);
            e.MovzxMem(S3, Ctxr, R9, 2, Ctx.Sexp, 16);
            PhysOf(R8, rm);
            e.StoreMem(S2, Ctxr, R8, 8, Ctx.Mm, 64);
            e.StoreMem(S3, Ctxr, R8, 2, Ctx.Sexp, 16);
            e.MovMemImm(Ctxr, R8, 1, Ctx.Empty, 0, 8);
            if (pop) EmitPopCommit();
            X87Finish(ins, bad, done);
            return true;
        }

        private bool EmitFldConstant(in Instruction ins, bool one)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            PhysOf(R9, 7);
            RequireEmpty(R9, bad);
            if (one) e.MovRegImm64(S2, 0x8000000000000000UL);
            else e.AluRegReg(6, S2, S2);
            e.StoreMem(S2, Ctxr, R9, 8, Ctx.Mm, 64);
            e.MovMemImm(Ctxr, R9, 2, Ctx.Sexp, one ? 0x3FFFu : 0u, 16);
            e.MovMemImm(Ctxr, R9, 1, Ctx.Empty, 0, 8);
            e.StoreCtx(R9, Ctxr, Ctx.Top);
            X87Finish(ins, bad, done);
            return true;
        }

        private bool EmitSqrt(in Instruction ins)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            EmitLoadSt(0, 14, bad);
            e.SseRegReg(0xF2, 0x51, 14, 14);
            EmitPrecisionRound(14);
            MovqFromXmm(S2, 14);
            PhysOf(R9, 0);
            EmitFromDouble(R9, bad);
            X87Finish(ins, bad, done);
            return true;
        }

        private bool EmitRndint(in Instruction ins)
        {
            X87Begin();
            var bad = NewLabel("x87_bad");
            var done = NewLabel("x87_done");
            var floor = NewLabel("ri_floor");
            var ceil = NewLabel("ri_ceil");
            var trunc = NewLabel("ri_trunc");
            var rounded = NewLabel("ri_rounded");
            EmitLoadSt(0, 14, bad);
            e.LoadCtx(S3, Ctxr, Ctx.Control);
            e.AluRegImm(4, S3, 0xC00);
            e.Jcc(4, rounded + "_n");
            e.AluRegImm(7, S3, 0x400);
            e.Jcc(4, floor);
            e.AluRegImm(7, S3, 0x800);
            e.Jcc(4, ceil);
            e.Label(trunc);
            e.Rr3(0x66, 0x0F, 0x3A, 0x0B, 14, 14); e.U8(0x0B);
            e.Jmp(rounded);
            e.Label(floor);
            e.Rr3(0x66, 0x0F, 0x3A, 0x0B, 14, 14); e.U8(0x09);
            e.Jmp(rounded);
            e.Label(ceil);
            e.Rr3(0x66, 0x0F, 0x3A, 0x0B, 14, 14); e.U8(0x0A);
            e.Jmp(rounded);
            e.Label(rounded + "_n");
            e.Rr3(0x66, 0x0F, 0x3A, 0x0B, 14, 14); e.U8(0x08);
            e.Label(rounded);
            EmitPrecisionRound(14);
            MovqFromXmm(S2, 14);
            PhysOf(R9, 0);
            EmitFromDouble(R9, bad);
            X87Finish(ins, bad, done);
            return true;
        }
    }
}
