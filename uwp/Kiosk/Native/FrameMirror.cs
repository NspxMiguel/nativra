using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.Xaml.Media.Imaging;

namespace Kiosk.Native
{
    /// <summary>
    /// Shows the game's frames by copying them, when nothing will show them
    /// directly.
    ///
    /// A finished frame lives on the graphics card. Every supported way of
    /// putting it on screen from inside a packaged application goes through a
    /// COM interface this runtime will not ask for, and this console's own
    /// window is already spoken for. What is left is the blunt way: read the
    /// frame back into ordinary memory and hand it to the interface framework
    /// as a picture, which is plain managed code and cannot be refused.
    ///
    /// It costs a read back from the card and two copies of a screen every
    /// frame. On a machine built to move far more than that, it is worth
    /// doing badly rather than not at all — and it is a fallback, not the
    /// plan.
    /// </summary>
    public static class FrameMirror
    {
        // ID3D11Device
        private const int CreateTexture2DSlot = 5;
        // Forty, not thirty-nine: CheckFeatureSupport sits between the counter
        // calls and the private-data ones, and leaving it out of the count
        // lands on GetDeviceRemovedReason — which takes no arguments, writes
        // nothing, and leaves the context quietly null.
        private const int GetImmediateContextSlot = 40;

        // ID3D11DeviceContext
        private const int MapSlot = 14;
        private const int UnmapSlot = 15;
        private const int CopyResourceSlot = 47;

        // IDXGISwapChain
        private const int GetBufferSlot = 9;

        private const int S_OK = 0;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTextureDelegate(
            IntPtr self, IntPtr desc, IntPtr initial, IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ContextDelegate(IntPtr self, IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BufferDelegate(
            IntPtr self, uint index, IntPtr riid, IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CopyDelegate(IntPtr self, IntPtr to, IntPtr from);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MapDelegate(
            IntPtr self, IntPtr resource, uint sub, uint how, uint flags, IntPtr mapped);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint sub);

        private static IntPtr context;
        private static IntPtr staging;
        private static IntPtr back;
        private static IntPtr given;
        private static IntPtr chain;
        private static int width;
        private static int height;

        private static WriteableBitmap picture;
        // Two buffers, used in turn: the game fills one while the interface
        // thread is still reading the other. One buffer means a frame half
        // overwritten while it is being shown, which looks like tearing.
        private static byte[][] scratch;
        private static int filling;
        private static int busy;
        private static int lastShown;

        /// <summary>How many frames actually reached the screen.</summary>
        public static long Shown;
        private static Windows.UI.Core.CoreDispatcher ui;
        private static Windows.UI.Xaml.Controls.Image target;

        /// <summary>Shown alongside the game's own picture, never under it.</summary>
        public static Windows.UI.Xaml.UIElement Credit;

        /// <summary>Frames copied to the screen, which is the proof it works.</summary>
        public static long Copied;

        public static bool Running { get; private set; }

        public static string Note = "not started";

        /// <summary>
        /// Prepares the copy. Everything here is one-off: the staging texture
        /// the card can be read into, the picture the framework can show, and
        /// the buffer between them.
        /// </summary>
        public static bool Start(
            IntPtr device, IntPtr swapChain, int pixelsWide, int pixelsHigh, int format,
            Windows.UI.Xaml.Controls.Image image, Windows.UI.Core.CoreDispatcher dispatcher,
            IntPtr texture = default(IntPtr))
        {
            try
            {
                // A chain is one way to reach the finished frame; being handed
                // the texture directly is another, and it is the one this
                // console ends up using — the chain the game holds is ours,
                // and it has no display behind it to ask. Requiring both was
                // left over from when there was only the first way.
                if (device == IntPtr.Zero || image == null ||
                    (swapChain == IntPtr.Zero && texture == IntPtr.Zero))
                {
                    Note = "nothing to mirror";
                    return false;
                }

                chain = swapChain;
                given = texture;
                width = pixelsWide;
                height = pixelsHigh;
                ui = dispatcher;
                target = image;

                var desc = Marshal.AllocHGlobal(44);
                try
                {
                    Marshal.WriteInt32(desc, 0, width);
                    Marshal.WriteInt32(desc, 4, height);
                    Marshal.WriteInt32(desc, 8, 1);        // MipLevels
                    Marshal.WriteInt32(desc, 12, 1);       // ArraySize
                    Marshal.WriteInt32(desc, 16, format);
                    Marshal.WriteInt32(desc, 20, 1);       // SampleDesc.Count
                    Marshal.WriteInt32(desc, 24, 0);       // SampleDesc.Quality
                    Marshal.WriteInt32(desc, 28, 3);       // D3D11_USAGE_STAGING
                    Marshal.WriteInt32(desc, 32, 0);       // BindFlags
                    Marshal.WriteInt32(desc, 36, 0x20000); // CPU read
                    Marshal.WriteInt32(desc, 40, 0);       // MiscFlags

                    var slot = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        Marshal.WriteIntPtr(slot, IntPtr.Zero);
                        var make = Marshal.GetDelegateForFunctionPointer<CreateTextureDelegate>(
                            ComProxy.Method(device, CreateTexture2DSlot));
                        var code = make(device, desc, IntPtr.Zero, slot);
                        if (code != S_OK)
                        {
                            Note = "no staging texture: 0x" + code.ToString("X8");
                            return false;
                        }
                        staging = Marshal.ReadIntPtr(slot);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(slot);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(desc);
                }

                var holder = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(holder, IntPtr.Zero);
                    var ask = Marshal.GetDelegateForFunctionPointer<ContextDelegate>(
                        ComProxy.Method(device, GetImmediateContextSlot));
                    ask(device, holder);
                    context = Marshal.ReadIntPtr(holder);
                }
                finally
                {
                    Marshal.FreeHGlobal(holder);
                }

                if (context == IntPtr.Zero)
                {
                    Note = "no device context";
                    return false;
                }

                // A texture handed straight over needs no asking: it is the
                // back buffer, because this application made it.
                if (given != IntPtr.Zero)
                {
                    back = given;
                    Marshal.AddRef(back);
                }
                else
                {
                    // Taken once. With the flip model the runtime rotates its own
                    // buffers and buffer zero stays valid, so asking for it sixty
                    // times a second is sixty interface calls that answer the same.
                    var handle = Marshal.AllocHGlobal(IntPtr.Size);
                    var id = Marshal.AllocHGlobal(16);
                    try
                    {
                        Marshal.StructureToPtr(
                            new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), id, false);
                        Marshal.WriteIntPtr(handle, IntPtr.Zero);
                        var get = Marshal.GetDelegateForFunctionPointer<BufferDelegate>(
                            ComProxy.Method(chain, GetBufferSlot));
                        if (get(chain, 0, id, handle) != S_OK)
                        {
                            Note = "no back buffer";
                            return false;
                        }
                        back = Marshal.ReadIntPtr(handle);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(handle);
                        Marshal.FreeHGlobal(id);
                    }
                }

                scratch = new[]
                {
                    new byte[width * height * 4],
                    new byte[width * height * 4],
                };

                var ready = new System.Threading.ManualResetEventSlim(false);
                var _ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
                {
                    try
                    {
                        picture = new WriteableBitmap(width, height);
                        target.Source = picture;
                        target.Visibility = Windows.UI.Xaml.Visibility.Visible;

                        // Whose work this is, shown over the game as well as
                        // over the menu. It is the whole point of a screenshot
                        // of a PC game running on a console: anyone who sees
                        // it should be able to read who did it.
                        if (Credit != null)
                        {
                            Credit.Visibility = Windows.UI.Xaml.Visibility.Visible;
                        }
                    }
                    catch (Exception error)
                    {
                        Note = "picture: " + error.GetType().Name;
                    }
                    finally
                    {
                        ready.Set();
                    }
                });
                ready.Wait(3000);

                Running = picture != null;
                Note = Running ? "mirroring" : Note;
                return Running;
            }
            catch (Exception error)
            {
                Note = error.GetType().Name + ": " + error.Message;
                return false;
            }
        }

        /// <summary>
        /// Copies one finished frame. Called before the frame is handed to the
        /// screen, because a flip-model chain rotates its buffers on the way
        /// out and what was just drawn is no longer where it was.
        /// </summary>
        public static void Take()
        {
            if (!Running) return;
            // Reserve the CPU buffer before writing it, including while the
            // dispatcher is still consuming the previous frame.
            var now = Environment.TickCount;
            if (unchecked(now - lastShown) < 50) return;
            if (System.Threading.Interlocked.Exchange(ref busy, 1) == 1) return;
            var mapped = IntPtr.Zero;
            var isMapped = false;
            var queued = false;
            try
            {
                var copy = Marshal.GetDelegateForFunctionPointer<CopyDelegate>(
                    ComProxy.Method(context, CopyResourceSlot));
                copy(context, staging, back);

                mapped = Marshal.AllocHGlobal(16);
                var map = Marshal.GetDelegateForFunctionPointer<MapDelegate>(
                    ComProxy.Method(context, MapSlot));
                if (map(context, staging, 0, 1, 0, mapped) != S_OK) return;
                isMapped = true;

                var from = Marshal.ReadIntPtr(mapped, 0);
                var pitch = Marshal.ReadInt32(mapped, 8);
                var into = scratch[filling];
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(from + row * pitch, into, row * width * 4, width * 4);
                }
                // The window back buffer uses IGNORE alpha; WriteableBitmap
                // instead composites alpha, so an otherwise valid frame can
                // disappear entirely unless we make it opaque here.
                for (var alpha = 3; alpha < into.Length; alpha += 4)
                    into[alpha] = 255;
                filling = 1 - filling;

                var unmap = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(
                    ComProxy.Method(context, UnmapSlot));
                unmap(context, staging, 0);
                isMapped = false;

                Copied++;

                // One update in flight at a time, and no more than thirty a
                // second. The interface thread has its own frame to draw, and
                // a thread given a screen-sized write sixty times a second
                // never draws it — which is how an application ends up alive,
                // busy, and absent from the screen.
                lastShown = now;
                var showing = into;
                var __ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        // Named in full rather than as an extension: which
                        // namespace carries it differs between runtimes, and a
                        // missing using here is a build that fails for a
                        // reason that has nothing to do with the problem.
                        using (var stream = WindowsRuntimeBufferExtensions.AsStream(
                            picture.PixelBuffer))
                        {
                            stream.Write(showing, 0, showing.Length);
                        }
                        picture.Invalidate();
                        Shown++;
                    }
                    catch
                    {
                        // A dropped frame is a dropped frame.
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref busy, 0);
                    }
                });
                queued = true;
            }
            catch (Exception error)
            {
                Note = "take: " + error.GetType().Name;
                Running = false;
            }
            finally
            {
                if (isMapped)
                {
                    var unmap = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(
                        ComProxy.Method(context, UnmapSlot));
                    unmap(context, staging, 0);
                }
                if (mapped != IntPtr.Zero) Marshal.FreeHGlobal(mapped);
                if (!queued) System.Threading.Interlocked.Exchange(ref busy, 0);
            }
        }
    }
}
