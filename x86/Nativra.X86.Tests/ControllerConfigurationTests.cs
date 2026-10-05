using System;
using System.Runtime.InteropServices;
using Kiosk.Native;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class ControllerConfigurationTests
    {
        [Fact]
        public void DeviceTreeIsFiniteAndIdentityRoundTripsWithExactBufferSizes()
        {
            var p = Marshal.AllocHGlobal(256);
            try
            {
                Assert.Equal(0u, ControllerConfiguration.Locate(p, IntPtr.Zero, 0, true));
                var root = (uint)Marshal.ReadInt32(p);
                Assert.Equal(0u, ControllerConfiguration.Relation(p, root, 0, 0));
                var device = (uint)Marshal.ReadInt32(p);
                Assert.Equal(ControllerConfiguration.Controller, device);
                Assert.Equal(13u, ControllerConfiguration.Relation(p, device, 0, 0));
                Assert.Equal(0, Marshal.ReadInt32(p));
                Assert.Equal(13u, ControllerConfiguration.Relation(p, device, 0, 2));
                Assert.Equal(0u, ControllerConfiguration.Relation(p, device, 0, 1));
                Assert.Equal(root, (uint)Marshal.ReadInt32(p));
                Assert.Equal(0u, ControllerConfiguration.GetSize(p, device, 0));
                var length = (uint)Marshal.ReadInt32(p);
                Marshal.WriteInt64(p + 128, 0x1234567890ABCDEF);
                Assert.Equal(26u, ControllerConfiguration.GetId(device, p, length, 0, true));
                Assert.Equal(0u, ControllerConfiguration.GetId(device, p, length + 1, 0, true));
                Assert.Equal(ControllerConfiguration.DeviceId, Marshal.PtrToStringUni(p));
                Assert.Equal(0u, ControllerConfiguration.Locate(p + 192, p, 0, true));
                Assert.Equal(device, (uint)Marshal.ReadInt32(p + 192));
                Assert.Equal(0u, ControllerConfiguration.GetId(device, p, length + 1, 0, false));
                Assert.Equal(ControllerConfiguration.DeviceId, Marshal.PtrToStringAnsi(p));
                Assert.Equal(0x1234567890ABCDEF, Marshal.ReadInt64(p + 128));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        [Fact]
        public void ConfigurationErrorsDoNotReportSuccessOrCorruptOutputs()
        {
            var p = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(p, 0x1234567890ABCDEF);
                Assert.Equal(3u, ControllerConfiguration.Relation(IntPtr.Zero, 1, 0, 0));
                Assert.Equal(4u, ControllerConfiguration.Relation(p, 1, 1, 0));
                Assert.Equal(13u, ControllerConfiguration.GetSize(p, 999, 0));
                Assert.Equal(26u, ControllerConfiguration.GetId(1, p, 2, 0, true));
                Assert.Equal(0x1234567890ABCDEF, Marshal.ReadInt64(p));
                Assert.Equal(0u, ControllerConfiguration.Status(p, p + 4, 1, 0));
                Assert.Equal(0x400A, Marshal.ReadInt32(p));
                Assert.Equal(0, Marshal.ReadInt32(p + 4));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        [Fact]
        public void HardwareIdIsMultiStringAndClassIsNotTheInterfaceGuid()
        {
            var data = ControllerConfiguration.Property(1, true, out var type);
            Assert.Equal(7u, type);
            Assert.Equal(@"HID\VID_045E&PID_02FF&IG_00" + "\0\0", System.Text.Encoding.Unicode.GetString(data));
            data = ControllerConfiguration.Property(8, false, out type);
            Assert.Equal(1u, type);
            Assert.Equal("{745a17a0-74d3-11d0-b6fe-00a0c90f57da}\0", System.Text.Encoding.ASCII.GetString(data));
            Assert.Null(ControllerConfiguration.Property(999, true, out type));
        }

        [Fact]
        public void SetupApiAndConfigurationManagerShareEveryDeviceTreeExport()
        {
            var imports = new SystemImports();
            ControllerConfiguration.Install(imports);
            foreach (var name in new[] { "CM_Get_Child", "CM_Get_Parent", "CM_Get_Sibling", "CM_Get_Device_IDA",
                "CM_Get_Device_IDW", "CM_Get_Device_ID_Size", "CM_Locate_DevNodeA", "CM_Locate_DevNodeW", "CM_Get_DevNode_Status" })
                Assert.Equal(imports.Overrides["setupapi.dll!" + name], imports.Overrides["cfgmgr32.dll!" + name]);
        }
    }
}
