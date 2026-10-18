using System;

namespace Kiosk
{
    /// <summary>Validated launch choices. Unknown values fall back to safe defaults.</summary>
    public sealed class GameProfile
    {
        public int FrameLimit { get; set; } = 60;
        public double ResolutionScale { get; set; } = 1;
        public bool ForceInterpreter { get; set; }
        public string ControllerLayout { get; set; } = "default";

        public void Normalize()
        {
            if (FrameLimit != 0 && FrameLimit != 30 && FrameLimit != 60) FrameLimit = 60;
            if (ResolutionScale != 0.5 && ResolutionScale != 0.75 && ResolutionScale != 1) ResolutionScale = 1;
            if (ControllerLayout != "desktop" && ControllerLayout != "native") ControllerLayout = "default";
        }

        public int ScreenWidth => (int)(1920 * ResolutionScale);
        public int ScreenHeight => (int)(1080 * ResolutionScale);
    }
}
