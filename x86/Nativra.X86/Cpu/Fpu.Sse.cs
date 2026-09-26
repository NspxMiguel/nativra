using System;

namespace Nativra.X86.Cpu
{
    /// <summary>A 128-bit value with lane accessors, for the SSE and MMX paths.</summary>
    public struct V128
    {
        public ulong Lo, Hi;

        public V128(ulong lo, ulong hi) { Lo = lo; Hi = hi; }

        public ulong Q(int i) => i == 0 ? Lo : Hi;

        public void SetQ(int i, ulong v) { if (i == 0) Lo = v; else Hi = v; }

        public uint D(int i) => (uint)(Q(i >> 1) >> ((i & 1) * 32));

        public void SetD(int i, uint v)
        {
            var shift = (i & 1) * 32;
            SetQ(i >> 1, (Q(i >> 1) & ~(0xFFFFFFFFUL << shift)) | ((ulong)v << shift));
        }

        public ushort W(int i) => (ushort)(Q(i >> 2) >> ((i & 3) * 16));

        public void SetW(int i, ushort v)
        {
            var shift = (i & 3) * 16;
            SetQ(i >> 2, (Q(i >> 2) & ~(0xFFFFUL << shift)) | ((ulong)v << shift));
        }

        public byte B(int i) => (byte)(Q(i >> 3) >> ((i & 7) * 8));

        public void SetB(int i, byte v)
        {
            var shift = (i & 7) * 8;
            SetQ(i >> 3, (Q(i >> 3) & ~(0xFFUL << shift)) | ((ulong)v << shift));
        }

        public float F(int i) => Bits.Int32BitsToSingle((int)D(i));

        public void SetF(int i, float v) => SetD(i, (uint)Bits.SingleToInt32Bits(v));

        public double Dbl(int i) => BitConverter.Int64BitsToDouble((long)Q(i));

        public void SetDbl(int i, double v) => SetQ(i, (ulong)BitConverter.DoubleToInt64Bits(v));

        /// <summary>Element <paramref name="i"/> of a given byte width, zero-extended.</summary>
        public ulong Lane(int width, int i)
        {
            switch (width)
            {
                case 1: return B(i);
                case 2: return W(i);
                case 4: return D(i);
                default: return Q(i);
            }
        }

        public void SetLane(int width, int i, ulong v)
        {
            switch (width)
            {
                case 1: SetB(i, (byte)v); break;
                case 2: SetW(i, (ushort)v); break;
                case 4: SetD(i, (uint)v); break;
                default: SetQ(i, v); break;
            }
        }
    }

    public sealed partial class FpuUnit
    {
        // --------------------------------------------------------- operand I/O

        private V128 Xmm(int i) => new V128(XmmLo[i], XmmHi[i]);

        private void SetXmm(int i, V128 v)
        {
            XmmLo[i] = v.Lo;
            XmmHi[i] = v.Hi;
        }

        private V128 ReadXmmRm(in Instruction ins)
        {
            if (ins.Mod == 3) return Xmm(ins.Rm);
            var at = Address(ins);
            return new V128(Memory.Read64(at), Memory.Read64(at + 8));
        }

        private void WriteXmmRm(in Instruction ins, V128 v)
        {
            if (ins.Mod == 3) { SetXmm(ins.Rm, v); return; }
            var at = Address(ins);
            Memory.Write64(at, v.Lo);
            Memory.Write64(at + 8, v.Hi);
        }

        /// <summary>Scalar source: a register's low lane, or just 4/8 bytes of memory.</summary>
        private V128 ReadScalarRm(in Instruction ins, int bytes)
        {
            if (ins.Mod == 3) return Xmm(ins.Rm);
            var at = Address(ins);
            return bytes == 4 ? new V128(Memory.Read32(at), 0) : new V128(Memory.Read64(at), 0);
        }

        private ulong ReadMmRm(in Instruction ins) => ins.Mod == 3 ? GetMm(ins.Rm) : Memory.Read64(Address(ins));

        // ------------------------------------------------------------- execute

        public bool ExecuteSse(in Instruction ins)
        {
            if (ins.Op >= 0x1000000) return false;              // VEX: not advertised
            if (ins.Op > 0xFFFF) return false;                   // SSSE3/SSE4.x: not advertised
            var op = ins.Op & 0xFF;
            // Mandatory prefix: F3/F2 win over 66.
            var pfx = ins.Rep == 0xF3 ? 3 : ins.Rep == 0xF2 ? 2 : ins.OpSize16 ? 1 : 0;

            if (op == 0xAE) return Group15(ins);
            if (op == 0xC3 && ins.Mod != 3) { Memory.Write32(Address(ins), owner.GetReg(ins.RegField, 32)); return true; } // MOVNTI
            if (op == 0x77) { Emms(); return true; }

            if ((op >= 0x60 && op <= 0x7F) || op >= 0xD0 || op == 0xC4 || op == 0xC5 || op == 0xE7)
                return IntegerOp(ins, op, pfx);

            return FloatOp(ins, op, pfx);
        }

        private bool Group15(in Instruction ins)
        {
            if (ins.Mod == 3) return ins.RegField >= 5; // LFENCE/MFENCE/SFENCE
            var at = Address(ins);
            switch (ins.RegField)
            {
                case 0: FxsaveTo(at); return true;
                case 1: FxrstorFrom(at); return true;
                case 2: Mxcsr = Memory.Read32(at); return true;
                case 3: Memory.Write32(at, Mxcsr); return true;
                case 7: return true; // CLFLUSH
            }
            return false;
        }

        // ------------------------------------------------------------ floating

        private float RoundF(double v)
        {
            return (float)v;
        }

        private long ConvertToInt(double value, bool truncate, bool wide)
        {
            double rounded;
            if (truncate) rounded = Math.Truncate(value);
            else
            {
                switch ((Mxcsr >> 13) & 3)
                {
                    case 0: rounded = Math.Round(value, MidpointRounding.ToEven); break;
                    case 1: rounded = Math.Floor(value); break;
                    case 2: rounded = Math.Ceiling(value); break;
                    default: rounded = Math.Truncate(value); break;
                }
            }
            var limit = wide ? 9223372036854775808.0 : 2147483648.0;
            if (double.IsNaN(rounded) || rounded >= limit || rounded < -limit)
                return wide ? long.MinValue : int.MinValue;
            return (long)rounded;
        }

        private static bool CompareLane(int predicate, double a, double b)
        {
            var unordered = double.IsNaN(a) || double.IsNaN(b);
            switch (predicate & 7)
            {
                case 0: return !unordered && a == b;
                case 1: return !unordered && a < b;
                case 2: return !unordered && a <= b;
                case 3: return unordered;
                case 4: return unordered || a != b;
                case 5: return unordered || !(a < b);
                case 6: return unordered || !(a <= b);
                default: return !unordered;
            }
        }

        private static double FloatArith(int op, double a, double b, bool single)
        {
            switch (op)
            {
                case 0x58: return a + b;
                case 0x59: return a * b;
                case 0x5C: return a - b;
                case 0x5E: return a / b;
                case 0x5D: return a < b ? a : b;   // MIN: the second operand on NaN or equal
                case 0x5F: return a > b ? a : b;   // MAX: likewise
                case 0x51: return single ? Math.Sqrt((float)b) : Math.Sqrt(b);
                case 0x52: return 1.0 / Math.Sqrt(b);
                default: return 1.0 / b;           // 0x53 RCP
            }
        }

        private bool FloatOp(in Instruction ins, int op, int pfx)
        {
            var d = ins.RegField;
            switch (op)
            {
                case 0x10: // MOVUPS/MOVUPD/MOVSS/MOVSD load
                    if (pfx == 3)
                    {
                        if (ins.Mod == 3) { var v = Xmm(d); v.SetD(0, Xmm(ins.Rm).D(0)); SetXmm(d, v); }
                        else SetXmm(d, new V128(Memory.Read32(Address(ins)), 0));
                        return true;
                    }
                    if (pfx == 2)
                    {
                        if (ins.Mod == 3) { var v = Xmm(d); v.Lo = Xmm(ins.Rm).Lo; SetXmm(d, v); }
                        else SetXmm(d, new V128(Memory.Read64(Address(ins)), 0));
                        return true;
                    }
                    SetXmm(d, ReadXmmRm(ins));
                    return true;
                case 0x11:
                    if (pfx == 3)
                    {
                        if (ins.Mod == 3) { var v = Xmm(ins.Rm); v.SetD(0, Xmm(d).D(0)); SetXmm(ins.Rm, v); }
                        else Memory.Write32(Address(ins), Xmm(d).D(0));
                        return true;
                    }
                    if (pfx == 2)
                    {
                        if (ins.Mod == 3) { var v = Xmm(ins.Rm); v.Lo = Xmm(d).Lo; SetXmm(ins.Rm, v); }
                        else Memory.Write64(Address(ins), Xmm(d).Lo);
                        return true;
                    }
                    WriteXmmRm(ins, Xmm(d));
                    return true;
                case 0x12: // MOVLPS/MOVLPD load, MOVHLPS
                {
                    if (pfx >= 2) return false; // MOVSLDUP/MOVDDUP are SSE3
                    var v = Xmm(d);
                    v.Lo = ins.Mod == 3 ? Xmm(ins.Rm).Hi : Memory.Read64(Address(ins));
                    SetXmm(d, v);
                    return true;
                }
                case 0x13:
                    if (ins.Mod == 3) return false;
                    Memory.Write64(Address(ins), Xmm(d).Lo);
                    return true;
                case 0x14: // UNPCKLPS / UNPCKLPD
                case 0x15: // UNPCKHPS / UNPCKHPD
                {
                    var a = Xmm(d);
                    var b = ReadXmmRm(ins);
                    var r = new V128();
                    if (pfx == 1)
                    {
                        r.Lo = op == 0x14 ? a.Lo : a.Hi;
                        r.Hi = op == 0x14 ? b.Lo : b.Hi;
                    }
                    else
                    {
                        var baseLane = op == 0x14 ? 0 : 2;
                        r.SetD(0, a.D(baseLane)); r.SetD(1, b.D(baseLane));
                        r.SetD(2, a.D(baseLane + 1)); r.SetD(3, b.D(baseLane + 1));
                    }
                    SetXmm(d, r);
                    return true;
                }
                case 0x16: // MOVHPS/MOVHPD load, MOVLHPS
                {
                    if (pfx >= 2) return false;
                    var v = Xmm(d);
                    v.Hi = ins.Mod == 3 ? Xmm(ins.Rm).Lo : Memory.Read64(Address(ins));
                    SetXmm(d, v);
                    return true;
                }
                case 0x17:
                    if (ins.Mod == 3) return false;
                    Memory.Write64(Address(ins), Xmm(d).Hi);
                    return true;
                case 0x28: SetXmm(d, ReadXmmRm(ins)); return true;
                case 0x29: WriteXmmRm(ins, Xmm(d)); return true;
                case 0x2B:
                    if (ins.Mod == 3) return false;
                    WriteXmmRm(ins, Xmm(d));
                    return true;
                case 0x2A:
                {
                    var v = Xmm(d);
                    if (pfx == 3 || pfx == 2)
                    {
                        var n = (int)(ins.Mod == 3 ? owner.GetReg(ins.Rm, 32) : Memory.Read32(Address(ins)));
                        if (pfx == 3) v.SetF(0, n); else v.SetDbl(0, n);
                    }
                    else
                    {
                        var mm = ReadMmRm(ins);
                        if (pfx == 1) { v.SetDbl(0, (int)(uint)mm); v.SetDbl(1, (int)(uint)(mm >> 32)); }
                        else { v.SetF(0, (int)(uint)mm); v.SetF(1, (int)(uint)(mm >> 32)); }
                        if (ins.Mod == 3) EnterMmx();
                    }
                    SetXmm(d, v);
                    return true;
                }
                case 0x2C:
                case 0x2D:
                {
                    var truncate = op == 0x2C;
                    if (pfx == 3 || pfx == 2)
                    {
                        var s = ReadScalarRm(ins, pfx == 3 ? 4 : 8);
                        double value = pfx == 3 ? s.F(0) : s.Dbl(0);
                        owner.SetReg(d, 32, (uint)ConvertToInt(value, truncate, false));
                        return true;
                    }
                    var src = ReadXmmRm(ins);
                    ulong packed;
                    if (pfx == 1)
                        packed = (uint)ConvertToInt(src.Dbl(0), truncate, false) | ((ulong)(uint)ConvertToInt(src.Dbl(1), truncate, false) << 32);
                    else
                        packed = (uint)ConvertToInt(src.F(0), truncate, false) | ((ulong)(uint)ConvertToInt(src.F(1), truncate, false) << 32);
                    SetMm(d, packed);
                    return true;
                }
                case 0x2E:
                case 0x2F:
                {
                    var s = ReadScalarRm(ins, pfx == 1 ? 8 : 4);
                    var dv = Xmm(d);
                    double a = pfx == 1 ? dv.Dbl(0) : dv.F(0);
                    double b = pfx == 1 ? s.Dbl(0) : s.F(0);
                    var f = owner.Cpu.EFlags & ~(Flag.ZF | Flag.PF | Flag.CF | Flag.OF | Flag.SF | Flag.AF);
                    if (double.IsNaN(a) || double.IsNaN(b)) f |= Flag.ZF | Flag.PF | Flag.CF;
                    else if (a < b) f |= Flag.CF;
                    else if (a == b) f |= Flag.ZF;
                    owner.Cpu.EFlags = f;
                    return true;
                }
                case 0x50:
                {
                    if (ins.Mod != 3) return false;
                    var v = Xmm(ins.Rm);
                    uint mask = pfx == 1
                        ? (uint)((v.Lo >> 63) | ((v.Hi >> 63) << 1))
                        : ((v.D(0) >> 31) | ((v.D(1) >> 31) << 1) | ((v.D(2) >> 31) << 2) | ((v.D(3) >> 31) << 3));
                    owner.SetReg(d, 32, mask);
                    return true;
                }
                case 0x54: case 0x55: case 0x56: case 0x57:
                {
                    var a = Xmm(d);
                    var b = ReadXmmRm(ins);
                    switch (op)
                    {
                        case 0x54: a.Lo &= b.Lo; a.Hi &= b.Hi; break;
                        case 0x55: a.Lo = ~a.Lo & b.Lo; a.Hi = ~a.Hi & b.Hi; break;
                        case 0x56: a.Lo |= b.Lo; a.Hi |= b.Hi; break;
                        default: a.Lo ^= b.Lo; a.Hi ^= b.Hi; break;
                    }
                    SetXmm(d, a);
                    return true;
                }
                case 0x51: case 0x52: case 0x53:
                case 0x58: case 0x59: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                {
                    if ((op == 0x52 || op == 0x53) && (pfx == 1 || pfx == 2)) return false;
                    var a = Xmm(d);
                    if (pfx == 3) // scalar single
                    {
                        var b = ReadScalarRm(ins, 4);
                        a.SetF(0, RoundF(FloatArith(op, a.F(0), b.F(0), true)));
                    }
                    else if (pfx == 2) // scalar double
                    {
                        var b = ReadScalarRm(ins, 8);
                        a.SetDbl(0, FloatArith(op, a.Dbl(0), b.Dbl(0), false));
                    }
                    else if (pfx == 1) // packed double
                    {
                        var b = ReadXmmRm(ins);
                        for (var i = 0; i < 2; i++) a.SetDbl(i, FloatArith(op, a.Dbl(i), b.Dbl(i), false));
                    }
                    else // packed single
                    {
                        var b = ReadXmmRm(ins);
                        for (var i = 0; i < 4; i++) a.SetF(i, RoundF(FloatArith(op, a.F(i), b.F(i), true)));
                    }
                    SetXmm(d, a);
                    return true;
                }
                case 0x5A:
                {
                    var a = Xmm(d);
                    if (pfx == 3) { var b = ReadScalarRm(ins, 4); a.SetDbl(0, b.F(0)); }
                    else if (pfx == 2) { var b = ReadScalarRm(ins, 8); a.SetF(0, (float)b.Dbl(0)); }
                    else if (pfx == 1)
                    {
                        var b = ReadXmmRm(ins);
                        var r = new V128();
                        r.SetF(0, (float)b.Dbl(0)); r.SetF(1, (float)b.Dbl(1));
                        a = r;
                    }
                    else
                    {
                        var b = ins.Mod == 3 ? Xmm(ins.Rm) : new V128(Memory.Read64(Address(ins)), 0);
                        var r = new V128();
                        r.SetDbl(0, b.F(0)); r.SetDbl(1, b.F(1));
                        a = r;
                    }
                    SetXmm(d, a);
                    return true;
                }
                case 0x5B:
                {
                    var b = ReadXmmRm(ins);
                    var r = new V128();
                    if (pfx == 0) for (var i = 0; i < 4; i++) r.SetF(i, (int)b.D(i));
                    else if (pfx == 1 || pfx == 3)
                        for (var i = 0; i < 4; i++) r.SetD(i, (uint)ConvertToInt(b.F(i), pfx == 3, false));
                    else return false;
                    SetXmm(d, r);
                    return true;
                }
                case 0xC2:
                {
                    var a = Xmm(d);
                    var pred = (int)ins.Imm;
                    if (pfx == 3) { var b = ReadScalarRm(ins, 4); a.SetD(0, CompareLane(pred, a.F(0), b.F(0)) ? 0xFFFFFFFF : 0); }
                    else if (pfx == 2) { var b = ReadScalarRm(ins, 8); a.SetQ(0, CompareLane(pred, a.Dbl(0), b.Dbl(0)) ? ulong.MaxValue : 0); }
                    else if (pfx == 1)
                    {
                        var b = ReadXmmRm(ins);
                        for (var i = 0; i < 2; i++) a.SetQ(i, CompareLane(pred, a.Dbl(i), b.Dbl(i)) ? ulong.MaxValue : 0);
                    }
                    else
                    {
                        var b = ReadXmmRm(ins);
                        for (var i = 0; i < 4; i++) a.SetD(i, CompareLane(pred, a.F(i), b.F(i)) ? 0xFFFFFFFF : 0);
                    }
                    SetXmm(d, a);
                    return true;
                }
                case 0xC6:
                {
                    var a = Xmm(d);
                    var b = ReadXmmRm(ins);
                    var imm = ins.Imm;
                    var r = new V128();
                    if (pfx == 1)
                    {
                        r.Lo = (imm & 1) == 0 ? a.Lo : a.Hi;
                        r.Hi = (imm & 2) == 0 ? b.Lo : b.Hi;
                    }
                    else
                    {
                        r.SetD(0, a.D((int)(imm & 3)));
                        r.SetD(1, a.D((int)((imm >> 2) & 3)));
                        r.SetD(2, b.D((int)((imm >> 4) & 3)));
                        r.SetD(3, b.D((int)((imm >> 6) & 3)));
                    }
                    SetXmm(d, r);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------- integer

        private static long SignExtend(ulong v, int width)
        {
            switch (width)
            {
                case 1: return (sbyte)v;
                case 2: return (short)v;
                case 4: return (int)v;
                default: return (long)v;
            }
        }

        private static ulong Saturate(long v, int width, bool signed)
        {
            long min, max;
            if (signed)
            {
                max = width == 1 ? sbyte.MaxValue : width == 2 ? short.MaxValue : int.MaxValue;
                min = width == 1 ? sbyte.MinValue : width == 2 ? short.MinValue : int.MinValue;
            }
            else
            {
                max = width == 1 ? byte.MaxValue : width == 2 ? ushort.MaxValue : uint.MaxValue;
                min = 0;
            }
            return (ulong)Math.Max(min, Math.Min(max, v));
        }

        private delegate ulong LaneFn(ulong a, ulong b);

        private static V128 Lanes(V128 a, V128 b, int width, int bytes, LaneFn f)
        {
            var r = new V128();
            var n = bytes / width;
            var mask = width == 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1;
            for (var i = 0; i < n; i++) r.SetLane(width, i, f(a.Lane(width, i), b.Lane(width, i)) & mask);
            return r;
        }

        private static V128 Unpack(V128 a, V128 b, int width, int bytes, bool high)
        {
            var r = new V128();
            var n = bytes / width;
            var start = high ? n / 2 : 0;
            for (var i = 0; i < n / 2; i++)
            {
                r.SetLane(width, 2 * i, a.Lane(width, start + i));
                r.SetLane(width, 2 * i + 1, b.Lane(width, start + i));
            }
            return r;
        }

        private static V128 Pack(V128 a, V128 b, int fromWidth, int bytes, bool signed)
        {
            var r = new V128();
            var n = bytes / fromWidth;
            var to = fromWidth / 2;
            for (var i = 0; i < n; i++)
            {
                r.SetLane(to, i, Saturate(SignExtend(a.Lane(fromWidth, i), fromWidth), to, signed));
                r.SetLane(to, n + i, Saturate(SignExtend(b.Lane(fromWidth, i), fromWidth), to, signed));
            }
            return r;
        }

        private static V128 ShiftLanes(V128 a, int width, int bytes, ulong count, int kind)
        {
            var r = new V128();
            var n = bytes / width;
            var bits = width * 8;
            for (var i = 0; i < n; i++)
            {
                var v = a.Lane(width, i);
                ulong result;
                if (kind == 2) // arithmetic right
                {
                    var c = (int)Math.Min(count, (ulong)(bits - 1));
                    result = (ulong)(SignExtend(v, width) >> c);
                }
                else if (count >= (ulong)bits) result = 0;
                else result = kind == 0 ? v >> (int)count : v << (int)count;
                r.SetLane(width, i, result & (width == 8 ? ulong.MaxValue : (1UL << bits) - 1));
            }
            return r;
        }

        private static V128 ByteShift(V128 a, int count, bool left)
        {
            var r = new V128();
            if (count > 15) return r;
            for (var i = 0; i < 16; i++)
            {
                var from = left ? i - count : i + count;
                if (from >= 0 && from < 16) r.SetB(i, a.B(from));
            }
            return r;
        }

        private bool IntegerOp(in Instruction ins, int op, int pfx)
        {
            var xmm = pfx == 1;
            var bytes = xmm ? 16 : 8;
            var d = ins.RegField;

            // Moves and the few instructions whose operands are not plain "dest op= src".
            switch (op)
            {
                case 0x6E: // MOVD mm/xmm, r/m32
                {
                    var v = ins.Mod == 3 ? owner.GetReg(ins.Rm, 32) : Memory.Read32(Address(ins));
                    if (xmm) SetXmm(d, new V128(v, 0)); else SetMm(d, v);
                    return true;
                }
                case 0x7E:
                    if (pfx == 3) // MOVQ xmm, xmm/m64
                    {
                        var v = ins.Mod == 3 ? Xmm(ins.Rm).Lo : Memory.Read64(Address(ins));
                        SetXmm(d, new V128(v, 0));
                        return true;
                    }
                    {
                        var v = xmm ? (uint)Xmm(d).Lo : (uint)GetMm(d);
                        if (!xmm) EnterMmx();
                        if (ins.Mod == 3) owner.SetReg(ins.Rm, 32, v); else Memory.Write32(Address(ins), v);
                        return true;
                    }
                case 0x6F:
                    if (pfx == 1 || pfx == 3) { SetXmm(d, ReadXmmRm(ins)); return true; }
                    if (pfx == 2) return false;
                    SetMm(d, ReadMmRm(ins));
                    return true;
                case 0x7F:
                    if (pfx == 1 || pfx == 3) { WriteXmmRm(ins, Xmm(d)); return true; }
                    if (pfx == 2) return false;
                    if (ins.Mod == 3) SetMm(ins.Rm, GetMm(d)); else { Memory.Write64(Address(ins), GetMm(d)); EnterMmx(); }
                    return true;
                case 0xE7: // MOVNTQ / MOVNTDQ
                    if (ins.Mod == 3) return false;
                    if (xmm) WriteXmmRm(ins, Xmm(d)); else { Memory.Write64(Address(ins), GetMm(d)); EnterMmx(); }
                    return true;
                case 0xD6:
                    if (pfx == 1) // MOVQ xmm/m64, xmm
                    {
                        if (ins.Mod == 3) SetXmm(ins.Rm, new V128(Xmm(d).Lo, 0));
                        else Memory.Write64(Address(ins), Xmm(d).Lo);
                        return true;
                    }
                    if (ins.Mod != 3) return false;
                    if (pfx == 3) { SetXmm(d, new V128(GetMm(ins.Rm), 0)); return true; } // MOVQ2DQ
                    if (pfx == 2) { SetMm(d, Xmm(ins.Rm).Lo); return true; }             // MOVDQ2Q
                    return false;
                case 0x70:
                {
                    var imm = ins.Imm;
                    if (pfx == 0)
                    {
                        var src = new V128(ReadMmRm(ins), 0);
                        var r = new V128();
                        for (var i = 0; i < 4; i++) r.SetW(i, src.W((int)((imm >> (2 * i)) & 3)));
                        SetMm(d, r.Lo);
                        return true;
                    }
                    var s = ReadXmmRm(ins);
                    var o = s;
                    if (pfx == 1) for (var i = 0; i < 4; i++) o.SetD(i, s.D((int)((imm >> (2 * i)) & 3)));
                    else if (pfx == 3) for (var i = 0; i < 4; i++) o.SetW(4 + i, s.W(4 + (int)((imm >> (2 * i)) & 3)));
                    else for (var i = 0; i < 4; i++) o.SetW(i, s.W((int)((imm >> (2 * i)) & 3)));
                    SetXmm(d, o);
                    return true;
                }
                case 0x71: case 0x72: case 0x73:
                {
                    if (ins.Mod != 3) return false;
                    var target = ins.Rm;
                    var a = xmm ? Xmm(target) : new V128(GetMm(target), 0);
                    var width = op == 0x71 ? 2 : op == 0x72 ? 4 : 8;
                    V128 r;
                    switch (ins.RegField)
                    {
                        case 2: r = ShiftLanes(a, width, bytes, ins.Imm, 0); break;
                        case 4: if (op == 0x73) return false; r = ShiftLanes(a, width, bytes, ins.Imm, 2); break;
                        case 6: r = ShiftLanes(a, width, bytes, ins.Imm, 1); break;
                        case 3: if (!xmm || op != 0x73) return false; r = ByteShift(a, (int)ins.Imm, false); break;
                        case 7: if (!xmm || op != 0x73) return false; r = ByteShift(a, (int)ins.Imm, true); break;
                        default: return false;
                    }
                    if (xmm) SetXmm(target, r); else SetMm(target, r.Lo);
                    return true;
                }
                case 0xC4: // PINSRW
                {
                    var v = (ushort)(ins.Mod == 3 ? owner.GetReg(ins.Rm, 32) : Memory.Read16(Address(ins)));
                    if (xmm) { var x = Xmm(d); x.SetW((int)(ins.Imm & 7), v); SetXmm(d, x); }
                    else { var m = new V128(GetMm(d), 0); m.SetW((int)(ins.Imm & 3), v); SetMm(d, m.Lo); }
                    return true;
                }
                case 0xC5: // PEXTRW
                {
                    if (ins.Mod != 3) return false;
                    var v = xmm ? Xmm(ins.Rm).W((int)(ins.Imm & 7)) : new V128(GetMm(ins.Rm), 0).W((int)(ins.Imm & 3));
                    if (!xmm) EnterMmx();
                    owner.SetReg(d, 32, v);
                    return true;
                }
                case 0xD7: // PMOVMSKB
                {
                    if (ins.Mod != 3) return false;
                    var v = xmm ? Xmm(ins.Rm) : new V128(GetMm(ins.Rm), 0);
                    uint mask = 0;
                    for (var i = 0; i < bytes; i++) if ((v.B(i) & 0x80) != 0) mask |= 1u << i;
                    if (!xmm) EnterMmx();
                    owner.SetReg(d, 32, mask);
                    return true;
                }
                case 0xF7: // MASKMOVQ / MASKMOVDQU: byte-masked store to [EDI]
                {
                    if (ins.Mod != 3) return false;
                    var data = xmm ? Xmm(d) : new V128(GetMm(d), 0);
                    var mask = xmm ? Xmm(ins.Rm) : new V128(GetMm(ins.Rm), 0);
                    var at = owner.Cpu.Edi + owner.SegmentBase(ins.Segment);
                    for (var i = 0; i < bytes; i++) if ((mask.B(i) & 0x80) != 0) Memory.Write8(at + (uint)i, data.B(i));
                    if (!xmm) EnterMmx();
                    return true;
                }
                case 0xE6:
                {
                    var b = ReadXmmRm(ins);
                    var r = new V128();
                    if (pfx == 3) { r.SetDbl(0, (int)b.D(0)); r.SetDbl(1, (int)b.D(1)); }               // CVTDQ2PD
                    else if (pfx == 1 || pfx == 2)                                                        // CVTTPD2DQ / CVTPD2DQ
                    {
                        r.SetD(0, (uint)ConvertToInt(b.Dbl(0), pfx == 1, false));
                        r.SetD(1, (uint)ConvertToInt(b.Dbl(1), pfx == 1, false));
                    }
                    else return false;
                    SetXmm(d, r);
                    return true;
                }
            }

            if (pfx >= 2) return false;
            if ((op == 0x6C || op == 0x6D) && !xmm) return false;

            var a0 = xmm ? Xmm(d) : new V128(GetMm(d), 0);
            var b0 = xmm ? ReadXmmRm(ins) : new V128(ReadMmRm(ins), 0);
            V128 res;
            switch (op)
            {
                case 0x60: res = Unpack(a0, b0, 1, bytes, false); break;
                case 0x61: res = Unpack(a0, b0, 2, bytes, false); break;
                case 0x62: res = Unpack(a0, b0, 4, bytes, false); break;
                case 0x6C: res = Unpack(a0, b0, 8, bytes, false); break;
                case 0x68: res = Unpack(a0, b0, 1, bytes, true); break;
                case 0x69: res = Unpack(a0, b0, 2, bytes, true); break;
                case 0x6A: res = Unpack(a0, b0, 4, bytes, true); break;
                case 0x6D: res = Unpack(a0, b0, 8, bytes, true); break;
                case 0x63: res = Pack(a0, b0, 2, bytes, true); break;
                case 0x67: res = Pack(a0, b0, 2, bytes, false); break;
                case 0x6B: res = Pack(a0, b0, 4, bytes, true); break;
                case 0x64: case 0x65: case 0x66:
                {
                    var w = 1 << (op - 0x64);
                    res = Lanes(a0, b0, w, bytes, (x, y) => SignExtend(x, w) > SignExtend(y, w) ? ulong.MaxValue : 0);
                    break;
                }
                case 0x74: case 0x75: case 0x76:
                {
                    var w = 1 << (op - 0x74);
                    res = Lanes(a0, b0, w, bytes, (x, y) => x == y ? ulong.MaxValue : 0);
                    break;
                }
                case 0xD1: case 0xD2: case 0xD3:
                    res = ShiftLanes(a0, op == 0xD1 ? 2 : op == 0xD2 ? 4 : 8, bytes, b0.Lo, 0); break;
                case 0xE1: case 0xE2:
                    res = ShiftLanes(a0, op == 0xE1 ? 2 : 4, bytes, b0.Lo, 2); break;
                case 0xF1: case 0xF2: case 0xF3:
                    res = ShiftLanes(a0, op == 0xF1 ? 2 : op == 0xF2 ? 4 : 8, bytes, b0.Lo, 1); break;
                case 0xD4: res = Lanes(a0, b0, 8, bytes, (x, y) => x + y); break;
                case 0xFB: res = Lanes(a0, b0, 8, bytes, (x, y) => x - y); break;
                case 0xFC: case 0xFD: case 0xFE:
                    res = Lanes(a0, b0, 1 << (op - 0xFC), bytes, (x, y) => x + y); break;
                case 0xF8: case 0xF9: case 0xFA:
                    res = Lanes(a0, b0, 1 << (op - 0xF8), bytes, (x, y) => x - y); break;
                case 0xEC: case 0xED:
                {
                    var w = op == 0xEC ? 1 : 2;
                    res = Lanes(a0, b0, w, bytes, (x, y) => Saturate(SignExtend(x, w) + SignExtend(y, w), w, true));
                    break;
                }
                case 0xE8: case 0xE9:
                {
                    var w = op == 0xE8 ? 1 : 2;
                    res = Lanes(a0, b0, w, bytes, (x, y) => Saturate(SignExtend(x, w) - SignExtend(y, w), w, true));
                    break;
                }
                case 0xDC: case 0xDD:
                {
                    var w = op == 0xDC ? 1 : 2;
                    res = Lanes(a0, b0, w, bytes, (x, y) => Saturate((long)(x + y), w, false));
                    break;
                }
                case 0xD8: case 0xD9:
                {
                    var w = op == 0xD8 ? 1 : 2;
                    res = Lanes(a0, b0, w, bytes, (x, y) => Saturate((long)x - (long)y, w, false));
                    break;
                }
                case 0xD5: res = Lanes(a0, b0, 2, bytes, (x, y) => (ulong)((short)x * (short)y)); break;
                case 0xE5: res = Lanes(a0, b0, 2, bytes, (x, y) => (ulong)(((short)x * (short)y) >> 16)); break;
                case 0xE4: res = Lanes(a0, b0, 2, bytes, (x, y) => (x * y) >> 16); break;
                case 0xF4:
                {
                    res = new V128();
                    res.Lo = (ulong)a0.D(0) * b0.D(0);
                    if (xmm) res.Hi = (ulong)a0.D(2) * b0.D(2);
                    break;
                }
                case 0xF5:
                {
                    res = new V128();
                    for (var i = 0; i < bytes / 4; i++)
                    {
                        var sum = (short)a0.W(2 * i) * (short)b0.W(2 * i) + (short)a0.W(2 * i + 1) * (short)b0.W(2 * i + 1);
                        res.SetD(i, (uint)sum);
                    }
                    break;
                }
                case 0xF6:
                {
                    res = new V128();
                    for (var half = 0; half < bytes / 8; half++)
                    {
                        uint sum = 0;
                        for (var i = 0; i < 8; i++) sum += (uint)Math.Abs(a0.B(half * 8 + i) - b0.B(half * 8 + i));
                        res.SetQ(half, sum);
                    }
                    break;
                }
                case 0xDA: res = Lanes(a0, b0, 1, bytes, (x, y) => Math.Min(x, y)); break;
                case 0xDE: res = Lanes(a0, b0, 1, bytes, (x, y) => Math.Max(x, y)); break;
                case 0xEA: res = Lanes(a0, b0, 2, bytes, (x, y) => (short)x < (short)y ? x : y); break;
                case 0xEE: res = Lanes(a0, b0, 2, bytes, (x, y) => (short)x > (short)y ? x : y); break;
                case 0xE0: res = Lanes(a0, b0, 1, bytes, (x, y) => (x + y + 1) >> 1); break;
                case 0xE3: res = Lanes(a0, b0, 2, bytes, (x, y) => (x + y + 1) >> 1); break;
                case 0xDB: res = new V128(a0.Lo & b0.Lo, a0.Hi & b0.Hi); break;
                case 0xDF: res = new V128(~a0.Lo & b0.Lo, ~a0.Hi & b0.Hi); break;
                case 0xEB: res = new V128(a0.Lo | b0.Lo, a0.Hi | b0.Hi); break;
                case 0xEF: res = new V128(a0.Lo ^ b0.Lo, a0.Hi ^ b0.Hi); break;
                default: return false;
            }
            if (xmm) SetXmm(d, res); else SetMm(d, res.Lo);
            return true;
        }
    }
}
