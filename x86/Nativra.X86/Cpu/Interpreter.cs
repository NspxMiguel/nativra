using System;

namespace Nativra.X86.Cpu
{
    /// <summary>
    /// A straightforward interpreter for 32-bit x86 integer code. It is the
    /// reference the JIT is checked against and the fallback for anything the
    /// JIT does not translate yet, so it favours being obviously correct over
    /// being fast: flags are computed eagerly, one instruction per call.
    ///
    /// Floating point (x87, SSE) lives in <see cref="FpuUnit"/>, reached from
    /// the escapes and the 0F map.
    /// </summary>
    public sealed partial class Interpreter
    {
        public CpuState Cpu { get; }
        public GuestMemory Memory { get; }

        /// <summary>Guest GS base; Win32 code never sets it, but an override is still honoured.</summary>
        public uint GsBase;

        /// <summary>x87 and SSE state, created on first floating-point instruction.</summary>
        public FpuUnit Fpu { get; }

        public Interpreter(CpuState cpu, GuestMemory memory)
        {
            Cpu = cpu;
            Memory = memory;
            Fpu = new FpuUnit(this);
        }

        // ------------------------------------------------------------ registers

        public uint GetReg(int reg, int size)
        {
            switch (size)
            {
                case 32: return Cpu.R[reg];
                case 16: return Cpu.R[reg] & 0xFFFF;
                default:
                    return reg < 4 ? Cpu.R[reg] & 0xFF : (Cpu.R[reg - 4] >> 8) & 0xFF;
            }
        }

        public void SetReg(int reg, int size, uint value)
        {
            switch (size)
            {
                case 32: Cpu.R[reg] = value; break;
                case 16: Cpu.R[reg] = (Cpu.R[reg] & 0xFFFF0000) | (value & 0xFFFF); break;
                default:
                    if (reg < 4) Cpu.R[reg] = (Cpu.R[reg] & 0xFFFFFF00) | (value & 0xFF);
                    else Cpu.R[reg - 4] = (Cpu.R[reg - 4] & 0xFFFF00FF) | ((value & 0xFF) << 8);
                    break;
            }
        }

        // -------------------------------------------------------------- memory

        public uint EffectiveAddress(in Instruction ins)
        {
            uint ea = ins.Disp;
            if (ins.Base >= 0) ea += Cpu.R[ins.Base];
            if (ins.Index >= 0) ea += Cpu.R[ins.Index] * (uint)ins.Scale;
            return ea;
        }

        /// <summary>The linear address of a memory operand, segment base included.</summary>
        public uint LinearAddress(in Instruction ins) => EffectiveAddress(ins) + SegmentBase(ins.Segment);

        public uint SegmentBase(int segment) =>
            segment == Seg.Fs ? Cpu.FsBase : segment == Seg.Gs ? GsBase : 0;

        public uint ReadMem(uint address, int size)
        {
            switch (size)
            {
                case 32: return Memory.Read32(address);
                case 16: return Memory.Read16(address);
                default: return Memory.Read8(address);
            }
        }

        public void WriteMem(uint address, int size, uint value)
        {
            switch (size)
            {
                case 32: Memory.Write32(address, value); break;
                case 16: Memory.Write16(address, (ushort)value); break;
                default: Memory.Write8(address, (byte)value); break;
            }
        }

        private uint ReadRm(in Instruction ins, int size) =>
            ins.Mod == 3 ? GetReg(ins.Rm, size) : ReadMem(LinearAddress(ins), size);

        private void WriteRm(in Instruction ins, int size, uint value)
        {
            if (ins.Mod == 3) SetReg(ins.Rm, size, value);
            else WriteMem(LinearAddress(ins), size, value);
        }

        public void Push32(uint value)
        {
            var esp = Cpu.Esp - 4;
            Memory.Write32(esp, value);
            Cpu.Esp = esp;
        }

        public uint Pop32()
        {
            var value = Memory.Read32(Cpu.Esp);
            Cpu.Esp += 4;
            return value;
        }

        private void Push(uint value, int size)
        {
            if (size == 16)
            {
                var esp = Cpu.Esp - 2;
                Memory.Write16(esp, (ushort)value);
                Cpu.Esp = esp;
            }
            else
            {
                Push32(value);
            }
        }

        private uint Pop(int size)
        {
            if (size == 16)
            {
                var value = Memory.Read16(Cpu.Esp);
                Cpu.Esp += 2;
                return value;
            }
            return Pop32();
        }

        // --------------------------------------------------------------- flags

        private static uint Mask(int size) => size == 32 ? 0xFFFFFFFF : size == 16 ? 0xFFFFu : 0xFFu;

        private static uint Sign(int size) => 1u << (size - 1);

        private static bool Parity(uint value)
        {
            value &= 0xFF;
            value ^= value >> 4;
            value ^= value >> 2;
            value ^= value >> 1;
            return (value & 1) == 0;
        }

        private void SetSzp(uint result, int size)
        {
            var f = Cpu.EFlags & ~(Flag.ZF | Flag.SF | Flag.PF);
            if ((result & Mask(size)) == 0) f |= Flag.ZF;
            if ((result & Sign(size)) != 0) f |= Flag.SF;
            if (Parity(result)) f |= Flag.PF;
            Cpu.EFlags = f;
        }

        private void SetFlags(bool cf, bool of, bool af)
        {
            var f = Cpu.EFlags & ~(Flag.CF | Flag.OF | Flag.AF);
            if (cf) f |= Flag.CF;
            if (of) f |= Flag.OF;
            if (af) f |= Flag.AF;
            Cpu.EFlags = f;
        }

        private bool CF => (Cpu.EFlags & Flag.CF) != 0;

        /// <summary>The eight classic ALU operations, with flags. CMP returns the difference.</summary>
        public uint Alu(int op, uint a, uint b, int size)
        {
            var mask = Mask(size);
            var sign = Sign(size);
            a &= mask;
            b &= mask;
            uint r;
            switch (op)
            {
                case 0: // ADD
                case 2: // ADC
                {
                    var carry = op == 2 && CF ? 1u : 0u;
                    var wide = (ulong)a + b + carry;
                    r = (uint)wide & mask;
                    SetFlags(wide > mask, ((a ^ r) & (b ^ r) & sign) != 0, ((a ^ b ^ r) & 0x10) != 0);
                    break;
                }
                case 3: // SBB
                case 5: // SUB
                case 7: // CMP
                {
                    var borrow = op == 3 && CF ? 1u : 0u;
                    r = (a - b - borrow) & mask;
                    SetFlags((ulong)a < (ulong)b + borrow, ((a ^ b) & (a ^ r) & sign) != 0, ((a ^ b ^ r) & 0x10) != 0);
                    break;
                }
                case 1: r = a | b; SetFlags(false, false, false); break;
                case 4: r = a & b; SetFlags(false, false, false); break;
                default: r = a ^ b; SetFlags(false, false, false); break;
            }
            SetSzp(r, size);
            return r;
        }

        private uint IncDec(uint a, int size, bool dec)
        {
            var mask = Mask(size);
            var sign = Sign(size);
            a &= mask;
            var r = (dec ? a - 1 : a + 1) & mask;
            var of = dec ? a == sign : r == sign;
            var keepCf = CF;
            SetFlags(keepCf, of, ((a ^ 1 ^ r) & 0x10) != 0);
            SetSzp(r, size);
            return r;
        }

        public bool Condition(int cc)
        {
            var f = Cpu.EFlags;
            bool cf = (f & Flag.CF) != 0, zf = (f & Flag.ZF) != 0, sf = (f & Flag.SF) != 0;
            bool of = (f & Flag.OF) != 0, pf = (f & Flag.PF) != 0;
            bool result;
            switch (cc >> 1)
            {
                case 0: result = of; break;
                case 1: result = cf; break;
                case 2: result = zf; break;
                case 3: result = cf || zf; break;
                case 4: result = sf; break;
                case 5: result = pf; break;
                case 6: result = sf != of; break;
                default: result = zf || sf != of; break;
            }
            return (cc & 1) != 0 ? !result : result;
        }

        // --------------------------------------------------------------- shifts

        private uint Shift(int op, uint a, uint count, int size)
        {
            var mask = Mask(size);
            var sign = Sign(size);
            a &= mask;
            count &= 0x1F;
            if (count == 0) return a;
            uint r;
            bool cf;
            switch (op)
            {
                case 0: // ROL
                {
                    var n = (int)(count % (uint)size);
                    r = n == 0 ? a : ((a << n) | (a >> (size - n))) & mask;
                    cf = (r & 1) != 0;
                    SetFlags(cf, ((r & sign) != 0) ^ cf, (Cpu.EFlags & Flag.AF) != 0);
                    return r;
                }
                case 1: // ROR
                {
                    var n = (int)(count % (uint)size);
                    r = n == 0 ? a : ((a >> n) | (a << (size - n))) & mask;
                    cf = (r & sign) != 0;
                    SetFlags(cf, ((r ^ (r << 1)) & sign) != 0, (Cpu.EFlags & Flag.AF) != 0);
                    return r;
                }
                case 2: // RCL
                {
                    var n = (int)(count % (uint)(size + 1));
                    var wide = ((ulong)(CF ? 1u : 0u) << size) | a;
                    for (var i = 0; i < n; i++)
                    {
                        var top = (wide >> size) & 1;
                        wide = ((wide << 1) | top) & ((1UL << (size + 1)) - 1);
                    }
                    r = (uint)wide & mask;
                    cf = ((wide >> size) & 1) != 0;
                    SetFlags(cf, ((r & sign) != 0) ^ cf, (Cpu.EFlags & Flag.AF) != 0);
                    return r;
                }
                case 3: // RCR
                {
                    var n = (int)(count % (uint)(size + 1));
                    var wide = ((ulong)(CF ? 1u : 0u) << size) | a;
                    var of = (((a & sign) != 0) ^ CF);
                    for (var i = 0; i < n; i++)
                    {
                        var low = wide & 1;
                        wide = (wide >> 1) | (low << size);
                    }
                    r = (uint)wide & mask;
                    cf = ((wide >> size) & 1) != 0;
                    SetFlags(cf, of, (Cpu.EFlags & Flag.AF) != 0);
                    return r;
                }
                case 4: // SHL
                case 6: // SAL
                {
                    r = count >= 32 ? 0 : (uint)(((ulong)a << (int)count) & mask);
                    cf = count <= (uint)size && ((a >> (size - (int)count)) & 1) != 0;
                    SetFlags(cf, ((r & sign) != 0) ^ cf, false);
                    SetSzp(r, size);
                    return r;
                }
                case 5: // SHR
                {
                    r = count >= 32 ? 0 : (a >> (int)count) & mask;
                    cf = ((a >> ((int)count - 1)) & 1) != 0;
                    SetFlags(cf, (a & sign) != 0, false);
                    SetSzp(r, size);
                    return r;
                }
                default: // SAR
                {
                    var signed = (long)(size == 32 ? (int)a : size == 16 ? (short)a : (sbyte)a);
                    var n = (int)Math.Min(count, (uint)size);
                    r = (uint)(signed >> n) & mask;
                    cf = ((signed >> (n - 1)) & 1) != 0;
                    SetFlags(cf, false, false);
                    SetSzp(r, size);
                    return r;
                }
            }
        }

        // ------------------------------------------------------------ execution

        private static int OperandSize(in Instruction ins) => ins.OpSize16 ? 16 : 32;

        /// <summary>
        /// Executes the instruction at EIP and advances. Guest-visible faults
        /// surface as <see cref="GuestException"/> with EIP still at the
        /// faulting instruction, which is what the guest's handlers expect.
        /// </summary>
        public void Step()
        {
            var start = Cpu.Eip;
            Instruction ins;
            try
            {
                ins = Decoder.Decode(Memory, start);
            }
            catch (GuestFaultException fault)
            {
                throw new GuestException(GuestException.AccessViolation, start, 8, fault.Address);
            }
            if (!ins.Valid) throw new GuestException(GuestException.IllegalInstruction, start);

            try
            {
                Execute(ins);
            }
            catch (GuestFaultException fault)
            {
                Cpu.Eip = start;
                throw new GuestException(GuestException.AccessViolation, start, fault.Write ? 1u : 0u, fault.Address);
            }
            catch (GuestException)
            {
                Cpu.Eip = start;
                throw;
            }
            Cpu.Retired++;
        }

        /// <summary>Runs until EIP reaches <paramref name="stop"/> or the step budget runs out.</summary>
        public bool RunUntil(uint stop, long maxSteps = 100_000_000)
        {
            for (long i = 0; i < maxSteps; i++)
            {
                if (Cpu.Eip == stop) return true;
                Step();
            }
            return Cpu.Eip == stop;
        }

        public void Execute(in Instruction ins)
        {
            var next = ins.Next;
            var size = OperandSize(ins);
            var op = ins.Op;
            Cpu.Eip = next;

            if (op < 0x100)
            {
                ExecuteOneByte(ins, op, size, next);
            }
            else if (op < 0x10000)
            {
                ExecuteTwoByte(ins, op & 0xFF, size, next);
            }
            else if ((op & 0xFFFF00) == 0x0F3800 || (op & 0xFFFF00) == 0x0F3A00)
            {
                if (!Fpu.ExecuteSse(ins)) throw new GuestException(GuestException.IllegalInstruction, ins.Address);
            }
            else
            {
                throw new GuestException(GuestException.IllegalInstruction, ins.Address);
            }
        }

        private void ExecuteOneByte(in Instruction ins, int op, int size, uint next)
        {
            // The ALU block: 00-3F, except the segment/BCD opcodes at x6/x7/xE/xF.
            if (op < 0x40 && (op & 7) < 6)
            {
                var alu = op >> 3;
                var width = (op & 1) == 0 ? 8 : size;
                uint a, b;
                switch (op & 7)
                {
                    case 0: case 1:
                        a = ReadRm(ins, width); b = GetReg(ins.RegField, width);
                        var r0 = Alu(alu, a, b, width);
                        if (alu != 7) WriteRm(ins, width, r0);
                        return;
                    case 2: case 3:
                        a = GetReg(ins.RegField, width); b = ReadRm(ins, width);
                        var r1 = Alu(alu, a, b, width);
                        if (alu != 7) SetReg(ins.RegField, width, r1);
                        return;
                    case 4:
                        var r2 = Alu(alu, GetReg(Reg.Eax, 8), ins.Imm, 8);
                        if (alu != 7) SetReg(Reg.Eax, 8, r2);
                        return;
                    default:
                        var r3 = Alu(alu, GetReg(Reg.Eax, size), ins.Imm, size);
                        if (alu != 7) SetReg(Reg.Eax, size, r3);
                        return;
                }
            }

            switch (op)
            {
                case 0x06: case 0x0E: case 0x16: case 0x1E:
                    Push(SegmentSelector((op >> 3) & 3), size); return;
                case 0x07: case 0x17: case 0x1F:
                    Pop(size); return;
                case 0x27: Daa(); return;
                case 0x2F: Das(); return;
                case 0x37: Aaa(); return;
                case 0x3F: Aas(); return;
            }

            if (op >= 0x40 && op <= 0x47) { SetReg(op - 0x40, size, IncDec(GetReg(op - 0x40, size), size, false)); return; }
            if (op >= 0x48 && op <= 0x4F) { SetReg(op - 0x48, size, IncDec(GetReg(op - 0x48, size), size, true)); return; }
            if (op >= 0x50 && op <= 0x57) { Push(GetReg(op - 0x50, size), size); return; }
            if (op >= 0x58 && op <= 0x5F) { SetReg(op - 0x58, size, Pop(size)); return; }
            if (op >= 0x70 && op <= 0x7F) { if (Condition(op - 0x70)) Cpu.Eip = next + (uint)(sbyte)ins.Imm; return; }
            if (op >= 0x91 && op <= 0x97)
            {
                var r = op - 0x90;
                var t = GetReg(Reg.Eax, size);
                SetReg(Reg.Eax, size, GetReg(r, size));
                SetReg(r, size, t);
                return;
            }
            if (op >= 0xB0 && op <= 0xB7) { SetReg(op - 0xB0, 8, ins.Imm); return; }
            if (op >= 0xB8 && op <= 0xBF) { SetReg(op - 0xB8, size, ins.Imm); return; }
            if (op >= 0xD8 && op <= 0xDF)
            {
                if (!Fpu.ExecuteX87(ins)) throw new GuestException(GuestException.IllegalInstruction, ins.Address);
                return;
            }

            switch (op)
            {
                case 0x60: // PUSHAD
                {
                    var esp = Cpu.Esp;
                    for (var r = 0; r < 8; r++) Push(r == Reg.Esp ? esp : GetReg(r, size), size);
                    return;
                }
                case 0x61: // POPAD
                    for (var r = 7; r >= 0; r--)
                    {
                        var v = Pop(size);
                        if (r != Reg.Esp) SetReg(r, size, v);
                    }
                    return;
                case 0x62: // BOUND
                {
                    var index = (int)GetReg(ins.RegField, size);
                    var at = LinearAddress(ins);
                    int lower, upper;
                    if (size == 16) { lower = (short)Memory.Read16(at); upper = (short)Memory.Read16(at + 2); index = (short)index; }
                    else { lower = (int)Memory.Read32(at); upper = (int)Memory.Read32(at + 4); }
                    if (index < lower || index > upper)
                        throw new GuestException(GuestException.ArrayBoundsExceeded, ins.Address);
                    return;
                }
                case 0x68: Push(ins.Imm, size); return;
                case 0x6A: Push((uint)(sbyte)ins.Imm, size); return;
                case 0x69:
                case 0x6B:
                {
                    var imm = op == 0x6B ? (uint)(sbyte)ins.Imm : ins.Imm;
                    SetReg(ins.RegField, size, IMul2(ReadRm(ins, size), imm, size));
                    return;
                }
                case 0x80: case 0x81: case 0x82: case 0x83:
                {
                    var width = op == 0x80 || op == 0x82 ? 8 : size;
                    var imm = op == 0x83 ? (uint)(sbyte)ins.Imm : ins.Imm;
                    var r = Alu(ins.RegField, ReadRm(ins, width), imm, width);
                    if (ins.RegField != 7) WriteRm(ins, width, r);
                    return;
                }
                case 0x84: case 0x85:
                {
                    var width = op == 0x84 ? 8 : size;
                    Alu(4, ReadRm(ins, width), GetReg(ins.RegField, width), width);
                    return;
                }
                case 0x86: case 0x87:
                {
                    var width = op == 0x86 ? 8 : size;
                    var a = ReadRm(ins, width);
                    WriteRm(ins, width, GetReg(ins.RegField, width));
                    SetReg(ins.RegField, width, a);
                    return;
                }
                case 0x88: WriteRm(ins, 8, GetReg(ins.RegField, 8)); return;
                case 0x89: WriteRm(ins, size, GetReg(ins.RegField, size)); return;
                case 0x8A: SetReg(ins.RegField, 8, ReadRm(ins, 8)); return;
                case 0x8B: SetReg(ins.RegField, size, ReadRm(ins, size)); return;
                case 0x8C: // MOV r/m16, Sreg
                    if (ins.Mod == 3) SetReg(ins.Rm, size, SegmentSelector(ins.RegField));
                    else WriteMem(LinearAddress(ins), 16, SegmentSelector(ins.RegField));
                    return;
                case 0x8D:
                    if (ins.Mod == 3) throw new GuestException(GuestException.IllegalInstruction, ins.Address);
                    SetReg(ins.RegField, size, EffectiveAddress(ins));
                    return;
                case 0x8E: // MOV Sreg, r/m16 — flat model: accepted and ignored.
                    ReadRm(ins, 16);
                    return;
                case 0x8F:
                {
                    // The address is computed after ESP is incremented.
                    var v = Pop(size);
                    WriteRm(ins, size, v);
                    return;
                }
                case 0x90:
                    return; // NOP / PAUSE
                case 0x98:
                    if (size == 16) SetReg(Reg.Eax, 16, (uint)(sbyte)GetReg(Reg.Eax, 8));
                    else Cpu.Eax = (uint)(short)Cpu.Eax;
                    return;
                case 0x99:
                    if (size == 16) SetReg(Reg.Edx, 16, (GetReg(Reg.Eax, 16) & 0x8000) != 0 ? 0xFFFFu : 0);
                    else Cpu.Edx = (Cpu.Eax & 0x80000000) != 0 ? 0xFFFFFFFF : 0;
                    return;
                case 0x9B: return; // FWAIT
                case 0x9C: Push(Cpu.EFlags & 0x00FCFFFF, size); return;
                case 0x9D:
                {
                    var v = Pop(size);
                    const uint writable = Flag.Arith | Flag.DF | Flag.TF | (1u << 18) | (1u << 21);
                    if (size == 16) v = (Cpu.EFlags & 0xFFFF0000) | v;
                    Cpu.EFlags = (Cpu.EFlags & ~writable) | (v & writable) | Flag.Fixed;
                    return;
                }
                case 0x9E: // SAHF
                {
                    const uint low = Flag.CF | Flag.PF | Flag.AF | Flag.ZF | Flag.SF;
                    Cpu.EFlags = (Cpu.EFlags & ~low) | (GetReg(4, 8) & low);
                    return;
                }
                case 0x9F: SetReg(4, 8, (Cpu.EFlags & 0xD7) | 2); return; // LAHF
                case 0xA0: SetReg(Reg.Eax, 8, Memory.Read8(ins.Disp + SegmentBase(ins.Segment))); return;
                case 0xA1: SetReg(Reg.Eax, size, ReadMem(ins.Disp + SegmentBase(ins.Segment), size)); return;
                case 0xA2: Memory.Write8(ins.Disp + SegmentBase(ins.Segment), (byte)Cpu.Eax); return;
                case 0xA3: WriteMem(ins.Disp + SegmentBase(ins.Segment), size, GetReg(Reg.Eax, size)); return;
                case 0xA4: case 0xA5: case 0xA6: case 0xA7:
                case 0xAA: case 0xAB: case 0xAC: case 0xAD: case 0xAE: case 0xAF:
                    StringOp(ins, op, (op & 1) == 0 ? 8 : size);
                    return;
                case 0xA8: Alu(4, GetReg(Reg.Eax, 8), ins.Imm, 8); return;
                case 0xA9: Alu(4, GetReg(Reg.Eax, size), ins.Imm, size); return;
                case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                {
                    var width = (op & 1) == 0 ? 8 : size;
                    var count = op <= 0xC1 ? ins.Imm : op <= 0xD1 ? 1u : Cpu.Ecx & 0xFF;
                    WriteRm(ins, width, Shift(ins.RegField, ReadRm(ins, width), count, width));
                    return;
                }
                case 0xC2: Cpu.Eip = Pop32(); Cpu.Esp += ins.Imm; return;
                case 0xC3: Cpu.Eip = Pop32(); return;
                case 0xC4: case 0xC5: // LES/LDS: flat model, load the offset only.
                    SetReg(ins.RegField, size, ReadMem(LinearAddress(ins), size));
                    return;
                case 0xC6: WriteRm(ins, 8, ins.Imm); return;
                case 0xC7: WriteRm(ins, size, ins.Imm); return;
                case 0xC8: Enter(ins.Imm & 0xFFFF, ins.Imm2 & 0x1F, size); return;
                case 0xC9: Cpu.Esp = Cpu.Ebp; SetReg(Reg.Ebp, size, Pop(size)); return;
                case 0xCC: throw new GuestException(GuestException.Breakpoint, ins.Address);
                case 0xCD:
                    if (ins.Imm == 3) throw new GuestException(GuestException.Breakpoint, ins.Address);
                    throw new GuestException(GuestException.AccessViolation, ins.Address, 0, 0xFFFFFFFF);
                case 0xCE:
                    if ((Cpu.EFlags & Flag.OF) != 0) throw new GuestException(GuestException.IntegerOverflow, ins.Address);
                    return;
                case 0xD4: // AAM
                {
                    if ((ins.Imm & 0xFF) == 0) throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                    var al = GetReg(Reg.Eax, 8);
                    var result = ((al / ins.Imm) << 8) | (al % ins.Imm);
                    SetReg(Reg.Eax, 16, result);
                    SetSzp(result & 0xFF, 8);
                    return;
                }
                case 0xD5: // AAD
                {
                    var ax = GetReg(Reg.Eax, 16);
                    var al = ((ax & 0xFF) + ((ax >> 8) * ins.Imm)) & 0xFF;
                    SetReg(Reg.Eax, 16, al);
                    SetSzp(al, 8);
                    return;
                }
                case 0xD6: SetReg(Reg.Eax, 8, CF ? 0xFFu : 0); return; // SALC
                case 0xD7: SetReg(Reg.Eax, 8, Memory.Read8(Cpu.Ebx + GetReg(Reg.Eax, 8) + SegmentBase(ins.Segment))); return;
                case 0xE0: case 0xE1: case 0xE2:
                {
                    Cpu.Ecx--;
                    var take = Cpu.Ecx != 0 && (op == 0xE2 ||
                        (op == 0xE1 && (Cpu.EFlags & Flag.ZF) != 0) ||
                        (op == 0xE0 && (Cpu.EFlags & Flag.ZF) == 0));
                    if (take) Cpu.Eip = next + (uint)(sbyte)ins.Imm;
                    return;
                }
                case 0xE3: if (Cpu.Ecx == 0) Cpu.Eip = next + (uint)(sbyte)ins.Imm; return;
                case 0xE8: Push32(next); Cpu.Eip = next + ins.Imm; return;
                case 0xE9: Cpu.Eip = next + ins.Imm; return;
                case 0xEB: Cpu.Eip = next + (uint)(sbyte)ins.Imm; return;
                case 0xF4: throw new GuestException(GuestException.PrivilegedInstruction, ins.Address);
                case 0xF5: Cpu.EFlags ^= Flag.CF; return;
                case 0xF6: case 0xF7: Group3(ins, op == 0xF6 ? 8 : size); return;
                case 0xF8: Cpu.EFlags &= ~Flag.CF; return;
                case 0xF9: Cpu.EFlags |= Flag.CF; return;
                case 0xFA: case 0xFB: throw new GuestException(GuestException.PrivilegedInstruction, ins.Address);
                case 0xFC: Cpu.EFlags &= ~Flag.DF; return;
                case 0xFD: Cpu.EFlags |= Flag.DF; return;
                case 0xFE:
                    if (ins.RegField > 1) throw new GuestException(GuestException.IllegalInstruction, ins.Address);
                    WriteRm(ins, 8, IncDec(ReadRm(ins, 8), 8, ins.RegField == 1));
                    return;
                case 0xFF: Group5(ins, size, next); return;
            }

            throw new GuestException(
                op == 0x9A || op == 0xEA || op == 0xCA || op == 0xCB || op == 0xCF || (op >= 0xE4 && op <= 0xEF) || (op >= 0x6C && op <= 0x6F)
                    ? GuestException.PrivilegedInstruction
                    : GuestException.IllegalInstruction,
                ins.Address);
        }

        /// <summary>The selectors a 32-bit Windows thread sees (the WoW64 values).</summary>
        public static uint SegmentSelector(int segment)
        {
            switch (segment)
            {
                case Seg.Cs: return 0x23;
                case Seg.Fs: return 0x53;
                case Seg.Gs: return 0x2B;
                default: return 0x2B;
            }
        }

        private void Enter(uint frame, uint level, int size)
        {
            Push(GetReg(Reg.Ebp, size), size);
            var frameTemp = Cpu.Esp;
            if (level > 0)
            {
                var step = size == 16 ? 2u : 4u;
                for (uint i = 1; i < level; i++)
                {
                    Cpu.Ebp -= step;
                    Push(ReadMem(Cpu.Ebp, size), size);
                }
                Push(frameTemp, size);
            }
            SetReg(Reg.Ebp, size, frameTemp);
            Cpu.Esp -= frame;
        }

        private uint IMul2(uint a, uint b, int size)
        {
            long product;
            bool overflow;
            if (size == 16)
            {
                product = (short)a * (long)(short)b;
                overflow = product != (short)product;
            }
            else
            {
                product = (int)a * (long)(int)b;
                overflow = product != (int)product;
            }
            SetFlags(overflow, overflow, false);
            SetSzp((uint)product, size);
            return (uint)product & Mask(size);
        }

        private void Group3(in Instruction ins, int size)
        {
            var mask = Mask(size);
            switch (ins.RegField)
            {
                case 0: case 1:
                    Alu(4, ReadRm(ins, size), ins.Imm, size);
                    return;
                case 2:
                    WriteRm(ins, size, ~ReadRm(ins, size) & mask);
                    return;
                case 3:
                {
                    var a = ReadRm(ins, size);
                    var r = Alu(5, 0, a, size);
                    WriteRm(ins, size, r);
                    return;
                }
                case 4: // MUL
                {
                    var a = ReadRm(ins, size);
                    if (size == 8)
                    {
                        var p = GetReg(Reg.Eax, 8) * a;
                        SetReg(Reg.Eax, 16, p);
                        SetFlags((p & 0xFF00) != 0, (p & 0xFF00) != 0, false);
                        SetSzp(p, 8);
                    }
                    else if (size == 16)
                    {
                        var p = GetReg(Reg.Eax, 16) * a;
                        SetReg(Reg.Eax, 16, p);
                        SetReg(Reg.Edx, 16, p >> 16);
                        SetFlags((p >> 16) != 0, (p >> 16) != 0, false);
                        SetSzp(p, 16);
                    }
                    else
                    {
                        var p = (ulong)Cpu.Eax * a;
                        Cpu.Eax = (uint)p;
                        Cpu.Edx = (uint)(p >> 32);
                        SetFlags(Cpu.Edx != 0, Cpu.Edx != 0, false);
                        SetSzp(Cpu.Eax, 32);
                    }
                    return;
                }
                case 5: // IMUL
                {
                    var a = ReadRm(ins, size);
                    if (size == 8)
                    {
                        var p = (sbyte)GetReg(Reg.Eax, 8) * (sbyte)a;
                        SetReg(Reg.Eax, 16, (uint)p);
                        var o = p != (sbyte)p;
                        SetFlags(o, o, false);
                        SetSzp((uint)p, 8);
                    }
                    else if (size == 16)
                    {
                        var p = (short)GetReg(Reg.Eax, 16) * (short)a;
                        SetReg(Reg.Eax, 16, (uint)p);
                        SetReg(Reg.Edx, 16, (uint)p >> 16);
                        var o = p != (short)p;
                        SetFlags(o, o, false);
                        SetSzp((uint)p, 16);
                    }
                    else
                    {
                        var p = (long)(int)Cpu.Eax * (int)a;
                        Cpu.Eax = (uint)p;
                        Cpu.Edx = (uint)((ulong)p >> 32);
                        var o = p != (int)p;
                        SetFlags(o, o, false);
                        SetSzp(Cpu.Eax, 32);
                    }
                    return;
                }
                case 6: // DIV
                {
                    var d = ReadRm(ins, size);
                    if (d == 0) throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                    if (size == 8)
                    {
                        var n = GetReg(Reg.Eax, 16);
                        var q = n / d;
                        if (q > 0xFF) throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                        SetReg(Reg.Eax, 16, ((n % d) << 8) | q);
                    }
                    else if (size == 16)
                    {
                        var n = (GetReg(Reg.Edx, 16) << 16) | GetReg(Reg.Eax, 16);
                        var q = n / d;
                        if (q > 0xFFFF) throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                        SetReg(Reg.Eax, 16, q);
                        SetReg(Reg.Edx, 16, n % d);
                    }
                    else
                    {
                        var n = ((ulong)Cpu.Edx << 32) | Cpu.Eax;
                        var q = n / d;
                        if (q > 0xFFFFFFFF) throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                        Cpu.Eax = (uint)q;
                        Cpu.Edx = (uint)(n % d);
                    }
                    return;
                }
                default: // IDIV
                {
                    var raw = ReadRm(ins, size);
                    if ((raw & mask) == 0) throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                    if (size == 8)
                    {
                        long n = (short)GetReg(Reg.Eax, 16);
                        long d = (sbyte)raw;
                        var q = n / d;
                        if (q > sbyte.MaxValue || q < sbyte.MinValue)
                            throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                        SetReg(Reg.Eax, 16, (((uint)(n % d) & 0xFF) << 8) | ((uint)q & 0xFF));
                    }
                    else if (size == 16)
                    {
                        long n = (int)((GetReg(Reg.Edx, 16) << 16) | GetReg(Reg.Eax, 16));
                        long d = (short)raw;
                        var q = n / d;
                        if (q > short.MaxValue || q < short.MinValue)
                            throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                        SetReg(Reg.Eax, 16, (uint)q);
                        SetReg(Reg.Edx, 16, (uint)(n % d));
                    }
                    else
                    {
                        var n = (long)(((ulong)Cpu.Edx << 32) | Cpu.Eax);
                        long d = (int)raw;
                        if (n == long.MinValue && d == -1)
                            throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                        var q = n / d;
                        if (q > int.MaxValue || q < int.MinValue)
                            throw new GuestException(GuestException.IntegerDivideByZero, ins.Address);
                        Cpu.Eax = (uint)q;
                        Cpu.Edx = (uint)(n % d);
                    }
                    return;
                }
            }
        }

        private void Group5(in Instruction ins, int size, uint next)
        {
            switch (ins.RegField)
            {
                case 0: WriteRm(ins, size, IncDec(ReadRm(ins, size), size, false)); return;
                case 1: WriteRm(ins, size, IncDec(ReadRm(ins, size), size, true)); return;
                case 2:
                {
                    var target = ReadRm(ins, 32);
                    Push32(next);
                    Cpu.Eip = target;
                    return;
                }
                case 4: Cpu.Eip = ReadRm(ins, 32); return;
                case 6: Push(ReadRm(ins, size), size); return;
                default:
                    // Far CALL/JMP (3, 5) and the undefined /7.
                    throw new GuestException(GuestException.IllegalInstruction, ins.Address);
            }
        }

        private void StringOp(in Instruction ins, int op, int size)
        {
            var step = (uint)(size / 8);
            var delta = (Cpu.EFlags & Flag.DF) != 0 ? (uint)-(int)step : step;
            var source = SegmentBase(ins.Segment);
            var rep = ins.Rep != 0;
            var compare = op == 0xA6 || op == 0xA7 || op == 0xAE || op == 0xAF;

            while (true)
            {
                if (rep && Cpu.Ecx == 0) return;
                switch (op)
                {
                    case 0xA4: case 0xA5:
                        WriteMem(Cpu.Edi, size, ReadMem(Cpu.Esi + source, size));
                        Cpu.Esi += delta; Cpu.Edi += delta;
                        break;
                    case 0xA6: case 0xA7:
                        Alu(7, ReadMem(Cpu.Esi + source, size), ReadMem(Cpu.Edi, size), size);
                        Cpu.Esi += delta; Cpu.Edi += delta;
                        break;
                    case 0xAA: case 0xAB:
                        WriteMem(Cpu.Edi, size, GetReg(Reg.Eax, size));
                        Cpu.Edi += delta;
                        break;
                    case 0xAC: case 0xAD:
                        SetReg(Reg.Eax, size, ReadMem(Cpu.Esi + source, size));
                        Cpu.Esi += delta;
                        break;
                    default: // SCAS
                        Alu(7, GetReg(Reg.Eax, size), ReadMem(Cpu.Edi, size), size);
                        Cpu.Edi += delta;
                        break;
                }
                if (!rep) return;
                Cpu.Ecx--;
                if (compare)
                {
                    var zf = (Cpu.EFlags & Flag.ZF) != 0;
                    if (ins.Rep == 0xF3 && !zf) return;
                    if (ins.Rep == 0xF2 && zf) return;
                }
            }
        }

        // ------------------------------------------------------------------ BCD

        private void Daa()
        {
            var al = GetReg(Reg.Eax, 8);
            var cf = CF;
            var af = (Cpu.EFlags & Flag.AF) != 0;
            var oldAl = al;
            var newCf = false;
            if ((al & 0xF) > 9 || af)
            {
                newCf = cf || al > 0xF9;
                al = (al + 6) & 0xFF;
                af = true;
            }
            else af = false;
            if (oldAl > 0x99 || cf) { al = (al + 0x60) & 0xFF; newCf = true; }
            SetReg(Reg.Eax, 8, al);
            SetFlags(newCf, false, af);
            SetSzp(al, 8);
        }

        private void Das()
        {
            var al = GetReg(Reg.Eax, 8);
            var cf = CF;
            var af = (Cpu.EFlags & Flag.AF) != 0;
            var oldAl = al;
            var newCf = false;
            if ((al & 0xF) > 9 || af)
            {
                newCf = cf || al < 6;
                al = (al - 6) & 0xFF;
                af = true;
            }
            else af = false;
            if (oldAl > 0x99 || cf) { al = (al - 0x60) & 0xFF; newCf = true; }
            SetReg(Reg.Eax, 8, al);
            SetFlags(newCf, false, af);
            SetSzp(al, 8);
        }

        private void Aaa()
        {
            var ax = GetReg(Reg.Eax, 16);
            var adjust = (ax & 0xF) > 9 || (Cpu.EFlags & Flag.AF) != 0;
            if (adjust) ax = (ax + 0x106) & 0xFFFF;
            SetReg(Reg.Eax, 16, ax & 0xFF0F);
            SetFlags(adjust, false, adjust);
        }

        private void Aas()
        {
            var ax = GetReg(Reg.Eax, 16);
            var adjust = (ax & 0xF) > 9 || (Cpu.EFlags & Flag.AF) != 0;
            if (adjust)
            {
                var al = (ax - 6) & 0xFF;
                var ah = ((ax >> 8) - 1) & 0xFF;
                ax = (ah << 8) | al;
            }
            SetReg(Reg.Eax, 16, ax & 0xFF0F);
            SetFlags(adjust, false, adjust);
        }
    }
}
