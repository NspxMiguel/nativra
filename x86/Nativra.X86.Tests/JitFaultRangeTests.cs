using Nativra.X86.Jit;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class JitFaultRangeTests
    {
        [Fact]
        public void PackedBlocksRemainIndependentAcrossRemovalAndReuse()
        {
            var clock = 0;
            var ranges = new JitFaultRanges();
            var offsets = new[] { 16 };
            var eips = new[] { 0x400000u };

            ranges.Register(0x10000, 96, 0x10040, offsets, eips); clock++;
            ranges.Register(0x10060, 80, 0x100A0, offsets, eips); clock++;
            Assert.Equal(2, ranges.Count);
            Assert.Equal(0x100A0UL, ranges.Find(0x10070).Value.Stub);
            Assert.False(ranges.Find(0x100B0).HasValue);

            ranges.Unregister(0x10000); clock++;
            Assert.Equal(0x100A0UL, ranges.Find(0x10070).Value.Stub);
            ranges.Register(0x10000, 48, 0x10020, offsets, eips); clock++;
            Assert.Equal(0x10020UL, ranges.Find(0x10010).Value.Stub);
            Assert.Equal(0x100A0UL, ranges.Find(0x10070).Value.Stub);

            ranges.Unregister(0x10060); clock++;
            ranges.Register(0x10060, 64, 0x10090, offsets, eips); clock++;
            Assert.Equal(0x10090UL, ranges.Find(0x10070).Value.Stub);
            Assert.Equal(2, ranges.Count);
            Assert.Equal(6, clock);
        }
    }
}
