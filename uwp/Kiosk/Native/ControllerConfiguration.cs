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
