using System;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// JIT against interpreter: bit tests, double shifts, bit scans, LAHF/SAHF,
    /// the high-byte forms and the no-operation encodings. The harness compares
    /// every arithmetic flag, so a flag the host leaves undefined has to match too.
    /// </summary>
    public sealed class JitBitsTests
    {
        private static readonly uint[] FlagSets =
        {
            Flag.Fixed,
            Flag.Fixed | Flag.CF | Flag.OF,
            Flag.Fixed | Flag.ZF | Flag.PF,
            Flag.Fixed | Flag.SF | Flag.AF,
            Flag.Fixed | Flag.CF | Flag.ZF | Flag.SF | Flag.OF | Flag.PF | Flag.AF,
        };

        private static readonly uint[] Values =
        {
            0, 1, 2, 0x80, 0x8000, 0x80000000, 0xFFFFFFFF, 0x12345678, 0xF0F0F0F0, 0x0000FF00, 0x7FFFFFFF, 0xAAAA5555,
        };

        private static void Check(string hex, Action<CpuState, GuestMemory, FpuUnit> setup, string label, bool expectTranslated = true)
        {
            using (var diff = new JitDiff(hex))
            {
                diff.Reset();
                diff.Both(setup);
                diff.Run(label, expectTranslated);
            }
        }

        [SkippableTheory]
        [InlineData("0F A3 C8")] [InlineData("0F AB C8")] [InlineData("0F B3 C8")] [InlineData("0F BB C8")]
        [InlineData("66 0F A3 C8")] [InlineData("66 0F AB C8")] [InlineData("66 0F B3 C8")] [InlineData("66 0F BB C8")]
        [InlineData("0F A3 0E")] [InlineData("0F AB 0E")] [InlineData("0F B3 0E")] [InlineData("0F BB 0E")]
        [InlineData("66 0F A3 0E")] [InlineData("66 0F AB 0E")] [InlineData("66 0F B3 0E")] [InlineData("66 0F BB 0E")]
        [InlineData("F0 0F AB 0E")] [InlineData("F0 0F B3 0E")] [InlineData("F0 0F BB 0E")]
        [InlineData("0F AB E5")] [InlineData("0F A3 FA")]
        public void BitTestWithRegisterOffsetMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                // The host raises a real fault outside the mapped pages (the guest-fault handler
                // is Windows-only), so a 32-bit offset stays within +-0x8000 bits of the operand.
                var offsets = new uint[] { 0, 1, 5, 15, 16, 31, 32, 33, 63, 100, 0xFFFFFFFF, 0xFFFFFFDF, 0xFFFFFFE0, 0x7FFF, 0xFFFF8000, 0x8001, 0xFFFF, 0xFFF0, 0xFFFFFFF0 };
                var sixteen = hex.StartsWith("66");
                foreach (var flags in FlagSets)
                    foreach (var offset in offsets)
                        foreach (var value in Values)
                        {
                            var fill = new byte[0x2000];
                            for (var i = 0; i < fill.Length; i += 4)
                                BitConverter.TryWriteBytes(new Span<byte>(fill, i, 4), value ^ ((uint)i * 0x01010101));
                            if (!sixteen && (offset == 0xFFFF || offset == 0xFFF0 || offset == 0x8001)) continue;
                            diff.Reset();
                            diff.Both((cpu, memory, fpu) =>
                            {
                                cpu.EFlags = flags;
                                cpu.Ecx = offset;
                                cpu.Esi = JitDiff.Data + 0x1000;
                                cpu.Eax = value;
                                cpu.Edx = value ^ 0xFFFFFFFF;
                                cpu.Ebp = offset;
                                memory.WriteBytes(JitDiff.Data, fill);
                            });
                            diff.Run($"{hex} flags={flags:X} offset={offset:X} value={value:X}", expectTranslated: hex.Contains("0E") || hex.Contains("4E") ? false : true);
                        }
            }
        }

        [SkippableTheory]
        [InlineData("0F BA E0 00")] [InlineData("0F BA E8 05")] [InlineData("0F BA F0 1F")] [InlineData("0F BA F8 20")]
        [InlineData("0F BA E0 3F")] [InlineData("0F BA E8 FF")] [InlineData("0F BA E2 08")] [InlineData("0F BA EC 01")]
        [InlineData("66 0F BA E0 0F")] [InlineData("66 0F BA E8 10")] [InlineData("66 0F BA F0 1F")] [InlineData("66 0F BA F8 07")]
        [InlineData("0F BA 26 00")] [InlineData("0F BA 2E 1F")] [InlineData("0F BA 36 20")] [InlineData("0F BA 3E FF")]
        [InlineData("66 0F BA 26 0F")] [InlineData("66 0F BA 2E 10")] [InlineData("66 0F BA 36 05")] [InlineData("66 0F BA 3E 08")]
        [InlineData("F0 0F BA 2E 07")] [InlineData("F0 0F BA 36 1F")] [InlineData("F0 0F BA 3E 01")]
        [InlineData("0F BA 66 08 03")]
        public void BitTestWithImmediateOffsetMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var flags in FlagSets)
                    foreach (var value in Values)
                    {
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) =>
                        {
                            cpu.EFlags = flags;
                            cpu.Eax = value;
                            cpu.Edx = value ^ 0x5A5A5A5A;
                            cpu.Esp = JitDiff.Stack - 0x20;
                            cpu.Esi = JitDiff.Data + 0x10;
                            memory.Write32(JitDiff.Data + 0x10, value);
                            memory.Write32(JitDiff.Data + 0x14, ~value);
                        });
                        diff.Run($"{hex} flags={flags:X} value={value:X}");
                    }
            }
        }

        [SkippableTheory]
        [InlineData("0F A4 C2 01")] [InlineData("0F A4 C2 05")] [InlineData("0F A4 C2 1F")]
        [InlineData("0F A4 C2 20")] [InlineData("0F A4 C2 21")] [InlineData("0F AC C2 01")]
        [InlineData("0F AC C2 07")] [InlineData("0F AC C2 1F")] [InlineData("0F AC C2 20")] [InlineData("0F AC C2 3F")]
        [InlineData("0F A5 C2")] [InlineData("0F AD C2")] [InlineData("0F A5 06")] [InlineData("0F AD 06")]
        [InlineData("0F A4 06 04")] [InlineData("0F AC 06 04")] [InlineData("0F A5 D8")] [InlineData("0F AD CB")]
        [InlineData("66 0F A4 C2 01")] [InlineData("66 0F A4 C2 05")] [InlineData("66 0F A4 C2 0F")]
        [InlineData("66 0F A4 C2 10")] [InlineData("66 0F A4 C2 11")] [InlineData("66 0F A4 C2 1F")] [InlineData("66 0F AC C2 01")]
        [InlineData("66 0F AC C2 0F")] [InlineData("66 0F AC C2 10")] [InlineData("66 0F AC C2 11")] [InlineData("66 0F AC C2 1F")]
        [InlineData("66 0F A5 C2")] [InlineData("66 0F AD C2")] [InlineData("66 0F A5 06")] [InlineData("66 0F AD 06")]
        [InlineData("66 0F A4 06 03")] [InlineData("66 0F AC 06 12")]
        public void DoubleShiftMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                var counts = new uint[] { 0, 1, 2, 15, 16, 17, 31, 32, 0x101 };
                var pairs = new uint[] { 0, 1, 0x80000000, 0xFFFFFFFF, 0x12345678, 0x8000 };
                // A count that masks to zero, or a 16-bit count above 16, stays with the interpreter.
                var immediateForm = !hex.Contains("A5") && !hex.Contains("AD");
                var fixedCount = Convert.ToUInt32(hex.Substring(hex.Length - 2), 16) & 0x1F;
                var translated = !immediateForm || (fixedCount != 0 && !(hex.StartsWith("66") && fixedCount > 16));
                foreach (var flags in new[] { FlagSets[1], FlagSets[4] })
                    foreach (var count in counts)
                        foreach (var dest in pairs)
                            foreach (var source in pairs)
                            {
                                diff.Reset();
                                diff.Both((cpu, memory, fpu) =>
                                {
                                    cpu.EFlags = flags;
                                    cpu.Ecx = count;
                                    cpu.Edx = dest;
                                    cpu.Eax = source;
                                    cpu.Ebx = dest;
                                    memory.Write32(JitDiff.Data, dest);
                                });
                                diff.Run($"{hex} flags={flags:X} count={count:X} dest={dest:X} src={source:X}", translated);
                            }
            }
        }

        [SkippableTheory]
        [InlineData("0F BC C1")] [InlineData("0F BD C1")] [InlineData("66 0F BC C1")] [InlineData("66 0F BD C1")]
        [InlineData("0F BC 06")] [InlineData("0F BD 06")] [InlineData("66 0F BC 06")] [InlineData("66 0F BD 06")]
        [InlineData("0F BC C0")] [InlineData("0F BD C0")] [InlineData("0F BD ED")] [InlineData("0F BC FE")]
        public void BitScanMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var flags in FlagSets)
                    foreach (var source in Values)
                        foreach (var dest in new uint[] { 0, 0xDEADBEEF, 0xFFFFFFFF })
                        {
                            diff.Reset();
                            diff.Both((cpu, memory, fpu) =>
                            {
                                cpu.EFlags = flags;
                                cpu.Ecx = source;
                                cpu.Eax = dest;
                                cpu.Ebp = source;
                                cpu.Edi = dest;
                                memory.Write32(JitDiff.Data, source);
                            });
                            diff.Run($"{hex} flags={flags:X} src={source:X} dest={dest:X}");
                        }
            }
        }

        [SkippableTheory]
        [InlineData("9F")] [InlineData("9E")] [InlineData("9F 9E")] [InlineData("9E 9F")]
        public void LahfAndSahfMatchInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var flags in FlagSets)
                    foreach (var eax in new uint[] { 0, 0x80FF8103, 0xFFFFFFFF, 0x0000D500, 0x00002A00, 0x00000200, 0xFFFF00FF, 0x12345600 })
                    {
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) => { cpu.EFlags = flags; cpu.Eax = eax; });
                        diff.Run($"{hex} flags={flags:X} eax={eax:X}");
                    }
            }
        }

        [SkippableTheory]
        [InlineData("0F 90 C0")] [InlineData("0F 91 C0")] [InlineData("0F 92 C0")] [InlineData("0F 93 C0")]
        [InlineData("0F 94 C0")] [InlineData("0F 95 C0")] [InlineData("0F 96 C0")] [InlineData("0F 97 C0")]
        [InlineData("0F 98 C0")] [InlineData("0F 99 C0")] [InlineData("0F 9A C0")] [InlineData("0F 9B C0")]
        [InlineData("0F 9C C0")] [InlineData("0F 9D C0")] [InlineData("0F 9E C0")] [InlineData("0F 9F C0")]
        [InlineData("0F 90 C4")] [InlineData("0F 94 C5")] [InlineData("0F 95 C6")] [InlineData("0F 9F C7")]
        [InlineData("0F 92 C3")] [InlineData("0F 94 C1")] [InlineData("0F 9C C2")]
        [InlineData("0F 90 06")] [InlineData("0F 94 06")] [InlineData("0F 92 06")] [InlineData("0F 9F 06")]
        [InlineData("0F 9E 46 05")]
        public void SetccMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                for (var bits = 0u; bits < 64; bits++)
                {
                    var flags = Flag.Fixed | ((bits & 1) != 0 ? Flag.CF : 0) | ((bits & 2) != 0 ? Flag.ZF : 0) |
                        ((bits & 4) != 0 ? Flag.SF : 0) | ((bits & 8) != 0 ? Flag.OF : 0) |
                        ((bits & 16) != 0 ? Flag.PF : 0) | ((bits & 32) != 0 ? Flag.AF : 0);
                    diff.Reset();
                    diff.Both((cpu, memory, fpu) =>
                    {
                        cpu.EFlags = flags;
                        cpu.Eax = 0x80FF81A3; cpu.Ecx = 0x1111A2B2; cpu.Edx = 0x3333C4D4; cpu.Ebx = 0x7654E0F0;
                        memory.Write32(JitDiff.Data, 0xA5A5A5A5);
                        memory.Write32(JitDiff.Data + 4, 0x5A5A5A5A);
                    });
                    diff.Run($"{hex} flags={flags:X}");
                }
            }
        }

        [SkippableTheory]
        [InlineData("B4 12")] [InlineData("B5 80")] [InlineData("B6 FF")] [InlineData("B7 00")]
        [InlineData("B0 12")] [InlineData("B1 34")] [InlineData("B2 56")] [InlineData("B3 78")]
        [InlineData("B4 12 B5 34 B6 56 B7 78")]
        [InlineData("F6 C4 5A")] [InlineData("F6 C5 80")] [InlineData("F6 C6 00")] [InlineData("F6 C7 FF")]
        [InlineData("F6 C0 5A")] [InlineData("F6 C1 80")] [InlineData("F6 C3 01")]
        [InlineData("F6 06 80")] [InlineData("F6 06 01")] [InlineData("F6 46 04 F0")]
        [InlineData("F6 D4")] [InlineData("F6 D5")] [InlineData("F6 D6")] [InlineData("F6 D7")]
        [InlineData("F6 DC")] [InlineData("F6 DD")] [InlineData("F6 DE")] [InlineData("F6 DF")]
        public void HighByteForms(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var flags in FlagSets)
                    foreach (var value in new uint[] { 0, 0x80FF8103, 0xFFFFFFFF, 0x00005A00, 0x0000FF00, 0x12348000 })
                    {
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) =>
                        {
                            cpu.EFlags = flags;
                            cpu.Eax = value; cpu.Ecx = value ^ 0xA5A5A5A5; cpu.Edx = ~value; cpu.Ebx = value * 3;
                            memory.Write32(JitDiff.Data, value);
                            memory.Write32(JitDiff.Data + 4, value);
                        });
                        diff.Run($"{hex} flags={flags:X} value={value:X}");
                    }
            }
        }

        [SkippableTheory]
        [InlineData("91")] [InlineData("92")] [InlineData("93")] [InlineData("94")] [InlineData("95")] [InlineData("96")] [InlineData("97")]
        [InlineData("90")] [InlineData("F3 90")] [InlineData("66 90")] [InlineData("9B")] [InlineData("9B 9B 90")]
        [InlineData("0F 1F 00")] [InlineData("0F 1F 40 00")] [InlineData("0F 1F 44 00 00")] [InlineData("66 0F 1F 44 00 00")]
        [InlineData("0F 1F 80 00 00 00 00")] [InlineData("0F 1F 84 00 00 00 00 00")] [InlineData("66 0F 1F 84 00 00 00 00 00")]
        [InlineData("66 66 0F 1F 84 00 00 00 00 00")] [InlineData("0F 18 06")] [InlineData("0F 0D 0E")] [InlineData("0F 1F C0")]
        public void ExchangeAndNoOperationForms(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff(hex))
            {
                foreach (var flags in FlagSets)
                {
                    diff.Reset();
                    diff.Both((cpu, memory, fpu) => { cpu.EFlags = flags; });
                    diff.Run($"{hex} flags={flags:X}");
                }
            }
        }

        [SkippableTheory]
        [InlineData("F0 FF 06")] [InlineData("F0 FF 0E")] [InlineData("F0 FE 06")] [InlineData("F0 FE 0E")] [InlineData("F0 66 FF 06")]
        [InlineData("F0 66 FF 0E")] [InlineData("FF 06")] [InlineData("FF 0E")] [InlineData("F0 FF 46 04")] [InlineData("F0 FF 4E 04")]
        [InlineData("87 06")] [InlineData("87 CA")] [InlineData("86 06")] [InlineData("86 CA")] [InlineData("66 87 06")] [InlineData("87 4E 04")]
        [InlineData("F0 87 06")] [InlineData("86 26")] [InlineData("86 E1")]
        [InlineData("0F C1 06")] [InlineData("F0 0F C1 06")] [InlineData("0F C1 CA")] [InlineData("0F C1 C0")] [InlineData("66 0F C1 06")]
        [InlineData("66 F0 0F C1 06")] [InlineData("0F C0 06")] [InlineData("F0 0F C0 06")] [InlineData("0F C0 CA")] [InlineData("0F C0 26")] [InlineData("0F C0 E1")]
        public void LockedReadModifyWriteMatchesInterpreter(string hex)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            var highByte = hex == "86 CA" || hex == "86 26" || hex == "86 E1" || hex == "0F C0 26" || hex == "0F C0 E1";
            using (var diff = new JitDiff(hex))
            {
                foreach (var flags in FlagSets)
                    foreach (var value in Values)
                        foreach (var other in new uint[] { 0, 0x7FFFFFFF, 0xFFFFFFFF, 0x12345678 })
                        {
                            diff.Reset();
                            diff.Both((cpu, memory, fpu) =>
                            {
                                cpu.EFlags = flags;
                                cpu.Eax = other; cpu.Ecx = other ^ 0x0F0F0F0F; cpu.Edx = value;
                                memory.Write32(JitDiff.Data, value);
                                memory.Write32(JitDiff.Data + 4, value + 1);
                            });
                            diff.Run($"{hex} flags={flags:X} value={value:X} other={other:X}", expectTranslated: !highByte);
                        }
            }
        }

        [SkippableFact]
        public void SixteenBitExchangeStaysWithInterpreter()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff("66 93"))
            {
                diff.Reset();
                diff.Run("66 93", expectTranslated: false);
            }
        }

        [SkippableFact]
        public void FnstcwAndFldcwMatchInterpreter()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            foreach (var hex in new[] { "D9 3E", "9B D9 3E", "D9 2E", "9B D9 3E D9 2E", "D9 7C 24 FC 66 8B 44 24 FC D9 6C 24 FC" })
                foreach (var word in new ushort[] { 0x037F, 0x027F, 0x0F7F, 0x0000, 0xFFFF })
                {
                    using (var diff = new JitDiff(hex))
                    {
                        diff.Reset();
                        diff.Both((cpu, memory, fpu) => { memory.Write16(JitDiff.Data, word); });
                        diff.Run($"{hex} word={word:X}");
                    }
                }
        }

        [SkippableTheory]
        [InlineData("F3 C3", 0u)] [InlineData("C3", 0u)] [InlineData("F2 C3", 0u)] [InlineData("F3 C2 08 00", 8u)] [InlineData("C2 08 00", 8u)]
        public void RepRetReturnsLikeRet(string hex, uint extra)
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            var code = Convert.FromHexString(hex.Replace(" ", ""));
            using (var memory = new GuestMemory(native: true))
            {
                memory.Map(JitDiff.Code, 0x1000);
                memory.Map(JitDiff.Stack - 0x1000, 0x2000);
                memory.WriteBytes(JitDiff.Code, code);
                memory.Write32(JitDiff.Stack, 0x00401234);
                var cpu = new CpuState { Eip = JitDiff.Code, Esp = JitDiff.Stack };
                using (var jit = new JitEngine(cpu, memory))
                {
                    jit.RunBlock(1);
                    Assert.Equal(0x00401234u, cpu.Eip);
                    Assert.Equal(JitDiff.Stack + 4 + extra, cpu.Esp);
                    Assert.Equal(0, jit.InterpreterFallbacks);
                }
            }
        }
    }
}
