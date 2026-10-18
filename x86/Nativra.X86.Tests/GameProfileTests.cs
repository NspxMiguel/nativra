using Kiosk;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GameProfileTests
    {
        [Theory]
        [InlineData(0, 0.5, "desktop", 960, 540)]
        [InlineData(30, 0.75, "native", 1440, 810)]
        [InlineData(60, 1, "default", 1920, 1080)]
        public void SupportedProfilesKeepLaunchChoices(int limit, double scale, string layout, int width, int height)
        {
            var profile = new GameProfile { FrameLimit = limit, ResolutionScale = scale, ControllerLayout = layout, ForceInterpreter = true };
            profile.Normalize();
            Assert.Equal(limit, profile.FrameLimit);
            Assert.Equal(layout, profile.ControllerLayout);
            Assert.Equal(width, profile.ScreenWidth);
            Assert.Equal(height, profile.ScreenHeight);
            Assert.True(profile.ForceInterpreter);
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(500, double.NaN)]
        [InlineData(31, double.PositiveInfinity)]
        public void InvalidProfilesFallBackToDefaults(int limit, double scale)
        {
            var profile = new GameProfile { FrameLimit = limit, ResolutionScale = scale, ControllerLayout = "unknown" };
            profile.Normalize();
            Assert.Equal(60, profile.FrameLimit);
            Assert.Equal(1, profile.ResolutionScale);
            Assert.Equal("default", profile.ControllerLayout);
        }
    }
}
