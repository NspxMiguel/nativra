using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class XInputKeystrokesTests
    {
        [Fact]
        public void DrainsSimultaneousButtonsThenReportsEmptyAndReleases()
        {
            var keys = new XInputKeystrokes();
            Assert.True(keys.Poll(0x3000, 0, 0, 0, 0, 0, 0, out var key, out var flags));
            Assert.Equal(0x5800, key);
            Assert.Equal(1, flags);
            Assert.True(keys.Poll(0x3000, 0, 0, 0, 0, 0, 0, out key, out flags));
            Assert.Equal(0x5801, key);
            Assert.False(keys.Poll(0x3000, 0, 0, 0, 0, 0, 0, out key, out flags));
            Assert.True(keys.Poll(0x2000, 0, 0, 0, 0, 0, 0, out key, out flags));
            Assert.Equal(0x5800, key);
            Assert.Equal(2, flags);
        }

        [Fact]
        public void TriggerThresholdAndGuideButtonMatchWineContract()
        {
            var keys = new XInputKeystrokes();
            Assert.False(keys.Poll(0x0400, 30, 30, 0, 0, 0, 0, out var key, out var flags));
            Assert.True(keys.Poll(0, 31, 0, 0, 0, 0, 0, out key, out flags));
            Assert.Equal(0x5806, key);
            Assert.Equal(1, flags);
            Assert.True(keys.Poll(0, 30, 0, 0, 0, 0, 0, out key, out flags));
            Assert.Equal(2, flags);
        }

        [Fact]
        public void StickDirectionReleasesBeforeChangingAndUsesDiagonalKeys()
        {
            var keys = new XInputKeystrokes();
            Assert.False(keys.Poll(0, 0, 0, 20000, -20000, 0, 0, out var key, out var flags));
            Assert.True(keys.Poll(0, 0, 0, -20001, 20001, 0, 0, out key, out flags));
            Assert.Equal(0x5824, key);
            Assert.Equal(1, flags);
            Assert.True(keys.Poll(0, 0, 0, 20001, 20001, 0, 0, out key, out flags));
            Assert.Equal(0x5824, key);
            Assert.Equal(2, flags);
            Assert.True(keys.Poll(0, 0, 0, 20001, 20001, 0, 0, out key, out flags));
            Assert.Equal(0x5825, key);
            Assert.Equal(1, flags);
        }

        [Fact]
        public void ControllersHaveIndependentEventState()
        {
            var first = new XInputKeystrokes();
            var second = new XInputKeystrokes();
            Assert.True(first.Poll(0x1000, 0, 0, 0, 0, 0, 0, out _, out _));
            Assert.True(second.Poll(0x1000, 0, 0, 0, 0, 0, 0, out _, out _));
            Assert.False(first.Poll(0x1000, 0, 0, 0, 0, 0, 0, out _, out _));
        }
    }
}
