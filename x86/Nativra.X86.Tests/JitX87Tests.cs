using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>JIT against interpreter for x87: every stack state, control word and edge value worth trying.</summary>
    public sealed class JitX87Tests
    {
        private struct Reg
        {
            public ulong M;
            public ushort Se;
            public Reg(ulong m, ushort se) { M = m; Se = se; }
            public static Reg D(double v) { FpuUnit.FromDouble(v, out var m, out var se); return new Reg(m, se); }
        }

        private static readonly double[] NormalDoubles =
        {
            1.5, -2.25, 3.141592653589793, -1e-10, 0.1, 123.789, 2.0, -7.0, 100.5, 0.3,
            -1.0, 8.5, 1e-20, 300.25, 16.0,
        };

        private static readonly double[] EdgeDoubles =
        {
            0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1e-310, 5e-324,
            double.MaxValue, 9223372036854775808.0, -9223372036854775808.0, 2147483648.0, -2147483649.0,
            32768.0, -32769.0, 0.5, -0.5, 2.5, -2.5, 3.5e38, 1e39, 1e-46, 1e-40, 4503599627370497.5,
        };

        private static readonly Reg[] RawRegs =
        {
            new Reg(0x8000000000000001, 0x3FFF), new Reg(0xFFFFFFFFFFFFFFFF, 0x3FFF),
            new Reg(0x8000000000000400, 0x3FFF), new Reg(0x8000000000000C00, 0x3FFF),
            new Reg(0x80000000000003FF, 0x3FFF), new Reg(0x8000000000000401, 0xBFFF),
            new Reg(0xFFFFFFFFFFFFFFFF, 0x43FE), new Reg(0xFFFFFFFFFFFFFC00, 0x43FE),
            new Reg(0x8000000000000000, 0x3C01), new Reg(0x8000000000000000, 0x3C00),
            new Reg(0xFFFFFFFFFFFFFFFF, 0x3C01), new Reg(0x8000000000000000, 0x43FF),
            new Reg(0x8000000000000000, 0x7FFF), new Reg(0xC000000000000000, 0x7FFF),
            new Reg(0x8000000000000000, 0x0000), new Reg(0x0000000000000001, 0x0000),
            new Reg(0x4000000000000000, 0x3FFF), new Reg(0x0000000000000000, 0x3FFF),
            new Reg(0x0000000000000000, 0x8000), new Reg(0x1234567890ABCDEF, 0x4001),
        };

        private static readonly long[] Ints =
        {
            0, 1, -1, 32767, -32768, 32768, -32769, 40000, 2147483647, -2147483648, 123456789,
            2147483648L, -2147483649L, 9223372036854775807L, long.MinValue, 16777217, 9007199254740993L,
        };

        private static Reg Pick(int k, bool normal)
        {
            if (normal) return Reg.D(NormalDoubles[k % NormalDoubles.Length]);
            var n = EdgeDoubles.Length + RawRegs.Length + NormalDoubles.Length;
            var i = k % n;
            if (i < EdgeDoubles.Length) return Reg.D(EdgeDoubles[i]);
            i -= EdgeDoubles.Length;
            if (i < RawRegs.Length) return RawRegs[i];
            return Reg.D(NormalDoubles[i - RawRegs.Length]);
        }

        /// <summary>Loads an x87 state: the given top, depth values (stack order) and control word.</summary>
        private static void SetX87(FpuUnit fpu, int top, int depth, ushort control, Func<int, Reg> value, uint mxcsr = 0x1F80)
        {
            var image = new byte[512];
            image[0] = (byte)control; image[1] = (byte)(control >> 8);
            var status = (ushort)(top << 11);
            image[2] = (byte)status; image[3] = (byte)(status >> 8);
            byte abridged = 0;
            for (var i = 0; i < depth; i++) abridged |= (byte)(1 << ((top + i) & 7));
            image[4] = abridged;
            image[24] = (byte)mxcsr; image[25] = (byte)(mxcsr >> 8);
            for (var i = 0; i < 8; i++)
            {
                var reg = i < depth ? value(i) : new Reg(0xDEADBEEFCAFEF00D, 0x1234);
                for (var b = 0; b < 8; b++) image[32 + i * 16 + b] = (byte)(reg.M >> (8 * b));
                image[40 + i * 16] = (byte)reg.Se; image[41 + i * 16] = (byte)(reg.Se >> 8);
            }
            fpu.ReadFxsave(image);
        }

        public enum Mem { None, F32, F64, I16, I32, I64, Raw80, Word }

        private static void WriteOperand(GuestMemory memory, Mem kind, int k, bool normal)
        {
            var reg = Pick(k * 7 + 1, normal);
            if (normal) k++;   // ordinary operands: no zero divisors from the integer table
            var d = FpuUnit.ToDouble(reg.M, reg.Se);
            switch (kind)
            {
                case Mem.F32: memory.Write32(JitDiff.Data, (uint)BitConverter.SingleToInt32Bits((float)d)); break;
                case Mem.F64: memory.Write64(JitDiff.Data, (ulong)BitConverter.DoubleToInt64Bits(d)); break;
                case Mem.I16: memory.Write16(JitDiff.Data, (ushort)Ints[k % Ints.Length]); break;
                case Mem.I32: memory.Write32(JitDiff.Data, (uint)Ints[k % Ints.Length]); break;
                case Mem.I64: memory.Write64(JitDiff.Data, (ulong)Ints[k % Ints.Length]); break;
                case Mem.Raw80: memory.Write64(JitDiff.Data, reg.M); memory.Write16(JitDiff.Data + 8, reg.Se); break;
                case Mem.Word: memory.Write16(JitDiff.Data, (ushort)(0x037F ^ (k * 0x0481))); break;
            }
            // Something in the destination too, so a store that does not happen shows up.
            memory.Write64(JitDiff.Data + 0x10, 0x1122334455667788);
            memory.Write16(JitDiff.Data + 0x18, 0x99AA);
        }

        private static readonly ushort[] Controls = { 0x037F, 0x027F, 0x007F, 0x0B7F, 0x0F7F, 0x077F };

        private void Sweep(string hex, Mem kind, bool normal, int[] depths, ushort[] controls, int rounds, bool mustTranslate)
        {
            using (var diff = new JitDiff(hex))
            {
                foreach (var depth in depths)
                    foreach (var control in controls)
                        foreach (var top in new[] { 0, 5 })
                            for (var k = 0; k < rounds; k++)
                            {
                                diff.Reset();
                                var kk = k;
                                diff.Both((cpu, memory, fpu) =>
                                {
                                    SetX87(fpu, top, depth, control, i => Pick(kk * 3 + i * 5, normal));
                                    WriteOperand(memory, kind, kk, normal);
                                    cpu.Eax = 0x80FF8103;
                                });
                                diff.Run($"{hex} depth={depth} cw={control:X4} top={top} k={k}", expectTranslated: mustTranslate);
                                if (mustTranslate) Assert.Equal(0, diff.Jit.InterpreterFallbacks);
                            }
            }
        }

        private static readonly int[] AllDepths = { 0, 1, 2, 8 };
        private static readonly ushort[] Quick = { 0x037F, 0x027F, 0x007F, 0x0F7F };

        [SkippableFact]
        public void EnvironmentStoreMatchesInterpreterForEveryTagClass()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            Sweep("D9 36", Mem.None, normal: false, AllDepths, Quick, 24, mustTranslate: true);
        }

        [SkippableFact]
        public void EnvironmentLoadMatchesInterpreterForTagsAndTop()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff("D9 26"))
            {
                foreach (var tag in new ushort[] { 0, 0xFFFF, 0xAAAA, 0x5555, 0xE4B1 })
                    for (var top = 0; top < 8; top++)
                    {
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) =>
                        {
                            SetX87(fpu, 5, 4, 0x037F, i => Pick(i + top, false));
                            memory.Write32(JitDiff.Data, 0xFFFF027F);
                            memory.Write32(JitDiff.Data + 4, (uint)(0xFFFF0000 | 0x0041 | (top << 11)));
                            memory.Write32(JitDiff.Data + 8, (uint)(0xFFFF0000 | tag));
                        });
                        diff.Run($"fldenv tag={tag:X4} top={top}");
                        Assert.Equal(0, diff.Jit.InterpreterFallbacks);
                    }
            }
        }

        public static IEnumerable<object[]> MemoryForms()
        {
            var forms = new (string hex, Mem kind)[]
            {
                ("D9 06", Mem.F32), ("DD 06", Mem.F64), ("D9 16", Mem.F32), ("D9 1E", Mem.F32),
                ("DD 16", Mem.F64), ("DD 1E", Mem.F64), ("DB 06", Mem.I32), ("DF 06", Mem.I16),
                ("DF 2E", Mem.I64), ("DB 16", Mem.I32), ("DB 1E", Mem.I32), ("DB 0E", Mem.I32),
                ("DF 16", Mem.I16), ("DF 1E", Mem.I16), ("DF 0E", Mem.I16), ("DF 3E", Mem.I64),
                ("DD 0E", Mem.I64), ("DB 2E", Mem.Raw80), ("DB 3E", Mem.Raw80), ("D9 2E", Mem.Word),
                ("D9 3E", Mem.Word), ("DD 3E", Mem.None), ("DF E0", Mem.None),
                ("D9 86 10 00 00 00", Mem.F32), ("DD 46 20", Mem.F64), ("DD 5E 30", Mem.F64),
            };
            foreach (var (hex, kind) in forms) yield return new object[] { hex, kind };
        }

        [SkippableTheory]
        [MemberData(nameof(MemoryForms))]
        public void LoadsAndStoresMatchInterpreter(string hex, Mem kind)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            Sweep(hex, kind, normal: false, AllDepths, Quick, 24, mustTranslate: false);
        }

        [SkippableTheory]
        [MemberData(nameof(MemoryForms))]
        public void CommonLoadsAndStoresRunNatively(string hex, Mem kind)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            // Ordinary stacks and values with the default or 53-bit control word.
            Sweep(hex, kind, normal: true, new[] { 2 }, new ushort[] { 0x037F, 0x027F }, 6, mustTranslate: true);
        }

        public static IEnumerable<object[]> MemoryArithmetic()
        {
            foreach (var (esc, kind) in new[] { (0xD8, Mem.F32), (0xDC, Mem.F64), (0xDA, Mem.I32), (0xDE, Mem.I16) })
                for (var reg = 0; reg < 8; reg++)
                    yield return new object[] { $"{esc:X2} {(0x06 | (reg << 3)):X2}", kind };
        }

        [SkippableTheory]
        [MemberData(nameof(MemoryArithmetic))]
        public void MemoryArithmeticMatchesInterpreter(string hex, Mem kind)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            Sweep(hex, kind, normal: false, AllDepths, Quick, 24, mustTranslate: false);
        }

        [SkippableTheory]
        [MemberData(nameof(MemoryArithmetic))]
        public void CommonMemoryArithmeticRunsNatively(string hex, Mem kind)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            Sweep(hex, kind, normal: true, new[] { 2 }, new ushort[] { 0x037F, 0x027F, 0x007F }, 8, mustTranslate: true);
        }

        public static IEnumerable<object[]> RegisterForms()
        {
            foreach (var i in new[] { 0, 1, 3, 7 })
            {
                foreach (var esc in new[] { 0xD8, 0xDC, 0xDE })
                    for (var reg = 0; reg < 8; reg++)
                    {
                        if (esc == 0xDE && reg == 2) continue;
                        yield return new object[] { $"{esc:X2} {(0xC0 | (reg << 3) | i):X2}" };
                    }
                foreach (var b in new[] { 0xC0, 0xC8, 0xD0, 0xD8, 0xE0, 0xE8 })
                    yield return new object[] { $"DD {(b | i):X2}" };
                yield return new object[] { $"D9 {(0xC0 | i):X2}" };
                yield return new object[] { $"D9 {(0xC8 | i):X2}" };
                yield return new object[] { $"DB {(0xE8 | i):X2}" };
                yield return new object[] { $"DB {(0xF0 | i):X2}" };
                yield return new object[] { $"DF {(0xE8 | i):X2}" };
                yield return new object[] { $"DF {(0xF0 | i):X2}" };
            }
            foreach (var hex in new[] { "D9 E0", "D9 E1", "D9 E8", "D9 EE", "D9 FA", "D9 FC", "DA E9", "DE D9", "DF E0", "D9 D0" })
                yield return new object[] { hex };
        }

        [SkippableTheory]
        [MemberData(nameof(RegisterForms))]
        public void RegisterFormsMatchInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            Sweep(hex, Mem.None, normal: false, new[] { 0, 2, 3, 8 }, Quick, 4, mustTranslate: false);
        }

        [SkippableTheory]
        [InlineData("D8 C1")] [InlineData("D8 C9")] [InlineData("D8 E1")] [InlineData("D8 E9")] [InlineData("D8 F1")] [InlineData("D8 F9")]
        [InlineData("DC C2")] [InlineData("DC CA")] [InlineData("DC E2")] [InlineData("DC EA")] [InlineData("DC F2")] [InlineData("DC FA")]
        [InlineData("DE C1")] [InlineData("DE C9")] [InlineData("DE E1")] [InlineData("DE E9")] [InlineData("DE F1")] [InlineData("DE F9")]
        [InlineData("D8 D1")] [InlineData("D8 D9")] [InlineData("DD E1")] [InlineData("DD E9")] [InlineData("DE D9")] [InlineData("DA E9")]
        [InlineData("DB E9")] [InlineData("DB F1")] [InlineData("DF E9")] [InlineData("DF F1")]
        [InlineData("D9 C9")] [InlineData("D9 C2")] [InlineData("D9 C0")] [InlineData("DD D2")] [InlineData("DD DA")] [InlineData("DD D8")]
        [InlineData("D9 E0")] [InlineData("D9 E1")] [InlineData("D9 E8")] [InlineData("D9 EE")] [InlineData("D9 FC")]
        public void CommonRegisterFormsRunNatively(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            Sweep(hex, Mem.None, normal: true, new[] { 3 }, new ushort[] { 0x037F, 0x027F, 0x007F }, 8, mustTranslate: true);
        }

        [SkippableFact]
        public void ReturnsStatusWordIntoAx()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            // fld1; fld1; fcom st(1); fnstsw ax; two fstp st(0).
            using (var diff = new JitDiff("D9 E8 D9 E8 D8 D1 DF E0 DD D8 DD D8"))
            {
                diff.Both((cpu, memory, fpu) => { });
                diff.Run("compare status");
            }
        }

        [SkippableFact]
        public void FlagsSurviveArithmetic()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff("39 C1 D9 06 D8 8E 10 00 00 00 D9 1E"))
            {
                diff.Both((cpu, memory, fpu) =>
                {
                    memory.Write32(JitDiff.Data, BitConverter.SingleToUInt32Bits(1.5f));
                    memory.Write32(JitDiff.Data + 0x10, BitConverter.SingleToUInt32Bits(4.0f));
                });
                diff.Run("flags around x87");
            }
        }

        [SkippableFact]
        public void MultiplyAndStoreSequenceRunsInOneBlock()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            // fld dword [esi]; fmul dword [esi+4]; fadd dword [esi+8]; fstp dword [edi]
            using (var diff = new JitDiff("D9 06 D8 4E 04 D8 46 08 D9 1F"))
            {
                diff.Both((cpu, memory, fpu) =>
                {
                    memory.Write32(JitDiff.Data, BitConverter.SingleToUInt32Bits(1.5f));
                    memory.Write32(JitDiff.Data + 4, BitConverter.SingleToUInt32Bits(-3.25f));
                    memory.Write32(JitDiff.Data + 8, BitConverter.SingleToUInt32Bits(100.0f));
                    cpu.Edi = JitDiff.Data + 0x20;
                });
                diff.Run("fld fmul fadd fstp");
                Assert.Equal(0, diff.Jit.InterpreterFallbacks);
            }
        }
    }
}
