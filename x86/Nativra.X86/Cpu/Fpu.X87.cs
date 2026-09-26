using System;

namespace Nativra.X86.Cpu
{
    /// <summary>
    /// x87, MMX and SSE/SSE2 state and execution for the interpreter.
    ///
    /// Registers are kept exactly as FXSAVE lays them out — each x87 register
    /// as a raw 80-bit value, XMM as two quadwords — so the state can be handed
    /// to the JIT (which runs these instructions natively and moves the state
    /// with FXRSTOR/FXSAVE) and back without loss. Arithmetic is done in
    /// double precision, which is what 32-bit Windows programs run at: the
    /// CRT sets the precision control to 53 bits at startup.
    /// </summary>
    public sealed partial class FpuUnit
    {
        private readonly Interpreter owner;

        // Physical registers R0..R7 (not stack-relative): 64-bit significand + sign/exponent.
        private readonly ulong[] mant = new ulong[8];
        private readonly ushort[] sexp = new ushort[8];
        private readonly bool[] empty = { true, true, true, true, true, true, true, true };

        public ushort Control = 0x037F;
        private ushort status;
        private int top;

        public uint Mxcsr = 0x1F80;
        public readonly ulong[] XmmLo = new ulong[8];
        public readonly ulong[] XmmHi = new ulong[8];

        public FpuUnit(Interpreter owner)
        {
            this.owner = owner;
        }

        private CpuState Cpu => owner.Cpu;
        private GuestMemory Memory => owner.Memory;

        public ushort Status => (ushort)((status & ~0x3800) | ((top & 7) << 11));

        public int Top => top;

        /// <summary>The whole x87/SSE register file, for switching guest threads.</summary>
        public sealed class Snapshot
        {
            internal readonly ulong[] Mant = new ulong[8];
            internal readonly ushort[] Sexp = new ushort[8];
            internal readonly bool[] Empty = new bool[8];
            internal readonly ulong[] XmmLo = new ulong[8];
            internal readonly ulong[] XmmHi = new ulong[8];
            internal ushort Control = 0x037F, Status;
            internal int Top;
            internal uint Mxcsr = 0x1F80;

            public Snapshot()
            {
                for (var i = 0; i < 8; i++) Empty[i] = true;
            }
        }

        public void SaveTo(Snapshot s)
        {
            Array.Copy(mant, s.Mant, 8);
            Array.Copy(sexp, s.Sexp, 8);
            Array.Copy(empty, s.Empty, 8);
            Array.Copy(XmmLo, s.XmmLo, 8);
            Array.Copy(XmmHi, s.XmmHi, 8);
            s.Control = Control;
            s.Status = status;
            s.Top = top;
            s.Mxcsr = Mxcsr;
        }

        public void LoadFrom(Snapshot s)
        {
            Array.Copy(s.Mant, mant, 8);
            Array.Copy(s.Sexp, sexp, 8);
            Array.Copy(s.Empty, empty, 8);
            Array.Copy(s.XmmLo, XmmLo, 8);
            Array.Copy(s.XmmHi, XmmHi, 8);
            Control = s.Control;
            status = s.Status;
            top = s.Top;
            Mxcsr = s.Mxcsr;
        }

        public void Reset()
        {
            Control = 0x037F;
            status = 0;
            top = 0;
            for (var i = 0; i < 8; i++) empty[i] = true;
        }

        // ---------------------------------------------------- 80-bit <-> double

        public static void FromDouble(double value, out ulong m, out ushort se)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            var sign = (ushort)((bits >> 48) & 0x8000);
            var exponent = (int)((bits >> 52) & 0x7FF);
            var fraction = (ulong)bits & 0xFFFFFFFFFFFFFUL;
            if (exponent == 0)
            {
                if (fraction == 0) { m = 0; se = sign; return; }
                // Denormal double: normalise into the wider exponent range.
                var shift = Bits.LeadingZeros(fraction) - 11;
                fraction <<= shift;
                m = fraction << 11;
                se = (ushort)(sign | (ushort)(1 - 1023 + 16383 - shift));
                return;
            }
            if (exponent == 0x7FF)
            {
                m = 0x8000000000000000UL | (fraction << 11);
                se = (ushort)(sign | 0x7FFF);
                return;
            }
            m = 0x8000000000000000UL | (fraction << 11);
            se = (ushort)(sign | (ushort)(exponent - 1023 + 16383));
        }

        public static double ToDouble(ulong m, ushort se)
        {
            var sign = (se & 0x8000) != 0;
            var exponent = se & 0x7FFF;
            if (exponent == 0 && m == 0) return sign ? -0.0 : 0.0;
            if (exponent == 0x7FFF)
            {
                if ((m & 0x7FFFFFFFFFFFFFFFUL) == 0) return sign ? double.NegativeInfinity : double.PositiveInfinity;
                var nanBits = (sign ? 1UL << 63 : 0) | (0x7FFUL << 52) | ((m >> 11) & 0xFFFFFFFFFFFFFUL);
                if ((nanBits & 0xFFFFFFFFFFFFFUL) == 0) nanBits |= 1UL << 51;
                return BitConverter.Int64BitsToDouble((long)nanBits);
            }
            if (m == 0) return sign ? -0.0 : 0.0;
            // value = m * 2^(exponent - 16383 - 63), rounded to nearest by the scaling.
            var result = Bits.ScaleB((double)(m >> 11), exponent - 16383 - 52);
            if ((m & 0x7FF) != 0)
            {
                // Round the 11 dropped bits to nearest-even ourselves.
                var truncated = m >> 11;
                var rest = m & 0x7FF;
                if (rest > 0x400 || (rest == 0x400 && (truncated & 1) != 0)) truncated++;
                result = Bits.ScaleB((double)truncated, exponent - 16383 - 52);
            }
            return sign ? -result : result;
        }

        // ------------------------------------------------------------ the stack

        private int Phys(int i) => (top + i) & 7;

        public double St(int i)
        {
            var p = Phys(i);
            if (empty[p]) { status |= 0x41; return double.NaN; } // stack underflow: IE|SF
            return ToDouble(mant[p], sexp[p]);
        }

        public void SetSt(int i, double value)
        {
            var p = Phys(i);
            FromDouble(RoundToPrecision(value), out mant[p], out sexp[p]);
            empty[p] = false;
        }

        private void SetStRaw(int i, ulong m, ushort se)
        {
            var p = Phys(i);
            mant[p] = m;
            sexp[p] = se;
            empty[p] = false;
        }

        public void Push(double value)
        {
            top = (top - 1) & 7;
            if (!empty[top]) status |= 0x241; // overflow: IE|SF|C1
            FromDouble(RoundToPrecision(value), out mant[top], out sexp[top]);
            empty[top] = false;
        }

        private void PushRaw(ulong m, ushort se)
        {
            top = (top - 1) & 7;
            mant[top] = m;
            sexp[top] = se;
            empty[top] = false;
        }

        public void Pop()
        {
            empty[top] = true;
            top = (top + 1) & 7;
        }

        /// <summary>Single-precision control narrows results the way the hardware would.</summary>
        private double RoundToPrecision(double value) =>
            ((Control >> 8) & 3) == 0 ? (float)value : value;

        private double RoundInteger(double value)
        {
            switch ((Control >> 10) & 3)
            {
                case 0: return Math.Round(value, MidpointRounding.ToEven);
                case 1: return Math.Floor(value);
                case 2: return Math.Ceiling(value);
                default: return Math.Truncate(value);
            }
        }

        // ------------------------------------------------------------- helpers

        private void SetCompare(double a, double b)
        {
            // C3 C2 C0 = 000 greater, 001 less, 100 equal, 111 unordered.
            status &= unchecked((ushort)~0x4500);
            if (double.IsNaN(a) || double.IsNaN(b)) status |= 0x4500;
            else if (a < b) status |= 0x0100;
            else if (a == b) status |= 0x4000;
        }

        private void SetEflagsCompare(double a, double b)
        {
            var f = Cpu.EFlags & ~(Flag.ZF | Flag.PF | Flag.CF | Flag.OF | Flag.SF | Flag.AF);
            if (double.IsNaN(a) || double.IsNaN(b)) f |= Flag.ZF | Flag.PF | Flag.CF;
            else if (a < b) f |= Flag.CF;
            else if (a == b) f |= Flag.ZF;
            Cpu.EFlags = f;
        }

        private static double Arith(int kind, double st0, double other)
        {
            switch (kind)
            {
                case 0: return st0 + other;
                case 1: return st0 * other;
                case 4: return st0 - other;
                case 5: return other - st0;
                case 6: return st0 / other;
                default: return other / st0;
            }
        }

        private long ToInteger(double value, int bits, bool truncate)
        {
            var rounded = truncate ? Math.Truncate(value) : RoundInteger(value);
            var limit = bits == 16 ? 32768.0 : bits == 32 ? 2147483648.0 : 9223372036854775808.0;
            if (double.IsNaN(rounded) || rounded >= limit || rounded < -limit)
            {
                status |= 0x01; // invalid
                return bits == 16 ? short.MinValue : bits == 32 ? int.MinValue : long.MinValue;
            }
            return (long)rounded;
        }

        private uint Address(in Instruction ins) => owner.LinearAddress(ins);

        // -------------------------------------------------------------- execute

        public bool ExecuteX87(in Instruction ins)
        {
            var escape = ins.Op - 0xD8;
            var reg = ins.RegField;
            var rm = ins.Rm;

            if (ins.Mod != 3)
            {
                var at = Address(ins);
                switch (escape)
                {
                    case 0: // m32real arithmetic
                    case 2: // m32int
                    case 4: // m64real
                    case 6: // m16int
                    {
                        double operand = escape == 0 ? Bits.Int32BitsToSingle((int)Memory.Read32(at))
                            : escape == 2 ? (int)Memory.Read32(at)
                            : escape == 4 ? BitConverter.Int64BitsToDouble((long)Memory.Read64(at))
                            : (short)Memory.Read16(at);
                        if (reg == 2 || reg == 3)
                        {
                            SetCompare(St(0), operand);
                            if (reg == 3) Pop();
                            return true;
                        }
                        SetSt(0, Arith(reg == 0 ? 0 : reg == 1 ? 1 : reg, St(0), operand));
                        return true;
                    }
                    case 1:
                        switch (reg)
                        {
                            case 0: Push(Bits.Int32BitsToSingle((int)Memory.Read32(at))); return true;
                            case 2: Memory.Write32(at, (uint)Bits.SingleToInt32Bits((float)St(0))); return true;
                            case 3: Memory.Write32(at, (uint)Bits.SingleToInt32Bits((float)St(0))); Pop(); return true;
                            case 4: LoadEnvironment(at); return true;
                            case 5: Control = Memory.Read16(at); return true;
                            case 6: StoreEnvironment(at); return true;
                            case 7: Memory.Write16(at, Control); return true;
                        }
                        return false;
                    case 3:
                        switch (reg)
                        {
                            case 0: Push((int)Memory.Read32(at)); return true;
                            case 1: Memory.Write32(at, (uint)ToInteger(St(0), 32, true)); Pop(); return true;
                            case 2: Memory.Write32(at, (uint)ToInteger(St(0), 32, false)); return true;
                            case 3: Memory.Write32(at, (uint)ToInteger(St(0), 32, false)); Pop(); return true;
                            case 5: PushRaw(Memory.Read64(at), Memory.Read16(at + 8)); return true;
                            case 7:
                            {
                                var p = Phys(0);
                                Memory.Write64(at, mant[p]);
                                Memory.Write16(at + 8, sexp[p]);
                                Pop();
                                return true;
                            }
                        }
                        return false;
                    case 5:
                        switch (reg)
                        {
                            case 0: Push(BitConverter.Int64BitsToDouble((long)Memory.Read64(at))); return true;
                            case 1: Memory.Write64(at, (ulong)ToInteger(St(0), 64, true)); Pop(); return true;
                            case 2: Memory.Write64(at, (ulong)BitConverter.DoubleToInt64Bits(St(0))); return true;
                            case 3: Memory.Write64(at, (ulong)BitConverter.DoubleToInt64Bits(St(0))); Pop(); return true;
                            case 4: Restore(at); return true;
                            case 6: Save(at); return true;
                            case 7: Memory.Write16(at, Status); return true;
                        }
                        return false;
                    case 7:
                        switch (reg)
                        {
                            case 0: Push((short)Memory.Read16(at)); return true;
                            case 1: Memory.Write16(at, (ushort)ToInteger(St(0), 16, true)); Pop(); return true;
                            case 2: Memory.Write16(at, (ushort)ToInteger(St(0), 16, false)); return true;
                            case 3: Memory.Write16(at, (ushort)ToInteger(St(0), 16, false)); Pop(); return true;
                            case 4: Push(LoadBcd(at)); return true;
                            case 5: Push((long)Memory.Read64(at)); return true;
                            case 6: StoreBcd(at, St(0)); Pop(); return true;
                            case 7: Memory.Write64(at, (ulong)ToInteger(St(0), 64, false)); Pop(); return true;
                        }
                        return false;
                }
                return false;
            }

            // Register forms.
            switch (escape)
            {
                case 0:
                    if (reg == 2 || reg == 3)
                    {
                        SetCompare(St(0), St(rm));
                        if (reg == 3) Pop();
                        return true;
                    }
                    SetSt(0, Arith(reg == 0 ? 0 : reg == 1 ? 1 : reg, St(0), St(rm)));
                    return true;
                case 1: return Escape1Register(reg, rm);
                case 2:
                    if (reg < 4)
                    {
                        var take = reg == 0 ? (Cpu.EFlags & Flag.CF) != 0
                            : reg == 1 ? (Cpu.EFlags & Flag.ZF) != 0
                            : reg == 2 ? (Cpu.EFlags & (Flag.CF | Flag.ZF)) != 0
                            : (Cpu.EFlags & Flag.PF) != 0;
                        if (take) CopyStack(rm, 0);
                        return true;
                    }
                    if (reg == 5 && rm == 1) { SetCompare(St(0), St(1)); Pop(); Pop(); return true; }
                    return false;
                case 3:
                    if (reg < 4)
                    {
                        var take = reg == 0 ? (Cpu.EFlags & Flag.CF) == 0
                            : reg == 1 ? (Cpu.EFlags & Flag.ZF) == 0
                            : reg == 2 ? (Cpu.EFlags & (Flag.CF | Flag.ZF)) == 0
                            : (Cpu.EFlags & Flag.PF) == 0;
                        if (take) CopyStack(rm, 0);
                        return true;
                    }
                    if (reg == 4)
                    {
                        if (rm == 2) { status &= 0x7F00; return true; }       // FNCLEX
                        if (rm == 3) { Reset(); return true; }                  // FNINIT
                        if (rm == 0 || rm == 1 || rm == 4) return true;         // FENI/FDISI/FSETPM
                        return false;
                    }
                    if (reg == 5 || reg == 6) { SetEflagsCompare(St(0), St(rm)); return true; }
                    return false;
                case 4:
                {
                    if (reg == 2 || reg == 3)
                    {
                        SetCompare(St(0), St(rm));
                        if (reg == 3) Pop();
                        return true;
                    }
                    // DC: destination is ST(i), and the SUB/DIV pairs are reversed.
                    var a = St(rm);
                    var b = St(0);
                    double r;
                    switch (reg)
                    {
                        case 0: r = a + b; break;
                        case 1: r = a * b; break;
                        case 4: r = b - a; break; // FSUBR ST(i), ST(0)
                        case 5: r = a - b; break; // FSUB  ST(i), ST(0)
                        case 6: r = b / a; break; // FDIVR ST(i), ST(0)
                        default: r = a / b; break; // FDIV ST(i), ST(0)
                    }
                    SetSt(rm, r);
                    return true;
                }
                case 5:
                    switch (reg)
                    {
                        case 0: empty[Phys(rm)] = true; return true;           // FFREE
                        case 1: return Escape1Register(1, rm);                   // FXCH alias
                        case 2: CopyStack(0, rm); return true;                   // FST ST(i)
                        case 3: CopyStack(0, rm); Pop(); return true;            // FSTP ST(i)
                        case 4: SetCompare(St(0), St(rm)); return true;          // FUCOM
                        case 5: SetCompare(St(0), St(rm)); Pop(); return true;   // FUCOMP
                    }
                    return false;
                case 6:
                {
                    if (reg == 3)
                    {
                        if (rm != 1) return false;
                        SetCompare(St(0), St(1)); Pop(); Pop();
                        return true;
                    }
                    if (reg == 2) { SetCompare(St(0), St(rm)); Pop(); return true; }
                    var a = St(rm);
                    var b = St(0);
                    double r;
                    switch (reg)
                    {
                        case 0: r = a + b; break;
                        case 1: r = a * b; break;
                        case 4: r = b - a; break; // FSUBRP
                        case 5: r = a - b; break; // FSUBP
                        case 6: r = b / a; break; // FDIVRP
                        default: r = a / b; break; // FDIVP
                    }
                    SetSt(rm, r);
                    Pop();
                    return true;
                }
                case 7:
                    if (reg == 4 && rm == 0) { owner.SetReg(Reg.Eax, 16, Status); return true; } // FNSTSW AX
                    if (reg == 5 || reg == 6) { SetEflagsCompare(St(0), St(rm)); Pop(); return true; }
                    if (reg == 0) { empty[Phys(rm)] = true; Pop(); return true; }                   // FFREEP
                    return false;
            }
            return false;
        }

        private void CopyStack(int from, int to)
        {
            var p = Phys(from);
            if (empty[p]) { SetSt(to, double.NaN); status |= 0x41; return; }
            SetStRaw(to, mant[p], sexp[p]);
        }

        private bool Escape1Register(int reg, int rm)
        {
            switch (reg)
            {
                case 0: // FLD ST(i)
                {
                    var p = Phys(rm);
                    if (empty[p]) { Push(double.NaN); status |= 0x41; return true; }
                    PushRaw(mant[p], sexp[p]);
                    return true;
                }
                case 1: // FXCH
                {
                    int a = Phys(0), b = Phys(rm);
                    (mant[a], mant[b]) = (mant[b], mant[a]);
                    (sexp[a], sexp[b]) = (sexp[b], sexp[a]);
                    (empty[a], empty[b]) = (empty[b], empty[a]);
                    return true;
                }
                case 2: return rm == 0; // FNOP
                case 3: CopyStack(0, rm); Pop(); return true; // FSTP1 alias
                case 4:
                    switch (rm)
                    {
                        case 0: { var p = Phys(0); sexp[p] ^= 0x8000; return true; } // FCHS
                        case 1: { var p = Phys(0); sexp[p] &= 0x7FFF; return true; } // FABS
                        case 4: SetCompare(St(0), 0.0); return true;               // FTST
                        case 5: Examine(); return true;                            // FXAM
                    }
                    return false;
                case 5:
                    switch (rm)
                    {
                        case 0: Push(1.0); return true;
                        case 1: Push(Bits.Log2(10.0)); return true;
                        case 2: Push(Bits.Log2(Math.E)); return true;
                        case 3: Push(Math.PI); return true;
                        case 4: Push(Math.Log10(2.0)); return true;
                        case 5: Push(Math.Log(2.0)); return true;
                        case 6: Push(0.0); return true;
                    }
                    return false;
                case 6:
                    switch (rm)
                    {
                        case 0: SetSt(0, Math.Pow(2.0, St(0)) - 1.0); return true;                   // F2XM1
                        case 1: { var r = St(1) * Bits.Log2(St(0)); Pop(); SetSt(0, r); return true; }  // FYL2X
                        case 2: SetSt(0, Math.Tan(St(0))); Push(1.0); status &= unchecked((ushort)~0x400); return true; // FPTAN
                        case 3: { var r = Math.Atan2(St(1), St(0)); Pop(); SetSt(0, r); return true; }  // FPATAN
                        case 4: // FXTRACT
                        {
                            var v = St(0);
                            if (v == 0) { SetSt(0, double.NegativeInfinity); Push(v); return true; }
                            var e = Bits.ILogB(v);
                            SetSt(0, e);
                            Push(Bits.ScaleB(v, -e));
                            return true;
                        }
                        case 5: Remainder(true); return true;  // FPREM1
                        case 6: top = (top - 1) & 7; return true; // FDECSTP
                        case 7: top = (top + 1) & 7; return true; // FINCSTP
                    }
                    return false;
                default:
                    switch (rm)
                    {
                        case 0: Remainder(false); return true; // FPREM
                        case 1: { var r = St(1) * Bits.Log2(St(0) + 1.0); Pop(); SetSt(0, r); return true; } // FYL2XP1
                        case 2: SetSt(0, Math.Sqrt(St(0))); return true;
                        case 3: { var v = St(0); SetSt(0, Math.Sin(v)); Push(Math.Cos(v)); status &= unchecked((ushort)~0x400); return true; }
                        case 4: SetSt(0, RoundInteger(St(0))); return true;
                        case 5: SetSt(0, Bits.ScaleB(St(0), (int)Math.Truncate(St(1)))); return true;
                        case 6: SetSt(0, Math.Sin(St(0))); status &= unchecked((ushort)~0x400); return true;
                        case 7: SetSt(0, Math.Cos(St(0))); status &= unchecked((ushort)~0x400); return true;
                    }
                    return false;
            }
        }

        private void Remainder(bool ieee)
        {
            var a = St(0);
            var b = St(1);
            var q = ieee ? Math.Round(a / b, MidpointRounding.ToEven) : Math.Truncate(a / b);
            // FPREM is the truncating remainder (C's fmod, exact); FPREM1 the IEEE one.
            var r = ieee ? Math.IEEERemainder(a, b) : a % b;
            SetSt(0, r);
            var qi = (long)Math.Abs(q);
            status &= unchecked((ushort)~0x4700);
            if ((qi & 1) != 0) status |= 0x0200; // C1 = Q0
            if ((qi & 2) != 0) status |= 0x4000; // C3 = Q1
            if ((qi & 4) != 0) status |= 0x0100; // C0 = Q2
        }

        private void Examine()
        {
            var p = Phys(0);
            status &= unchecked((ushort)~0x4700);
            if ((sexp[p] & 0x8000) != 0) status |= 0x0200;
            if (empty[p]) { status |= 0x4100; return; }                  // empty: C3 C0
            var e = sexp[p] & 0x7FFF;
            if (e == 0x7FFF)
            {
                status |= (mant[p] & 0x7FFFFFFFFFFFFFFFUL) == 0 ? (ushort)0x0500 : (ushort)0x0100; // inf / NaN
                return;
            }
            if (e == 0 && mant[p] == 0) { status |= 0x4000; return; }      // zero
            if (e == 0) { status |= 0x4400; return; }                     // denormal
            status |= 0x0400;                                             // normal
        }

        // ---------------------------------------------------------- BCD helpers

        private double LoadBcd(uint at)
        {
            double value = 0;
            for (var i = 8; i >= 0; i--)
            {
                var b = Memory.Read8(at + (uint)i);
                value = value * 100 + (b >> 4) * 10 + (b & 0xF);
            }
            return (Memory.Read8(at + 9) & 0x80) != 0 ? -value : value;
        }

        private void StoreBcd(uint at, double value)
        {
            var negative = value < 0;
            var n = (ulong)Math.Abs(RoundInteger(value));
            for (var i = 0; i < 9; i++)
            {
                var low = n % 10; n /= 10;
                var high = n % 10; n /= 10;
                Memory.Write8(at + (uint)i, (byte)((high << 4) | low));
            }
            Memory.Write8(at + 9, (byte)(negative ? 0x80 : 0));
        }

        // ------------------------------------------------ environment and state

        private ushort TagWord()
        {
            ushort tags = 0;
            for (var p = 0; p < 8; p++)
            {
                int tag;
                if (empty[p]) tag = 3;
                else if ((sexp[p] & 0x7FFF) == 0 && mant[p] == 0) tag = 1;
                else if ((sexp[p] & 0x7FFF) == 0x7FFF || (sexp[p] & 0x7FFF) == 0 || (mant[p] >> 63) == 0) tag = 2;
                else tag = 0;
                tags |= (ushort)(tag << (p * 2));
            }
            return tags;
        }

        private void SetTagWord(ushort tags)
        {
            for (var p = 0; p < 8; p++) empty[p] = ((tags >> (p * 2)) & 3) == 3;
        }

        private void StoreEnvironment(uint at)
        {
            Memory.Write32(at, Control | 0xFFFF0000u);
            Memory.Write32(at + 4, Status | 0xFFFF0000u);
            Memory.Write32(at + 8, TagWord() | 0xFFFF0000u);
            Memory.Write32(at + 12, 0);
            Memory.Write32(at + 16, 0x23);
            Memory.Write32(at + 20, 0);
            Memory.Write32(at + 24, 0x2B);
            Control |= 0x3F; // FNSTENV masks all exceptions afterwards
        }

        private void LoadEnvironment(uint at)
        {
            Control = Memory.Read16(at);
            var sw = Memory.Read16(at + 4);
            status = sw;
            top = (sw >> 11) & 7;
            SetTagWord(Memory.Read16(at + 8));
        }

        private void Save(uint at)
        {
            StoreEnvironment(at);
            for (var i = 0; i < 8; i++)
            {
                var p = Phys(i);
                Memory.Write64(at + 28 + (uint)i * 10, mant[p]);
                Memory.Write16(at + 36 + (uint)i * 10, sexp[p]);
            }
            Reset();
        }

        private void Restore(uint at)
        {
            LoadEnvironment(at);
            for (var i = 0; i < 8; i++)
            {
                var p = Phys(i);
                mant[p] = Memory.Read64(at + 28 + (uint)i * 10);
                sexp[p] = Memory.Read16(at + 36 + (uint)i * 10);
            }
        }

        /// <summary>Writes the 512-byte FXSAVE image (32-bit format) into a byte array.</summary>
        public void WriteFxsave(byte[] image, int offset = 0)
        {
            Array.Clear(image, offset, 512);
            void W16(int at, ushort v) { image[offset + at] = (byte)v; image[offset + at + 1] = (byte)(v >> 8); }
            void W32(int at, uint v) { for (var i = 0; i < 4; i++) image[offset + at + i] = (byte)(v >> (8 * i)); }
            void W64(int at, ulong v) { for (var i = 0; i < 8; i++) image[offset + at + i] = (byte)(v >> (8 * i)); }
            W16(0, Control);
            W16(2, Status);
            byte abridged = 0;
            for (var p = 0; p < 8; p++) if (!empty[p]) abridged |= (byte)(1 << p);
            image[offset + 4] = abridged;
            W32(24, Mxcsr);
            W32(28, 0xFFFF);
            for (var i = 0; i < 8; i++)
            {
                var p = Phys(i);
                W64(32 + i * 16, mant[p]);
                W16(40 + i * 16, sexp[p]);
            }
            for (var i = 0; i < 8; i++)
            {
                W64(160 + i * 16, XmmLo[i]);
                W64(168 + i * 16, XmmHi[i]);
            }
        }

        /// <summary>Loads state from a 512-byte FXSAVE image.</summary>
        public void ReadFxsave(byte[] image, int offset = 0)
        {
            ushort R16(int at) => (ushort)(image[offset + at] | (image[offset + at + 1] << 8));
            uint R32(int at) => BitConverter.ToUInt32(image, offset + at);
            ulong R64(int at) => BitConverter.ToUInt64(image, offset + at);
            Control = R16(0);
            var sw = R16(2);
            status = sw;
            top = (sw >> 11) & 7;
            var abridged = image[offset + 4];
            for (var p = 0; p < 8; p++) empty[p] = (abridged & (1 << p)) == 0;
            Mxcsr = R32(24);
            for (var i = 0; i < 8; i++)
            {
                var p = Phys(i);
                mant[p] = R64(32 + i * 16);
                sexp[p] = R16(40 + i * 16);
            }
            for (var i = 0; i < 8; i++)
            {
                XmmLo[i] = R64(160 + i * 16);
                XmmHi[i] = R64(168 + i * 16);
            }
        }

        private void FxsaveTo(uint at)
        {
            var image = new byte[512];
            WriteFxsave(image);
            Memory.WriteBytes(at, image);
        }

        private void FxrstorFrom(uint at)
        {
            ReadFxsave(Memory.ReadBytes(at, 512));
        }

        // ----------------------------------------------------------------- MMX

        public ulong GetMm(int i) => mant[i];

        public void SetMm(int i, ulong value)
        {
            mant[i] = value;
            sexp[i] = 0xFFFF;
            EnterMmx();
        }

        private void EnterMmx()
        {
            top = 0;
            for (var p = 0; p < 8; p++) empty[p] = false;
        }

        private void Emms()
        {
            for (var p = 0; p < 8; p++) empty[p] = true;
        }
    }
}
