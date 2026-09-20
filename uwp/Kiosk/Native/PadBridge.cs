using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace Kiosk.Native
{
    /// <summary>
    /// The controller, answered in the shape a PC game expects.
    ///
    /// A Windows game reads pads through XInput, and the libraries that carry
    /// it do not exist on a console — which is a naming problem, not a hardware
    /// one: the pad in his hands is the pad XInput was written for. The console
    /// hands it over through a different interface, so the reading is taken
    /// from there and written into the sixteen bytes XInput promises.
    ///
    /// Nothing in the game changes. It asks for pad zero and gets pad zero.
    /// </summary>
    public static class PadBridge
    {
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_DEVICE_NOT_CONNECTED = 1167;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StateDelegate(uint index, IntPtr state);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VibrationDelegate(uint index, IntPtr vibration);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CapabilitiesDelegate(uint index, uint flags, IntPtr caps);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EnableDelegate(int enable);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BatteryDelegate(uint index, byte type, IntPtr information);

        private static StateDelegate state;
        private static VibrationDelegate vibration;
        private static CapabilitiesDelegate capabilities;
        private static EnableDelegate enable;
        private static BatteryDelegate battery;

        private static uint packet;

        /// <summary>How many readings were taken, which proves input is live.</summary>
        public static long Reads;

        /// <summary>XInput button bits, in the order the console reports them.</summary>
        private static ushort Buttons(GamepadButtons pressed)
        {
            ushort bits = 0;
            if ((pressed & GamepadButtons.DPadUp) != 0) bits |= 0x0001;
            if ((pressed & GamepadButtons.DPadDown) != 0) bits |= 0x0002;
            if ((pressed & GamepadButtons.DPadLeft) != 0) bits |= 0x0004;
            if ((pressed & GamepadButtons.DPadRight) != 0) bits |= 0x0008;
            if ((pressed & GamepadButtons.Menu) != 0) bits |= 0x0010;
            if ((pressed & GamepadButtons.View) != 0) bits |= 0x0020;
            if ((pressed & GamepadButtons.LeftThumbstick) != 0) bits |= 0x0040;
            if ((pressed & GamepadButtons.RightThumbstick) != 0) bits |= 0x0080;
            if ((pressed & GamepadButtons.LeftShoulder) != 0) bits |= 0x0100;
            if ((pressed & GamepadButtons.RightShoulder) != 0) bits |= 0x0200;
            if ((pressed & GamepadButtons.A) != 0) bits |= 0x1000;
            if ((pressed & GamepadButtons.B) != 0) bits |= 0x2000;
            if ((pressed & GamepadButtons.X) != 0) bits |= 0x4000;
            if ((pressed & GamepadButtons.Y) != 0) bits |= 0x8000;
            return bits;
        }

        private static short Axis(double value)
        {
            var scaled = value * 32767.0;
            if (scaled > 32767.0) scaled = 32767.0;
            if (scaled < -32768.0) scaled = -32768.0;
            return (short)scaled;
        }

        public static void Install(SystemImports imports)
        {
            state = (index, target) =>
            {
                if (target == IntPtr.Zero) return ERROR_DEVICE_NOT_CONNECTED;
                try
                {
                    var pads = Gamepad.Gamepads;
                    if (index >= (uint)pads.Count) return ERROR_DEVICE_NOT_CONNECTED;

                    var reading = pads[(int)index].GetCurrentReading();
                    Reads++;

                    // The packet number only has to change when the reading
                    // does; a game that compares it to skip work is right to.
                    Marshal.WriteInt32(target, 0, (int)(++packet));
                    Marshal.WriteInt16(target, 4, (short)Buttons(reading.Buttons));
                    Marshal.WriteByte(target, 6, (byte)(reading.LeftTrigger * 255.0));
                    Marshal.WriteByte(target, 7, (byte)(reading.RightTrigger * 255.0));
                    Marshal.WriteInt16(target, 8, Axis(reading.LeftThumbstickX));
                    Marshal.WriteInt16(target, 10, Axis(reading.LeftThumbstickY));
                    Marshal.WriteInt16(target, 12, Axis(reading.RightThumbstickX));
                    Marshal.WriteInt16(target, 14, Axis(reading.RightThumbstickY));
                    return ERROR_SUCCESS;
                }
                catch
                {
                    return ERROR_DEVICE_NOT_CONNECTED;
                }
            };

            vibration = (index, source) =>
            {
                try
                {
                    var pads = Gamepad.Gamepads;
                    if (index >= (uint)pads.Count || source == IntPtr.Zero)
                    {
                        return ERROR_DEVICE_NOT_CONNECTED;
                    }
                    // XINPUT_VIBRATION is two words, left motor then right.
                    var left = (ushort)Marshal.ReadInt16(source, 0) / 65535.0;
                    var right = (ushort)Marshal.ReadInt16(source, 2) / 65535.0;
                    pads[(int)index].Vibration = new GamepadVibration
                    {
                        LeftMotor = left,
                        RightMotor = right,
                    };
                    return ERROR_SUCCESS;
                }
                catch
                {
                    return ERROR_DEVICE_NOT_CONNECTED;
                }
            };

            capabilities = (index, flags, target) =>
            {
                if (target == IntPtr.Zero) return ERROR_DEVICE_NOT_CONNECTED;
                try
                {
                    if (index >= (uint)Gamepad.Gamepads.Count)
                    {
                        return ERROR_DEVICE_NOT_CONNECTED;
                    }
                    // XINPUT_CAPABILITIES: type, subtype, flags, then a state
                    // and a vibration block saying which fields are present.
                    Marshal.WriteByte(target, 0, 1);      // XINPUT_DEVTYPE_GAMEPAD
                    Marshal.WriteByte(target, 1, 1);      // XINPUT_DEVSUBTYPE_GAMEPAD
                    Marshal.WriteInt16(target, 2, 0);
                    Marshal.WriteInt16(target, 4, unchecked((short)0xF3FF));
                    Marshal.WriteByte(target, 6, 0xFF);
                    Marshal.WriteByte(target, 7, 0xFF);
                    Marshal.WriteInt16(target, 8, unchecked((short)0xFFFF));
                    Marshal.WriteInt16(target, 10, unchecked((short)0xFFFF));
                    Marshal.WriteInt16(target, 12, unchecked((short)0xFFFF));
                    Marshal.WriteInt16(target, 14, unchecked((short)0xFFFF));
                    Marshal.WriteInt16(target, 16, unchecked((short)0xFFFF));
                    Marshal.WriteInt16(target, 18, unchecked((short)0xFFFF));
                    return ERROR_SUCCESS;
                }
                catch
                {
                    return ERROR_DEVICE_NOT_CONNECTED;
                }
            };

            enable = on => { };

            battery = (index, type, information) =>
            {
                if (information == IntPtr.Zero) return ERROR_DEVICE_NOT_CONNECTED;
                Marshal.WriteByte(information, 0, 1); // wired
                Marshal.WriteByte(information, 1, 3); // full
                return ERROR_SUCCESS;
            };

            var ours = new Dictionary<string, IntPtr>
            {
                { "XInputGetState", Marshal.GetFunctionPointerForDelegate(state) },
                { "XInputSetState", Marshal.GetFunctionPointerForDelegate(vibration) },
                { "XInputGetCapabilities",
                    Marshal.GetFunctionPointerForDelegate(capabilities) },
                { "XInputEnable", Marshal.GetFunctionPointerForDelegate(enable) },
                { "XInputGetBatteryInformation",
                    Marshal.GetFunctionPointerForDelegate(battery) },
                // Reading a pad by its hidden ordinal is how some engines get
                // the guide button; the ordinary reading is the honest answer.
                { "#100", Marshal.GetFunctionPointerForDelegate(state) },
            };

            foreach (var module in new[]
            {
                "xinput1_4.dll", "XINPUT1_4.dll", "xinput1_3.dll", "XINPUT1_3.dll",
                "xinput9_1_0.dll", "XINPUT9_1_0.dll", "xinputuap.dll", "XINPUTUAP.dll",
            })
            {
                foreach (var pair in ours)
                {
                    imports.Overrides[module + "!" + pair.Key] = pair.Value;
                }
            }
        }
    }
}
