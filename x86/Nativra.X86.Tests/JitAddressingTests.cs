using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// Frame-pointer addressing under the JIT. Guest EBP lives in r12, whose
    /// memory operands always need a SIB byte; a stray REX.X there once
    /// turned every [ebp+disp] into [ebp+ebp+disp] (found by the lockstep
    /// check on crt_static.exe).
    /// </summary>
    public sealed class JitAddressingTests
    {
        // push ebp / mov ebp,esp / sub esp,16 / [ebp-4]=0x11111111 / [ebp-8]=0x22222222
        // eax = lea [ebp-0xC] - esp (4) + [ebp-8] + [ebp-4]; leave; ret
        private static readonly byte[] Frame =
        {
            0x55, 0x89, 0xE5, 0x83, 0xEC, 0x10, 0xC7, 0x45, 0xFC, 0x11, 0x11, 0x11, 0x11, 0xC7, 0x45, 0xF8,
            0x22, 0x22, 0x22, 0x22, 0x8D, 0x45, 0xF4, 0x29, 0xE0, 0x8B, 0x4D, 0xF8, 0x03, 0x4D, 0xFC, 0x01,
            0xC8, 0x89, 0xEC, 0x5D, 0xC3,
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EbpRelativeOperandsAddressTheFrame(bool jit)
        {
            using (var p = new GuestProcess(new GuestMemory(native: jit), useJit: jit))
            {
                Assert.Equal(jit, p.UsesJit);
                p.Memory.Map(0x00600000, 0x1000);
                p.Memory.WriteBytes(0x00600000, Frame);
                var result = p.Call(0x00600000, out var eax, 1000);
                Assert.True(result.Ok, result.ToString());
                Assert.Equal(0x33333337u, eax);
            }
        }

        // eax = 7; eax = [esp+4] * 24; ecx = 5; ecx = [esp+4] * 1000; return eax + ecx
        // (the memory form of three-operand imul once multiplied the
        // destination's old value in; found by the lockstep check)
        private static readonly byte[] ImulMemory =
        {
            0xB8, 0x07, 0x00, 0x00, 0x00, 0x6B, 0x44, 0x24, 0x04, 0x18, 0xB9, 0x05, 0x00, 0x00, 0x00, 0x69,
            0x4C, 0x24, 0x04, 0xE8, 0x03, 0x00, 0x00, 0x01, 0xC8, 0xC3,
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ThreeOperandImulWithAMemorySourceIgnoresTheOldDestination(bool jit)
        {
            using (var p = new GuestProcess(new GuestMemory(native: jit), useJit: jit))
            {
                Assert.Equal(jit, p.UsesJit);
                p.Memory.Map(0x00600000, 0x1000);
                p.Memory.WriteBytes(0x00600000, ImulMemory);
                var result = p.Call(0x00600000, out var eax, 1000, 14);
                Assert.True(result.Ok, result.ToString());
                Assert.Equal(14u * 24u + 14u * 1000u, eax);
            }
        }

        // xchg al,ah / movzx ecx,ah / mov dh,0x30 / add cl,dh / test ah,2 /
        // setnz dl — AH-style byte registers, which the JIT must not map onto
        // its host homes for EBP..EDI, and an 8-bit xchg it once performed
        // twice (emitted, then handed to the interpreter as well).
        private static readonly byte[] HighBytes =
        {
            0xB8, 0x02, 0x41, 0x00, 0x00, 0x86, 0xC4, 0x0F, 0xB6, 0xCC, 0xB6, 0x30, 0x00, 0xF1, 0xF6, 0xC4,
            0x02, 0x0F, 0x95, 0xC2, 0x0F, 0xB6, 0xD2, 0xC1, 0xE2, 0x10, 0x01, 0xD0, 0xC1, 0xE1, 0x18, 0x01,
            0xC8, 0xC3,
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HighByteRegistersAndByteSwapsMatchTheInterpreter(bool jit)
        {
            using (var p = new GuestProcess(new GuestMemory(native: jit), useJit: jit))
            {
                Assert.Equal(jit, p.UsesJit);
                p.Memory.Map(0x00600000, 0x1000);
                p.Memory.WriteBytes(0x00600000, HighBytes);
                var result = p.Call(0x00600000, out var eax, 1000);
                Assert.True(result.Ok, result.ToString());
                Assert.Equal(0x32010241u, eax);
            }
        }
    }
}
