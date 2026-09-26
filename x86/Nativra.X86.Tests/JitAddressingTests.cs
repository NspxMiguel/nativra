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
    }
}
