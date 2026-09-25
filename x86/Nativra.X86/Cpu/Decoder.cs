using System;

namespace Nativra.X86.Cpu
{
    /// <summary>Where the decoder reads instruction bytes from.</summary>
    public interface ICodeReader
    {
        byte Read8(uint address);
    }

    /// <summary>Segment override prefixes, in x86 segment-register order.</summary>
    public static class Seg
    {
        public const int None = -1, Es = 0, Cs = 1, Ss = 2, Ds = 3, Fs = 4, Gs = 5;
    }

    /// <summary>
    /// One decoded 32-bit x86 instruction. The decoder fills in the encoding
    /// facts (prefixes, opcode, ModRM, SIB, displacement, immediates, length);
    /// what the instruction means is left to the interpreter and the JIT, which
    /// each switch on <see cref="Op"/>.
    ///
    /// Two-byte opcodes are stored as 0x0F00 | second byte, three-byte ones as
    /// 0x0F3800 | third byte and 0x0F3A00 | third byte.
    /// </summary>
    public struct Instruction
    {
        public uint Address;
        public int Length;
        public int Op;

        public bool OpSize16;
        public bool AddrSize16;
        public int Rep;            // 0, 0xF3 (REP/REPE) or 0xF2 (REPNE)
        public bool Lock;
        public int Segment;        // Seg.None or Seg.*

        public bool HasModRm;
        public int Mod, RegField, Rm;

        /// <summary>Memory operand: Base/Index are register numbers, or -1 for none.</summary>
        public int Base, Index, Scale;
        public uint Disp;

        public uint Imm;
        public uint Imm2;
        public int ImmSize;

        /// <summary>Offset of the ModRM byte within the instruction, for re-encoding.</summary>
        public int ModRmOffset;

        /// <summary>Offset of the opcode's first byte (after prefixes).</summary>
        public int OpcodeOffset;

        public bool Valid;

        public bool IsMemory => HasModRm && Mod != 3;

        public uint Next => Address + (uint)Length;

        public override string ToString() =>
            $"{Address:X8}: op={Op:X} len={Length}" + (HasModRm ? $" mod={Mod} reg={RegField} rm={Rm}" : "");
    }

    /// <summary>
    /// Length-and-operand decoder for 32-bit protected-mode x86, covering the
    /// one-byte map, the 0F map, the 0F38/0F3A maps and the x87 escapes. It is
    /// deliberately table-driven rather than semantic: getting the length of
    /// every instruction right is what lets the JIT copy an instruction it does
    /// not otherwise understand, and lets the interpreter skip nothing.
    /// </summary>
    public static class Decoder
    {
        // Immediate kinds.
        private const byte I0 = 0, Ib = 1, Iw = 2, Iz = 3, Iwb = 4, Ip = 5;

        private static readonly bool[] ModRm1 = new bool[256];
        private static readonly byte[] Imm1 = new byte[256];
        private static readonly bool[] ModRm2 = new bool[256];
        private static readonly byte[] Imm2 = new byte[256];
        private static readonly bool[] Invalid1 = new bool[256];

        static Decoder()
        {
            // --- one-byte map -------------------------------------------------
            for (var i = 0; i < 0x40; i += 8)
            {
                // ALU families: r/m,r / r,r/m in each size, then AL,imm8 and eAX,immz.
                ModRm1[i] = ModRm1[i + 1] = ModRm1[i + 2] = ModRm1[i + 3] = true;
                Imm1[i + 4] = Ib;
                Imm1[i + 5] = Iz;
            }
            ModRm1[0x62] = ModRm1[0x63] = true;
            Imm1[0x68] = Iz; Imm1[0x6A] = Ib;
            ModRm1[0x69] = true; Imm1[0x69] = Iz;
            ModRm1[0x6B] = true; Imm1[0x6B] = Ib;
            for (var i = 0x70; i <= 0x7F; i++) Imm1[i] = Ib;
            for (var i = 0x80; i <= 0x8F; i++) ModRm1[i] = true;
            Imm1[0x80] = Ib; Imm1[0x81] = Iz; Imm1[0x82] = Ib; Imm1[0x83] = Ib;
            Imm1[0x9A] = Ip;
            Imm1[0xA8] = Ib; Imm1[0xA9] = Iz;
            for (var i = 0xB0; i <= 0xB7; i++) Imm1[i] = Ib;
            for (var i = 0xB8; i <= 0xBF; i++) Imm1[i] = Iz;
            ModRm1[0xC0] = ModRm1[0xC1] = true; Imm1[0xC0] = Imm1[0xC1] = Ib;
            Imm1[0xC2] = Iw;
            ModRm1[0xC4] = ModRm1[0xC5] = true;
            ModRm1[0xC6] = true; Imm1[0xC6] = Ib;
            ModRm1[0xC7] = true; Imm1[0xC7] = Iz;
            Imm1[0xC8] = Iwb; Imm1[0xCA] = Iw; Imm1[0xCD] = Ib;
            for (var i = 0xD0; i <= 0xD3; i++) ModRm1[i] = true;
            Imm1[0xD4] = Imm1[0xD5] = Ib;
            for (var i = 0xD8; i <= 0xDF; i++) ModRm1[i] = true;
            for (var i = 0xE0; i <= 0xE7; i++) Imm1[i] = Ib;
            Imm1[0xE8] = Iz; Imm1[0xE9] = Iz; Imm1[0xEA] = Ip; Imm1[0xEB] = Ib;
            ModRm1[0xF6] = ModRm1[0xF7] = ModRm1[0xFE] = ModRm1[0xFF] = true;

            // --- two-byte (0F) map --------------------------------------------
            foreach (var op in new[] { 0x00, 0x01, 0x02, 0x03, 0x0D })
                ModRm2[op] = true;
            for (var i = 0x10; i <= 0x17; i++) ModRm2[i] = true;
            for (var i = 0x18; i <= 0x1F; i++) ModRm2[i] = true;
            for (var i = 0x20; i <= 0x23; i++) ModRm2[i] = true;
            for (var i = 0x28; i <= 0x2F; i++) ModRm2[i] = true;
            for (var i = 0x40; i <= 0x4F; i++) ModRm2[i] = true;
            for (var i = 0x50; i <= 0x7F; i++) ModRm2[i] = true;
            ModRm2[0x77] = false;                         // EMMS
            Imm2[0x70] = Imm2[0x71] = Imm2[0x72] = Imm2[0x73] = Ib;
            for (var i = 0x80; i <= 0x8F; i++) Imm2[i] = Iz;
            for (var i = 0x90; i <= 0x9F; i++) ModRm2[i] = true;
            foreach (var op in new[] { 0xA3, 0xA4, 0xA5, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF })
                ModRm2[op] = true;
            Imm2[0xA4] = Imm2[0xAC] = Ib;
            for (var i = 0xB0; i <= 0xBF; i++) ModRm2[i] = true;
            Imm2[0xBA] = Ib;
            for (var i = 0xC0; i <= 0xC7; i++) ModRm2[i] = true;
            Imm2[0xC2] = Imm2[0xC4] = Imm2[0xC5] = Imm2[0xC6] = Ib;
            for (var i = 0xD0; i <= 0xFF; i++) ModRm2[i] = true;
        }

        public static Instruction Decode(ICodeReader code, uint address)
        {
            var ins = new Instruction
            {
                Address = address,
                Segment = Seg.None,
                Base = -1,
                Index = -1,
                Scale = 1,
            };
            var at = address;

            byte Next() => code.Read8(at++);

            // Prefixes, at most fifteen bytes in all.
            byte b;
            while (true)
            {
                if (at - address >= 14) return ins; // runaway prefixes: invalid
                b = Next();
                switch (b)
                {
                    case 0x66: ins.OpSize16 = true; continue;
                    case 0x67: ins.AddrSize16 = true; continue;
                    case 0xF0: ins.Lock = true; continue;
                    case 0xF2: case 0xF3: ins.Rep = b; continue;
                    case 0x26: ins.Segment = Seg.Es; continue;
                    case 0x2E: ins.Segment = Seg.Cs; continue;
                    case 0x36: ins.Segment = Seg.Ss; continue;
                    case 0x3E: ins.Segment = Seg.Ds; continue;
                    case 0x64: ins.Segment = Seg.Fs; continue;
                    case 0x65: ins.Segment = Seg.Gs; continue;
                }
                break;
            }
            ins.OpcodeOffset = (int)(at - address - 1);

            bool modrm;
            byte immKind;
            if (b == 0x0F)
            {
                var second = Next();
                if (second == 0x38 || second == 0x3A)
                {
                    var third = Next();
                    ins.Op = (second == 0x38 ? 0x0F3800 : 0x0F3A00) | third;
                    modrm = true;
                    immKind = second == 0x3A ? Ib : I0;
                }
                else if (second == 0x0F)
                {
                    // 3DNow!: ModRM, then the real opcode as a trailing byte.
                    ins.Op = 0x0F0F;
                    modrm = true;
                    immKind = Ib;
                }
                else
                {
                    ins.Op = 0x0F00 | second;
                    modrm = ModRm2[second];
                    immKind = Imm2[second];
                }
            }
            else
            {
                ins.Op = b;
                modrm = ModRm1[b];
                immKind = Imm1[b];
                if (Invalid1[b]) return ins;

                // VEX in 32-bit mode: C4/C5 with ModRM.mod == 11 are VEX, not LES/LDS.
                if ((b == 0xC4 || b == 0xC5) && (code.Read8(at) & 0xC0) == 0xC0)
                {
                    return DecodeVex(code, ins, address, at, b);
                }
                // F6/F7 carry an immediate only in their TEST form (/0 and /1).
                if (b == 0xF6 || b == 0xF7)
                {
                    var reg = (code.Read8(at) >> 3) & 7;
                    if (reg <= 1) immKind = b == 0xF6 ? Ib : Iz;
                }
            }

            if (modrm)
            {
                if (!ReadModRm(code, ref ins, address, ref at)) return ins;
            }

            // A0-A3 carry a memory offset whose size follows the address size.
            if (ins.Op >= 0xA0 && ins.Op <= 0xA3)
            {
                ins.Disp = ins.AddrSize16 ? ReadN(code, ref at, 2) : ReadN(code, ref at, 4);
            }

            switch (immKind)
            {
                case Ib: ins.Imm = Next(); ins.ImmSize = 1; break;
                case Iw: ins.Imm = ReadN(code, ref at, 2); ins.ImmSize = 2; break;
                case Iz:
                    // Relative branches always take 4 bytes here; everything else shrinks with 66.
                    var z = ins.OpSize16 && !IsRelZ(ins.Op) ? 2 : 4;
                    ins.Imm = ReadN(code, ref at, z);
                    ins.ImmSize = z;
                    break;
                case Iwb:
                    ins.Imm = ReadN(code, ref at, 2);
                    ins.Imm2 = Next();
                    ins.ImmSize = 3;
                    break;
                case Ip:
                    ins.Imm = ReadN(code, ref at, ins.OpSize16 ? 2 : 4);
                    ins.Imm2 = ReadN(code, ref at, 2);
                    ins.ImmSize = ins.OpSize16 ? 4 : 6;
                    break;
            }

            ins.Length = (int)(at - address);
            ins.Valid = ins.Length <= 15;
            return ins;
        }

        private static bool IsRelZ(int op) => op == 0xE8 || op == 0xE9 || (op >= 0x0F80 && op <= 0x0F8F);

        private static uint ReadN(ICodeReader code, ref uint at, int n)
        {
            uint value = 0;
            for (var i = 0; i < n; i++) value |= (uint)code.Read8(at++) << (8 * i);
            return value;
        }

        private static bool ReadModRm(ICodeReader code, ref Instruction ins, uint address, ref uint at)
        {
            ins.HasModRm = true;
            ins.ModRmOffset = (int)(at - address);
            var m = code.Read8(at++);
            ins.Mod = m >> 6;
            ins.RegField = (m >> 3) & 7;
            ins.Rm = m & 7;
            if (ins.Mod == 3) return true;

            if (ins.AddrSize16)
            {
                // 16-bit addressing is never produced for 32-bit Windows code
                // outside hand-written oddities; decode its length only.
                if (ins.Mod == 0 && ins.Rm == 6) ins.Disp = ReadN(code, ref at, 2);
                else if (ins.Mod == 1) ins.Disp = (uint)(sbyte)code.Read8(at++);
                else if (ins.Mod == 2) ins.Disp = ReadN(code, ref at, 2);
                return true;
            }

            if (ins.Rm == 4)
            {
                var sib = code.Read8(at++);
                ins.Scale = 1 << (sib >> 6);
                var index = (sib >> 3) & 7;
                ins.Index = index == 4 ? -1 : index;
                var @base = sib & 7;
                if (@base == 5 && ins.Mod == 0)
                {
                    ins.Base = -1;
                    ins.Disp = ReadN(code, ref at, 4);
                    return true;
                }
                ins.Base = @base;
            }
            else if (ins.Rm == 5 && ins.Mod == 0)
            {
                ins.Base = -1;
                ins.Disp = ReadN(code, ref at, 4);
                return true;
            }
            else
            {
                ins.Base = ins.Rm;
            }

            if (ins.Mod == 1) ins.Disp = (uint)(sbyte)code.Read8(at++);
            else if (ins.Mod == 2) ins.Disp = ReadN(code, ref at, 4);
            return true;
        }

        /// <summary>
        /// VEX-encoded (AVX) instructions. Only the length and operand facts
        /// matter here; in 32-bit mode the inverted R/X/B bits are always set.
        /// </summary>
        private static Instruction DecodeVex(ICodeReader code, Instruction ins, uint address, uint at, byte lead)
        {
            int map;
            if (lead == 0xC5)
            {
                at++;          // R vvvv L pp
                map = 1;
            }
            else
            {
                var p1 = code.Read8(at++);
                at++;          // W vvvv L pp
                map = p1 & 0x1F;
            }
            var op = code.Read8(at++);
            ins.Op = (map == 1 ? 0x0F00 : map == 2 ? 0x0F3800 : 0x0F3A00) | op | 0x1000000;
            if (!ReadModRm(code, ref ins, address, ref at)) return ins;
            var hasImm = map == 3 || (map == 1 && (op == 0x70 || op == 0x71 || op == 0x72 || op == 0x73 ||
                                                     op == 0xC2 || op == 0xC4 || op == 0xC5 || op == 0xC6));
            if (hasImm)
            {
                ins.Imm = code.Read8(at++);
                ins.ImmSize = 1;
            }
            ins.Length = (int)(at - address);
            ins.Valid = ins.Length <= 15;
            return ins;
        }
    }
}
