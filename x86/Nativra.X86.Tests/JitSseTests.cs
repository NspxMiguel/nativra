using System;
using System.Collections.Generic;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>JIT against interpreter for the SSE/SSE2 forms the block translator runs natively.</summary>
    public sealed class JitSseTests
    {
        private static readonly uint[] Floats =
        {
            0x00000000, 0x80000000, 0x3FC00000, 0x40200000, 0xC0200000, 0x3F000000, 0xBF000000,
            0x3EFFFFFF, 0x4EFFFFFF, 0x4F000000, 0xCF000000, 0xCF000001, 0x4F32D05E, 0x7FC00000,
            0x7F800001, 0xFFC00001, 0x7F800000, 0xFF800000, 0x00000001, 0x4B800001, 0x47F12064,
            0x7149F2CA, 0x007FFFFF, 0x4B000001, 0xBF800000, 0x3F800000, 0x4EFFFFFE, 0xCEFFFFFF,
        };

        private static readonly ulong[] Doubles =
        {
            0x0000000000000000, 0x8000000000000000, 0x3FF8000000000000, 0x4004000000000000,
            0xC004000000000000, 0x3FE0000000000000, 0x41DFFFFFFFE00000, 0x41E0000000000000,
            0xC1E0000000100000, 0xC1E0000000200000, 0x41F0000000000000, 0x7FF8000000000000,
            0x7FF0000000000001, 0xFFF8000000000001, 0x7FF0000000000000, 0xFFF0000000000000,
            0x00000000000007E8, 0x7FE0000000000000, 0x47EFFFFFE0000000, 0x47F0000000000000,
            0x36A0000000000000, 0x369C000000000000, 0x4170000000100000, 0x3FF0000010000000,
            0x41DFFFFFFFC00000, 0xC1E0000000000000, 0xC1DFFFFFFFC00000, 0x0010000000000000,
            0x380FFFFFE0000000, 0x47E00000000000F0, 0x4330000000000001, 0x43E0000000000000,
            0xC3E0000000000000, 0x43DFFFFFFFFFFFFF,
        };

        private static readonly uint[] Ints =
        {
            0, 1, 0xFFFFFFFF, 0x01000001, 0x7FFFFFFF, 0x80000000, 0x80000001, 0x075BCD15,
            0x00FFFFFF, 0x01000003, 0xFEFFFFFF, 0x40000001, 0x7FFFFF80, 0x7FFFFFC0,
        };

        // hex, uses MXCSR.RC (so only the default rounding mode runs natively)
        public static IEnumerable<object[]> ScalarConversions()
        {
            foreach (var prefix in new[] { "F3", "F2" })
            {
                foreach (var modrm in new[] { "C1", "06", "07", "DA", "F1", "E9", "EA", "FB" })
                {
                    yield return new object[] { $"{prefix} 0F 5A {modrm}", false };
                    yield return new object[] { $"{prefix} 0F 2C {modrm}", false };
                    yield return new object[] { $"{prefix} 0F 2D {modrm}", true };
                }
            }
        }

        public static IEnumerable<object[]> PackedConversions()
        {
            foreach (var modrm in new[] { "C1", "06", "07", "DA", "F9" })
            {
                yield return new object[] { $"0F 5A {modrm}", false };
                yield return new object[] { $"66 0F 5A {modrm}", false };
                yield return new object[] { $"0F 5B {modrm}", false };
                yield return new object[] { $"F3 0F 5B {modrm}", false };
                yield return new object[] { $"66 0F 5B {modrm}", true };
                yield return new object[] { $"F3 0F E6 {modrm}", false };
                yield return new object[] { $"66 0F E6 {modrm}", false };
                yield return new object[] { $"F2 0F E6 {modrm}", true };
            }
        }

        [SkippableTheory]
        [MemberData(nameof(ScalarConversions))]
        [MemberData(nameof(PackedConversions))]
        public void ConversionMatchesInterpreter(string hex, bool rounds)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                for (var rc = 0; rc < 4; rc++)
                    for (var i = 0; i < Doubles.Length; i++)
                    {
                        var a = Floats[i % Floats.Length];
                        var b = Floats[(i * 7 + 3) % Floats.Length];
                        var c = Floats[(i * 5 + 1) % Floats.Length];
                        var d = Floats[(i * 11 + 2) % Floats.Length];
                        var loPair = i % 2 == 0 ? Doubles[i] : ((ulong)b << 32) | a;
                        var hiPair = i % 3 == 0 ? Doubles[(i + 5) % Doubles.Length] : ((ulong)d << 32) | c;
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) =>
                        {
                            fpu.Mxcsr = 0x1F80u | (uint)(rc << 13);
                            foreach (var reg in new[] { 0, 1, 2, 3, 5, 7 })
                            {
                                fpu.XmmLo[reg] = loPair;
                                fpu.XmmHi[reg] = hiPair;
                            }
                            fpu.XmmLo[0] = 0xAAAAAAAABBBBBBBB;
                            fpu.XmmHi[0] = 0xCCCCCCCCDDDDDDDD;
                            memory.Write64(JitDiff.Data, loPair);
                            memory.Write64(JitDiff.Data + 8, hiPair);
                            memory.Write64(JitDiff.Data + 0x100, hiPair);
                            memory.Write64(JitDiff.Data + 0x108, loPair);
                            cpu.Esp = 0x7777;
                        });
                        diff.Run($"{hex} rc={rc} #{i}", expectTranslated: !rounds || rc == 0);
                    }
            }
        }

        public static IEnumerable<object[]> IntegerSources()
        {
            foreach (var prefix in new[] { "F3", "F2" })
                foreach (var modrm in new[] { "C1", "06", "07", "C5", "C6", "C7", "C4", "EA", "FD" })
                    yield return new object[] { $"{prefix} 0F 2A {modrm}" };
        }

        [SkippableTheory]
        [MemberData(nameof(IntegerSources))]
        public void ScalarIntegerToFloatMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var value in Ints)
                {
                    diff.Reset();
                    diff.Both((cpu, memory, fpu) =>
                    {
                        cpu.Ecx = cpu.Ebp = cpu.Esi = cpu.Edi = cpu.Esp = cpu.Edx = value;
                        cpu.Ebp = value;
                        memory.Write32(JitDiff.Data, value);
                        memory.Write32(JitDiff.Data + 0x100, value);
                        fpu.XmmLo[0] = 0x1122334455667788;
                        fpu.XmmHi[0] = 0x99AABBCCDDEEFF00;
                        fpu.XmmLo[1] = 0xFEDCBA9876543210;
                        fpu.XmmHi[2] = 0x0123456789ABCDEF;
                        fpu.XmmLo[5] = 0x0F1E2D3C4B5A6978;
                    });
                    // Esi/Edi point at the data in the memory forms: keep those two usable.
                    if (hex.EndsWith("06") || hex.EndsWith("07"))
                        diff.Both((cpu, memory, fpu) => { cpu.Esi = JitDiff.Data; cpu.Edi = JitDiff.Data + 0x100; });
                    diff.Run($"{hex} {value:X8}");
                }
            }
        }

        [SkippableTheory]
        [InlineData("66 0F 73 D1 05")]
        [InlineData("66 0F 73 D1 40")]
        [InlineData("66 0F 73 F1 3F")]
        [InlineData("66 0F 73 F1 00")]
        [InlineData("66 0F 73 D9 03")]
        [InlineData("66 0F 73 F9 0F")]
        [InlineData("66 0F 73 F9 10")]
        [InlineData("66 0F 72 D1 07")]
        [InlineData("66 0F 72 E1 1F")]
        [InlineData("66 0F 72 E1 40")]
        [InlineData("66 0F 72 F1 20")]
        [InlineData("66 0F 71 D2 04")]
        [InlineData("66 0F 71 E2 10")]
        [InlineData("66 0F 71 F2 09")]
        public void ShiftByImmediateMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                for (var i = 0; i < Doubles.Length; i++)
                {
                    diff.Reset();
                    diff.Both((cpu, memory, fpu) =>
                    {
                        for (var r = 0; r < 8; r++)
                        {
                            fpu.XmmLo[r] = Doubles[(i + r) % Doubles.Length] ^ 0x8123456789ABCDEF;
                            fpu.XmmHi[r] = Doubles[(i + r * 3 + 1) % Doubles.Length];
                        }
                    });
                    diff.Run($"{hex} #{i}");
                }
            }
        }

        public static IEnumerable<object[]> PackedIntegerOps()
        {
            var ops = new List<int>();
            for (var op = 0x60; op <= 0x6D; op++) ops.Add(op);
            ops.AddRange(new[] { 0x74, 0x75, 0x76, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5 });
            for (var op = 0xD8; op <= 0xDF; op++) ops.Add(op);
            for (var op = 0xE0; op <= 0xE5; op++) ops.Add(op);
            for (var op = 0xE8; op <= 0xEF; op++) ops.Add(op);
            for (var op = 0xF1; op <= 0xF6; op++) ops.Add(op);
            for (var op = 0xF8; op <= 0xFE; op++) ops.Add(op);
            foreach (var op in ops)
            {
                yield return new object[] { $"66 0F {op:X2} C1" };
                yield return new object[] { $"66 0F {op:X2} 06" };
            }
        }

        [SkippableTheory]
        [MemberData(nameof(PackedIntegerOps))]
        public void PackedIntegerMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                var rng = new Random(hex.GetHashCode());
                for (var i = 0; i < 40; i++)
                {
                    var values = new ulong[8];
                    for (var k = 0; k < values.Length; k++)
                    {
                        values[k] = (ulong)rng.NextInt64();
                        if (i % 5 == 1) values[k] &= 0x00FF00FF00FF00FF;
                        if (i % 5 == 2) values[k] |= 0x8000800080008000;
                        if (i % 5 == 3) values[k] = k % 2 == 0 ? 0x7FFF7FFF7FFF7FFF : 0x8000800080008000;
                        if (i % 7 == 4) values[k] = k % 2 == 0 ? ulong.MaxValue : 0;
                        if (i % 7 == 5) values[k] = (ulong)(rng.Next(0, 70));
                    }
                    diff.Reset();
                    diff.Both((cpu, memory, fpu) =>
                    {
                        fpu.XmmLo[0] = values[0]; fpu.XmmHi[0] = values[1];
                        fpu.XmmLo[1] = values[2]; fpu.XmmHi[1] = values[3];
                        memory.Write64(JitDiff.Data, values[4]);
                        memory.Write64(JitDiff.Data + 8, values[5]);
                    });
                    diff.Run($"{hex} #{i}");
                }
            }
        }

        [SkippableTheory]
        [InlineData("F3 0F 70 C1 1B")]
        [InlineData("F2 0F 70 C1 4E")]
        [InlineData("F3 0F 70 06 E4")]
        [InlineData("F2 0F 70 06 39")]
        [InlineData("66 0F 70 C1 93")]
        [InlineData("66 0F D7 C1")]
        [InlineData("66 0F D7 F1")]
        [InlineData("0F 50 C1")]
        [InlineData("66 0F 50 F1")]
        [InlineData("F3 0F 7E C1")]
        [InlineData("F3 0F 7E 06")]
        [InlineData("66 0F D6 C1")]
        [InlineData("66 0F D6 07")]
        [InlineData("66 0F 7E C1")]
        [InlineData("66 0F 7E CD")]
        [InlineData("66 0F 7E 07")]
        [InlineData("66 0F E7 07")]
        [InlineData("0F 13 07")]
        [InlineData("66 0F 17 07")]
        [InlineData("0F 12 C1")]
        [InlineData("0F 16 C1")]
        [InlineData("66 0F 16 06")]
        [InlineData("0F 2B 07")]
        [InlineData("0F 15 C1")]
        [InlineData("66 0F 15 06")]
        [InlineData("0F C6 C1 1B")]
        [InlineData("66 0F C6 C1 01")]
        [InlineData("0F C6 06 E4")]
        public void MovesAndShufflesMatchInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                for (var i = 0; i < 16; i++)
                {
                    diff.Reset();
                    diff.Both((cpu, memory, fpu) =>
                    {
                        fpu.XmmLo[0] = 0x0807060504030201UL * (ulong)(i + 1);
                        fpu.XmmHi[0] = 0x8070605040302010UL ^ (ulong)i;
                        fpu.XmmLo[1] = (i % 2 == 0 ? 0x80FF7F00UL : 0x01020304UL) | ((ulong)(i * 0x11111111) << 32);
                        fpu.XmmHi[1] = 0xF0E0D0C0B0A09080UL + (ulong)i;
                        memory.Write64(JitDiff.Data, 0x1122334455667788UL ^ (ulong)i);
                        memory.Write64(JitDiff.Data + 8, 0x99AABBCCDDEEFF00UL + (ulong)i);
                        cpu.Ebp = 0xCAFEBABE;
                    });
                    diff.Run($"{hex} #{i}");
                }
            }
        }

        [SkippableTheory]
        [InlineData("F3 0F 51 C1")]
        [InlineData("F2 0F 51 C1")]
        [InlineData("0F 51 C1")]
        [InlineData("66 0F 51 06")]
        [InlineData("F3 0F 5D C1")]
        [InlineData("F2 0F 5F C1")]
        [InlineData("0F 5D 06")]
        [InlineData("66 0F 5F C1")]
        [InlineData("F3 0F C2 C1 00")]
        [InlineData("F3 0F C2 C1 01")]
        [InlineData("F2 0F C2 C1 02")]
        [InlineData("0F C2 C1 03")]
        [InlineData("66 0F C2 C1 04")]
        [InlineData("0F C2 06 05")]
        [InlineData("66 0F C2 06 06")]
        [InlineData("0F C2 C1 07")]
        [InlineData("0F 2E C1")]
        [InlineData("66 0F 2E C1")]
        [InlineData("0F 2E 06")]
        [InlineData("66 0F 2E 06")]
        public void FloatingPointMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                for (var i = 0; i < Doubles.Length; i++)
                    for (var j = 0; j < 6; j++)
                    {
                        var a = Doubles[i];
                        var b = Doubles[(i + j * 5 + 1) % Doubles.Length];
                        var fa = ((ulong)Floats[(i * 3) % Floats.Length] << 32) | Floats[(i * 5) % Floats.Length];
                        var fb = ((ulong)Floats[(i + j) % Floats.Length] << 32) | Floats[(i * 7 + j) % Floats.Length];
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) =>
                        {
                            fpu.XmmLo[0] = j % 2 == 0 ? a : fa;
                            fpu.XmmHi[0] = j % 3 == 0 ? b : fb;
                            fpu.XmmLo[1] = j % 2 == 0 ? b : fb;
                            fpu.XmmHi[1] = j % 3 == 0 ? a : fa;
                            memory.Write64(JitDiff.Data, j % 2 == 0 ? b : fb);
                            memory.Write64(JitDiff.Data + 8, j % 3 == 0 ? a : fa);
                        });
                        diff.Run($"{hex} #{i}/{j}");
                    }
            }
        }

        [SkippableFact]
        public void EarlyFallbackExitKeepsTheGuestXmmState()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            // The divide leaves the block before its first SSE instruction runs: the
            // XMM registers must still hold the guest's values when that exit stores them.
            using (var diff = new JitDiff("F7 F1 66 0F 6F C1"))
            {
                diff.Both((cpu, memory, fpu) =>
                {
                    cpu.Ecx = 0;
                    cpu.Edx = 1;
                    for (var r = 0; r < 8; r++) { fpu.XmmLo[r] = 0x1111111111111111UL * (ulong)(r + 1); fpu.XmmHi[r] = ~fpu.XmmLo[r]; }
                });
                diff.Run("divide before sse", expectTranslated: false);
                Assert.Equal(0x1111111111111111UL * 3, diff.ActualFpu.XmmLo[2]);
            }
        }
    }
}
