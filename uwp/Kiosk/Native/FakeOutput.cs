using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// A display that is not there.
    ///
    /// Between making its graphics device and asking for a swap chain, an
    /// engine walks the adapter's outputs to find out what the screen can do:
    /// which resolutions, at which refresh rates. On a desktop that is the
    /// monitor. Inside this console's application container there is no output
    /// at all — the app does not own the screen, the shell does — so the walk
    /// comes back empty and the engine never reaches the swap chain. Measured:
    /// the device is created, the old factory is created, and nothing follows.
    ///
    /// So the adapter is given one output to find, and it answers the way the
    /// television actually behaves. The modes listed here are the ones the
    /// console really drives, which is what makes this honest rather than a
    /// convenient lie: the engine picks from this list, and every entry on it
    /// is a mode the hardware can hold.
    /// </summary>
    internal static class FakeOutput
    {
        private const int S_OK = 0;
        private const int E_INVALIDARG = unchecked((int)0x80070057);
        private const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);

        // DXGI_MODE_SCANLINE_ORDER_PROGRESSIVE, DXGI_MODE_SCALING_UNSPECIFIED.
        private const int Progressive = 1;
        private const int Unscaled = 0;

        /// <summary>One entry per mode the console can actually output.</summary>
        private struct Mode
        {
            public int Width;
            public int Height;
            public int Hertz;
        }

        // Kept in the order DXGI promises: by width, then height, then rate.
        // An engine that takes the last entry gets the best the box can do.
        private static readonly Mode[] Modes =
        {
            new Mode { Width = 1280, Height = 720,  Hertz = 60 },
            new Mode { Width = 1920, Height = 1080, Hertz = 60 },
            new Mode { Width = 1920, Height = 1080, Hertz = 120 },
            new Mode { Width = 2560, Height = 1440, Hertz = 60 },
            new Mode { Width = 2560, Height = 1440, Hertz = 120 },
            new Mode { Width = 3840, Height = 2160, Hertz = 60 },
            new Mode { Width = 3840, Height = 2160, Hertz = 120 },
        };

        private const int ModeSize = 28;
        private const int DescSize = 96;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DataDelegate(IntPtr self, IntPtr a, IntPtr b, IntPtr c);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ParentDelegate(IntPtr self, IntPtr riid, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DescribeDelegate(IntPtr self, IntPtr desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ModeListDelegate(
            IntPtr self, int format, uint flags, IntPtr count, IntPtr modes);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ClosestDelegate(
            IntPtr self, IntPtr wanted, IntPtr closest, IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PlainDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OneArgDelegate(IntPtr self, IntPtr a);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OwnDelegate(IntPtr self, IntPtr device, int exclusive);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VoidDelegate(IntPtr self);

        // Held for as long as the object lives: a delegate that is collected
        // while the engine still holds its address is a crash with no stack.
        private static DataDelegate privateData;
        private static ParentDelegate parent;
        private static DescribeDelegate describe;
        private static ModeListDelegate modeList;
        private static ClosestDelegate closest;
        private static PlainDelegate vblank;
        private static OwnDelegate takeOwnership;
        private static VoidDelegate releaseOwnership;
        private static OneArgDelegate oneArg;
        private static DataDelegate twoArgs;

        private static IntPtr made;
        private static IntPtr owner;

        /// <summary>What happened, for the report.</summary>
        public static string Note = "not built";

        /// <summary>How many times an engine asked what the screen can do.</summary>
        public static long Asked;

        /// <summary>
        /// Builds the output, once. The adapter it belongs to is remembered so
        /// that walking back up from the output lands on the same stand-in the
        /// rest of the bridge hands out, rather than on the real adapter.
        /// </summary>
        public static IntPtr Build(ComProxy proxy, IntPtr adapter)
        {
            if (made != IntPtr.Zero) return made;
            try
            {
                owner = adapter;

                privateData = (self, a, b, c) => S_OK;
                twoArgs = (self, a, b, c) => S_OK;
                oneArg = (self, a) => S_OK;

                // Walking up from the output has to reach the adapter the
                // engine already holds, not the one underneath it.
                parent = (self, riid, result) =>
                {
                    if (result == IntPtr.Zero) return E_INVALIDARG;
                    Marshal.WriteIntPtr(result, owner);
                    if (owner != IntPtr.Zero) Marshal.AddRef(owner);
                    return S_OK;
                };

                describe = (self, desc) =>
                {
                    if (desc == IntPtr.Zero) return E_INVALIDARG;
                    for (var i = 0; i < DescSize; i++) Marshal.WriteByte(desc, i, 0);

                    var name = "XBOX";
                    for (var i = 0; i < name.Length; i++)
                    {
                        Marshal.WriteInt16(desc, i * 2, (short)name[i]);
                    }

                    // The desktop rectangle is what an engine reads to decide
                    // how big a borderless window should be.
                    var best = Modes[Modes.Length - 1];
                    Marshal.WriteInt32(desc, 64, 0);            // left
                    Marshal.WriteInt32(desc, 68, 0);            // top
                    Marshal.WriteInt32(desc, 72, best.Width);   // right
                    Marshal.WriteInt32(desc, 76, best.Height);  // bottom
                    Marshal.WriteInt32(desc, 80, 1);            // attached
                    Marshal.WriteInt32(desc, 84, 1);            // rotation: none
                    // A monitor handle that is not null and never dereferenced
                    // by anything on this console, because nothing here takes
                    // one. Zero, though, reads as "no screen" and puts the
                    // engine back where it started.
                    Marshal.WriteIntPtr(desc, 88, new IntPtr(1));
                    return S_OK;
                };

                modeList = (self, format, flags, count, modes) =>
                {
                    Asked++;
                    if (count == IntPtr.Zero) return E_INVALIDARG;

                    // The first call asks only how many there are.
                    if (modes == IntPtr.Zero)
                    {
                        Marshal.WriteInt32(count, Modes.Length);
                        return S_OK;
                    }

                    var room = Marshal.ReadInt32(count);
                    if (room < Modes.Length)
                    {
                        Marshal.WriteInt32(count, Modes.Length);
                        return DXGI_ERROR_MORE_DATA;
                    }

                    for (var i = 0; i < Modes.Length; i++)
                    {
                        WriteMode(modes + i * ModeSize, Modes[i], format);
                    }
                    Marshal.WriteInt32(count, Modes.Length);
                    return S_OK;
                };

                closest = (self, wanted, match, device) =>
                {
                    if (match == IntPtr.Zero) return E_INVALIDARG;

                    // Answer with the listed mode nearest to what was asked
                    // for, which is what the real one does — an engine that
                    // gets back something not on the list stops trusting it.
                    var width = wanted == IntPtr.Zero ? 0 : Marshal.ReadInt32(wanted, 0);
                    var height = wanted == IntPtr.Zero ? 0 : Marshal.ReadInt32(wanted, 4);
                    var format = wanted == IntPtr.Zero ? 0 : Marshal.ReadInt32(wanted, 16);

                    var pick = Modes[Modes.Length - 1];
                    var best = long.MaxValue;
                    foreach (var mode in Modes)
                    {
                        long dw = mode.Width - width;
                        long dh = mode.Height - height;
                        var distance = dw * dw + dh * dh;
                        if (distance >= best) continue;
                        best = distance;
                        pick = mode;
                    }

                    WriteMode(match, pick, format);
                    return S_OK;
                };

                // Returning at once, always. A real one blocks until the
                // screen's next refresh; blocking here would hand an engine a
                // stall it has no way to see past, on a screen we do not own.
                vblank = self => S_OK;

                takeOwnership = (self, device, exclusive) => S_OK;
                releaseOwnership = self => { };

                made = proxy.Create(
                    new[]
                    {
                        // IDXGIObject
                        Marshal.GetFunctionPointerForDelegate(privateData),  // SetPrivateData
                        Marshal.GetFunctionPointerForDelegate(privateData),  // …Interface
                        Marshal.GetFunctionPointerForDelegate(privateData),  // GetPrivateData
                        Marshal.GetFunctionPointerForDelegate(parent),       // GetParent
                        // IDXGIOutput
                        Marshal.GetFunctionPointerForDelegate(describe),
                        Marshal.GetFunctionPointerForDelegate(modeList),
                        Marshal.GetFunctionPointerForDelegate(closest),
                        Marshal.GetFunctionPointerForDelegate(vblank),
                        Marshal.GetFunctionPointerForDelegate(takeOwnership),
                        Marshal.GetFunctionPointerForDelegate(releaseOwnership),
                        Marshal.GetFunctionPointerForDelegate(oneArg),       // gamma caps
                        Marshal.GetFunctionPointerForDelegate(oneArg),       // SetGammaControl
                        Marshal.GetFunctionPointerForDelegate(oneArg),       // GetGammaControl
                        Marshal.GetFunctionPointerForDelegate(oneArg),       // SetDisplaySurface
                        Marshal.GetFunctionPointerForDelegate(oneArg),       // GetDisplaySurfaceData
                        Marshal.GetFunctionPointerForDelegate(oneArg),       // GetFrameStatistics
                    },
                    new[] { "2411e7e1-12ac-4ccf-bd14-9798e8534dc0" });

                Note = made == IntPtr.Zero
                    ? "could not be built"
                    : "one screen, " + Modes.Length + " modes";
                return made;
            }
            catch (Exception error)
            {
                Note = "build: " + error.GetType().Name;
                return IntPtr.Zero;
            }
        }

        private static void WriteMode(IntPtr at, Mode mode, int format)
        {
            Marshal.WriteInt32(at, 0, mode.Width);
            Marshal.WriteInt32(at, 4, mode.Height);
            Marshal.WriteInt32(at, 8, mode.Hertz);   // refresh numerator
            Marshal.WriteInt32(at, 12, 1);           // refresh denominator
            // Asked for a specific format, answer in it; asked for none, the
            // one every engine on this console ends up using.
            Marshal.WriteInt32(at, 16, format == 0 ? 87 : format);
            Marshal.WriteInt32(at, 20, Progressive);
            Marshal.WriteInt32(at, 24, Unscaled);
        }
    }
}
