using System;

namespace Kiosk.Native
{
    /// <summary>Retains subpixel motion between input samples.</summary>
    public sealed class PointerPosition
    {
        private double x;
        private double y;

        public PointerPosition(double initialX, double initialY)
        {
            x = initialX;
            y = initialY;
        }

        public int X => (int)Math.Round(x, MidpointRounding.AwayFromZero);
        public int Y => (int)Math.Round(y, MidpointRounding.AwayFromZero);

        public void Move(double deltaX, double deltaY, int width, int height)
        {
            if (width < 1 || height < 1) return;
            if (double.IsNaN(deltaX) || double.IsInfinity(deltaX) ||
                double.IsNaN(deltaY) || double.IsInfinity(deltaY)) return;
            // Clamp the continuous position too: pushing at an edge must not
            // accumulate invisible distance that delays movement away from it.
            x = Math.Max(0, Math.Min(width - 1, x + deltaX));
            y = Math.Max(0, Math.Min(height - 1, y + deltaY));
        }
    }
}
