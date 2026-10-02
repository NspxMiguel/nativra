using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class JitFallbackTests
    {
        private const uint Code = 0x00400000;
        private const uint Data = 0x00500000;
        private const uint Stack = 0x00300000;

        private static bool CanJit =>
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;

        public static IEnumerable<object[]> HighByteMoves()
        {
            for (var reg = 0; reg < 8; reg++)
                for (var rm = 0; rm < 8; rm++)
                    if (reg >= 4 || rm >= 4)
                        yield return new object[] { $"8A {(0xC0 | (reg << 3) | rm):X2}" };
            for (var reg = 4; reg < 8; reg++)
                yield return new object[] { $"8A {(reg << 3 | 6):X2}" };
        }

        [SkippableTheory]
        [MemberData(nameof(HighByteMoves))]
        public void AllHighByteMoveRegistersMatchInterpreter(string hex) => TranslatedInstructionMatchesInterpreter(hex);

        [SkippableTheory]
        [InlineData("FF 36")]
        [InlineData("66 FF 36")]
        [InlineData("8F 07")]
        [InlineData("66 8F 07")]
        [InlineData("C9")]
        [InlineData("66 C9")]
        [InlineData("98")]
        [InlineData("66 98")]
        [InlineData("99")]
        [InlineData("66 99")]
        [InlineData("A0 00 00 50 00")]
        [InlineData("A1 00 00 50 00")]
        [InlineData("66 A1 00 00 50 00")]
        [InlineData("A2 00 00 50 00")]
        [InlineData("A3 00 00 50 00")]
        [InlineData("66 A3 00 00 50 00")]
        [InlineData("02 E0")]
        [InlineData("3A E0")]
        [InlineData("80 EC 01")]
        [InlineData("8A 26")]
        [InlineData("88 27")]
        [InlineData("0F B6 C4")]
        [InlineData("0F BE C4")]
        [InlineData("66 0F B6 C4")]
        [InlineData("66 0F BE C4")]
        [InlineData("66 0F B6 06")]
        [InlineData("66 0F BE 06")]
        [InlineData("84 E4")]
        [InlineData("0A E0")]
        [InlineData("0F 94 C4")]
        public void TranslatedInstructionMatchesInterpreter(string hex)
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            var code = Convert.FromHexString(hex.Replace(" ", ""));
            using (var expectedMemory = new GuestMemory())
            using (var actualMemory = new GuestMemory(native: true))
            {
                foreach (var memory in new[] { expectedMemory, actualMemory })
                {
                    memory.Map(Code, 0x1000);
                    memory.Map(Data, 0x1000);
                    memory.Map(Stack - 0x1000, 0x2000);
                    memory.WriteBytes(Code, code);
                    memory.Write32(Data, 0xFEDCBA98);
                    memory.Write32(Stack - 4, 0x13579BDF);
                }
                var expected = NewCpu();
                var actual = NewCpu();
                var interpreter = new Interpreter(expected, expectedMemory);
                while (expected.Eip < Code + code.Length) interpreter.Step();
                using (var jit = new JitEngine(actual, actualMemory))
                {
                    Assert.True(jit.RunToStop(Code + (uint)code.Length));
                    Assert.True(jit.LastFullyTranslated, hex);
                    Assert.Equal(0, jit.InterpreterFallbacks);
                }
                Assert.Equal(expected.R, actual.R);
                Assert.Equal(expected.EFlags & Flag.Arith, actual.EFlags & Flag.Arith);
                Assert.Equal(expectedMemory.Read32(Data), actualMemory.Read32(Data));
                Assert.Equal(expectedMemory.Read32(Stack - 4), actualMemory.Read32(Stack - 4));
            }
        }

        [SkippableTheory]
        [InlineData("F7 F1", 0u, 0u, 0u, true)]
        [InlineData("F7 F1", 0xFFFFFFFFu, 1u, 1u, true)]
        [InlineData("F7 F1", 100u, 0u, 3u, false)]
        [InlineData("F7 F9", 0x80000000u, 0xFFFFFFFFu, 0xFFFFFFFFu, true)]
        [InlineData("F7 F9", 100u, 0u, 3u, false)]
        [InlineData("F7 36", 100u, 0u, 0u, true)]
        [InlineData("66 F7 F1", 0x10000u, 0u, 0u, true)]
        [InlineData("F6 F1", 0x10000u, 0u, 0u, true)]
        public void DivideEdgesMatchInterpreter(string hex, uint eax, uint edx, uint divisor, bool faults)
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            var code = Convert.FromHexString(hex.Replace(" ", ""));
            using (var expectedMemory = new GuestMemory())
            using (var actualMemory = new GuestMemory(native: true))
            {
                foreach (var memory in new[] { expectedMemory, actualMemory })
                {
                    memory.Map(Code, 0x1000);
                    memory.Map(Data, 0x1000);
                    memory.WriteBytes(Code, code);
                    memory.Write32(Data, divisor);
                }
                var expected = NewCpu();
                var actual = NewCpu();
                foreach (var cpu in new[] { expected, actual })
                {
                    cpu.Eax = eax;
                    cpu.Edx = edx;
                    cpu.Ecx = divisor;
                }
                var interpreter = new Interpreter(expected, expectedMemory);
                var expectedError = Record.Exception(() => interpreter.Step()) as GuestException;
                using (var jit = new JitEngine(actual, actualMemory))
                {
                    var actualError = Record.Exception(() => jit.RunToStop(Code + (uint)code.Length)) as GuestException;
                    Assert.Equal(faults, expectedError != null);
                    Assert.Equal(expectedError?.Code, actualError?.Code);
                    Assert.Equal(expectedError?.Eip, actualError?.Eip);
                    Assert.Equal(expected.R, actual.R);
                }
            }
        }

        private static CpuState NewCpu()
        {
            var cpu = new CpuState { Eip = Code, Esp = Stack - 4, EFlags = Flag.Fixed | Flag.CF | Flag.OF };
            cpu.Eax = 0x80FF8103;
            cpu.Ebx = 0x76543210;
            cpu.Ecx = 0x11112222;
            cpu.Edx = 0x33334444;
            cpu.Esi = Data;
            cpu.Edi = Data + 4;
            cpu.Ebp = Stack - 4;
            return cpu;
        }

        [SkippableFact]
        public void FallbackHistogramIsOptIn()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var memory = new GuestMemory(native: true))
            {
                memory.Map(Code, 0x1000);
                memory.Write8(Code, 0x9B); // FWAIT stays in the interpreter.
                var cpu = NewCpu();
                using (var jit = new JitEngine(cpu, memory))
                {
                    Assert.True(jit.RunToStop(Code + 1));
                    Assert.Empty(jit.FallbackCounts);
                    cpu.Eip = Code;
                    jit.CollectFallbacks = true;
                    Assert.True(jit.RunToStop(Code + 1));
                    Assert.Equal(1, jit.FallbackCounts["9B"]);
                }
            }
        }
    }
}
