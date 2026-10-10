using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Native
{
    /// <summary>The device tree for the controller exposed by HidBridge.</summary>
    internal static class ControllerConfiguration
    {
        internal const uint Controller = 1, Root = 2;
        internal const string DeviceId = @"HID\VID_045E&PID_02FF&IG_00\1&2&0&0000";
        private const string RootId = @"HTREE\ROOT\0";
        private const uint Success = 0, InvalidPointer = 3, InvalidFlag = 4;
        private const uint NoSuchDevice = 13, BufferSmall = 26;
        private static readonly List<Delegate> roots = new List<Delegate>();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint RelationDelegate(IntPtr result, uint node, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint IdDelegate(uint node, IntPtr buffer, uint length, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint LocateDelegate(IntPtr result, IntPtr name, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint StatusDelegate(IntPtr status, IntPtr problem, uint node, uint flags);

        private static string Identity(uint node) => node == Controller ? DeviceId : node == Root ? RootId : null;

        internal static uint Relation(IntPtr result, uint node, uint flags, int direction)
        {
            if (result == IntPtr.Zero) return InvalidPointer;
            if (flags != 0) return InvalidFlag;
            if (Identity(node) == null) return NoSuchDevice;
            var related = direction == 0 && node == Root ? Controller :
                direction == 1 && node == Controller ? Root : 0;
            Marshal.WriteInt32(result, (int)related);
            return related == 0 ? NoSuchDevice : Success;
        }

        internal static uint GetId(uint node, IntPtr buffer, uint length, uint flags, bool wide)
        {
            if (buffer == IntPtr.Zero) return InvalidPointer;
            if (flags != 0) return InvalidFlag;
            var id = Identity(node);
            if (id == null) return NoSuchDevice;
            if (length <= id.Length) return BufferSmall;
            var data = (wide ? Encoding.Unicode : Encoding.ASCII).GetBytes(id + "\0");
            Marshal.Copy(data, 0, buffer, data.Length);
            return Success;
        }

        internal static uint GetSize(IntPtr result, uint node, uint flags)
        {
            if (result == IntPtr.Zero) return InvalidPointer;
            if (flags != 0) return InvalidFlag;
            var id = Identity(node);
            if (id == null) return NoSuchDevice;
            Marshal.WriteInt32(result, id.Length); // Excludes the terminator, unlike SetupAPI.
            return Success;
        }

        internal static uint Locate(IntPtr result, IntPtr name, uint flags, bool wide)
        {
            if (result == IntPtr.Zero) return InvalidPointer;
            if (flags != 0) return InvalidFlag;
            var id = name == IntPtr.Zero ? null : wide ? Marshal.PtrToStringUni(name) : Marshal.PtrToStringAnsi(name);
            var node = string.IsNullOrEmpty(id) || string.Equals(id, RootId, StringComparison.OrdinalIgnoreCase) ? Root :
                string.Equals(id, DeviceId, StringComparison.OrdinalIgnoreCase) ? Controller : 0;
            Marshal.WriteInt32(result, (int)node);
            return node == 0 ? NoSuchDevice : Success;
        }

        internal static uint Status(IntPtr status, IntPtr problem, uint node, uint flags)
        {
            if (status == IntPtr.Zero || problem == IntPtr.Zero) return InvalidPointer;
            if (flags != 0) return InvalidFlag;
            if (Identity(node) == null) return NoSuchDevice;
            Marshal.WriteInt32(status, node == Controller ? 0x400A : 0x4000); // Enumerated, started, driver loaded.
            Marshal.WriteInt32(problem, 0);
            return Success;
        }

        internal static byte[] Property(uint property, bool wide, out uint type)
        {
            type = 1; // REG_SZ
            string value;
            switch (property)
            {
                case 0: case 12: value = "Xbox Controller"; break;
                case 1:
                    type = 7; // REG_MULTI_SZ, with the additional final terminator.
                    value = @"HID\VID_045E&PID_02FF&IG_00" + "\0";
                    break;
                case 7: value = "HIDClass"; break;
                case 8: value = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"; break;
                case 11: value = "Microsoft"; break;
                case 22: value = "HID"; break;
                default: return null;
            }
            return (wide ? Encoding.Unicode : Encoding.ASCII).GetBytes(value + "\0");
        }

        internal static byte[] DeviceProperty(Guid format, uint property, out uint type)
        {
            type = 0x12; // DEVPROP_TYPE_STRING
            string value = null;
            if (format == new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"))
            {
                switch (property)
                {
                    case 2: case 14: value = "Xbox Controller"; break;
                    case 3: type = 0x2012; value = @"HID\VID_045E&PID_02FF&IG_00" + "\0"; break;
                    case 10: type = 0xD; return new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da").ToByteArray();
                    case 13: value = "Microsoft"; break;
                    case 24: value = "HID"; break;
                }
            }
            else if (format == new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57") && property == 256)
                value = DeviceId;
            else if (format == new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7") && property == 8)
                value = RootId;
            else if (format == new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2") && property == 4)
                value = "Xbox Controller";
            else if (format == new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c") && property == 2)
            {
                type = 0xD;
                return new Guid("4e617469-7672-4100-8000-000000000001").ToByteArray();
            }
            if (value == null) { type = 0; return null; }
            return Encoding.Unicode.GetBytes(value + "\0");
        }

        internal static uint QueryDeviceProperty(IntPtr key, IntPtr type, IntPtr buffer,
            uint size, IntPtr required, uint flags)
        {
            if (flags != 0) return 1004; // ERROR_INVALID_FLAGS
            if (key == IntPtr.Zero || type == IntPtr.Zero) return 87;
            if (buffer == IntPtr.Zero && size != 0) return 1784; // ERROR_INVALID_USER_BUFFER
            var guidBytes = new byte[16];
            Marshal.Copy(key, guidBytes, 0, 16);
            var data = DeviceProperty(new Guid(guidBytes), (uint)Marshal.ReadInt32(key, 16), out var propertyType);
            Marshal.WriteInt32(type, (int)propertyType);
            if (data == null) return 1168; // ERROR_NOT_FOUND
            if (required != IntPtr.Zero) Marshal.WriteInt32(required, data.Length);
            if (buffer == IntPtr.Zero || size < data.Length) return 122;
            Marshal.Copy(data, 0, buffer, data.Length);
            return 0;
        }

        public static void Install(SystemImports imports)
        {
            var exports = new Dictionary<string, Delegate>
            {
                ["CM_Get_Child"] = new RelationDelegate((p, n, f) => Relation(p, n, f, 0)),
                ["CM_Get_Parent"] = new RelationDelegate((p, n, f) => Relation(p, n, f, 1)),
                ["CM_Get_Sibling"] = new RelationDelegate((p, n, f) => Relation(p, n, f, 2)),
                ["CM_Get_Device_ID_Size"] = new RelationDelegate(GetSize),
                ["CM_Get_Device_IDA"] = new IdDelegate((n, p, l, f) => GetId(n, p, l, f, false)),
                ["CM_Get_Device_IDW"] = new IdDelegate((n, p, l, f) => GetId(n, p, l, f, true)),
                ["CM_Locate_DevNodeA"] = new LocateDelegate((p, n, f) => Locate(p, n, f, false)),
                ["CM_Locate_DevNodeW"] = new LocateDelegate((p, n, f) => Locate(p, n, f, true)),
                ["CM_Get_DevNode_Status"] = new StatusDelegate(Status),
            };
            foreach (var export in exports)
            {
                roots.Add(export.Value);
                var pointer = Marshal.GetFunctionPointerForDelegate(export.Value);
                foreach (var module in new[] { "setupapi.dll", "SETUPAPI.dll", "cfgmgr32.dll", "CFGMGR32.dll" })
                    imports.Overrides[module + "!" + export.Key] = pointer;
            }
        }
    }
}
