using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// The screen, described the way Windows describes screens.
    ///
    /// A game builds its resolution list by asking for display device zero and
    /// then walking its modes. Answering "there are none" is not neutral: a
    /// game with no modes has nothing to choose, and either refuses to start or
    /// starts at a size nobody asked for. A console has exactly one screen and
    /// it is 1920x1080 at 60Hz, so that is what gets written — into the real
    /// structures, at the real offsets, because a game reads the fields rather
    /// than the return value.
    /// </summary>
    public static class DisplayStubs
    {
        private const int Width = 1920;
        private const int Height = 1080;
        private const int Refresh = 60;
        private const int Depth = 32;

        private const int DM_POSITION = 0x00000020;
        private const int DM_BITSPERPEL = 0x00040000;
        private const int DM_PELSWIDTH = 0x00080000;
        private const int DM_PELSHEIGHT = 0x00100000;
        private const int DM_DISPLAYFREQUENCY = 0x00400000;

        private const int AttachedToDesktop = 0x00000001;
        private const int PrimaryDevice = 0x00000004;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SettingsDelegate(IntPtr device, int mode, IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DevicesDelegate(
            IntPtr device, uint index, IntPtr target, uint flags);

        private static SettingsDelegate settingsWide;
        private static SettingsDelegate settingsNarrow;
        private static DevicesDelegate devicesWide;
        private static DevicesDelegate devicesNarrow;

        private static void Wide(IntPtr at, int offset, string text, int room)
        {
            for (var i = 0; i < room / 2; i++)
            {
                Marshal.WriteInt16(at, offset + i * 2, (short)(i < text.Length ? text[i] : '\0'));
            }
        }

        private static void Narrow(IntPtr at, int offset, string text, int room)
        {
            for (var i = 0; i < room; i++)
            {
                Marshal.WriteByte(at, offset + i, (byte)(i < text.Length ? text[i] : '\0'));
            }
        }

        /// <summary>
        /// DEVMODE. The two variants differ only in how wide their two name
        /// fields are, so the numbers go in at an offset the caller supplies.
        /// </summary>
        private static void WriteMode(IntPtr at, int nameRoom)
        {
            var afterName = nameRoom;                 // dmSpecVersion
            Marshal.WriteInt16(at, afterName, 0x0401);
            Marshal.WriteInt16(at, afterName + 2, 0x0401);
            // The size of the whole structure, which differs between the two
            // variants only by the two name fields.
            Marshal.WriteInt16(at, afterName + 4, (short)(nameRoom == 64 ? 220 : 156));
            Marshal.WriteInt16(at, afterName + 6, 0);
            Marshal.WriteInt32(at, afterName + 8,
                DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT
                | DM_DISPLAYFREQUENCY);

            var union = afterName + 12;
            Marshal.WriteInt32(at, union, 0);         // dmPosition.x
            Marshal.WriteInt32(at, union + 4, 0);     // dmPosition.y
            Marshal.WriteInt32(at, union + 8, 0);     // dmDisplayOrientation
            Marshal.WriteInt32(at, union + 12, 0);    // dmDisplayFixedOutput

            // dmColor through dmCollate: six shorts nobody reads on a screen.
            var after = union + 16;
            for (var i = 0; i < 5; i++) Marshal.WriteInt16(at, after + i * 2, 0);

            var formName = after + 10;
            var tail = formName + nameRoom;           // dmLogPixels
            Marshal.WriteInt16(at, tail, 96);
            Marshal.WriteInt32(at, tail + 2, Depth);
            Marshal.WriteInt32(at, tail + 6, Width);
            Marshal.WriteInt32(at, tail + 10, Height);
            Marshal.WriteInt32(at, tail + 14, 0);     // dmDisplayFlags
            Marshal.WriteInt32(at, tail + 18, Refresh);

            // Everything after the frequency is printer business, and a caller
            // that allocated without clearing would read whatever was there.
            for (var offset = tail + 22; offset + 4 <= (nameRoom == 64 ? 220 : 156); offset += 4)
            {
                Marshal.WriteInt32(at, offset, 0);
            }
        }

        public static void Install(SystemImports imports)
        {
            // Mode zero is the one mode there is. Asked for the current or the
            // stored settings — the two negative numbers — the answer is the
            // same, because on a console it always is.
            settingsWide = (device, mode, target) =>
            {
                if (target == IntPtr.Zero) return 0;
                if (mode > 0) return 0;
                Wide(target, 0, @"\\.\DISPLAY1", 64);
                WriteMode(target, 64);
                return 1;
            };
            settingsNarrow = (device, mode, target) =>
            {
                if (target == IntPtr.Zero) return 0;
                if (mode > 0) return 0;
                Narrow(target, 0, @"\\.\DISPLAY1", 32);
                WriteMode(target, 32);
                return 1;
            };

            devicesWide = (device, index, target, flags) =>
            {
                if (target == IntPtr.Zero || index != 0) return 0;
                Marshal.WriteInt32(target, 0, 840);
                Wide(target, 4, @"\\.\DISPLAY1", 64);
                Wide(target, 68, "Xbox", 256);
                Marshal.WriteInt32(target, 324, AttachedToDesktop | PrimaryDevice);
                Wide(target, 328, @"\\.\DISPLAY1", 256);
                Wide(target, 584, string.Empty, 256);
                return 1;
            };
            devicesNarrow = (device, index, target, flags) =>
            {
                if (target == IntPtr.Zero || index != 0) return 0;
                Marshal.WriteInt32(target, 0, 424);
                Narrow(target, 4, @"\\.\DISPLAY1", 32);
                Narrow(target, 36, "Xbox", 128);
                Marshal.WriteInt32(target, 164, AttachedToDesktop | PrimaryDevice);
                Narrow(target, 168, @"\\.\DISPLAY1", 128);
                Narrow(target, 296, string.Empty, 128);
                return 1;
            };

            foreach (var module in new[] { "USER32.dll", "user32.dll", "User32.dll" })
            {
                imports.Overrides[module + "!EnumDisplaySettingsW"] =
                    Marshal.GetFunctionPointerForDelegate(settingsWide);
                imports.Overrides[module + "!EnumDisplaySettingsExW"] =
                    Marshal.GetFunctionPointerForDelegate(settingsWide);
                imports.Overrides[module + "!EnumDisplaySettingsA"] =
                    Marshal.GetFunctionPointerForDelegate(settingsNarrow);
                imports.Overrides[module + "!EnumDisplaySettingsExA"] =
                    Marshal.GetFunctionPointerForDelegate(settingsNarrow);
                imports.Overrides[module + "!EnumDisplayDevicesW"] =
                    Marshal.GetFunctionPointerForDelegate(devicesWide);
                imports.Overrides[module + "!EnumDisplayDevicesA"] =
                    Marshal.GetFunctionPointerForDelegate(devicesNarrow);
            }
        }
    }
}
