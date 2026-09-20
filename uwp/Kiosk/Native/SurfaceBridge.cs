using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Shows the game's picture without ever bringing it back to the processor.
    ///
    /// The first way that worked copied every frame into main memory and wrote
    /// it into a bitmap: the card was asked to hand the frame over, the
    /// processor waited for it, copied three and a half megabytes, and handed
    /// the same three and a half megabytes back for the interface to draw. It
    /// worked, and it cost a full stop between card and processor on every
    /// single frame — the application stayed alive for about a second and was
    /// then suspended by the console, which is what happens to an application
    /// that misses enough of its own frames.
    ///
    /// This asks the framework for a surface instead. The framework hands back
    /// a texture that belongs to it, the game's frame is copied into it by the
    /// card, and nothing crosses to the processor at all. The interface thread
    /// does no work per frame beyond being told the surface changed.
    ///
    /// Whether the console allows this at all is the thing being measured:
    /// the panel interface this console refuses outright, and a surface source
    /// is a different route to the same place, implemented by the framework
    /// rather than by the compositor.
    /// </summary>
    internal static class SurfaceBridge
    {
        // ISurfaceImageSourceNative, counting from IUnknown.
        private const int SetDeviceSlot = 3;
        private const int BeginDrawSlot = 4;
        private const int EndDrawSlot = 5;

        // ID3D11DeviceContext
        private const int CopyRegionSlot = 46;

        private const int S_OK = 0;

        private static readonly Guid SurfaceSourceNative =
            new Guid("F2E9EDC1-D307-4525-9886-0FAFAA44163C");
        private static readonly Guid DxgiDevice =
            new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid Texture2D =
            new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X, Y;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetDeviceDelegate(IntPtr self, IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BeginDrawDelegate(
            IntPtr self, Rect area, out IntPtr surface, out Point offset);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EndDrawDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CopyRegionDelegate(
            IntPtr self, IntPtr to, uint toPart, uint x, uint y, uint z,
            IntPtr from, uint fromPart, IntPtr box);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AskDelegate(IntPtr self, ref Guid id, out IntPtr found);

        private static IntPtr native;
        private static IntPtr context;
        private static int width;
        private static int height;
        private static int busy;

        /// <summary>Frames the card copied straight into the framework's surface.</summary>
        public static long Drawn;

        /// <summary>What happened, in one line, for the report.</summary>
        public static string Note = "not started";

        /// <summary>Whether this route is the one carrying the picture.</summary>
        public static bool Running { get; private set; }

        /// <summary>
        /// Asks one object for another interface. Written out rather than left
        /// to the runtime because .NET Native refuses to ask a WinRT object for
        /// an arbitrary interface on our behalf — the whole reason every COM
        /// call in this project is made by slot.
        /// </summary>
        private static IntPtr Ask(IntPtr unknown, Guid id)
        {
            if (unknown == IntPtr.Zero) return IntPtr.Zero;
            var ask = Marshal.GetDelegateForFunctionPointer<AskDelegate>(
                ComProxy.Method(unknown, 0));
            IntPtr found;
            return ask(unknown, ref id, out found) == S_OK ? found : IntPtr.Zero;
        }

        /// <summary>
        /// Builds the surface and hands it to the image on screen. Must run on
        /// the interface thread, because that is the thread that owns the tree
        /// the image lives in.
        /// </summary>
        public static bool Start(
            IntPtr device, IntPtr immediateContext, int pixelsWide, int pixelsHigh,
            Windows.UI.Xaml.Controls.Image image)
        {
            try
            {
                if (device == IntPtr.Zero || image == null)
                {
                    Note = "nothing to draw into";
                    return false;
                }

                width = pixelsWide;
                height = pixelsHigh;
                context = immediateContext;

                var source = new Windows.UI.Xaml.Media.Imaging.SurfaceImageSource(
                    pixelsWide, pixelsHigh, true);
                var unknown = Marshal.GetIUnknownForObject(source);
                native = Ask(unknown, SurfaceSourceNative);
                Marshal.Release(unknown);

                if (native == IntPtr.Zero)
                {
                    // The measurement this class exists to take. The console
                    // refuses the panel interface the same way; if it refuses
                    // this one too, the picture has to come back through main
                    // memory and the cost has to be paid somewhere else.
                    Note = "console refused the surface interface";
                    return false;
                }

                // The surface has to be told which card drew the frame, or it
                // has no way to accept a texture from it.
                var dxgi = Ask(device, DxgiDevice);
                if (dxgi == IntPtr.Zero)
                {
                    Note = "device is not a display device";
                    return false;
                }

                var setDevice = Marshal.GetDelegateForFunctionPointer<SetDeviceDelegate>(
                    ComProxy.Method(native, SetDeviceSlot));
                var code = setDevice(native, dxgi);
                Marshal.Release(dxgi);
                if (code != S_OK)
                {
                    Note = "surface refused the device: 0x" + code.ToString("X8");
                    return false;
                }

                image.Source = source;
                Running = true;
                Note = "drawing straight into the framework's surface";
                return true;
            }
            catch (Exception error)
            {
                Note = "start: " + error.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// One frame. Called from whichever thread the game finished drawing
        /// on, which is not the interface thread — the copy is the card's work,
        /// so the thread that asks for it does not matter.
        /// </summary>
        public static void Take(IntPtr back)
        {
            if (!Running || back == IntPtr.Zero) return;

            // A frame still being handed over is a frame not worth queueing
            // behind. Dropping it costs nothing; waiting for it costs the
            // stall this class was written to remove.
            if (System.Threading.Interlocked.Exchange(ref busy, 1) == 1) return;

            var surface = IntPtr.Zero;
            var into = IntPtr.Zero;
            var opened = false;
            try
            {
                var begin = Marshal.GetDelegateForFunctionPointer<BeginDrawDelegate>(
                    ComProxy.Method(native, BeginDrawSlot));
                var area = new Rect { Left = 0, Top = 0, Right = width, Bottom = height };
                Point offset;
                var code = begin(native, area, out surface, out offset);
                if (code != S_OK)
                {
                    Note = "begin: 0x" + code.ToString("X8");
                    Running = false;
                    return;
                }
                opened = true;

                into = Ask(surface, Texture2D);
                if (into == IntPtr.Zero)
                {
                    Note = "surface is not a texture";
                    Running = false;
                    return;
                }

                // The framework hands back a corner of a larger sheet, so the
                // frame goes in at the offset it gave, not at zero.
                var copy = Marshal.GetDelegateForFunctionPointer<CopyRegionDelegate>(
                    ComProxy.Method(context, CopyRegionSlot));
                copy(context, into, 0, (uint)offset.X, (uint)offset.Y, 0,
                     back, 0, IntPtr.Zero);

                Drawn++;
            }
            catch (Exception error)
            {
                Note = "take: " + error.GetType().Name;
                Running = false;
            }
            finally
            {
                if (into != IntPtr.Zero) Marshal.Release(into);
                if (surface != IntPtr.Zero) Marshal.Release(surface);
                if (opened)
                {
                    try
                    {
                        var end = Marshal.GetDelegateForFunctionPointer<EndDrawDelegate>(
                            ComProxy.Method(native, EndDrawSlot));
                        end(native);
                    }
                    catch
                    {
                        // Nothing useful to do: the frame is lost either way,
                        // and the next one will say so through BeginDraw.
                    }
                }
                System.Threading.Interlocked.Exchange(ref busy, 0);
            }
        }
    }
}
