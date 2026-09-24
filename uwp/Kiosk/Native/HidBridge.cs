using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Lists the console's controller the way Windows lists one.
    ///
    /// Unity's input system does not ask XInput whether a pad exists. It walks
    /// the HID devices through SetupAPI, and an Xbox pad is the HID device
    /// whose path carries "IG_" — only then does it read that pad through
    /// XInput. The console has no SetupAPI, so the walk failed on its first
    /// call ("Could not enumerate input devices"), no pad was ever found, and
    /// XInput was never read: zero probes, zero reads, measured.
    ///
    /// So one device is listed: an XInput-class pad. Opening it hands back a
    /// handle of ours, its attributes say Microsoft's controller, and every
    /// reading of it goes through PadBridge's XInput over Windows.Gaming.Input.
    /// </summary>
    internal static class HidBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr ClassDevsDelegate(IntPtr guid, IntPtr enumerator, IntPtr parent, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumInterfacesDelegate(IntPtr set, IntPtr info, IntPtr guid, uint index, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumInfoDelegate(IntPtr set, uint index, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DetailDelegate(IntPtr set, IntPtr data, IntPtr detail, uint size, IntPtr required, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OneDelegate(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TwoDelegate(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StringDelegate(IntPtr handle, IntPtr buffer, uint length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CapsListDelegate(int type, IntPtr caps, IntPtr length, IntPtr preparsed);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DataDelegate(int type, IntPtr data, IntPtr length, IntPtr preparsed, IntPtr report, uint reportLength);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint MaxDataDelegate(int type, IntPtr preparsed);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AnyDelegate(IntPtr a, IntPtr b, IntPtr c, IntPtr d);

        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern void SetLastError(uint error);

        private const uint ErrorNoMoreItems = 259;
        private const uint ErrorInsufficientBuffer = 122;
        private const int HidpStatusSuccess = 0x00110000;

        // The HID device interface class, and a path of the shape Windows
        // gives an XInput pad: vendor Microsoft, "IG_00" for the first slot.
        private static readonly Guid HidClass = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        public const string DevicePath =
            @"\\?\hid#vid_045e&pid_02ff&ig_00#1&2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        private static readonly IntPtr DeviceSet = new IntPtr(0x4E1D0000);
        private static readonly IntPtr DeviceHandle = new IntPtr(0x4E1D0100);
        private static IntPtr preparsed;
        private static readonly List<Delegate> roots = new List<Delegate>();

        /// <summary>Times the game walked the device list, and opened the pad.</summary>
        public static long Listed;
        public static long Opened;

        private static IntPtr Keep(Delegate function)
        {
            roots.Add(function);
            return Marshal.GetFunctionPointerForDelegate(function);
        }

        private static void WriteGuid(IntPtr at, Guid guid)
        {
            Marshal.Copy(guid.ToByteArray(), 0, at, 16);
        }

        private static void FillInfo(IntPtr info)
        {
            if (info == IntPtr.Zero) return;
            // SP_DEVINFO_DATA: cbSize, ClassGuid, DevInst, Reserved.
            WriteGuid(info + 4, HidClass);
            Marshal.WriteInt32(info, 20, 1);
            Marshal.WriteIntPtr(info, 24, IntPtr.Zero);
        }

        private static int WriteWide(IntPtr buffer, uint bytes, string text)
        {
            if (buffer == IntPtr.Zero || bytes < 2) return 0;
            var chars = text.ToCharArray();
            var fit = (int)Math.Min(chars.Length, bytes / 2 - 1);
            Marshal.Copy(chars, 0, buffer, fit);
            Marshal.WriteInt16(buffer, fit * 2, 0);
            return 1;
        }

        public static void Install(SystemImports imports)
        {
            preparsed = Marshal.AllocHGlobal(64);
            for (var i = 0; i < 64; i += 8) Marshal.WriteInt64(preparsed, i, 0);

            var setup = new Dictionary<string, IntPtr>
            {
                ["SetupDiGetClassDevsA"] = Keep(new ClassDevsDelegate((guid, enumerator, parent, flags) =>
                {
                    System.Threading.Interlocked.Increment(ref Listed);
                    return DeviceSet;
                })),
                ["SetupDiGetClassDevsW"] = Keep(new ClassDevsDelegate((guid, enumerator, parent, flags) =>
                {
                    System.Threading.Interlocked.Increment(ref Listed);
                    return DeviceSet;
                })),
                ["SetupDiEnumDeviceInfo"] = Keep(new EnumInfoDelegate((set, index, info) =>
                {
                    if (set != DeviceSet || index > 0)
                    {
                        SetLastError(ErrorNoMoreItems);
                        return 0;
                    }
                    FillInfo(info);
                    return 1;
                })),
                ["SetupDiEnumDeviceInterfaces"] = Keep(new EnumInterfacesDelegate((set, info, guid, index, data) =>
                {
                    if (set != DeviceSet || index > 0 || data == IntPtr.Zero)
                    {
                        SetLastError(ErrorNoMoreItems);
                        return 0;
                    }
                    // SP_DEVICE_INTERFACE_DATA: cbSize, class, flags (active).
                    WriteGuid(data + 4, HidClass);
                    Marshal.WriteInt32(data, 20, 1);
                    Marshal.WriteIntPtr(data, 24, IntPtr.Zero);
                    return 1;
                })),
                ["SetupDiGetDeviceInterfaceDetailW"] = Keep(new DetailDelegate((set, data, detail, size, required, info) =>
                {
                    // cbSize, then the path; the caller asks for the size first.
                    var needed = (uint)(4 + (DevicePath.Length + 1) * 2);
                    if (required != IntPtr.Zero) Marshal.WriteInt32(required, (int)needed);
                    if (detail == IntPtr.Zero || size < needed)
                    {
                        SetLastError(ErrorInsufficientBuffer);
                        return 0;
                    }
                    WriteWide(detail + 4, size - 4, DevicePath);
                    FillInfo(info);
                    return 1;
                })),
                ["SetupDiDestroyDeviceInfoList"] = Keep(new OneDelegate(set => 1)),
            };
            foreach (var pair in setup)
            {
                imports.Overrides["SETUPAPI.dll!" + pair.Key] = pair.Value;
                imports.Overrides["setupapi.dll!" + pair.Key] = pair.Value;
            }

            var hid = new Dictionary<string, IntPtr>
            {
                ["HidD_GetAttributes"] = Keep(new TwoDelegate((handle, attributes) =>
                {
                    if (handle != DeviceHandle || attributes == IntPtr.Zero) return 0;
                    Marshal.WriteInt32(attributes, 0, 12);
                    Marshal.WriteInt16(attributes, 4, 0x045E);
                    Marshal.WriteInt16(attributes, 6, 0x02FF);
                    Marshal.WriteInt16(attributes, 8, 0x0100);
                    return 1;
                })),
                ["HidD_GetPreparsedData"] = Keep(new TwoDelegate((handle, result) =>
                {
                    if (handle != DeviceHandle || result == IntPtr.Zero) return 0;
                    Marshal.WriteIntPtr(result, preparsed);
                    return 1;
                })),
                ["HidD_FreePreparsedData"] = Keep(new OneDelegate(data => 1)),
                ["HidD_GetProductString"] = Keep(new StringDelegate((handle, buffer, length) =>
                    handle == DeviceHandle ? WriteWide(buffer, length, "Xbox Controller") : 0)),
                ["HidD_GetManufacturerString"] = Keep(new StringDelegate((handle, buffer, length) =>
                    handle == DeviceHandle ? WriteWide(buffer, length, "Microsoft") : 0)),
                ["HidD_GetSerialNumberString"] = Keep(new StringDelegate((handle, buffer, length) =>
                    handle == DeviceHandle ? WriteWide(buffer, length, "0") : 0)),
                ["HidP_GetCaps"] = Keep(new TwoDelegate((data, caps) =>
                {
                    if (caps == IntPtr.Zero) return HidpStatusSuccess;
                    for (var i = 0; i < 64; i += 4) Marshal.WriteInt32(caps, i, 0);
                    // HIDP_CAPS: usage 5 (game pad) on page 1 (generic desktop).
                    Marshal.WriteInt16(caps, 0, 5);
                    Marshal.WriteInt16(caps, 2, 1);
                    Marshal.WriteInt16(caps, 4, 16);
                    Marshal.WriteInt16(caps, 44, 1);
                    return HidpStatusSuccess;
                })),
                ["HidP_GetButtonCaps"] = Keep(new CapsListDelegate((type, caps, length, data) =>
                {
                    if (length != IntPtr.Zero) Marshal.WriteInt16(length, 0);
                    return HidpStatusSuccess;
                })),
                ["HidP_GetValueCaps"] = Keep(new CapsListDelegate((type, caps, length, data) =>
                {
                    if (length != IntPtr.Zero) Marshal.WriteInt16(length, 0);
                    return HidpStatusSuccess;
                })),
                ["HidP_GetData"] = Keep(new DataDelegate((type, list, length, data, report, reportLength) =>
                {
                    if (length != IntPtr.Zero) Marshal.WriteInt32(length, 0);
                    return HidpStatusSuccess;
                })),
                ["HidP_MaxDataListLength"] = Keep(new MaxDataDelegate((type, data) => 0)),
                ["HidP_SetUsageValue"] = Keep(new AnyDelegate((a, b, c, d) => HidpStatusSuccess)),
                ["HidP_SetUsages"] = Keep(new AnyDelegate((a, b, c, d) => HidpStatusSuccess)),
            };
            foreach (var pair in hid)
            {
                imports.Overrides["HID.DLL!" + pair.Key] = pair.Value;
                imports.Overrides["hid.dll!" + pair.Key] = pair.Value;
            }

            // Opening the listed path hands back our handle; every other
            // open goes where it went before. Chained through FileWatch
            // rather than by converting its function pointer back into a
            // delegate, which the native runtime refuses with a cast error.
            FileWatch.Intercept = name =>
            {
                if (name == IntPtr.Zero || Marshal.ReadInt16(name) != '\\') return IntPtr.Zero;
                var path = Marshal.PtrToStringUni(name);
                if (!string.Equals(path, DevicePath, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                System.Threading.Interlocked.Increment(ref Opened);
                return DeviceHandle;
            };
        }
    }
}
