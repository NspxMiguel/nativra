using System;
using Nativra.X86.Cpu;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>JIT against interpreter: string instructions, cmpxchg, cmpxchg8b and MXCSR.</summary>
    public sealed class JitMiscTests
    {
        [SkippableTheory]
        [InlineData("A4")] [InlineData("A5")] [InlineData("66 A5")] [InlineData("F3 A4")] [InlineData("F3 A5")] [InlineData("F3 66 A5")]
        [InlineData("AA")] [InlineData("AB")] [InlineData("66 AB")] [InlineData("F3 AA")] [InlineData("F3 AB")] [InlineData("F3 66 AB")]
        [InlineData("AC")] [InlineData("AD")] [InlineData("66 AD")] [InlineData("F3 AC")] [InlineData("F3 AD")]
        [InlineData("F2 A5")] [InlineData("F3 A5 F3 AB")] [InlineData("F3 A4 AA AC")]
        public void StringInstructionsMatchInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var count in new uint[] { 0, 1, 2, 5, 33, 100 })
                    foreach (var overlap in new[] { 0x100u, 3u, 0u })
                        foreach (var df in new[] { false, true })
                        {
                            diff.Reset();
                            diff.Both((cpu, memory, fpu) =>
                            {
                                var bytes = new byte[0x300];
                                for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 3);
                                memory.WriteBytes(JitDiff.Data, bytes);
                                cpu.Ecx = count;
                                cpu.Esi = JitDiff.Data + 0x10 + (df ? 0x80u : 0);
                                cpu.Edi = JitDiff.Data + 0x10 + overlap + (df ? 0x80u : 0);
                                cpu.Eax = 0xA1B2C3D4;
                                if (df) cpu.EFlags |= Flag.DF;
                            });
                            diff.Run($"{hex} ecx={count} overlap={overlap:X} df={df}", expectTranslated: !df);
                        }
            }
        }

        [SkippableTheory]
        [InlineData("0F B1 0E")] [InlineData("F0 0F B1 0E")] [InlineData("0F B1 CA")] [InlineData("0F B0 0E")]
        [InlineData("F0 0F B0 0E")] [InlineData("66 0F B1 0E")] [InlineData("0F B0 CA")] [InlineData("0F B1 2E")]
        [InlineData("0F B1 C8")] [InlineData("0F B0 E1")] [InlineData("0F B0 0F")] [InlineData("0F B1 3F")]
        [InlineData("F0 0F C7 0E")] [InlineData("0F C7 0E")] [InlineData("F0 0F C7 0F")]
        public void CmpxchgMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                var values = new uint[] { 0, 0x12345678, 0xFFFFFFFF, 0x80000000, 0x000000FF, 0x0000FFFF };
                foreach (var accumulator in values)
                    foreach (var memoryValue in values)
                        foreach (var source in new uint[] { 0, 0xCAFEBABE, 0x7F })
                        {
                            diff.Reset();
                            diff.Both((cpu, memory, fpu) =>
                            {
                                cpu.Eax = accumulator;
                                cpu.Edx = accumulator ^ 0x55555555;
                                cpu.Ecx = source;
                                cpu.Ebx = source * 3 + 1;
                                cpu.Ebp = source ^ 0x0F0F0F0F;
                                memory.Write32(JitDiff.Data, memoryValue);
                                memory.Write32(JitDiff.Data + 4, memoryValue ^ 0xAAAAAAAA);
                                memory.Write32(JitDiff.Data + 0x100, memoryValue);
                                memory.Write32(JitDiff.Data + 0x104, memoryValue ^ 0xAAAAAAAA);
                            });
                            diff.Run($"{hex} eax={accumulator:X} mem={memoryValue:X} src={source:X}", expectTranslated: !hex.EndsWith("B0 E1"));
                            // The 64-bit compare, equal case.
                            if (hex.EndsWith("C7 0E") || hex.EndsWith("C7 0F"))
                            {
                                diff.Reset();
                                diff.Both((cpu, memory, fpu) =>
                                {
                                    cpu.Eax = accumulator; cpu.Edx = accumulator ^ 0x55555555;
                                    cpu.Ecx = source; cpu.Ebx = source + 9;
                                    memory.Write32(JitDiff.Data, accumulator); memory.Write32(JitDiff.Data + 4, accumulator ^ 0x55555555);
                                    memory.Write32(JitDiff.Data + 0x100, accumulator); memory.Write32(JitDiff.Data + 0x104, accumulator ^ 0x55555555);
                                });
                                diff.Run($"{hex} equal {accumulator:X}");
                            }
                        }
            }
        }

        [SkippableTheory]
        [InlineData("0F AE 1E")] [InlineData("0F AE 16")] [InlineData("0F AE 16 0F AE 1F")]
        [InlineData("0F AE 16 F3 0F 2D C1")] [InlineData("0F AE 16 F2 0F 2D C1")] [InlineData("0F AE 16 F3 0F 2C C1")]
        [InlineData("0F AE 16 F3 0F 5A C1 0F AE 1F")]
        public void MxcsrMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var value in new uint[] { 0x1F80, 0x1F80 | 0x2000, 0x1F80 | 0x4000, 0x1F80 | 0x6000, 0x9FC0, 0xFFFF, 0 })
                    foreach (var start in new uint[] { 0x1F80, 0x5F80 })
                    {
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) =>
                        {
                            fpu.Mxcsr = start;
                            memory.Write32(JitDiff.Data, value);
                            memory.Write32(JitDiff.Data + 0x100, 0x12345678);
                            fpu.XmmLo[1] = BitConverter.SingleToUInt32Bits(2.5f);
                        });
                        diff.Run($"{hex} mxcsr={value:X} start={start:X}", expectTranslated: !hex.Contains("2D") || (value & 0x6000) == 0);
                    }
            }
        }
    }
}
