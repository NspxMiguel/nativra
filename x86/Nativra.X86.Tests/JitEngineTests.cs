using System;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// End-to-end engine checks: real little programs with loops and memory
    /// traffic, driven to completion. Straight-line bodies run as JIT-compiled
    /// x64; branches fall back to the interpreter and the two stay in step.
    /// </summary>
    public sealed class JitEngineTests
    {
        private const uint Code = 0x00400000;
        private const uint StackTop = 0x00300000;
        private const uint Data = 0x00500000;

        private static bool CanJit => IntPtr.Size == 8;

        private static JitEngine Fresh(GuestMemory memory, out CpuState cpu)
        {
            memory.Map(Code, 0x1000);
            memory.Map(StackTop - 0x2000, 0x4000);
            memory.Map(Data, 0x1000);
            cpu = new CpuState { Eip = Code, Esp = StackTop };
            return new JitEngine(cpu, memory);
        }

        [SkippableFact]
        public void SumLoopWithFallbackBranch()
        {
            Skip.IfNot(CanJit, "JIT needs a 64-bit host");
            using (var memory = new GuestMemory(native: true))
            using (var jit = Fresh(memory, out var cpu))
            {
                // eax = 0; do { eax += ecx; ecx--; } while (ecx != 0);
                var program = new byte[]
                {
                    0x31, 0xC0,             // xor eax, eax
                    0x01, 0xC8,             // add eax, ecx
                    0x49,                   // dec ecx
                    0x75, 0xFB,             // jnz -5 (back to add)
                    0x90,                   // nop (landing pad = end)
                };
                memory.WriteBytes(Code, program);
                var end = Code + (uint)program.Length - 1;
                cpu.Ecx = 100;

                Assert.True(jit.RunUntil(end, 100000), "loop did not reach the end");
                Assert.Equal(5050u, cpu.Eax);          // 100 * 101 / 2
                Assert.Equal(0u, cpu.Ecx);
                Assert.True(jit.InterpreterFallbacks > 0, "the branch should have used the interpreter");
                Assert.True(jit.BlocksExecuted > 1, "the JIT body should have run many times");
            }
        }

        [SkippableFact]
        public void ArraySumThroughGuestMemory()
        {
            Skip.IfNot(CanJit, "JIT needs a 64-bit host");
            using (var memory = new GuestMemory(native: true))
            using (var jit = Fresh(memory, out var cpu))
            {
                uint expected = 0;
                for (uint i = 0; i < 16; i++) { var v = i * 7 + 3; memory.Write32(Data + i * 4, v); expected += v; }

                // esi = Data; eax = 0; ecx = 16;
                // loop: add eax, [esi]; add esi, 4; dec ecx; jnz loop
                var program = new byte[]
                {
                    0x31, 0xC0,             // xor eax, eax
                    0x03, 0x06,             // add eax, [esi]
                    0x83, 0xC6, 0x04,       // add esi, 4
                    0x49,                   // dec ecx
                    0x75, 0xF8,             // jnz -8 (back to add eax,[esi])
                    0x90,                   // end
                };
                memory.WriteBytes(Code, program);
                cpu.Esi = Data;
                cpu.Ecx = 16;
                var end = Code + (uint)program.Length - 1;

                Assert.True(jit.RunUntil(end, 100000));
                Assert.Equal(expected, cpu.Eax);
                Assert.Equal(Data + 64, cpu.Esi);
            }
        }

        [SkippableFact]
        public void CallAndReturnThroughGuestStack()
        {
            Skip.IfNot(CanJit, "JIT needs a 64-bit host");
            using (var memory = new GuestMemory(native: true))
            using (var jit = Fresh(memory, out var cpu))
            {
                // call +2 (push return, jump over the two filler bytes); the
                // callee adds 7 and returns. Exercises push/pop of a real guest
                // stack across the interpreter's control-flow handling.
                var program = new byte[]
                {
                    0xB8, 0x23, 0x01, 0x00, 0x00,   // mov eax, 0x123
                    0xE8, 0x02, 0x00, 0x00, 0x00,   // call +2 -> lands at 0x0C
                    0xEB, 0x04,                     // jmp +4 -> end (skip callee)
                    0x83, 0xC0, 0x07,               // add eax, 7   (callee)
                    0xC3,                           // ret
                    0x90,                           // end (offset 0x10)
                };
                memory.WriteBytes(Code, program);
                var end = Code + 0x10;

                Assert.True(jit.RunUntil(end, 100000));
                Assert.Equal(0x12Au, cpu.Eax);              // 0x123 + 7
                Assert.Equal(StackTop, cpu.Esp);            // stack balanced
            }
        }

        [SkippableFact]
        public void BlocksAreCachedAcrossIterations()
        {
            Skip.IfNot(CanJit, "JIT needs a 64-bit host");
            using (var memory = new GuestMemory(native: true))
            using (var jit = Fresh(memory, out var cpu))
            {
                var program = new byte[] { 0x49, 0x75, 0xFD, 0x90 }; // loop: dec ecx; jnz loop; end
                memory.WriteBytes(Code, program);
                cpu.Ecx = 50;
                var end = Code + 3;

                Assert.True(jit.RunUntil(end, 100000));
                Assert.Equal(0u, cpu.Ecx);
                // The one body block is compiled once and reused every iteration.
                Assert.True(jit.BlocksCompiled < jit.BlocksExecuted,
                    $"expected reuse: compiled={jit.BlocksCompiled} executed={jit.BlocksExecuted}");
            }
        }
    }
}
