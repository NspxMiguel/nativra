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
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PropertyDelegate(IntPtr set, IntPtr info, uint property, IntPtr type, IntPtr buffer, uint size, IntPtr required);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InstanceIdDelegate(IntPtr set, IntPtr info, IntPtr buffer, uint size, IntPtr required);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DevicePropertyDelegate(IntPtr set, IntPtr info, IntPtr key, IntPtr type,
            IntPtr buffer, uint size, IntPtr required, uint flags);

        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern void SetLastError(uint error);

        private const uint ErrorNoMoreItems = 259;
        private const uint ErrorInvalidData = 13;
        private const uint ErrorInsufficientBuffer = 122;
        private const int HidpStatusSuccess = 0x00110000;

        // The HID device interface class, and a path of the shape Windows
        // gives an XInput pad: vendor Microsoft, "IG_00" for the first slot.
        private static readonly Guid HidClass = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        public const string DevicePath =
            @"\\?\hid#vid_045e&pid_02ff&ig_00#1&2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        private static readonly IntPtr DeviceSet = new IntPtr(0x4E1D0000);
        private static readonly IntPtr EmptyDeviceSet = new IntPtr(0x4E1D0001);
        private static readonly Guid HidSetupClass = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
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
            WriteGuid(info + 4, HidSetupClass);
            Marshal.WriteInt32(info, 20, (int)ControllerConfiguration.Controller);
            Marshal.WriteIntPtr(info, 24, IntPtr.Zero);
        }

        internal static IntPtr SelectDevices(Guid? requestedClass, string enumerator, uint flags)
        {
            if ((flags & ~0x1Fu) != 0 || (requestedClass == null && (flags & 4) == 0))
                return new IntPtr(-1);
            var matchesClass = (flags & 4) != 0 || requestedClass == ((flags & 16) != 0 ? HidClass : HidSetupClass);
            var matchesEnumerator = string.IsNullOrEmpty(enumerator)
                || enumerator.Equals("HID", StringComparison.OrdinalIgnoreCase)
                || ((flags & 16) != 0 && enumerator.Equals(ControllerConfiguration.DeviceId, StringComparison.OrdinalIgnoreCase));
            return matchesClass && matchesEnumerator ? DeviceSet : EmptyDeviceSet;
        }

        internal static uint DeviceCount(IntPtr set) => set == DeviceSet ? 1u : 0u;

        private static IntPtr ClassDevices(IntPtr guid, IntPtr enumerator, uint flags, bool wide)
        {
            System.Threading.Interlocked.Increment(ref Listed);
            var requested = guid == IntPtr.Zero ? (Guid?)null : Marshal.PtrToStructure<Guid>(guid);
            var filter = enumerator == IntPtr.Zero ? null : wide ? Marshal.PtrToStringUni(enumerator) : Marshal.PtrToStringAnsi(enumerator);
            var set = SelectDevices(requested, filter, flags);
            if (set == new IntPtr(-1)) SetLastError(87);
            return set;
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

        private static bool ValidDevice(IntPtr set, IntPtr info)
        {
            if (set == DeviceSet && info != IntPtr.Zero && Marshal.ReadInt32(info) == 32 &&
                Marshal.ReadInt32(info, 20) == ControllerConfiguration.Controller) return true;
            SetLastError(87);
            return false;
        }

        private static int InstanceId(IntPtr set, IntPtr info, IntPtr buffer, uint size, IntPtr required, bool wide)
        {
            if (!ValidDevice(set, info)) return 0;
            if (required != IntPtr.Zero) Marshal.WriteInt32(required, ControllerConfiguration.DeviceId.Length + 1);
            var result = ControllerConfiguration.GetId(ControllerConfiguration.Controller, buffer, size, 0, wide);
            if (result == 0) return 1;
            SetLastError(ErrorInsufficientBuffer);
            return 0;
        }

        private static int RegistryProperty(IntPtr set, IntPtr info, uint property, IntPtr type,
            IntPtr buffer, uint size, IntPtr required, bool wide)
        {
            if (!ValidDevice(set, info)) return 0;
            uint registryType;
            var data = ControllerConfiguration.Property(property, wide, out registryType);
            if (data == null)
            {
                SetLastError(ErrorInvalidData);
                return 0;
            }
            if (type != IntPtr.Zero) Marshal.WriteInt32(type, (int)registryType);
            if (required != IntPtr.Zero) Marshal.WriteInt32(required, data.Length);
            if (buffer == IntPtr.Zero || size < data.Length)
            {
                SetLastError(ErrorInsufficientBuffer);
                return 0;
            }
            Marshal.Copy(data, 0, buffer, data.Length);
            return 1;
        }

        public static void Install(SystemImports imports)
        {
            ControllerConfiguration.Install(imports);
            preparsed = Marshal.AllocHGlobal(64);
            for (var i = 0; i < 64; i += 8) Marshal.WriteInt64(preparsed, i, 0);

            var setup = new Dictionary<string, IntPtr>
            {
                ["SetupDiGetClassDevsA"] = Keep(new ClassDevsDelegate((guid, enumerator, parent, flags) => ClassDevices(guid, enumerator, flags, false))),
                ["SetupDiGetClassDevsW"] = Keep(new ClassDevsDelegate((guid, enumerator, parent, flags) => ClassDevices(guid, enumerator, flags, true))),
                ["SetupDiEnumDeviceInfo"] = Keep(new EnumInfoDelegate((set, index, info) =>
                {
                    if (index >= DeviceCount(set))
                    {
                        SetLastError(ErrorNoMoreItems);
                        return 0;
                    }
                    FillInfo(info);
                    return 1;
                })),
                ["SetupDiEnumDeviceInterfaces"] = Keep(new EnumInterfacesDelegate((set, info, guid, index, data) =>
                {
                    if (index >= DeviceCount(set) || data == IntPtr.Zero || guid == IntPtr.Zero || Marshal.PtrToStructure<Guid>(guid) != HidClass)
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
                ["SetupDiGetDeviceRegistryPropertyA"] = Keep(new PropertyDelegate((set, info, property, type, buffer, size, required) =>
                    RegistryProperty(set, info, property, type, buffer, size, required, false))),
                ["SetupDiGetDeviceRegistryPropertyW"] = Keep(new PropertyDelegate((set, info, property, type, buffer, size, required) =>
                    RegistryProperty(set, info, property, type, buffer, size, required, true))),
                ["SetupDiGetDeviceInstanceIdA"] = Keep(new InstanceIdDelegate((set, info, buffer, size, required) =>
                    InstanceId(set, info, buffer, size, required, false))),
                ["SetupDiGetDeviceInstanceIdW"] = Keep(new InstanceIdDelegate((set, info, buffer, size, required) =>
                    InstanceId(set, info, buffer, size, required, true))),
                ["SetupDiGetDevicePropertyW"] = Keep(new DevicePropertyDelegate((set, info, key, type, buffer, size, required, flags) =>
                {
                    if (!ValidDevice(set, info)) return 0;
                    var error = ControllerConfiguration.QueryDeviceProperty(key, type, buffer, size, required, flags);
                    if (error != 0) SetLastError(error);
                    return error == 0 ? 1 : 0;
                })),
            };
            // Mono looks a P/Invoke up by its declared name before adding A or W.
            setup["SetupDiGetDeviceRegistryProperty"] = setup["SetupDiGetDeviceRegistryPropertyW"];
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
