using System;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class JitBranchTests
    {
        private const uint Code = 0x00400000;
        private const uint Stack = 0x00300000;
        private const uint Data = 0x00500000;
        private static bool CanJit =>
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;

        private static JitEngine Fresh(out GuestMemory memory, out CpuState cpu)
        {
            memory = new GuestMemory(native: true);
            memory.Map(Code, 0x1000);
            memory.Map(Stack - 0x1000, 0x2000);
            memory.Map(Data, 0x1000);
            cpu = new CpuState { Eip = Code, Esp = Stack };
            return new JitEngine(cpu, memory);
        }

        [SkippableTheory]
        [InlineData(new byte[] { 0xEB, 0x05 }, 7)]
        [InlineData(new byte[] { 0xE9, 0x05, 0, 0, 0 }, 10)]
        public void RelativeJumps(byte[] code, uint offset)
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, code);
                jit.RunBlock(1);
                Assert.Equal(Code + offset, cpu.Eip);
                Assert.Equal(0, jit.InterpreterFallbacks);
            }
        }

        [SkippableTheory]
        [InlineData(0u, 7u)]
        [InlineData(1u, 2u)]
        [InlineData(0x10000u, 2u)]
        public void JecxzMatchesInterpreterWithoutChangingFlags(uint ecx, uint offset)
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, new byte[] { 0xE3, 0x05 });
                cpu.Ecx = ecx;
                cpu.EFlags = Flag.Fixed | Flag.CF | Flag.OF;
                var expected = cpu.Clone();
                new Interpreter(expected, memory).Step();
                jit.RunBlock(1);
                Assert.Equal(Code + offset, cpu.Eip);
                Assert.Equal(expected.Eip, cpu.Eip);
                Assert.Equal(expected.EFlags, cpu.EFlags);
                Assert.Equal(0, jit.InterpreterFallbacks);
            }
        }

        [SkippableFact]
        public void EveryConditionMatchesInterpreterForAllArithmeticFlagCombinations()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                for (var cc = 0; cc < 16; cc++)
                    for (var near = 0; near < 2; near++)
                        for (var bits = 0; bits < 32; bits++)
                        {
                            var flags = Flag.Fixed;
                            var masks = new[] { Flag.CF, Flag.PF, Flag.ZF, Flag.SF, Flag.OF };
                            for (var i = 0; i < masks.Length; i++) if ((bits & (1 << i)) != 0) flags |= masks[i];
                            var code = near == 0
                                ? new byte[] { (byte)(0x70 + cc), 0x05 }
                                : new byte[] { 0x0F, (byte)(0x80 + cc), 0x05, 0, 0, 0 };
                            memory.WriteBytes(Code, code);
                            jit.Invalidate(Code, (uint)code.Length);
                            cpu.Eip = Code;
                            cpu.EFlags = flags;
                            var expected = cpu.Clone();
                            new Interpreter(expected, memory).Step();
                            jit.RunBlock(1);
                            Assert.Equal(expected.Eip, cpu.Eip);
                            Assert.Equal(expected.EFlags, cpu.EFlags);
                        }
                Assert.Equal(0, jit.InterpreterFallbacks);
            }
        }

        [SkippableFact]
        public void CallsAndReturnsMatchGuestStackSemantics()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, new byte[] { 0xE8, 0x05, 0, 0, 0 });
                jit.RunBlock(1);
                Assert.Equal(Code + 10, cpu.Eip);
                Assert.Equal(Stack - 4, cpu.Esp);
                Assert.Equal(Code + 5, memory.Read32(cpu.Esp));

                memory.WriteBytes(Code + 10, new byte[] { 0xC2, 0x08, 0x00 });
                jit.RunBlock(1);
                Assert.Equal(Code + 5, cpu.Eip);
                Assert.Equal(Stack + 8, cpu.Esp);

                cpu.Eip = Code + 10;
                cpu.Esp = Stack - 4;
                memory.Write32(cpu.Esp, Code + 20);
                memory.Write8(Code + 10, 0xC3);
                jit.Invalidate(Code + 10, 1);
                jit.RunBlock(1);
                Assert.Equal(Code + 20, cpu.Eip);
                Assert.Equal(Stack, cpu.Esp);
                Assert.Equal(0, jit.InterpreterFallbacks);
            }
        }

        [SkippableTheory]
        [InlineData(0xD0, false)]
        [InlineData(0x10, true)]
        [InlineData(0xE0, false)]
        [InlineData(0x20, true)]
        public void IndirectBranchesUseRegisterOrMemory(byte modrm, bool throughMemory)
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, new byte[] { 0xFF, modrm });
                cpu.Eax = throughMemory ? Data : Code + 40;
                if (throughMemory) memory.Write32(Data, Code + 40);
                jit.RunBlock(1);
                Assert.Equal(Code + 40, cpu.Eip);
                var call = (modrm & 0x38) == 0x10;
                Assert.Equal(call ? Stack - 4 : Stack, cpu.Esp);
                if (call) Assert.Equal(Code + 2, memory.Read32(cpu.Esp));
                Assert.Equal(0, jit.InterpreterFallbacks);
            }
        }

        [SkippableFact]
        public void IndirectCallReadsEspBeforePushingReturnAddress()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, new byte[] { 0xFF, 0xD4 });
                jit.RunBlock(1);
                Assert.Equal(Stack, cpu.Eip);
                Assert.Equal(Stack - 4, cpu.Esp);
                Assert.Equal(Code + 2, memory.Read32(cpu.Esp));
            }
        }

        [SkippableFact]
        public void ChainingStopsAtImportAndCallerStop()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, new byte[] { 0xEB, 0x00, 0xEB, 0x00, 0xEB, 0x00, 0xFF, 0xE0 });
                cpu.Eax = 0x7EF00000;
                jit.RunBlock(64);
                Assert.Equal(0x7EF00000u, cpu.Eip);
                Assert.Equal(4, jit.LastRunBlocks);
                cpu.Eip = Code;
                jit.RunBlock(64, Code + 4);
                Assert.Equal(Code + 4, cpu.Eip);
                Assert.Equal(2, jit.LastRunBlocks);
                Assert.Equal(0, jit.InterpreterFallbacks);
            }
        }

        [SkippableFact]
        public void ChainRespectsItsBlockBudget()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                var code = new byte[256];
                for (var i = 0; i < code.Length; i += 2)
                {
                    code[i] = 0xEB;
                    code[i + 1] = 0;
                }
                memory.WriteBytes(Code, code);
                jit.RunBlock(64);
                Assert.Equal(Code + 128, cpu.Eip);
                Assert.Equal(64, jit.LastRunBlocks);
                Assert.Equal(64, jit.BlocksExecuted);
                Assert.Equal(0, jit.InterpreterFallbacks);
            }
        }

        [SkippableFact]
        public void ChainUsesRecompiledTargetAfterInvalidation()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, new byte[] { 0xEB, 0x00, 0xEB, 0x00 });
                jit.RunBlock(2);
                Assert.Equal(Code + 4, cpu.Eip);
                memory.WriteBytes(Code + 2, new byte[] { 0xEB, 0x05 });
                jit.Invalidate(Code + 2, 2);
                cpu.Eip = Code;
                jit.RunBlock(2);
                Assert.Equal(Code + 9, cpu.Eip);
            }
        }

        [SkippableFact]
        public void FaultInSecondChainedBlockReportsItsGuestEip()
        {
            Skip.IfNot(CanJit, "JIT needs an x64 host");
            using (var jit = Fresh(out var memory, out var cpu))
            using (memory)
            {
                memory.WriteBytes(Code, new byte[] { 0xEB, 0x00, 0xA1, 0x00, 0x00, 0x60, 0x00 });
                var fault = Assert.Throws<GuestException>(() => jit.RunBlock(2));
                Assert.Equal(Code + 2, fault.Eip);
                Assert.Equal(Code + 2, cpu.Eip);
                Assert.Equal(2, jit.LastRunBlocks);
            }
        }
    }
}
