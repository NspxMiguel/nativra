using System;
using System.Collections.Generic;

namespace Nativra.X86.Jit
{
    /// <summary>Host x64 register numbers, in encoding order.</summary>
    public static class X64
    {
        public const int Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rsp = 4, Rbp = 5, Rsi = 6, Rdi = 7;
        public const int R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15;
    }

    /// <summary>
    /// A tiny x64 machine-code emitter: just the encodings the block translator
    /// needs. Sizes are 32 or 64 bits (guest 16/8-bit lanes are handled by the
    /// translator with masks). It grows a byte buffer and patches jump
    /// displacements once the block's layout is known.
    /// </summary>
    public sealed class X64Emit
    {
        private byte[] buffer = new byte[256];
        public int Length { get; private set; }

        private readonly List<(int at, string label)> fixups = new List<(int, string)>();
        private readonly Dictionary<string, int> labels = new Dictionary<string, int>();

        public byte[] ToArray()
        {
            var result = new byte[Length];
            Array.Copy(buffer, result, Length);
            return result;
        }

        public int Here => Length;

        private void Ensure(int extra)
        {
            if (Length + extra <= buffer.Length) return;
            var grown = new byte[Math.Max(buffer.Length * 2, Length + extra)];
            Array.Copy(buffer, grown, Length);
            buffer = grown;
        }

        public void U8(int v) { Ensure(1); buffer[Length++] = (byte)v; }
        public void U32(uint v) { Ensure(4); for (var i = 0; i < 4; i++) buffer[Length++] = (byte)(v >> (8 * i)); }
        public void U64(ulong v) { Ensure(8); for (var i = 0; i < 8; i++) buffer[Length++] = (byte)(v >> (8 * i)); }
        public void Bytes(byte[] b) { Ensure(b.Length); Array.Copy(b, 0, buffer, Length, b.Length); Length += b.Length; }

        // ------------------------------------------------------------- encoding

        private void Rex(bool w, int reg, int index, int rmBase)
        {
            var value = 0x40 | (w ? 8 : 0) | ((reg & 8) >> 1) | ((index & 8) >> 2) | ((rmBase & 8) >> 3);
            if (value != 0x40 || NeedRex(reg) || NeedRex(rmBase)) U8(value);
        }

        // 8-bit forms addressing sil/dil/bpl/spl need a REX even when otherwise unnecessary.
        private static bool NeedRex(int reg) => false;

        /// <summary>Emits REX only when some high register or W bit requires it.</summary>
        private void MaybeRex(bool w, int reg, int index, int rmBase)
        {
            if (w || (reg & 8) != 0 || (index & 8) != 0 || (rmBase & 8) != 0)
                U8(0x40 | (w ? 8 : 0) | ((reg & 8) >> 1) | ((index & 8) >> 2) | ((rmBase & 8) >> 3));
        }

        /// <summary>reg,reg form: ModRM mod=11.</summary>
        private void ModRegReg(int reg, int rm) => U8(0xC0 | ((reg & 7) << 3) | (rm & 7));

        /// <summary>reg, [base + index*scale + disp] form.</summary>
        private void ModMem(int reg, int baseReg, int index, int scale, int disp)
        {
            var mod = disp == 0 && (baseReg & 7) != X64.Rbp ? 0 : (disp >= -128 && disp <= 127 ? 1 : 2);
            var needSib = index >= 0 || (baseReg & 7) == X64.Rsp;
            if (index < 0 && (baseReg & 7) == X64.Rbp && mod == 0) mod = 1;

            if (needSib)
            {
                U8((byte)((mod << 6) | ((reg & 7) << 3) | 4));
                var ss = scale == 8 ? 3 : scale == 4 ? 2 : scale == 2 ? 1 : 0;
                var idx = index < 0 ? 4 : (index & 7);
                U8((byte)((ss << 6) | (idx << 3) | (baseReg & 7)));
            }
            else
            {
                U8((byte)((mod << 6) | ((reg & 7) << 3) | (baseReg & 7)));
            }
            if (mod == 1) U8((byte)(sbyte)disp);
            else if (mod == 2) U32((uint)disp);
        }

        // --------------------------------------------------------------- moves

        public void MovRegReg(int dst, int src, bool wide = false)
        {
            MaybeRex(wide, src, 0, dst);
            U8(0x89);
            ModRegReg(src, dst);
        }

        public void MovRegImm32(int dst, uint imm)
        {
            MaybeRex(false, 0, 0, dst);
            U8((byte)(0xB8 + (dst & 7)));
            U32(imm);
        }

        public void MovRegImm64(int dst, ulong imm)
        {
            U8((byte)(0x48 | ((dst & 8) >> 3)));
            U8((byte)(0xB8 + (dst & 7)));
            U64(imm);
        }

        /// <summary>
        /// dst = [base + index*scale + disp]. For 8/16-bit loads the destination
        /// is filled by mov to the sub-register, preserving the guest reg's upper
        /// bits, exactly as x86 does. A 32-bit load clears the upper half; a
        /// 64-bit load uses REX.W (used only for the guest-memory base pointer).
        /// </summary>
        public void LoadMem(int dst, int baseReg, int index, int scale, int disp, int size = 32)
        {
            switch (size)
            {
                case 8: MaybeRex(false, dst, index, baseReg); U8(0x8A); break;             // mov r8, m8
                case 16: U8(0x66); MaybeRex(false, dst, index, baseReg); U8(0x8B); break;  // mov r16, m16
                case 64: MaybeRex(true, dst, index, baseReg); U8(0x8B); break;             // mov r64, m64
                default: MaybeRex(false, dst, index, baseReg); U8(0x8B); break;            // mov r32, m32
            }
            ModMem(dst, baseReg, index, scale, disp);
        }

        public void StoreMem(int src, int baseReg, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(size == 64, src, index, baseReg);
            U8(size == 8 ? (byte)0x88 : (byte)0x89);
            ModMem(src, baseReg, index, scale, disp);
        }

        /// <summary>mov between two registers at a guest operand size (8/16/32).</summary>
        public void MovRegRegSized(int dst, int src, int size)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, src, 0, dst);
            U8(size == 8 ? (byte)0x88 : (byte)0x89);
            ModRegReg(src, dst);
        }

        public void MovRegImm8(int reg, byte imm)
        {
            MaybeRex(false, 0, 0, reg);
            U8((byte)(0xB0 + (reg & 7)));
            U8(imm);
        }

        public void MovRegImm16(int reg, ushort imm)
        {
            U8(0x66);
            MaybeRex(false, 0, 0, reg);
            U8((byte)(0xB8 + (reg & 7)));
            U8((byte)imm);
            U8((byte)(imm >> 8));
        }

        /// <summary>lea dst, [index*scale + disp] with no base register.</summary>
        public void LeaNoBase(int dst, int index, int scale, int disp)
        {
            MaybeRex(false, dst, index, 0);
            U8(0x8D);
            U8((byte)(((dst & 7) << 3) | 4));           // mod=00, r/m=SIB
            var ss = scale == 8 ? 3 : scale == 4 ? 2 : scale == 2 ? 1 : 0;
            U8((byte)((ss << 6) | ((index & 7) << 3) | 5)); // base=5, mod=0 => disp32 only
            U32((uint)disp);
        }

        public void TestMemReg(int reg, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, reg, index, baseR);
            U8(size == 8 ? (byte)0x84 : (byte)0x85);
            ModMem(reg, baseR, index, scale, disp);
        }

        public void TestMemImm(int baseR, int index, int scale, int disp, uint imm, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0xF6 : (byte)0xF7);
            ModMem(0, baseR, index, scale, disp);
            if (size == 8) U8((byte)imm);
            else if (size == 16) { U8((byte)imm); U8((byte)(imm >> 8)); }
            else U32(imm);
        }

        /// <summary>lea dst, [base + index*scale + disp] using 32-bit result (top cleared).</summary>
        public void Lea(int dst, int baseReg, int index, int scale, int disp)
        {
            MaybeRex(false, dst, index, baseReg);
            U8(0x8D);
            ModMem(dst, baseReg, index, scale, disp);
        }

        public void LeaWide(int dst, int baseReg, int index, int scale, int disp)
        {
            MaybeRex(true, dst, index, baseReg);
            U8(0x8D);
            ModMem(dst, baseReg, index, scale, disp);
        }

        // ------------------------------------------------------- ALU reg,reg/imm

        /// <summary>The /r opcode base for an ALU op index (0 add .. 7 cmp) in the r/m,r direction.</summary>
        private static int AluOpcode(int op) => (op << 3) | 0x01;

        public void AluRegReg(int op, int dst, int src, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(size == 64, src, 0, dst);
            U8((byte)(size == 8 ? AluOpcode(op) - 1 : AluOpcode(op)));
            ModRegReg(src, dst);
        }

        public void AluRegImm(int op, int dst, uint imm, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(size == 64, 0, 0, dst);
            if (size == 8)
            {
                U8(0x80);
                ModRegReg(op, dst);
                U8((byte)imm);
            }
            else
            {
                U8(0x81);
                ModRegReg(op, dst);
                if (size == 16) { U8((byte)imm); U8((byte)(imm >> 8)); }
                else U32(imm);
            }
        }

        /// <summary>A unary Group-3 op (2 NOT, 3 NEG, 4 MUL, 5 IMUL, 6 DIV, 7 IDIV) on a register.</summary>
        public void Group3(int sub, int reg, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, 0, reg);
            U8(size == 8 ? (byte)0xF6 : (byte)0xF7);
            ModRegReg(sub, reg);
        }

        /// <summary>Group-2 shift/rotate by CL (sub 0 ROL..7 SAR).</summary>
        public void ShiftCl(int sub, int reg, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, 0, reg);
            U8(size == 8 ? (byte)0xD2 : (byte)0xD3);
            ModRegReg(sub, reg);
        }

        public void ShiftImm(int sub, int reg, byte count, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, 0, reg);
            U8(size == 8 ? (byte)0xC0 : (byte)0xC1);
            ModRegReg(sub, reg);
            U8(count);
        }

        /// <summary>
        /// The by-one shift/rotate form (D0/D1), kept distinct from the by-imm
        /// form (C0/C1): they are different instructions and leave the
        /// officially-undefined AF in different states on real silicon, so the
        /// JIT must emit whichever one the guest used to match it bit for bit.
        /// </summary>
        public void ShiftOne(int sub, int reg, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, 0, reg);
            U8(size == 8 ? (byte)0xD0 : (byte)0xD1);
            ModRegReg(sub, reg);
        }

        public void ShiftMemOne(int sub, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0xD0 : (byte)0xD1);
            ModMem(sub, baseR, index, scale, disp);
        }

        public void ImulRegReg(int dst, int src, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, dst, 0, src);
            U8(0x0F); U8(0xAF);
            ModRegReg(dst, src);
        }

        public void ImulRegRegImm(int dst, int src, uint imm, int size, bool imm8)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, dst, 0, src);
            U8(imm8 ? (byte)0x6B : (byte)0x69);
            ModRegReg(dst, src);
            if (imm8) U8((byte)imm);
            else if (size == 16) { U8((byte)imm); U8((byte)(imm >> 8)); }
            else U32(imm);
        }

        // ----------------------------------------------------- memory ALU forms

        /// <summary>op reg, [mem] — the r, r/m direction.</summary>
        public void AluRegMem(int op, int reg, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, reg, index, baseR);
            U8((byte)(size == 8 ? (op << 3) | 0x02 : (op << 3) | 0x03));
            ModMem(reg, baseR, index, scale, disp);
        }

        /// <summary>op [mem], reg — the r/m, r direction.</summary>
        public void AluMemReg(int op, int reg, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, reg, index, baseR);
            U8((byte)(size == 8 ? (op << 3) : (op << 3) | 0x01));
            ModMem(reg, baseR, index, scale, disp);
        }

        public void AluMemImm(int op, int baseR, int index, int scale, int disp, uint imm, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0x80 : (byte)0x81);
            ModMem(op, baseR, index, scale, disp);
            if (size == 8) U8((byte)imm);
            else if (size == 16) { U8((byte)imm); U8((byte)(imm >> 8)); }
            else U32(imm);
        }

        public void TestRegReg(int a, int b, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, b, 0, a);
            U8(size == 8 ? (byte)0x84 : (byte)0x85);
            ModRegReg(b, a);
        }

        public void TestRegImm(int reg, uint imm, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, 0, reg);
            U8(size == 8 ? (byte)0xF6 : (byte)0xF7);
            ModRegReg(0, reg);
            if (size == 8) U8((byte)imm);
            else if (size == 16) { U8((byte)imm); U8((byte)(imm >> 8)); }
            else U32(imm);
        }

        /// <summary>inc (sub 0) / dec (sub 1) on a register, via the FF/FE group (the 0x40-0x47 bytes are REX in x64).</summary>
        public void IncDecReg(int sub, int reg, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, 0, reg);
            U8(size == 8 ? (byte)0xFE : (byte)0xFF);
            ModRegReg(sub, reg);
        }

        public void Group3Mem(int sub, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0xF6 : (byte)0xF7);
            ModMem(sub, baseR, index, scale, disp);
        }

        public void ShiftMemImm(int sub, int baseR, int index, int scale, int disp, byte count, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0xC0 : (byte)0xC1);
            ModMem(sub, baseR, index, scale, disp);
            U8(count);
        }

        public void ShiftMemCl(int sub, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0xD2 : (byte)0xD3);
            ModMem(sub, baseR, index, scale, disp);
        }

        public void IncDecMem(int sub, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0xFE : (byte)0xFF);
            ModMem(sub, baseR, index, scale, disp);
        }

        public void MovMemImm(int baseR, int index, int scale, int disp, uint imm, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, 0, index, baseR);
            U8(size == 8 ? (byte)0xC6 : (byte)0xC7);
            ModMem(0, baseR, index, scale, disp);
            if (size == 8) U8((byte)imm);
            else if (size == 16) { U8((byte)imm); U8((byte)(imm >> 8)); }
            else U32(imm);
        }

        public void MovzxMem(int dst, int baseR, int index, int scale, int disp, int size)
        {
            MaybeRex(false, dst, index, baseR);
            U8(0x0F); U8(size == 8 ? (byte)0xB6 : (byte)0xB7);
            ModMem(dst, baseR, index, scale, disp);
        }

        public void MovsxMem(int dst, int baseR, int index, int scale, int disp, int size)
        {
            MaybeRex(false, dst, index, baseR);
            U8(0x0F); U8(size == 8 ? (byte)0xBE : (byte)0xBF);
            ModMem(dst, baseR, index, scale, disp);
        }

        public void ImulRegMem(int dst, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, dst, index, baseR);
            U8(0x0F); U8(0xAF);
            ModMem(dst, baseR, index, scale, disp);
        }

        public void CmovccMem(int cc, int dst, int baseR, int index, int scale, int disp, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, dst, index, baseR);
            U8(0x0F); U8((byte)(0x40 + cc));
            ModMem(dst, baseR, index, scale, disp);
        }

        public void SetccMem(int cc, int baseR, int index, int scale, int disp)
        {
            MaybeRex(false, 0, index, baseR);
            U8(0x0F); U8((byte)(0x90 + cc));
            ModMem(0, baseR, index, scale, disp);
        }

        /// <summary>Adds a 32-bit immediate to a register (top cleared), used for address arithmetic.</summary>
        public void AddRegImm32(int reg, uint imm) => AluRegImm(0, reg, imm);

        public void Movzx(int dst, int src, int size)
        {
            MaybeRex(false, dst, 0, src);
            U8(0x0F);
            U8(size == 8 ? (byte)0xB6 : (byte)0xB7);
            ModRegReg(dst, src);
        }

        public void Movsx(int dst, int src, int size)
        {
            MaybeRex(false, dst, 0, src);
            U8(0x0F);
            U8(size == 8 ? (byte)0xBE : (byte)0xBF);
            ModRegReg(dst, src);
        }

        public void MovsxdRegReg(int dst, int src)
        {
            MaybeRex(true, dst, 0, src);
            U8(0x63);
            ModRegReg(dst, src);
        }

        public void Setcc(int cc, int reg8)
        {
            MaybeRex(false, 0, 0, reg8);
            U8(0x0F);
            U8((byte)(0x90 + cc));
            ModRegReg(0, reg8);
        }

        public void Cmovcc(int cc, int dst, int src, int size = 32)
        {
            if (size == 16) U8(0x66);
            MaybeRex(false, dst, 0, src);
            U8(0x0F);
            U8((byte)(0x40 + cc));
            ModRegReg(dst, src);
        }

        public void Bswap(int reg)
        {
            MaybeRex(false, 0, 0, reg);
            U8(0x0F);
            U8((byte)(0xC8 + (reg & 7)));
        }

        public void Cdq() => U8(0x99);

        // ------------------------------------------------------------- stack/misc

        public void PushReg(int reg) { MaybeRex(false, 0, 0, reg); U8((byte)(0x50 + (reg & 7))); }
        public void PopReg(int reg) { MaybeRex(false, 0, 0, reg); U8((byte)(0x58 + (reg & 7))); }
        public void Pushfq() => U8(0x9C);
        public void Popfq() => U8(0x9D);
        public void Ret() => U8(0xC3);
        public void Nop() => U8(0x90);
        public void Int3() => U8(0xCC);
        public void ClearDf() => U8(0xFC);

        /// <summary>Loads a 64-bit displacement off the context pointer into a register.</summary>
        public void LoadCtx(int dst, int ctxReg, int offset, int size = 32)
        {
            LoadMem(dst, ctxReg, -1, 1, offset, size);
        }

        public void StoreCtx(int src, int ctxReg, int offset, int size = 32)
        {
            StoreMem(src, ctxReg, -1, 1, offset, size);
        }

        // ------------------------------------------------------------- branching

        public void Label(string name) => labels[name] = Length;

        /// <summary>jmp rel32 to a label resolved at Finish().</summary>
        public void Jmp(string label)
        {
            U8(0xE9);
            fixups.Add((Length, label));
            U32(0);
        }

        public void Jcc(int cc, string label)
        {
            U8(0x0F);
            U8((byte)(0x80 + cc));
            fixups.Add((Length, label));
            U32(0);
        }

        /// <summary>call through a register (an absolute helper address loaded first).</summary>
        public void CallReg(int reg)
        {
            MaybeRex(false, 0, 0, reg);
            U8(0xFF);
            ModRegReg(2, reg);
        }

        public void JmpReg(int reg)
        {
            MaybeRex(false, 0, 0, reg);
            U8(0xFF);
            ModRegReg(4, reg);
        }

        public void Finish()
        {
            foreach (var (at, label) in fixups)
            {
                if (!labels.TryGetValue(label, out var target))
                    throw new InvalidOperationException("unresolved JIT label " + label);
                var rel = target - (at + 4);
                buffer[at] = (byte)rel;
                buffer[at + 1] = (byte)(rel >> 8);
                buffer[at + 2] = (byte)(rel >> 16);
                buffer[at + 3] = (byte)(rel >> 24);
            }
        }
    }
}
