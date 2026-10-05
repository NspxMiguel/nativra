using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Kiosk.Native;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class PadBridgeContractTests
    {
        [Fact]
        public void AudioEndpointQueriesAllowOmittedDirectionsAndBoundOutputWrites()
        {
            PadBridge.Install(new SystemImports());
            var handler = (Delegate)typeof(PadBridge).GetField("audioIds", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var p = Marshal.AllocHGlobal(16);
            try
            {
                Assert.Equal(0, (int)handler.DynamicInvoke(0u, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                Assert.Equal(1167, (int)handler.DynamicInvoke(3u, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                Marshal.WriteInt64(p, 0x1234567890ABCDEF);
                Marshal.WriteInt32(p + 8, 1);
                Assert.Equal(0, (int)handler.DynamicInvoke(0u, p, p + 8, IntPtr.Zero, IntPtr.Zero));
                Assert.Equal(0, Marshal.ReadInt16(p));
                Assert.Equal(0x1234567890AB0000, Marshal.ReadInt64(p));
                Assert.Equal(0, Marshal.ReadInt32(p + 8));
                Marshal.WriteInt16(p, 123);
                Assert.Equal(0, (int)handler.DynamicInvoke(0u, p, p + 8, IntPtr.Zero, IntPtr.Zero));
                Assert.Equal(123, Marshal.ReadInt16(p));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        [Fact]
        public void ExportDiscoveryAndKeystrokeAbiWorkWithoutAWindowsHost()
        {
            var imports = new SystemImports();
            PadBridge.Install(imports);
            foreach (var dll in new[] { "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll" })
            {
                Assert.Equal(imports.Overrides[dll + "!XInputGetKeystroke"], imports.Overrides[dll + "!#8"]);
                Assert.Equal(imports.Overrides[dll + "!XInputGetDSoundAudioDeviceGuids"], imports.Overrides[dll + "!#6"]);
                Assert.Equal(imports.Overrides[dll + "!XInputGetAudioDeviceIds"], imports.Overrides[dll + "!#10"]);
            }
            var handler = (Delegate)typeof(PadBridge).GetField("keystroke", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var target = Marshal.AllocHGlobal(16);
            int Poll(uint index) => (int)handler.DynamicInvoke(index, 0u, target);
            try
            {
                Marshal.WriteInt64(target, 8, 0x1234567890ABCDEF);
                Assert.Equal(160, Poll(4));
                Assert.Equal(1167, Poll(3));
                Assert.Equal(4306, Poll(255));
                PointerBridge.HostKeys[195] = true;
                Assert.Equal(0, Poll(255));
                Assert.Equal(0x5800, Marshal.ReadInt16(target, 0));
                Assert.Equal(0, Marshal.ReadInt16(target, 2));
                Assert.Equal(1, Marshal.ReadInt16(target, 4));
                Assert.Equal(0, Marshal.ReadByte(target, 6));
                Assert.Equal(0, Marshal.ReadByte(target, 7));
                Assert.Equal(4306, Poll(0));
                PointerBridge.HostKeys[195] = false;
                Assert.Equal(0, Poll(0));
                Assert.Equal(2, Marshal.ReadInt16(target, 4));
                Assert.Equal(0x1234567890ABCDEF, Marshal.ReadInt64(target, 8));
            }
            finally
            {
                PointerBridge.HostKeys[195] = false;
                Marshal.FreeHGlobal(target);
            }
        }
    }
}

// The linked production bridge is tested against a deterministic platform boundary.
namespace Kiosk.Native
{
    public sealed class SystemImports
    {
        public Dictionary<string, IntPtr> Overrides = new Dictionary<string, IntPtr>();
    }
    internal static class ControllerMode
    {
        public static bool Desktop => false;
        public static int SystemButtons => 0;
    }
    internal static class PointerBridge
    {
        public static readonly bool[] HostKeys = new bool[256];
        public static void PostDeviceChange() { }
    }
    internal static class FileWatch
    {
        public static Func<IntPtr, IntPtr> Intercept;
    }
    internal static class NativeProbe
    {
        public static bool GameRunning => false;
    }
}
namespace Windows.Gaming.Input
{
    [Flags]
    public enum GamepadButtons
    {
        DPadUp = 1, DPadDown = 2, DPadLeft = 4, DPadRight = 8,
        LeftThumbstick = 16, RightThumbstick = 32, LeftShoulder = 64,
        RightShoulder = 128, A = 256, B = 512, X = 1024, Y = 2048,
    }
    public struct GamepadVibration
    {
        public double LeftMotor, RightMotor;
    }
    public struct GamepadReading
    {
        public GamepadButtons Buttons;
        public double LeftTrigger, RightTrigger, LeftThumbstickX, LeftThumbstickY, RightThumbstickX, RightThumbstickY;
    }
    public sealed class Gamepad
    {
        public static IReadOnlyList<Gamepad> Gamepads => Array.Empty<Gamepad>();
        public static event Action<object, Gamepad> GamepadAdded { add { } remove { } }
        public static event Action<object, Gamepad> GamepadRemoved { add { } remove { } }
        public GamepadVibration Vibration { get; set; }
        public GamepadReading GetCurrentReading() => default;
    }
}
