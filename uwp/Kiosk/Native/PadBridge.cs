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
        private const int ERROR_CALL_NOT_IMPLEMENTED = 120;

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
        private static VibrationDelegate unsupported;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AudioGuidsDelegate(uint index, IntPtr render, IntPtr capture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AudioIdsDelegate(uint index, IntPtr render, IntPtr renderCount, IntPtr capture, IntPtr captureCount);

        private static CapabilitiesDelegate keystroke;
        private static AudioGuidsDelegate audioGuids;
        private static AudioIdsDelegate audioIds;
        private static readonly object keystrokeGate = new object();
        private static readonly Nativra.X86.Loader.XInputKeystrokes[] keystrokes =
        {
            new Nativra.X86.Loader.XInputKeystrokes(), new Nativra.X86.Loader.XInputKeystrokes(),
            new Nativra.X86.Loader.XInputKeystrokes(), new Nativra.X86.Loader.XInputKeystrokes(),
        };
        private static IntPtr keystrokeState;

        private static uint packet;

        /// <summary>How many readings were taken, which proves input is live.</summary>
        public static long Reads;

        private static readonly List<Gamepad> known = new List<Gamepad>();
        private static bool watching;

        /// <summary>
        /// Starts following pads as they connect. A UWP app's Gamepad.Gamepads
        /// can stay empty until GamepadAdded has a subscriber, which left the
        /// controller mode reading nothing while desktop mode, fed by window
        /// key events, still worked.
        /// </summary>
        public static void Watch()
        {
            if (watching) return;
            watching = true;
            Gamepad.GamepadAdded += (sender, pad) =>
            {
                lock (known) if (!known.Contains(pad)) known.Add(pad);
                if (NativeProbe.GameRunning) PointerBridge.PostDeviceChange();
            };
            Gamepad.GamepadRemoved += (sender, pad) =>
            {
                lock (known) known.Remove(pad);
                if (NativeProbe.GameRunning) PointerBridge.PostDeviceChange();
            };
            lock (known)
                foreach (var pad in Gamepad.Gamepads)
                    if (!known.Contains(pad)) known.Add(pad);
        }

        /// <summary>The connected pads, in the order they arrived.</summary>
        public static IReadOnlyList<Gamepad> Pads
        {
            get
            {
                lock (known)
                {
                    if (known.Count > 0) return known.ToArray();
                }
                return Gamepad.Gamepads;
            }
        }

        /// <summary>How many times the game asked whether a pad exists.</summary>
        public static long Probes;

        /// <summary>
        /// Whether a slot answers as connected. Slot zero always does: engines
        /// probe once at startup, often before the console has listed its pads
        /// and while desktop mode is on, and a pad reported missing then is
        /// never asked for again. Desktop mode mutes the pad, it does not
        /// unplug it.
        /// </summary>
        private static bool Present(uint index, out IReadOnlyList<Gamepad> pads)
        {
            pads = Pads;
            return index == 0 || index < (uint)pads.Count;
        }

        /// <summary>XInput button bits, in the order the console reports them.</summary>
        private static ushort Buttons(GamepadButtons pressed)
        {
            ushort bits = 0;
            if ((pressed & GamepadButtons.DPadUp) != 0) bits |= 0x0001;
            if ((pressed & GamepadButtons.DPadDown) != 0) bits |= 0x0002;
            if ((pressed & GamepadButtons.DPadLeft) != 0) bits |= 0x0004;
            if ((pressed & GamepadButtons.DPadRight) != 0) bits |= 0x0008;
            bits |= (ushort)ControllerMode.SystemButtons;
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

        // Windows.System.VirtualKey values of the gamepad keys.
        private const int KeyA = 195, KeyB = 196, KeyX = 197, KeyY = 198;
        private const int KeyRightShoulder = 199, KeyLeftShoulder = 200;
        private const int KeyLeftTrigger = 201, KeyRightTrigger = 202;
        private const int KeyUp = 203, KeyDown = 204, KeyLeft = 205, KeyRight = 206;
        private const int KeyLeftStick = 209, KeyRightStick = 210;
        private const int KeyLeftStickUp = 211, KeyLeftStickDown = 212, KeyLeftStickRight = 213, KeyLeftStickLeft = 214;
        private const int KeyRightStickUp = 215, KeyRightStickDown = 216, KeyRightStickRight = 217, KeyRightStickLeft = 218;

        private static short Digital(bool[] keys, int positive, int negative) =>
            (short)((keys[positive] ? 32767 : 0) - (keys[negative] ? 32767 : 0));

        /// <summary>The digital buttons the window's gamepad keys say are down.</summary>
        private static ushort KeyButtons(bool[] keys)
        {
            ushort bits = 0;
            if (keys[KeyUp]) bits |= 0x0001;
            if (keys[KeyDown]) bits |= 0x0002;
            if (keys[KeyLeft]) bits |= 0x0004;
            if (keys[KeyRight]) bits |= 0x0008;
            if (keys[KeyLeftStick]) bits |= 0x0040;
            if (keys[KeyRightStick]) bits |= 0x0080;
            if (keys[KeyLeftShoulder]) bits |= 0x0100;
            if (keys[KeyRightShoulder]) bits |= 0x0200;
            if (keys[KeyA]) bits |= 0x1000;
            if (keys[KeyB]) bits |= 0x2000;
            if (keys[KeyX]) bits |= 0x4000;
            if (keys[KeyY]) bits |= 0x8000;
            return bits;
        }

        private static void WriteFromKeys(IntPtr target, bool[] keys)
        {
            ushort bits = 0;
            if (keys[KeyUp]) bits |= 0x0001;
            if (keys[KeyDown]) bits |= 0x0002;
            if (keys[KeyLeft]) bits |= 0x0004;
            if (keys[KeyRight]) bits |= 0x0008;
            bits |= (ushort)ControllerMode.SystemButtons;
            if (keys[KeyLeftStick]) bits |= 0x0040;
            if (keys[KeyRightStick]) bits |= 0x0080;
            if (keys[KeyLeftShoulder]) bits |= 0x0100;
            if (keys[KeyRightShoulder]) bits |= 0x0200;
            if (keys[KeyA]) bits |= 0x1000;
            if (keys[KeyB]) bits |= 0x2000;
            if (keys[KeyX]) bits |= 0x4000;
            if (keys[KeyY]) bits |= 0x8000;
            Marshal.WriteInt32(target, 0, (int)(++packet));
            Marshal.WriteInt16(target, 4, (short)bits);
            Marshal.WriteByte(target, 6, (byte)(keys[KeyLeftTrigger] ? 255 : 0));
            Marshal.WriteByte(target, 7, (byte)(keys[KeyRightTrigger] ? 255 : 0));
            Marshal.WriteInt16(target, 8, Digital(keys, KeyLeftStickRight, KeyLeftStickLeft));
            Marshal.WriteInt16(target, 10, Digital(keys, KeyLeftStickUp, KeyLeftStickDown));
            Marshal.WriteInt16(target, 12, Digital(keys, KeyRightStickRight, KeyRightStickLeft));
            Marshal.WriteInt16(target, 14, Digital(keys, KeyRightStickUp, KeyRightStickDown));
            System.Threading.Interlocked.Increment(ref KeyReads);
        }

        /// <summary>Readings built from window keys because no pad was listed.</summary>
        public static long KeyReads;

        private static short Axis(double value)
        {
            var scaled = value * 32767.0;
            if (scaled > 32767.0) scaled = 32767.0;
            if (scaled < -32768.0) scaled = -32768.0;
            return (short)scaled;
        }

        [DllImport("api-ms-win-core-processenvironment-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetEnvironmentVariableW(string name, string value);

        /// <summary>
        /// Steers SDL2 onto XInput. SDL reads an Xbox pad through
        /// Windows.Gaming.Input when it can, which goes around this bridge:
        /// Hades never called XInputGetState, so Device Portal input never
        /// reached it and the PC/controller mode switch did not apply (the pad
        /// kept driving the game in PC mode). SDL takes its hints from the
        /// process environment, read when the game starts, so they are set
        /// before it loads. Games without SDL never look at these names.
        /// </summary>
        private static void SteerSdl()
        {
            try
            {
                SetEnvironmentVariableW("SDL_JOYSTICK_WGI", "0");
                SetEnvironmentVariableW("SDL_JOYSTICK_RAWINPUT", "0");
                SetEnvironmentVariableW("SDL_JOYSTICK_HIDAPI", "0");
                SetEnvironmentVariableW("SDL_XINPUT_ENABLED", "1");
            }
            catch
            {
                // Without the hints SDL picks its own backend, as before.
            }
        }

        /// <summary>The XInput answers, made once and shared by the 64-bit loader and the 32-bit layer.</summary>
        private static void MakeHandlers()
        {
            if (state != null) return;
            state = (index, target) =>
            {
                if (target == IntPtr.Zero) return ERROR_DEVICE_NOT_CONNECTED;
                try
                {
                    if (!Present(index, out var pads)) return ERROR_DEVICE_NOT_CONNECTED;
                    System.Threading.Interlocked.Increment(ref Reads);

                    if (!ControllerMode.Desktop && index == 0 && pads.Count == 0)
                    {
                        // No pad listed, yet the window receives the pad's
                        // buttons as keys — the same channel desktop mode
                        // runs on, and the one Device Portal input uses.
                        WriteFromKeys(target, PointerBridge.HostKeys);
                        return ERROR_SUCCESS;
                    }

                    if (ControllerMode.Desktop || index >= (uint)pads.Count)
                    {
                        // A resting pad: same packet number, nothing pressed.
                        Marshal.WriteInt32(target, 0, (int)packet);
                        for (var offset = 4; offset < 16; offset += 4) Marshal.WriteInt32(target, offset, 0);
                        return ERROR_SUCCESS;
                    }

                    var reading = pads[(int)index].GetCurrentReading();

                    // The packet number only has to change when the reading
                    // does; a game that compares it to skip work is right to.
                    Marshal.WriteInt32(target, 0, (int)(++packet));
                    // Buttons that reach the window as keys count too: Device
                    // Portal input and any pad the list missed, merged with
                    // the physical reading rather than hidden behind it.
                    var pressed = Buttons(reading.Buttons);
                    if (index == 0) pressed |= KeyButtons(PointerBridge.HostKeys);
                    Marshal.WriteInt16(target, 4, (short)pressed);
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
                    if (source == IntPtr.Zero || !Present(index, out var pads)) return ERROR_DEVICE_NOT_CONNECTED;
                    if (ControllerMode.Desktop || index >= (uint)pads.Count) return ERROR_SUCCESS;
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
                System.Threading.Interlocked.Increment(ref Probes);
                if (target == IntPtr.Zero) return ERROR_DEVICE_NOT_CONNECTED;
                try
                {
                    if (!Present(index, out _)) return ERROR_DEVICE_NOT_CONNECTED;
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

            keystrokeState = Marshal.AllocHGlobal(16);
            keystroke = (index, reserved, target) =>
            {
                if ((index >= 4 && index != 255) || target == IntPtr.Zero) return 160; // ERROR_BAD_ARGUMENTS
                lock (keystrokeGate)
                {
                    var first = index == 255 ? 0u : index;
                    var last = index == 255 ? 3u : index;
                    for (var slot = first; slot <= last; slot++)
                    {
                        var result = state(slot, keystrokeState);
                        if (result != ERROR_SUCCESS)
                        {
                            if (index != 255) return result;
                            continue;
                        }
                        if (!keystrokes[slot].Poll(unchecked((ushort)Marshal.ReadInt16(keystrokeState, 4)),
                            Marshal.ReadByte(keystrokeState, 6), Marshal.ReadByte(keystrokeState, 7),
                            Marshal.ReadInt16(keystrokeState, 8), Marshal.ReadInt16(keystrokeState, 10),
                            Marshal.ReadInt16(keystrokeState, 12), Marshal.ReadInt16(keystrokeState, 14),
                            out var key, out var flags)) continue;
                        Marshal.WriteInt16(target, 0, (short)key);
                        Marshal.WriteInt16(target, 2, 0); // Unicode is unused for gamepads.
                        Marshal.WriteInt16(target, 4, (short)flags);
                        Marshal.WriteByte(target, 6, (byte)slot);
                        Marshal.WriteByte(target, 7, 0);
                        return ERROR_SUCCESS;
                    }
                    return 4306; // ERROR_EMPTY: no transition pending.
                }
            };
            audioGuids = (index, render, capture) =>
            {
                if (index >= 4 || render == IntPtr.Zero || capture == IntPtr.Zero) return 160;
                if (!Present(index, out _)) return ERROR_DEVICE_NOT_CONNECTED;
                return 50; // ERROR_NOT_SUPPORTED: no controller audio endpoint is exposed.
            };
            audioIds = (index, render, renderCount, capture, captureCount) =>
            {
                if (index >= 4 || renderCount == IntPtr.Zero || captureCount == IntPtr.Zero) return 160;
                if (!Present(index, out _)) return ERROR_DEVICE_NOT_CONNECTED;
                Marshal.WriteInt32(renderCount, 0);
                Marshal.WriteInt32(captureCount, 0);
                return ERROR_SUCCESS; // Connected controller, no audio endpoints.
            };

            // The guide-button and bus-information entries (ordinals 101-104 in xinput1_3/1_4): present so a
            // library probe that insists on finding them (Rewired does) accepts this module, and honest
            // about not supporting them.
            unsupported = (index, argument) => ERROR_CALL_NOT_IMPLEMENTED;

            battery = (index, type, information) =>
            {
                if (!Present(index, out _)) return ERROR_DEVICE_NOT_CONNECTED;
                if (information == IntPtr.Zero) return ERROR_DEVICE_NOT_CONNECTED;
                Marshal.WriteByte(information, 0, 1); // wired
                Marshal.WriteByte(information, 1, 3); // full
                return ERROR_SUCCESS;
            };

        }

        public static void Install(SystemImports imports)
        {
            SteerSdl();
            MakeHandlers();
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
                // xinput1_3 and 1_4 export these by number too, and some games
                // (LEGO Jurassic World) import them that way.
                { "#2", Marshal.GetFunctionPointerForDelegate(state) },
                { "#3", Marshal.GetFunctionPointerForDelegate(vibration) },
                { "#4", Marshal.GetFunctionPointerForDelegate(capabilities) },
                { "#5", Marshal.GetFunctionPointerForDelegate(enable) },
                { "#7", Marshal.GetFunctionPointerForDelegate(battery) },
                { "#101", Marshal.GetFunctionPointerForDelegate(unsupported) },
                { "#102", Marshal.GetFunctionPointerForDelegate(unsupported) },
                { "#103", Marshal.GetFunctionPointerForDelegate(unsupported) },
                { "#104", Marshal.GetFunctionPointerForDelegate(unsupported) },
                { "XInputGetStateEx", Marshal.GetFunctionPointerForDelegate(state) },
                { "XInputGetKeystroke", Marshal.GetFunctionPointerForDelegate(keystroke) },
                { "XInputGetDSoundAudioDeviceGuids", Marshal.GetFunctionPointerForDelegate(audioGuids) },
                { "XInputGetAudioDeviceIds", Marshal.GetFunctionPointerForDelegate(audioIds) },
                { "#6", Marshal.GetFunctionPointerForDelegate(audioGuids) },
                { "#8", Marshal.GetFunctionPointerForDelegate(keystroke) },
                { "#10", Marshal.GetFunctionPointerForDelegate(audioIds) },
            };

            foreach (var module in new[]
            {
                "xinput1_4.dll", "XINPUT1_4.dll", "xinput1_3.dll", "XINPUT1_3.dll",
                "xinput1_1.dll", "XINPUT1_1.dll", "xinput1_2.dll", "XINPUT1_2.dll",
                "xinput9_1_0.dll", "XINPUT9_1_0.dll", "xinputuap.dll", "XINPUTUAP.dll",
            })
            {
                foreach (var pair in ours)
                {
                    imports.Overrides[module + "!" + pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>
        /// The same pad for a 32-bit game: xinput1_3/1_4/9_1_0 served to the x86
        /// layer, whose structures are the guest's own bytes (XINPUT_STATE and
        /// friends hold no pointers, so the layouts match), reached at the guest
        /// address plus the guest space's host base.
        /// </summary>
        public static void InstallX86(Nativra.X86.Loader.GuestProcess process)
        {
            MakeHandlers();
            var host = process.Memory.HostBase;
            IntPtr At(uint guest) => guest == 0 ? IntPtr.Zero : new IntPtr(host.ToInt64() + guest);
            foreach (var module in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput1_2.dll", "xinput1_1.dll", "xinput9_1_0.dll" })
            {
                var i = process.Imports;
                var cc = Nativra.X86.Loader.CallConv.Stdcall;
                i.Register(module, "XInputGetState", cc, 2, c => (uint)state(c.Arg(0), At(c.Arg(1))));
                i.Register(module, "XInputSetState", cc, 2, c => (uint)vibration(c.Arg(0), At(c.Arg(1))));
                i.Register(module, "XInputGetCapabilities", cc, 3, c => (uint)capabilities(c.Arg(0), c.Arg(1), At(c.Arg(2))));
                i.Register(module, "XInputEnable", cc, 1, c => { enable((int)c.Arg(0)); return 0; });
                i.Register(module, "XInputGetBatteryInformation", cc, 3, c => (uint)battery(c.Arg(0), (byte)c.Arg(1), At(c.Arg(2))));
                i.Register(module, "XInputGetStateEx", cc, 2, c => (uint)state(c.Arg(0), At(c.Arg(1))));
                i.Register(module, "XInputGetKeystroke", cc, 3, c => (uint)keystroke(c.Arg(0), c.Arg(1), At(c.Arg(2))));
                i.Register(module, "XInputGetDSoundAudioDeviceGuids", cc, 3, c => (uint)audioGuids(c.Arg(0), At(c.Arg(1)), At(c.Arg(2))));
                i.Register(module, "XInputGetAudioDeviceIds", cc, 5, c => (uint)audioIds(c.Arg(0), At(c.Arg(1)), At(c.Arg(2)), At(c.Arg(3)), At(c.Arg(4))));
                i.RegisterOrdinal(module, 6, cc, 3, c => (uint)audioGuids(c.Arg(0), At(c.Arg(1)), At(c.Arg(2))));
                i.RegisterOrdinal(module, 7, cc, 3, c => (uint)battery(c.Arg(0), (byte)c.Arg(1), At(c.Arg(2))));
                i.RegisterOrdinal(module, 8, cc, 3, c => (uint)keystroke(c.Arg(0), c.Arg(1), At(c.Arg(2))));
                i.RegisterOrdinal(module, 10, cc, 5, c => (uint)audioIds(c.Arg(0), At(c.Arg(1)), At(c.Arg(2)), At(c.Arg(3)), At(c.Arg(4))));
                // By number too, as the 64-bit table above; #100 is the reading with the guide button.
                i.RegisterOrdinal(module, 2, cc, 2, c => (uint)state(c.Arg(0), At(c.Arg(1))));
                i.RegisterOrdinal(module, 3, cc, 2, c => (uint)vibration(c.Arg(0), At(c.Arg(1))));
                i.RegisterOrdinal(module, 4, cc, 3, c => (uint)capabilities(c.Arg(0), c.Arg(1), At(c.Arg(2))));
                i.RegisterOrdinal(module, 5, cc, 1, c => { enable((int)c.Arg(0)); return 0; });
                i.RegisterOrdinal(module, 100, cc, 2, c => (uint)state(c.Arg(0), At(c.Arg(1))));
            }
        }
    }
}
