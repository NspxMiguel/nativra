using System;
using System.Diagnostics;

namespace Nativra.X86.Cpu
{
    public sealed partial class Interpreter
    {
        /// <summary>
        /// What CPUID reports. Both engines honour it: the interpreter executes
        /// every feature listed here, and the JIT only ever sees code that
        /// checked for these. SSE3 and later stay off until the interpreter
        /// covers them, because a guest that reads a bit and then hits the
        /// fallback path with an instruction it cannot run would be worse than
        /// one that takes its older path from the start.
        /// </summary>
        public static class CpuidFeatures
        {
            // FPU TSC CX8 CMOV CLFSH MMX FXSR SSE SSE2
            public const uint Edx1 = (1u << 0) | (1u << 4) | (1u << 8) | (1u << 15) | (1u << 19) |
                                     (1u << 23) | (1u << 24) | (1u << 25) | (1u << 26);
            public const uint Ecx1 = 0;
        }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public void Cpuid()
        {
            var leaf = Cpu.Eax;
            uint a = 0, b = 0, c = 0, d = 0;
            switch (leaf)
            {
                case 0:
                    a = 1;
                    // "GenuineIntel": older engines assume 3DNow! on anything else.
                    b = 0x756E6547; d = 0x49656E69; c = 0x6C65746E;
                    break;
                case 1:
                    a = 0x000106A5;                // family 6, a generic SSE2-era model
                    b = (8u << 8) | (1u << 16);    // CLFLUSH line 64 bytes, one logical processor
                    c = CpuidFeatures.Ecx1;
                    d = CpuidFeatures.Edx1;
                    break;
                case 0x80000000:
                    a = 0x80000004;
                    break;
                case 0x80000002: case 0x80000003: case 0x80000004:
                {
                    var brand = "Nativra x86 on x64".PadRight(48, '\0');
                    var offset = (int)(leaf - 0x80000002) * 16;
                    uint Word(int at) => (uint)(brand[offset + at] | (brand[offset + at + 1] << 8) |
                                               (brand[offset + at + 2] << 16) | (brand[offset + at + 3] << 24));
                    a = Word(0); b = Word(4); c = Word(8); d = Word(12);
                    break;
                }
            }
            Cpu.Eax = a; Cpu.Ebx = b; Cpu.Ecx = c; Cpu.Edx = d;
        }

        private void ExecuteTwoByte(in Instruction ins, int op, int size, uint next)
        {
            if (op >= 0x80 && op <= 0x8F) { if (Condition(op - 0x80)) Cpu.Eip = next + ins.Imm; return; }
            if (op >= 0x40 && op <= 0x4F)
            {
                var v = ReadRm(ins, size);
                if (Condition(op - 0x40)) SetReg(ins.RegField, size, v);
                return;
            }
            if (op >= 0x90 && op <= 0x9F) { WriteRm(ins, 8, Condition(op - 0x90) ? 1u : 0); return; }
            if (op >= 0xC8 && op <= 0xCF)
            {
                var r = op - 0xC8;
                var v = Cpu.R[r];
                Cpu.R[r] = (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
                return;
            }

            switch (op)
            {
                case 0x00: // SLDT/STR/LLDT/LTR/VERR/VERW
                    if (ins.RegField <= 1) { WriteRm(ins, ins.Mod == 3 ? size : 16, ins.RegField == 1 ? 0x40u : 0); return; }
                    if (ins.RegField >= 4) { ReadRm(ins, 16); Cpu.EFlags |= Flag.ZF; return; }
                    throw new GuestException(GuestException.PrivilegedInstruction, ins.Address);
                case 0x01:
                    if (ins.Mod == 3)
                    {
                        if (ins.RegField == 2 && ins.Rm == 0) { Cpu.Eax = 3; Cpu.Edx = 0; return; } // XGETBV: x87 + SSE
                        if (ins.RegField == 4) { SetReg(ins.Rm, size, 0x0033); return; }          // SMSW
                        throw new GuestException(GuestException.PrivilegedInstruction, ins.Address);
                    }
                    switch (ins.RegField)
                    {
                        case 0: case 1: // SGDT/SIDT: six bytes in 32-bit mode
                        {
                            var at = LinearAddress(ins);
                            Memory.Write16(at, 0x7F);
                            Memory.Write32(at + 2, ins.RegField == 0 ? 0x80B95000u : 0x80B95400u);
                            return;
                        }
                        case 4: WriteMem(LinearAddress(ins), 16, 0x0033); return;
                        case 7: return; // INVLPG would fault in user mode; CLFLUSHOPT-like no-op is harmless here
                    }
                    throw new GuestException(GuestException.PrivilegedInstruction, ins.Address);
                case 0x0B: case 0xB9: case 0xFF:
                    throw new GuestException(GuestException.IllegalInstruction, ins.Address);
                case 0x0D: case 0x18: case 0x19: case 0x1A: case 0x1B: case 0x1C: case 0x1D: case 0x1E: case 0x1F:
                    return; // prefetch hints and multi-byte NOP
                case 0x31:
                {
                    var ticks = (ulong)Clock.ElapsedTicks;
                    Cpu.Eax = (uint)ticks;
                    Cpu.Edx = (uint)(ticks >> 32);
                    return;
                }
                case 0xA0: Push(SegmentSelector(Seg.Fs), size); return;
                case 0xA1: Pop(size); return;
                case 0xA8: Push(SegmentSelector(Seg.Gs), size); return;
                case 0xA9: Pop(size); return;
                case 0xA2: Cpuid(); return;
                case 0xA3: case 0xAB: case 0xB3: case 0xBB:
                    BitTest(ins, size, (op >> 3) & 3, GetReg(ins.RegField, size), true);
                    return;
                case 0xBA:
                    if (ins.RegField < 4) throw new GuestException(GuestException.IllegalInstruction, ins.Address);
                    BitTest(ins, size, ins.RegField - 4, ins.Imm, false);
                    return;
                case 0xA4: case 0xA5: case 0xAC: case 0xAD:
                {
                    var count = (op & 1) == 0 ? ins.Imm : Cpu.Ecx;
                    WriteRm(ins, size, DoubleShift(ReadRm(ins, size), GetReg(ins.RegField, size), count, size, op < 0xAC));
                    return;
                }
                case 0xAF: SetReg(ins.RegField, size, IMul2(GetReg(ins.RegField, size), ReadRm(ins, size), size)); return;
                case 0xB0: case 0xB1:
                {
                    var width = op == 0xB0 ? 8 : size;
                    var dest = ReadRm(ins, width);
                    var acc = GetReg(Reg.Eax, width);
                    Alu(7, acc, dest, width);
                    if ((Cpu.EFlags & Flag.ZF) != 0) WriteRm(ins, width, GetReg(ins.RegField, width));
                    else
                    {
                        // Real hardware writes the destination back either way.
                        WriteRm(ins, width, dest);
                        SetReg(Reg.Eax, width, dest);
                    }
                    return;
                }
                case 0xB6: SetReg(ins.RegField, size, ReadRm(ins, 8)); return;
                case 0xB7: SetReg(ins.RegField, size, ReadRm(ins, 16)); return;
                case 0xBE: SetReg(ins.RegField, size, (uint)(sbyte)ReadRm(ins, 8)); return;
                case 0xBF: SetReg(ins.RegField, size, (uint)(short)ReadRm(ins, 16)); return;
                case 0xB8:
                    if (ins.Rep != 0xF3) throw new GuestException(GuestException.IllegalInstruction, ins.Address);
                    {
                        var v = ReadRm(ins, size);
                        var count = (uint)Bits.PopCount(v);
                        SetReg(ins.RegField, size, count);
                        Cpu.EFlags &= ~Flag.Arith;
                        if (v == 0) Cpu.EFlags |= Flag.ZF;
                    }
                    return;
                case 0xBC: case 0xBD:
                {
                    var v = ReadRm(ins, size);
                    if (ins.Rep == 0xF3)
                    {
                        // TZCNT / LZCNT: what the hardware under the JIT does.
                        uint count;
                        if (op == 0xBC) count = v == 0 ? (uint)size : (uint)Bits.TrailingZeros(v);
                        else count = v == 0 ? (uint)size
                            : (uint)(Bits.LeadingZeros(v) - (32 - size));
                        SetReg(ins.RegField, size, count);
                        SetFlags(v == 0, (Cpu.EFlags & Flag.OF) != 0, (Cpu.EFlags & Flag.AF) != 0);
                        Cpu.SetFlag(Flag.ZF, count == 0);
                        return;
                    }
                    if (v == 0) { Cpu.EFlags |= Flag.ZF; return; }
                    Cpu.EFlags &= ~Flag.ZF;
                    SetReg(ins.RegField, size, op == 0xBC
                        ? (uint)Bits.TrailingZeros(v)
                        : (uint)(31 - Bits.LeadingZeros(v)));
                    return;
                }
                case 0xC0: case 0xC1:
                {
                    var width = op == 0xC0 ? 8 : size;
                    var dest = ReadRm(ins, width);
                    var src = GetReg(ins.RegField, width);
                    var sum = Alu(0, dest, src, width);
                    SetReg(ins.RegField, width, dest);
                    WriteRm(ins, width, sum);
                    return;
                }
                case 0xC7:
                    if (ins.RegField == 1 && ins.Mod != 3)
                    {
                        var at = LinearAddress(ins);
                        var current = Memory.Read64(at);
                        var expected = ((ulong)Cpu.Edx << 32) | Cpu.Eax;
                        if (current == expected)
                        {
                            Memory.Write64(at, ((ulong)Cpu.Ecx << 32) | Cpu.Ebx);
                            Cpu.EFlags |= Flag.ZF;
                        }
                        else
                        {
                            Cpu.Eax = (uint)current;
                            Cpu.Edx = (uint)(current >> 32);
                            Cpu.EFlags &= ~Flag.ZF;
                        }
                        return;
                    }
                    throw new GuestException(GuestException.IllegalInstruction, ins.Address);
                case 0x05: case 0x07: case 0x34: case 0x35:
                    throw new GuestException(GuestException.IllegalInstruction, ins.Address);
            }

            if (!Fpu.ExecuteSse(ins)) throw new GuestException(GuestException.IllegalInstruction, ins.Address);
        }

        /// <summary>BT/BTS/BTR/BTC. A register bit offset may reach past a memory operand.</summary>
        private void BitTest(in Instruction ins, int size, int kind, uint offset, bool registerOffset)
        {
            uint value, bit;
            uint address = 0;
            if (ins.Mod == 3)
            {
                bit = offset & (uint)(size - 1);
                value = GetReg(ins.Rm, size);
            }
            else
            {
                address = LinearAddress(ins);
                if (registerOffset)
                {
                    var signed = size == 16 ? (int)(short)offset : (int)offset;
                    var unit = size / 8;
                    address += (uint)((signed >> (size == 16 ? 4 : 5)) * unit);
                    bit = (uint)signed & (uint)(size - 1);
                }
                else
                {
                    bit = offset & (uint)(size - 1);
                }
                value = ReadMem(address, size);
            }

            Cpu.SetFlag(Flag.CF, ((value >> (int)bit) & 1) != 0);
            if (kind == 0) return;
            switch (kind)
            {
                case 1: value |= 1u << (int)bit; break;
                case 2: value &= ~(1u << (int)bit); break;
                default: value ^= 1u << (int)bit; break;
            }
            if (ins.Mod == 3) SetReg(ins.Rm, size, value);
            else WriteMem(address, size, value);
        }

        private uint DoubleShift(uint dest, uint source, uint count, int size, bool left)
        {
            count &= 0x1F;
            if (count == 0) return dest & Mask(size);
            var mask = Mask(size);
            var sign = Sign(size);
            dest &= mask;
            source &= mask;
            uint r;
            bool cf;
            if (size == 32)
            {
                if (left)
                {
                    var wide = ((ulong)dest << 32) | source;
                    r = (uint)((wide << (int)count) >> 32);
                    cf = ((dest >> (32 - (int)count)) & 1) != 0;
                }
                else
                {
                    var wide = ((ulong)source << 32) | dest;
                    r = (uint)(wide >> (int)count);
                    cf = ((dest >> ((int)count - 1)) & 1) != 0;
                }
            }
            else
            {
                // 16-bit: counts above 16 are architecturally undefined; this
                // follows the shift of the 32-bit concatenation.
                var joined = left ? ((ulong)dest << 16) | source : ((ulong)source << 16) | dest;
                if (left)
                {
                    r = (uint)((joined << (int)count) >> 16) & mask;
                    cf = ((joined >> (32 - (int)count)) & 1) != 0;
                }
                else
                {
                    r = (uint)(joined >> (int)count) & mask;
                    cf = ((joined >> ((int)count - 1)) & 1) != 0;
                }
            }
            SetFlags(cf, ((r ^ dest) & sign) != 0, false);
            SetSzp(r, size);
            return r;
        }
    }
}
