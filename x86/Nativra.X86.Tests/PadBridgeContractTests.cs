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
        public void ButtonEvidenceDoesNotConfuseAnalogActivityAndInvalidCalls()
        {
            PadBridge.Install(new SystemImports());
            Windows.Gaming.Input.Gamepad.Gamepads = Array.Empty<Windows.Gaming.Input.Gamepad>();
            ControllerMode.Desktop = false;
            ControllerMode.SystemButtons = 0;
            var handler = (Delegate)typeof(PadBridge).GetField("state", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var target = Marshal.AllocHGlobal(24);
            int Poll(uint index, IntPtr output) => (int)handler.DynamicInvoke(index, output);
            try
            {
                Array.Clear(PointerBridge.HostKeys, 0, PointerBridge.HostKeys.Length);
                var buttons = PadBridge.ButtonReads;
                var active = PadBridge.ActiveReads;
                var nulls = PadBridge.NullStates;
                var invalid = PadBridge.InvalidSlots;
                PointerBridge.HostKeys[213] = true;
                Assert.Equal(0, Poll(0, target));
                Assert.True(PadBridge.ActiveReads > active);
                Assert.Equal(buttons, PadBridge.ButtonReads);
                PointerBridge.HostKeys[195] = true;
                Assert.Equal(0, Poll(0, target));
                Assert.Equal(buttons + 1, PadBridge.ButtonReads);
                Assert.Equal(0x1000, PadBridge.LastActiveButtons);
                Array.Clear(PointerBridge.HostKeys, 0, PointerBridge.HostKeys.Length);
                Assert.Equal(0, Poll(0, target));
                Assert.Equal(0, PadBridge.LastButtons);
                Assert.Equal(0x1000, PadBridge.LastActiveButtons);
                Assert.Equal(160, Poll(0, IntPtr.Zero));
                Assert.Equal(160, Poll(4, target));
                Assert.Equal(nulls + 1, PadBridge.NullStates);
                Assert.Equal(invalid + 1, PadBridge.InvalidSlots);
            }
            finally
            {
                Array.Clear(PointerBridge.HostKeys, 0, PointerBridge.HostKeys.Length);
                ControllerMode.Desktop = false;
                Marshal.FreeHGlobal(target);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PortalStateMergesWithPhysicalInputAndPacketsOnlyChangeWithState(bool physical)
        {
            Windows.Gaming.Input.Gamepad.Gamepads = physical
                ? new[] { new Windows.Gaming.Input.Gamepad { Reading = new Windows.Gaming.Input.GamepadReading { Buttons = Windows.Gaming.Input.GamepadButtons.B, LeftThumbstickY = 0.5 } } }
                : Array.Empty<Windows.Gaming.Input.Gamepad>();
            PadBridge.Install(new SystemImports());
            var handler = (Delegate)typeof(PadBridge).GetField("state", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var target = Marshal.AllocHGlobal(24);
            int Poll(uint index = 0) => (int)handler.DynamicInvoke(index, target);
            try
            {
                Marshal.WriteInt64(target, 16, 0x1234567890ABCDEF);
                PointerBridge.HostKeys[195] = true;
                PointerBridge.HostKeys[201] = true;
                PointerBridge.HostKeys[213] = true;
                Assert.Equal(0, Poll());
                Assert.Equal(physical ? 0x3000 : 0x1000, (ushort)Marshal.ReadInt16(target, 4));
                Assert.Equal(255, Marshal.ReadByte(target, 6));
                Assert.Equal(32767, Marshal.ReadInt16(target, 8));
                Assert.Equal(physical ? 16383 : 0, Marshal.ReadInt16(target, 10));
                var packet = Marshal.ReadInt32(target);
                Assert.Equal(0, Poll());
                Assert.Equal(packet, Marshal.ReadInt32(target));
                PointerBridge.HostKeys[195] = false;
                Assert.Equal(0, Poll());
                Assert.Equal(unchecked(packet + 1), Marshal.ReadInt32(target));
                Assert.Equal(0x1234567890ABCDEF, Marshal.ReadInt64(target, 16));
                Assert.Equal(160, Poll(4));
                ControllerMode.Desktop = true;
                ControllerMode.SystemButtons = 0x10;
                Assert.Equal(0, Poll());
                Assert.Equal(0, Marshal.ReadInt32(target, 4));
                Assert.Equal(0, Marshal.ReadInt64(target, 8));
            }
            finally
            {
                ControllerMode.Desktop = false;
                ControllerMode.SystemButtons = 0;
                Array.Clear(PointerBridge.HostKeys, 0, PointerBridge.HostKeys.Length);
                Windows.Gaming.Input.Gamepad.Gamepads = Array.Empty<Windows.Gaming.Input.Gamepad>();
                Marshal.FreeHGlobal(target);
            }
        }

        [Fact]
        public void GuestXInputDispatchWritesTheSameInjectedStateWithinSixteenBytes()
        {
            using (var process = new Nativra.X86.Loader.GuestProcess(new Nativra.X86.Cpu.GuestMemory(native: true), useJit: false))
            {
                new Nativra.X86.Loader.GuestKernel(process).Install();
                PadBridge.InstallX86(process);
                const uint target = 0x00601000;
                process.Memory.Map(target, 0x1000);
                process.Memory.Write32(target + 16, 0xC0FFEE);
                PointerBridge.HostKeys[195] = true;
                try
                {
                    foreach (var module in new[] { "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll" })
                    {
                        var address = process.Imports.Bind(module, "XInputGetState", -1);
                        var result = process.Call(address, out var eax, 1000, 0, target);
                        Assert.True(result.Ok, result.ToString());
                        Assert.Equal(0u, eax);
                        Assert.Equal(0x1000, process.Memory.Read16(target + 4));
                        Assert.Equal(0xC0FFEEu, process.Memory.Read32(target + 16));
                    }
                }
                finally { PointerBridge.HostKeys[195] = false; }
            }
        }

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
        public static bool Desktop;
        public static int SystemButtons;
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
        public static IReadOnlyList<Gamepad> Gamepads { get; set; } = Array.Empty<Gamepad>();
        public static event Action<object, Gamepad> GamepadAdded { add { } remove { } }
        public static event Action<object, Gamepad> GamepadRemoved { add { } remove { } }
        public GamepadVibration Vibration { get; set; }
        public GamepadReading Reading;
        public GamepadReading GetCurrentReading() => Reading;
    }
}
