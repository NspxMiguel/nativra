namespace Nativra.X86.Cpu
{
    /// <summary>General-purpose register numbers, in x86 encoding order.</summary>
    public static class Reg
    {
        public const int Eax = 0, Ecx = 1, Edx = 2, Ebx = 3, Esp = 4, Ebp = 5, Esi = 6, Edi = 7;
    }

    /// <summary>EFLAGS bits the translator models.</summary>
    public static class Flag
    {
        public const uint CF = 1u << 0;
        public const uint PF = 1u << 2;
        public const uint AF = 1u << 4;
        public const uint ZF = 1u << 6;
        public const uint SF = 1u << 7;
        public const uint TF = 1u << 8;
        public const uint IF = 1u << 9;
        public const uint DF = 1u << 10;
        public const uint OF = 1u << 11;

        /// <summary>The arithmetic flags an ALU instruction rewrites.</summary>
        public const uint Arith = CF | PF | AF | ZF | SF | OF;

        /// <summary>Bit 1 is architecturally always set; IF is always set in user mode.</summary>
        public const uint Fixed = (1u << 1) | IF;
    }

    /// <summary>
    /// The guest's architectural state. Laid out as a plain struct-like class so
    /// the JIT can address fields at fixed offsets through <see cref="Layout"/>
    /// when it lowers to a native context block.
    /// </summary>
    public sealed class CpuState
    {
        public readonly uint[] R = new uint[8];
        public uint Eip;
        public uint EFlags = Flag.Fixed;

        /// <summary>Linear address of the 32-bit TEB; what FS-relative accesses add to their offset.</summary>
        public uint FsBase;

        /// <summary>Instructions retired, for tests and for bounding runaway guests.</summary>
        public long Retired;

        public uint Eax { get => R[Reg.Eax]; set => R[Reg.Eax] = value; }
        public uint Ecx { get => R[Reg.Ecx]; set => R[Reg.Ecx] = value; }
        public uint Edx { get => R[Reg.Edx]; set => R[Reg.Edx] = value; }
        public uint Ebx { get => R[Reg.Ebx]; set => R[Reg.Ebx] = value; }
        public uint Esp { get => R[Reg.Esp]; set => R[Reg.Esp] = value; }
        public uint Ebp { get => R[Reg.Ebp]; set => R[Reg.Ebp] = value; }
        public uint Esi { get => R[Reg.Esi]; set => R[Reg.Esi] = value; }
        public uint Edi { get => R[Reg.Edi]; set => R[Reg.Edi] = value; }

        public bool GetFlag(uint flag) => (EFlags & flag) != 0;

        public void SetFlag(uint flag, bool on)
        {
            if (on) EFlags |= flag; else EFlags &= ~flag;
        }

        public CpuState Clone()
        {
            var copy = new CpuState { Eip = Eip, EFlags = EFlags, FsBase = FsBase, Retired = Retired };
            System.Array.Copy(R, copy.R, 8);
            return copy;
        }

        public override string ToString() =>
            $"eax={Eax:X8} ecx={Ecx:X8} edx={Edx:X8} ebx={Ebx:X8} esp={Esp:X8} ebp={Ebp:X8} " +
            $"esi={Esi:X8} edi={Edi:X8} eip={Eip:X8} fl={EFlags & 0xFFF:X3}";
    }
}
