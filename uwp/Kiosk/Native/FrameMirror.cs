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
        // Two staging textures, used in turn. The frame copied now is read on
        // the next call, by which time the card has finished it: mapping the
        // copy that was just queued makes the processor wait for the whole
        // frame to render, every frame.
        private const int RingDepth = 2;
        private static readonly IntPtr[] ring = new IntPtr[RingDepth];
        private static int ringNext;
        private static int ringPrimed;
        private static IntPtr back;
        private static IntPtr given;
        private static IntPtr chain;
        private static int width;
        private static int height;
        public static int PixelWidth => width;
        public static int PixelHeight => height;
        private static int pixelFormat;
        private static readonly object frameGate = new object();
        private static int generation;

        private static WriteableBitmap picture;
        private static System.IO.Stream pixels;   // the picture's buffer, opened once per picture
        // One buffer between the card and the picture. The busy flag already
        // keeps the game from filling it until the interface thread has copied
        // it into the picture, so a second one would only ever sit idle (8 MB
        // at 1080p, on the large-object heap). Kept across resizes when big
        // enough.
        private static byte[] scratch;
        private static int busy;

        // Everything a frame needs, made once per Start instead of per frame:
        // the context's methods as delegates, the mapped-subresource block,
        // and the interface-thread handler.
        private static CopyDelegate copyFn;
        private static MapDelegate mapFn;
        private static UnmapDelegate unmapFn;
        private static IntPtr mappedBlock;
        private static Windows.UI.Core.DispatchedHandler showFrame;
        private static int showVersion;
        private static long showQueuedAt;

        /// <summary>Bytes this mirror holds: staging textures, the CPU buffer, the picture.</summary>
        public static long Bytes
        {
            get
            {
                if (!Running) return scratch?.LongLength ?? 0;
                var frame = (long)width * height * 4;
                return RingDepth * frame + (scratch?.LongLength ?? 0) + frame;
            }
        }
        private static long mapTicks;
        private static long pixelsTicks;
        private static long uiTicks;
        private static long timedFrames;

        public static string Timing
        {
            get
            {
                var count = Math.Max(1, timedFrames);
                var scale = 1000.0 / System.Diagnostics.Stopwatch.Frequency / count;
                return "map=" + (mapTicks * scale).ToString("F2") +
                    "ms pixels=" + (pixelsTicks * scale).ToString("F2") +
                    "ms ui=" + (uiTicks * scale).ToString("F2") + "ms samples=" + count;
            }
        }

        /// <summary>How many frames actually reached the screen.</summary>
        public static long Shown;
        private static Windows.UI.Core.CoreDispatcher ui;
        private static Windows.UI.Xaml.Controls.Image target;

        /// <summary>Shown alongside the game's own picture, never under it.</summary>
        public static Windows.UI.Xaml.UIElement Credit;
        public static Action Presented;

        /// <summary>Frames copied to the screen, which is the proof it works.</summary>
        public static long Copied;

        public static bool Running { get; private set; }

        public static string Note = "not started";
        public static string CaptureNote = "not captured";

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
            lock (frameGate)
                return Prepare(device, swapChain, pixelsWide, pixelsHigh, format, image, dispatcher, texture);
        }

        private static bool Prepare(
            IntPtr device, IntPtr swapChain, int pixelsWide, int pixelsHigh, int format,
            Windows.UI.Xaml.Controls.Image image, Windows.UI.Core.CoreDispatcher dispatcher,
            IntPtr texture)
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

                Running = false;
                var version = ++generation;
                if (back != IntPtr.Zero) Marshal.Release(back);
                for (var i = 0; i < RingDepth; i++)
                {
                    if (ring[i] != IntPtr.Zero) Marshal.Release(ring[i]);
                    ring[i] = IntPtr.Zero;
                }
                ringNext = ringPrimed = 0;
                if (context != IntPtr.Zero) Marshal.Release(context);
                back = staging = context = IntPtr.Zero;
                copyFn = null;
                mapFn = null;
                unmapFn = null;
                picture = null;
                pixels = null;
                chain = swapChain;
                given = texture;
                width = pixelsWide;
                height = pixelsHigh;
                pixelFormat = format;
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
                        var make = Marshal.GetDelegateForFunctionPointer<CreateTextureDelegate>(
                            ComProxy.Method(device, CreateTexture2DSlot));
                        for (var i = 0; i < RingDepth; i++)
                        {
                            Marshal.WriteIntPtr(slot, IntPtr.Zero);
                            var code = make(device, desc, IntPtr.Zero, slot);
                            if (code != S_OK)
                            {
                                Note = "no staging texture: 0x" + code.ToString("X8");
                                return false;
                            }
                            ring[i] = Marshal.ReadIntPtr(slot);
                        }
                        staging = ring[0];
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
                copyFn = Marshal.GetDelegateForFunctionPointer<CopyDelegate>(ComProxy.Method(context, CopyResourceSlot));
                mapFn = Marshal.GetDelegateForFunctionPointer<MapDelegate>(ComProxy.Method(context, MapSlot));
                unmapFn = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(ComProxy.Method(context, UnmapSlot));
                if (mappedBlock == IntPtr.Zero) mappedBlock = Marshal.AllocHGlobal(16);
                if (showFrame == null) showFrame = ShowFrame;

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

                var needed = width * height * 4;
                if (scratch == null || scratch.Length < needed) scratch = null;   // let the old one go first
                if (scratch == null) scratch = new byte[needed];

                var ready = new System.Threading.ManualResetEventSlim(false);
                var _ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
                {
                    try
                    {
                        if (version != generation) return;
                        picture = new WriteableBitmap(pixelsWide, pixelsHigh);
                        // Named in full rather than as an extension: which
                        // namespace carries it differs between runtimes.
                        pixels = WindowsRuntimeBufferExtensions.AsStream(picture.PixelBuffer);
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
                Note = Running ? "mirroring " + width + "x" + height + " format=" + format : Note;
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
        private static unsafe void CopyOpaque(
            IntPtr from, int pitch, byte[] into, int wide, int high, bool swapRedBlue)
        {
            fixed (byte* start = into)
            {
                var target = (uint*)start;
                for (var row = 0; row < high; row++)
                {
                    var source = (uint*)((byte*)from + (long)row * pitch);
                    var line = target + (long)row * wide;
                    if (swapRedBlue)
                    {
                        for (var x = 0; x < wide; x++)
                        {
                            var v = source[x];
                            line[x] = (v & 0x0000FF00u) | ((v & 0xFFu) << 16) | ((v >> 16) & 0xFFu) | 0xFF000000u;
                        }
                    }
                    else
                    {
                        for (var x = 0; x < wide; x++) line[x] = source[x] | 0xFF000000u;
                    }
                }
            }
        }

        public static void Take()
        {
            lock (frameGate) TakeLocked();
        }

        private static void TakeLocked()
        {
            if (!Running) return;
            // Reserve the CPU buffer before writing it, including while the
            // dispatcher is still consuming the previous frame.
            // The in-flight update is the backpressure. A second 16 ms gate
            // skips otherwise-ready frames when the render clock jitters.
            if (System.Threading.Interlocked.Exchange(ref busy, 1) == 1) return;
            var mapped = mappedBlock;
            var isMapped = false;
            var queued = false;
            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                copyFn(context, ring[ringNext], back);
                var readable = (ringNext + 1) % RingDepth;
                ringNext = readable;
                if (ringPrimed < RingDepth - 1)
                {
                    ringPrimed++;
                    return;
                }
                staging = ring[readable];

                if (mapFn(context, staging, 0, 1, 0, mapped) != S_OK) return;
                isMapped = true;
                var mappedAt = System.Diagnostics.Stopwatch.GetTimestamp();

                var from = Marshal.ReadIntPtr(mapped, 0);
                var pitch = Marshal.ReadInt32(mapped, 8);
                var into = scratch;
                // One pass from the mapped texture into the picture. The
                // window back buffer uses IGNORE alpha, while WriteableBitmap
                // composites it, so every pixel is made opaque; and the
                // bitmap is BGRA while games may render RGBA.
                CopyOpaque(from, pitch, into, width, height, pixelFormat == 28 || pixelFormat == 29);

                unmapFn(context, staging, 0);
                isMapped = false;
                var pixelsAt = System.Diagnostics.Stopwatch.GetTimestamp();
                mapTicks += mappedAt - started;
                pixelsTicks += pixelsAt - mappedAt;
                timedFrames++;

                Copied++;
                // One bounded raw-frame capture separates a renderer problem
                // from a screen-capture/compositor problem without tracing the
                // graphics hot path. Pixels only; no process memory or tokens.
                if (Copied == 600)
                {
                    var captured = new byte[width * height * 4];   // the buffer may be larger after a resize
                    Buffer.BlockCopy(into, 0, captured, 0, captured.Length);
                    var capturedWidth = width;
                    var capturedHeight = height;
                    var capture = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var local = Windows.Storage.ApplicationData.Current.LocalFolder;
                            var file = await local.CreateFileAsync("native-frame.bgra",
                                Windows.Storage.CreationCollisionOption.ReplaceExisting);
                            await Windows.Storage.FileIO.WriteBytesAsync(file, captured);
                            CaptureNote = capturedWidth + "x" + capturedHeight + " BGRA8";
                        }
                        catch (Exception error) { CaptureNote = error.GetType().Name; }
                    });
                }
                if (Copied % 60 == 1)
                {
                    var peak = 0;
                    for (var p = 0; p < into.Length; p += 256)
                        peak = Math.Max(peak, Math.Max(into[p], Math.Max(into[p + 1], into[p + 2])));
                    Note = "mirroring " + width + "x" + height + " format=" + pixelFormat + " sampledRgbPeak=" + peak;
                }

                // Keep one update in flight; drop frames while the UI is busy
                // instead of accumulating latency behind the current picture.
                showVersion = generation;
                showQueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var __ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, showFrame);
                queued = true;
            }
            catch (Exception error)
            {
                Note = "take: " + error.GetType().Name;
                Running = false;
            }
            finally
            {
                if (isMapped) unmapFn(context, staging, 0);
                if (!queued) System.Threading.Interlocked.Exchange(ref busy, 0);
            }
        }

        /// <summary>On the interface thread: the CPU buffer into the picture.</summary>
        private static void ShowFrame()
        {
            try
            {
                if (showVersion != generation || pixels == null) return;
                pixels.Position = 0;
                pixels.Write(scratch, 0, width * height * 4);
                picture.Invalidate();
                System.Threading.Interlocked.Add(ref uiTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - showQueuedAt);
                Shown++;
                Presented?.Invoke();
            }
            catch
            {
                // A dropped frame is a dropped frame.
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref busy, 0);
            }
        }
    }
}
