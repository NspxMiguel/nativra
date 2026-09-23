using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    internal static class DxgiDescriptions
    {
        // DXGI_SWAP_CHAIN_FULLSCREEN_DESC is five consecutive 32-bit fields.
        // Initialize every output byte; callers may pass uninitialized memory.
        public static int WriteWindowedFullscreenDescription(IntPtr target)
        {
            if (target == IntPtr.Zero) return unchecked((int)0x80070057);
            Marshal.WriteInt32(target, 0, 60);
            Marshal.WriteInt32(target, 4, 1);
            Marshal.WriteInt32(target, 8, 0);
            Marshal.WriteInt32(target, 12, 0);
            Marshal.WriteInt32(target, 16, 1);
            return 0;
        }
    }
}
