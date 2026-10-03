using Nativra.X86.Cpu;
using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class JitBitAddressTests
    {
        [SkippableFact]
        public void LargeNegativeBitOffsetRaisesGuestFaultWithoutNativeAccess()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff("0F A3 0E"))
            {
                diff.Both((cpu, memory, fpu) =>
                {
                    cpu.Esi = JitDiff.Data + 0x1000;
                    cpu.Ecx = 0x80000000;
                });
                diff.Run("bt [esi], ecx with negative 32-bit offset", expectTranslated: false);
            }
        }

        [Fact]
        public void RegisterBitOffsetOutsideGuestReservationFallsBack()
        {
            using (var memory = new GuestMemory())
            {
                memory.Map(JitDiff.Code, 0x1000);
                memory.WriteBytes(JitDiff.Code, new byte[] { 0x0F, 0xA3, 0x0E }); // bt [esi], ecx
                var translator = new BlockTranslator();
                translator.Translate(memory, JitDiff.Code, JitDiff.Code + 3);
                Assert.False(translator.FullyTranslated);
            }
        }
    }
}
