using Nativra.X86.Cpu;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class JitXaddAddressTests
    {
        [SkippableFact]
        public void LockedXaddKeepsTheOriginalSibAddress()
        {
            Skip.IfNot(JitDiff.CanJit, "JIT needs an x64 host");
            using (var diff = new JitDiff("F0 0F C1 4C CE 39"))
            {
                diff.Both((cpu, memory, fpu) =>
                {
                    cpu.Ecx = 1;
                    cpu.Esi = JitDiff.Data + 0xF80;
                    memory.Write32(JitDiff.Data + 0xFC1, 0x19D0ACF9);
                });
                diff.Run("lock xadd [esi+ecx*8+0x39], ecx");
                Assert.Equal(0x19D0ACFAu, diff.ExpectedMemory.Read32(JitDiff.Data + 0xFC1));
                Assert.Equal(0x19D0ACF9u, diff.ExpectedCpu.Ecx);
            }
        }

    }
}
