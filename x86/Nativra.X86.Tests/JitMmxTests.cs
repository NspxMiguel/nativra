using System;
using System.Collections.Generic;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>JIT against interpreter for MMX, including its aliasing of the x87 register file.</summary>
    public sealed class JitMmxTests
    {
        private static readonly int[] BinaryOps =
        {
            0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B,
            0x74, 0x75, 0x76, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
            0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE,
        };

        public static IEnumerable<object[]> Binary()
        {
            foreach (var op in BinaryOps)
            {
                yield return new object[] { $"0F {op:X2} C1" };
                yield return new object[] { $"0F {op:X2} 06" };
            }
        }

        public static IEnumerable<object[]> Others()
        {
            foreach (var hex in new[]
            {
                "0F 6E C1", "0F 6E 06", "0F 6E C5", "0F 7E C8", "0F 7E 0F", "0F 7E E8", "0F 6F C1", "0F 6F 06", "0F 7F C8",
                "0F 7F 07", "0F E7 07", "0F 77", "0F 70 C1 1B", "0F 70 06 E4", "0F 71 D1 05", "0F 71 E1 03",
                "0F 71 F1 02", "0F 71 D1 10", "0F 72 D1 07", "0F 72 E2 40", "0F 72 F1 1F", "0F 72 E1 1F", "0F 73 D1 05",
                "0F 73 F1 3F", "0F 73 D1 40", "0F C4 C1 02", "0F C4 06 05", "0F C5 C1 03", "0F C5 D1 01", "0F D7 C1",
                "0F 6F C1 D9 E8", "0F 6F C1 0F 77 D9 E8 DD D8", "D9 E8 0F FC C1 0F 77", "D9 E8 D9 E8 0F 7E C8 0F 77",
                "0F 77 0F 6E C1 DD 06 0F 77",
            })
                yield return new object[] { hex };
        }

        private static void SetState(Nativra.X86.Cpu.FpuUnit fpu, int variant, Random rng)
        {
            var image = new byte[512];
            image[0] = 0x7F; image[1] = 0x03;
            image[24] = 0x80; image[25] = 0x1F;
            var top = variant == 1 ? 3 : variant == 2 ? 5 : 0;
            var status = (ushort)(top << 11);
            image[2] = (byte)status; image[3] = (byte)(status >> 8);
            image[4] = (byte)(variant == 1 ? 0xFF : variant == 3 ? 0x0F : 0);
            for (var i = 0; i < 8; i++)
            {
                ulong m;
                if (variant == 1 || variant == 3) Nativra.X86.Cpu.FpuUnit.FromDouble(rng.NextDouble() * 100 - 50, out m, out var se);
                else m = (ulong)rng.NextInt64() * 3;
                if (variant == 0 || variant == 2)
                    m = rng.Next(4) == 0 ? 0x00FF00FF00FF00FFUL : rng.Next(3) == 0 ? (ulong)rng.Next(0, 70) : m;
                for (var b = 0; b < 8; b++) image[32 + i * 16 + b] = (byte)(m >> (8 * b));
                var exp = (ushort)(variant == 1 || variant == 3 ? 0x4001 : 0xFFFF);
                image[40 + i * 16] = (byte)exp; image[41 + i * 16] = (byte)(exp >> 8);
            }
            fpu.ReadFxsave(image);
        }

        [SkippableTheory]
        [MemberData(nameof(Binary))]
        [MemberData(nameof(Others))]
        public void MmxMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                var rng = new Random(hex.GetHashCode() ^ 0x5A5A);
                for (var i = 0; i < 24; i++)
                {
                    var variant = i % 4;
                    var seed = rng.Next();
                    var operand = i % 5 == 0 ? (ulong)rng.Next(0, 70) : (ulong)rng.NextInt64();
                    if (i % 7 == 3) operand = 0x8000800080008000;
                    diff.Reset();
                    diff.Both((cpu, memory, fpu) =>
                    {
                        SetState(fpu, variant, new Random(seed));
                        memory.Write64(JitDiff.Data, operand);
                        memory.Write64(JitDiff.Data + 0x100, 0x0123456789ABCDEF ^ operand);
                        cpu.Ecx = 0xFEDCBA98 ^ (uint)seed;
                        cpu.Ebp = 0x13572468;
                    });
                    diff.Run($"{hex} #{i} variant={variant}", expectTranslated: !hex.Contains("D9 E8") && !hex.Contains("DD 06"));
                }
            }
        }

        [SkippableFact]
        public void CommonMmxSequenceRunsInOneBlock()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            // movq mm0,[esi]; paddw mm0,[esi+8]; punpcklbw mm0,mm1; movq [edi],mm0; emms
            using (var diff = new JitDiff("0F 6F 06 0F FD 46 08 0F 60 C1 0F 7F 07 0F 77"))
            {
                diff.Both((cpu, memory, fpu) =>
                {
                    memory.Write64(JitDiff.Data, 0x7FFF0001FFFF8000);
                    memory.Write64(JitDiff.Data + 8, 0x0001000100010001);
                });
                diff.Run("mmx sequence");
                Assert.Equal(0, diff.Jit.InterpreterFallbacks);
            }
        }
    }
}
