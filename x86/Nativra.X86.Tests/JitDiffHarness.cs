using System;
using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Runs one snippet twice, in the reference interpreter and through the JIT,
    /// from identical state, and compares everything a guest can observe: the
    /// general registers, the arithmetic flags, the x87/MMX/SSE register file
    /// (through the FXSAVE image, which carries tags, TOP, the control and status
    /// words, MXCSR, every x87 register and every XMM register) and two pages of
    /// memory. The interpreter is the reference; the JIT must match it exactly.
    /// </summary>
    internal sealed class JitDiff : IDisposable
    {
        public const uint Code = 0x00400000;
        public const uint Data = 0x00500000;
        public const uint Stack = 0x00300000;

        public static bool CanJit =>
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;

        public CpuState ExpectedCpu { get; }
        public CpuState ActualCpu { get; }
        public GuestMemory ExpectedMemory { get; }
        public GuestMemory ActualMemory { get; }
        public Interpreter Interpreter { get; }
        public JitEngine Jit { get; }
        public FpuUnit ExpectedFpu => Interpreter.Fpu;
        public FpuUnit ActualFpu => Jit.Interpreter.Fpu;

        private readonly byte[] code;
        public bool FullyTranslated { get; private set; }

        public JitDiff(string hex)
        {
            code = Convert.FromHexString(hex.Replace(" ", ""));
            ExpectedMemory = new GuestMemory();
            ActualMemory = new GuestMemory(native: true);
            foreach (var memory in new[] { ExpectedMemory, ActualMemory })
            {
                memory.Map(Code, 0x1000);
                memory.Map(Data, 0x2000);
                memory.Map(Stack - 0x1000, 0x2000);
                memory.WriteBytes(Code, code);
            }
            ExpectedCpu = new CpuState();
            ActualCpu = new CpuState();
            Interpreter = new Interpreter(ExpectedCpu, ExpectedMemory);
            Jit = new JitEngine(ActualCpu, ActualMemory);
            Reset();
        }

        /// <summary>Back to the same starting point for both sides (memory and FPU state included).</summary>
        public void Reset()
        {
            foreach (var cpu in new[] { ExpectedCpu, ActualCpu })
            {
                cpu.Eip = Code;
                cpu.Esp = Stack - 4;
                cpu.EFlags = Flag.Fixed | Flag.CF | Flag.OF;
                cpu.Eax = 0x80FF8103;
                cpu.Ebx = 0x76543210;
                cpu.Ecx = 0x11112222;
                cpu.Edx = 0x33334444;
                cpu.Esi = Data;
                cpu.Edi = Data + 0x100;
                cpu.Ebp = Stack - 4;
            }
            foreach (var memory in new[] { ExpectedMemory, ActualMemory })
            {
                memory.WriteBytes(Data, new byte[0x2000]);
                memory.WriteBytes(Stack - 0x1000, new byte[0x2000]);
            }
            var image = new byte[512];
            image[0] = 0x7F; image[1] = 0x03;       // the default control word
            image[24] = 0x80; image[25] = 0x1F;     // the default MXCSR
            foreach (var fpu in new[] { ExpectedFpu, ActualFpu })
                fpu.ReadFxsave(image);
        }

        /// <summary>Applies the same setup to both sides.</summary>
        public void Both(Action<CpuState, GuestMemory, FpuUnit> init)
        {
            init(ExpectedCpu, ExpectedMemory, ExpectedFpu);
            init(ActualCpu, ActualMemory, ActualFpu);
        }

        /// <summary>Runs the snippet on both sides and compares them.</summary>
        public void Run(string label, bool expectTranslated = true, bool sameFault = false)
        {
            var end = Code + (uint)code.Length;
            ExpectedCpu.Eip = ActualCpu.Eip = Code;
            Exception expectedError = null, actualError = null;
            try { while (ExpectedCpu.Eip != end) Interpreter.Step(); }
            catch (GuestException ex) { expectedError = ex; }
            try { Jit.RunToStop(end); }
            catch (GuestException ex) { actualError = ex; }
            FullyTranslated = Jit.LastFullyTranslated;
            if (expectTranslated) Assert.True(FullyTranslated, label + ": not fully translated");
            Assert.Equal(expectedError?.GetType(), actualError?.GetType());
            if (expectedError is GuestException e1 && actualError is GuestException e2)
            {
                Assert.Equal(e1.Code, e2.Code);
                Assert.Equal(e1.Eip, e2.Eip);
            }
            AssertSame(label);
        }

        public void AssertSame(string label)
        {
            Assert.True(ExpectedCpu.R.AsSpan().SequenceEqual(ActualCpu.R), $"{label}: registers {Describe(ExpectedCpu.R)} vs {Describe(ActualCpu.R)}");
            Assert.True((ExpectedCpu.EFlags & Flag.Arith) == (ActualCpu.EFlags & Flag.Arith),
                $"{label}: flags {ExpectedCpu.EFlags & Flag.Arith:X} vs {ActualCpu.EFlags & Flag.Arith:X}");
            Assert.Equal(ExpectedCpu.Eip, ActualCpu.Eip);

            var expectedImage = new byte[512];
            var actualImage = new byte[512];
            ExpectedFpu.WriteFxsave(expectedImage);
            ActualFpu.WriteFxsave(actualImage);
            for (var i = 0; i < 512; i++)
                Assert.True(expectedImage[i] == actualImage[i],
                    $"{label}: fxsave byte {i} = {actualImage[i]:X2}, interpreter {expectedImage[i]:X2}");
            // The registers a guest cannot see through FXSAVE alone.
            Assert.Equal(ExpectedFpu.Top, ActualFpu.Top);

            var expectedData = ExpectedMemory.ReadBytes(Data, 0x2000);
            var actualData = ActualMemory.ReadBytes(Data, 0x2000);
            for (var i = 0; i < expectedData.Length; i++)
                Assert.True(expectedData[i] == actualData[i], $"{label}: data byte {i:X} = {actualData[i]:X2}, interpreter {expectedData[i]:X2}");
            var expectedStack = ExpectedMemory.ReadBytes(Stack - 0x1000, 0x2000);
            var actualStack = ActualMemory.ReadBytes(Stack - 0x1000, 0x2000);
            for (var i = 0; i < expectedStack.Length; i++)
                Assert.True(expectedStack[i] == actualStack[i], $"{label}: stack byte {i:X} = {actualStack[i]:X2}, interpreter {expectedStack[i]:X2}");
        }

        private static string Describe(uint[] regs) => string.Join(",", Array.ConvertAll(regs, r => r.ToString("X8")));

        public void Dispose()
        {
            Jit.Dispose();
            ActualMemory.Dispose();
            ExpectedMemory.Dispose();
        }
    }
}
