using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Gets a PC game's pictures onto a console screen.
    ///
    /// Everything a Windows game does to draw works here as it is — the Xbox
    /// runs the same Direct3D — except for one sentence in the middle of it:
    /// the swap chain, the thing that hands finished frames to the screen, is
    /// created against a window handle, and this operating system has no window
    /// handles. It has a CoreWindow, and a different call that takes one.
    ///
    /// So the factory the game receives is not the system's. It is a stand-in
    /// whose table is the system's, entry for entry, with the two window-shaped
    /// entries replaced by ones that hand over the console's own window. The
    /// game asks the same question it always asked and never learns that the
    /// answer came from somewhere else — which is the whole point: no game
    /// should have to be modified to run here.
    /// </summary>
    public static class GraphicsBridge
    {
        // Slots in IDXGIFactory2, counting from IUnknown.
        private const int QueryInterfaceSlot = 0;
        private const int EnumAdaptersSlot = 7;
        private const int EnumAdapters1Slot = 12;
        // On IDXGIAdapter, slot seven is EnumOutputs — the same number the
        // factory uses for EnumAdapters, on a different interface.
        private const int EnumOutputsSlot = 7;
        // On IDXGIDevice, that same seventh slot is GetAdapter.
        private const int GetAdapterSlot = 7;
        // On IDXGIAdapter: GetDesc is eight, and IDXGIAdapter1 adds GetDesc1
        // at ten with CheckInterfaceSupport between them. Named for the
        // adapter because the swap chain has a GetDesc of its own, at twelve.
        private const int AdapterDescSlot = 8;
        private const int AdapterDesc1Slot = 10;
        // ID3D11Device, counting from IUnknown. A device asked for by that
        // name has exactly this many, whatever the console's is underneath.
        private const int DeviceMethods = 43;
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        private const int GetParentSlot = 6;
        private const int AdapterMethods = 11;
        private const int CreateSwapChainSlot = 10;
        private const int CreateForHwndSlot = 15;
        private const int CreateForCoreWindowSlot = 16;
        private const int CreateForCompositionSlot = 24;
        private const int FactoryMethods = 25;

        // Slots in IDXGISwapChain1.
        private const int GetBufferSlot = 9;
        private const int PresentSlot = 8;
        private const int Present1Slot = 22;
        private const int SetFullscreenSlot = 10;
        private const int GetFullscreenSlot = 11;
        private const int GetDescSlot = 12;
        private const int ResizeTargetSlot = 14;
        private const int SwapChainMethods = 29;

        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);

        /// <summary>
        /// The console's window, as COM sees it. Taken on the interface thread
        /// at startup, because that is the only thread that can ask for it.
        /// </summary>
        public static IntPtr ConsoleWindow;

        /// <summary>Where copied frames are shown when composing is refused.</summary>
        public static Windows.UI.Xaml.Controls.Image Mirror;

        /// <summary>The element the finished frames are composed into.</summary>
        public static Windows.UI.Xaml.Controls.SwapChainPanel Surface;

        /// <summary>The interface thread, which is the only one that may touch it.</summary>
        public static Windows.UI.Core.CoreDispatcher OnUi;

        /// <summary>What happened, in order, for the report to carry.</summary>
        public static readonly List<string> Notes = new List<string>();

        private static readonly ComProxy Proxy = new ComProxy();
        private static SystemImports imports;

        private static void Note(string line)
        {
            lock (Notes)
            {
                if (Notes.Count < 60) Notes.Add(line);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FactoryDelegate(IntPtr riid, IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Factory2Delegate(uint flags, IntPtr riid, IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int QueryInterfaceDelegate(IntPtr self, IntPtr riid, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateSwapChainDelegate(
            IntPtr self, IntPtr device, IntPtr desc, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateForHwndDelegate(
            IntPtr self, IntPtr device, IntPtr window, IntPtr desc,
            IntPtr fullscreen, IntPtr restrict, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateForCoreWindowDelegate(
            IntPtr self, IntPtr device, IntPtr window, IntPtr desc,
            IntPtr restrict, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateForCompositionDelegate(
            IntPtr self, IntPtr device, IntPtr desc, IntPtr restrict, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetSwapChainDelegate(IntPtr self, IntPtr chain);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ItemOutDelegate(IntPtr self, uint index, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OneOutDelegate(IntPtr self, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetFullscreenGetDelegate(IntPtr self, IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PresentDelegate(IntPtr self, uint interval, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BufferDelegate(
            IntPtr self, uint index, IntPtr riid, IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Present1Delegate(
            IntPtr self, uint interval, uint flags, IntPtr parameters);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetFullscreenDelegate(IntPtr self, int on, IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetFullscreenDelegate(IntPtr self, IntPtr on, IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ResizeTargetDelegate(IntPtr self, IntPtr mode);

        private static FactoryDelegate factory;
        private static FactoryDelegate factory1;
        private static Factory2Delegate factory2;
        private static QueryInterfaceDelegate queryInterface;
        private static CreateSwapChainDelegate createSwapChain;
        private static CreateForHwndDelegate createForHwnd;
        private static PresentDelegate present;
        private static PresentDelegate presentThrough;
        private static Present1Delegate presentOne;
        private static Present1Delegate presentOneThrough;
        private static BufferDelegate buffer;
        private static BufferDelegate bufferThrough;

        /// <summary>
        /// How many times the back buffer was handed out. A game that never
        /// asks for it is not drawing at all; a game that asks and never
        /// presents is drawing into something it cannot show.
        /// </summary>
        public static long Buffers;

        /// <summary>Whether frames are being copied to the screen.</summary>
        public static bool Mirroring;

        /// <summary>
        /// Render at 1280x720 instead of full size. Every copied frame costs
        /// its own size twice over, so this is the one dial that changes what
        /// the whole path costs — and a console has always had this dial.
        /// </summary>
        public static bool Smaller;

        /// <summary>
        /// Refuse to make a swap chain at all. Only to find out whether making
        /// one is what takes the screen away from this application: a chain is
        /// the one thing a game creates that the display system knows about.
        /// </summary>
        public static bool NoChain;

        /// <summary>
        /// Leaves the graphics device as the console made it.
        ///
        /// Standing in for the device closes the way round the bridge, and
        /// closing it is the point — but it also puts a stand-in under every
        /// call a game makes to its own device, which is a large thing to have
        /// no way of switching off while it is being judged.
        /// </summary>
        public static bool NoDeviceStandIn;

        /// <summary>
        /// Describes the console's graphics part the way the rest of the world
        /// describes it.
        ///
        /// The console answers "Microsoft", "SraKmd_arden", device 0xD000 —
        /// true, and a name no engine has ever heard of. Engines keep tables
        /// of which hardware needs which workaround, keyed on exactly those
        /// numbers, and hardware that matches nothing gets the cautious
        /// defaults instead of the right ones. Some go further and refuse what
        /// they cannot name.
        ///
        /// What is underneath is an AMD RDNA2 part, the same architecture as
        /// the desktop cards of its generation. Saying so is not a disguise —
        /// it is a more useful truth than the console's own answer, because it
        /// is the one those tables are written against. The memory figures and
        /// the adapter's identity are left exactly as the console reported
        /// them; only the three fields that name the part are answered.
        /// </summary>
        public static bool NameTheCard;
        // Captured from the real DXGI response before compatibility overrides.
        public static string ReportedAdapter;
        public static uint ReportedVendor;
        public static uint ReportedDevice;
        public static ulong ReportedVideoMemory;
        public static int ReportedFeatureLevel;

        /// <summary>
        /// Leave the frames where they are. The copy runs on the engine's own
        /// render thread, which makes it the largest source of managed calls
        /// coming out of native code — and that is worth being able to switch
        /// off while looking for something that stops the whole process.
        /// </summary>
        public static bool NoMirror;

        /// <summary>
        /// Frames per second the game is allowed to hand over. An application
        /// shares this console's graphics with the system that draws around it,
        /// and a game that was written to take a whole machine does not know to
        /// leave anything. Zero lets it run as fast as it can.
        /// </summary>
        public static int Ceiling;

        private static readonly System.Diagnostics.Stopwatch paceClock =
            System.Diagnostics.Stopwatch.StartNew();
        private static double lastPresentDone;
        private static double nextDue;
        private static double workTotal;
        private static long workSamples;
        private static double workWorst;

        /// <summary>
        /// What each frame cost the game before the wait, in milliseconds:
        /// the headroom a rate at the ceiling hides.
        /// </summary>
        public static string Work
        {
            get
            {
                var samples = System.Threading.Interlocked.Read(ref workSamples);
                if (samples == 0) return "none";
                var average = workTotal / samples;
                var worst = workWorst;
                workWorst = 0;
                return average.ToString("F2") + "ms avg, " + worst.ToString("F1") + "ms worst";
            }
        }

        [DllImport("api-ms-win-core-synch-l1-2-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("api-ms-win-core-synch-l1-1-0.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(
            IntPtr timer, ref long due, int period, IntPtr routine, IntPtr argument, bool resume);

        [DllImport("api-ms-win-core-synch-l1-1-0.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [ThreadStatic] private static IntPtr paceTimer;
        [ThreadStatic] private static bool paceTimerTried;

        /// <summary>
        /// Sleeps for a fraction of a frame without the ~15.6 ms granularity
        /// of Thread.Sleep. A high-resolution waitable timer does that without
        /// spinning; where one cannot be made, the caller's spin covers it.
        /// </summary>
        private static void WaitPrecisely(double milliseconds)
        {
            if (!paceTimerTried)
            {
                paceTimerTried = true;
                try
                {
                    // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS.
                    paceTimer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);
                }
                catch
                {
                    paceTimer = IntPtr.Zero;
                }
            }
            if (paceTimer == IntPtr.Zero) return;
            var due = -(long)(milliseconds * 10000);
            if (SetWaitableTimer(paceTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(paceTimer, 100);
        }

        /// <summary>
        /// Holds each frame to its slot on the ceiling's clock. Measured with
        /// the high-resolution counter: TickCount moves in ~15.6 ms steps, so
        /// pacing on it alternated short and long frames — judder, and a rate
        /// that settled near 57 instead of 60.
        /// </summary>
        private static void Pace()
        {
            var now = paceClock.Elapsed.TotalMilliseconds;
            if (lastPresentDone > 0)
            {
                var work = now - lastPresentDone;
                workTotal += work;
                workSamples++;
                if (work > workWorst) workWorst = work;
            }
            if (Ceiling > 0)
            {
                var gap = 1000.0 / Ceiling;
                // A frame that ran late starts a new schedule instead of
                // racing to catch up on the ones it missed.
                if (nextDue <= 0 || now - nextDue > gap) nextDue = now;
                nextDue += gap;
                var wait = nextDue - now;
                if (wait > 1.5) WaitPrecisely(wait - 0.5);
                while (paceClock.Elapsed.TotalMilliseconds < nextDue) System.Threading.Thread.SpinWait(64);
            }
            lastPresentDone = paceClock.Elapsed.TotalMilliseconds;
        }
        private static SetFullscreenDelegate setFullscreen;

        /// <summary>
        /// Frames handed to the screen. It is the only honest measure of
        /// whether a game is running: everything else says it is trying.
        /// </summary>
        public static long Frames;

        /// <summary>When the first frame reached the screen.</summary>
        public static int FirstFrameAt;
        private static GetFullscreenDelegate getFullscreen;
        private static ResizeTargetDelegate resizeTarget;
        private static SetFullscreenGetDelegate describe;

        /// <summary>The interface identifiers a factory stand-in answers for.</summary>
        private static readonly string[] FactoryIds =
        {
            "00000000-0000-0000-c000-000000000046", // IUnknown
            "aec22fb8-76f3-4639-9be0-28eb43a67a2e", // IDXGIObject
            "7b7166ec-21c7-44ae-b21a-c9ae321ae369", // IDXGIFactory
            "770aae78-f26f-4dba-a829-253c83d1b387", // IDXGIFactory1
            "50c83a1c-e072-4c48-87b0-3630fa36a6d0", // IDXGIFactory2
            // The five the list used to stop short of. An engine that asks for
            // one of these and is handed the console's own factory instead of
            // the stand-in has walked straight out of the bridge — and then
            // asks that factory for a window-shaped swap chain, which is the
            // one thing this console refuses. Unity asks for the fifth, to
            // find out whether the screen can tear.
            "25483823-cd46-4c7d-86ca-47aa95b837bd", // IDXGIFactory3
            "1bc6ea02-ef36-464f-bf0c-21ca39e5168a", // IDXGIFactory4
            "7632e1f5-ee65-4dca-87fd-84cd75f8838d", // IDXGIFactory5
            "c1b6694f-ff09-44a9-b03c-77900a0a1d17", // IDXGIFactory6
            "a4966eed-76db-44da-84c1-ee9a7afb20a8", // IDXGIFactory7
        };

        /// <summary>
        /// Each factory interface and how many methods its table holds,
        /// newest first. Handing back a table one entry short of what the
        /// caller believes it has is a call into whatever sits next in memory.
        /// </summary>
        private static readonly Tuple<string, int>[] FactoryShapes =
        {
            Tuple.Create("a4966eed-76db-44da-84c1-ee9a7afb20a8", 32), // IDXGIFactory7
            Tuple.Create("c1b6694f-ff09-44a9-b03c-77900a0a1d17", 30), // IDXGIFactory6
            Tuple.Create("7632e1f5-ee65-4dca-87fd-84cd75f8838d", 29), // IDXGIFactory5
            Tuple.Create("1bc6ea02-ef36-464f-bf0c-21ca39e5168a", 28), // IDXGIFactory4
            Tuple.Create("25483823-cd46-4c7d-86ca-47aa95b837bd", 26), // IDXGIFactory3
            Tuple.Create("50c83a1c-e072-4c48-87b0-3630fa36a6d0", 25), // IDXGIFactory2
        };

        // How many methods each version of the display-device interface has,
        // counted from IUnknown. Copying a table needs its length, and a table
        // copied one entry short is a call into whatever follows it.
        private static readonly Dictionary<string, int> DxgiDeviceIds =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "54ec77fa-1377-44e6-8c32-88fd5f44c84c", 12 },  // IDXGIDevice
                { "77db970f-6276-48ba-ba28-070143b4392c", 14 },  // IDXGIDevice1
                { "05008617-fbfd-4051-a790-144884b4f6a9", 17 },  // IDXGIDevice2
                { "6007896c-3244-4afd-bf18-a6d3beda5023", 18 },  // IDXGIDevice3
                { "95b4f95f-d8da-4ca4-9ee6-3b76d5968a10", 20 },  // IDXGIDevice4
            };

        /// <summary>
        /// The way around the bridge, and the reason a game could take it.
        ///
        /// Everything here stands between a game and the factory, because the
        /// factory is where a window-shaped swap chain is asked for and this
        /// console refuses that. But a graphics device knows its own display
        /// device, a display device knows its adapter, and an adapter knows
        /// the factory that made it — the real one. An engine that walks those
        /// three steps is holding the object the bridge exists to replace, and
        /// nothing here would ever see it happen.
        ///
        /// Measured, and this is what the game does: the device is created,
        /// the bridge sees nothing more, and the engine turns forever. It was
        /// asking for its swap chain the whole time, on the other side.
        ///
        /// So the device handed back is a stand-in too. It answers only one
        /// question differently — which display device it is — and from there
        /// the walk lands back on our adapter and our factory.
        /// </summary>
        private static IntPtr WrapDevice(IntPtr device)
        {
            if (device == IntPtr.Zero) return device;
            try
            {
                // Only the first slot is ours. A device created by asking for
                // ID3D11Device has forty-three methods by contract, and any
                // later version is asked for through the slot we own, so the
                // ones past the end are never reached from here.
                return Proxy.Wrap(device, DeviceMethods, new Dictionary<int, IntPtr>
                {
                    { QueryInterfaceSlot, Marshal.GetFunctionPointerForDelegate(deviceAsk) },
                });
            }
            catch
            {
                return device;
            }
        }

        private static IntPtr WrapDisplayDevice(IntPtr display, int methods)
        {
            if (display == IntPtr.Zero) return display;
            try
            {
                return Proxy.Wrap(display, methods, new Dictionary<int, IntPtr>
                {
                    { GetAdapterSlot, Marshal.GetFunctionPointerForDelegate(displayAdapter) },
                    { GetParentSlot, Marshal.GetFunctionPointerForDelegate(displayParent) },
                });
            }
            catch
            {
                return display;
            }
        }

        private static bool IsDisplayDeviceId(IntPtr riid, out int methods)
        {
            methods = 0;
            if (riid == IntPtr.Zero) return false;
            try
            {
                var id = Marshal.PtrToStructure<Guid>(riid).ToString();
                return DxgiDeviceIds.TryGetValue(id, out methods);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFactoryId(IntPtr riid)
        {
            if (riid == IntPtr.Zero) return false;
            try
            {
                var id = Marshal.PtrToStructure<Guid>(riid).ToString();
                foreach (var known in FactoryIds)
                {
                    if (string.Equals(id, known, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch
            {
                // An unreadable identifier is not one of ours.
            }
            return false;
        }

        /// <summary>
        /// Rewrites a description into one a console swap chain accepts.
        ///
        /// The size and the pixel format are the game's business and are kept.
        /// The rest is not a preference, it is what this platform allows: the
        /// flip model, no multisampling on the back buffer, at least two
        /// buffers. A game asking for the old model is not asking for anything
        /// it can see — it is asking for how frames reach the screen, and here
        /// there is one way.
        /// </summary>
        /// <summary>
        /// The combinations a composed chain is willing to be made with.
        ///
        /// Scaling, the swap effect and the alpha mode are not preferences —
        /// each platform accepts a short list and refuses everything else with
        /// one undifferentiated error. Rather than guess which one this console
        /// wants, the bridge tries them in order and keeps the one that works.
        /// </summary>
        /// <summary>
        /// The shapes a chain made for the console's own window accepts, which
        /// are not the same as the ones a composed chain accepts. A window is
        /// a real display surface and wants its pixels unscaled; a composed
        /// one is a texture and wants them stretched to fit. Passing one the
        /// other's description is refused with the same single error, which is
        /// how this cost a night.
        /// </summary>
        private static readonly int[][] WindowShapes =
        {
            //  scaling, swap effect, alpha
            new[] { 1, 4, 1 },   // none, flip discard, ignore
            new[] { 1, 3, 1 },   // none, flip sequential, ignore
            new[] { 0, 4, 1 },   // stretch, flip discard, ignore
            new[] { 0, 3, 1 },   // stretch, flip sequential, ignore
        };

        private static readonly int[][] Shapes =
        {
            //  scaling, swap effect, alpha
            new[] { 0, 3, 1 },   // stretch, flip sequential, ignore
            new[] { 0, 4, 1 },   // stretch, flip discard, ignore
            new[] { 0, 3, 2 },   // stretch, flip sequential, premultiplied
            new[] { 1, 4, 1 },   // none, flip discard, ignore
        };

        private static IntPtr ConsoleDescription(
            int width, int height, int format, int flags, int shape = 0,
            bool forWindow = false)
        {
            // Zero means "the size of the window" when there is a window. A
            // composed surface has no window to measure, so the screen is the
            // honest answer.
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;
            if (Smaller)
            {
                width = 1280;
                height = 720;
            }

            // Mode switching and tearing are things you ask a display for. A
            // composed surface is not a display, and asking refuses the whole
            // chain rather than just the flag.
            flags = 0;

            var desc = Marshal.AllocHGlobal(48);
            Marshal.WriteInt32(desc, 0, width);
            Marshal.WriteInt32(desc, 4, height);
            // The game's render-target view must be compatible with the
            // actual resource format. Silently replacing RGBA with BGRA can
            // invalidate that view; convert channels only during readback.
            Marshal.WriteInt32(desc, 8, format == 0 ? 87 : format);
            Marshal.WriteInt32(desc, 12, 0);        // Stereo
            Marshal.WriteInt32(desc, 16, 1);        // SampleDesc.Count
            Marshal.WriteInt32(desc, 20, 0);        // SampleDesc.Quality
            Marshal.WriteInt32(desc, 24, 0x20);     // DXGI_USAGE_RENDER_TARGET_OUTPUT
            Marshal.WriteInt32(desc, 28, 2);        // BufferCount
            var table = forWindow ? WindowShapes : Shapes;
            var chosen = table[shape % table.Length];
            Marshal.WriteInt32(desc, 32, chosen[0]);   // scaling
            Marshal.WriteInt32(desc, 36, chosen[1]);   // swap effect
            Marshal.WriteInt32(desc, 40, chosen[2]);   // alpha mode
            Marshal.WriteInt32(desc, 44, flags);
            return desc;
        }

        /// <summary>Formats a flip-model chain can present; anything else becomes one.</summary>
        private static int Supported(int format)
        {
            switch (format)
            {
                case 28:  // R8G8B8A8_UNORM
                case 87:  // B8G8R8A8_UNORM
                case 10:  // R16G16B16A16_FLOAT
                case 24:  // R10G10B10A2_UNORM
                    return format;
                case 29:  // R8G8B8A8_UNORM_SRGB, which the flip model refuses
                    return 28;
                case 91:  // B8G8R8A8_UNORM_SRGB
                    return 87;
                default:
                    return 28;
            }
        }

        private static int MakeChain(
            IntPtr self, IntPtr device, IntPtr desc, IntPtr result, string from) =>
            MakeChainOn(ComProxy.Original(self), device, desc, result, from);

        private static int MakeChainOn(
            IntPtr original, IntPtr device, IntPtr desc, IntPtr result, string from)
        {
            // Written down before anything is attempted. The engine's own log
            // proved it had a swap chain that none of these notes mentioned,
            // which can only mean it was asked for somewhere this does not
            // watch — and a note that is only written on success can never
            // tell the difference between "not asked" and "asked and stuck".
            Note(from + ": asked");

            if (original == IntPtr.Zero)
            {
                Note(from + ": no factory to ask");
                Marshal.FreeHGlobal(desc);
                return E_FAIL;
            }

            var renderDevice = IntPtr.Zero;
            try
            {
                if (!NoMirror && (Mirror == null || OnUi == null))
                {
                    Note("swap-chain host is not ready; refusing native display fallback");
                    if (result != IntPtr.Zero) Marshal.WriteIntPtr(result, IntPtr.Zero);
                    return E_FAIL;
                }
                // The console's own window first. It is the shortest path
                // there is — the frames go straight to the screen, with no
                // surface to attach and nothing to keep in step — and if this
                // host allows it, everything downstream gets simpler.
                // A chain the display system knows nothing about.
                //
                // Holding a real one costs this application the screen about a
                // second later, measured with every other cause ruled out. The
                // game is given one that owns no display: it draws into a
                // texture, and handing the frame over copies it to the screen.
                if (!Direct && !NoMirror && Mirror != null && OnUi != null)
                {
                    // DXGI accepts IUnknown here, not an ID3D11Device vtable.
                    // Query the interface before calling device-specific slots.
                    renderDevice = Ask(device, "db6f6ddb-ac77-4e88-8253-819df9bbf140");
                    if (renderDevice == IntPtr.Zero)
                    {
                        Note("swap-chain device has no ID3D11Device interface");
                        return E_FAIL;
                    }
                    var mirrorDevice = renderDevice;
                    Note("swap-chain device normalized=" + (renderDevice != device));
                    var wide = Marshal.ReadInt32(desc, 0);
                    var high = Marshal.ReadInt32(desc, 4);
                    var shape = Marshal.ReadInt32(desc, 8);

                    var invented = FakeSwapChain.Build(Proxy, mirrorDevice, wide, high, shape);
                    Note("made up a chain: " + FakeSwapChain.Note);
                    if (invented == IntPtr.Zero) return E_FAIL;

                    Mirroring = FrameMirror.Start(
                        mirrorDevice, IntPtr.Zero, wide, high, shape, Mirror, OnUi,
                        FakeSwapChain.BackBuffer);
                    Note("mirror: " + FrameMirror.Note);
                    FakeSwapChain.OnResize = (w, h, f, texture) =>
                    {
                        Mirroring = FrameMirror.Start(mirrorDevice, IntPtr.Zero, w, h, f, Mirror, OnUi, texture);
                        Note("mirror resize: " + FrameMirror.Note);
                    };
                    FakeSwapChain.OnPresent = () =>
                    {
                        if (Frames == 0) FirstFrameAt = Environment.TickCount;
                        Frames++;
                        FrameMirror.Take();
                        Pace();
                    };

                    Marshal.WriteIntPtr(result, invented);
                    return S_OK;
                }

                var composed = false;
                var code = E_FAIL;
                if (NoChain)
                {
                    Note(from + ": refused on purpose");
                    return E_FAIL;
                }
                if (!Direct && ConsoleWindow != IntPtr.Zero)
                {
                    var direct = Marshal.GetDelegateForFunctionPointer<
                        CreateForCoreWindowDelegate>(
                        ComProxy.Method(original, CreateForCoreWindowSlot));
                    for (var shape = 0; code != S_OK && shape < WindowShapes.Length; shape++)
                    {
                        var wanted = ConsoleDescription(
                            Marshal.ReadInt32(desc, 0), Marshal.ReadInt32(desc, 4),
                            Marshal.ReadInt32(desc, 8), 0, shape, true);
                        code = direct(
                            original, device, ConsoleWindow, wanted, IntPtr.Zero, result);
                        Note(from + " core window, shape " + shape + ": 0x"
                            + code.ToString("X8"));
                        Marshal.FreeHGlobal(wanted);
                    }
                }

                // Otherwise a surface the framework composes, which is the
                // supported route for an application built out of XAML.
                var compose = Marshal.GetDelegateForFunctionPointer<CreateForCompositionDelegate>(
                    ComProxy.Method(original, CreateForCompositionSlot));
                if (code != S_OK)
                {
                    composed = true;
                    // The game's device is our stand-in. DXGI asks the device it
                    // is given for its own interfaces, and asked through the
                    // stand-in it never returned; the console's device is the
                    // one that can answer it.
                    var composeDevice = device;
                    if (Direct && Proxy.TryUnwrap(device, out var consoleDevice) && consoleDevice != IntPtr.Zero)
                        composeDevice = consoleDevice;
                    Note(from + " composing with " + (composeDevice == device ? "the given device" : "the console's device"));
                    code = compose(original, composeDevice, desc, IntPtr.Zero, result);
                    Note(from + " composition: 0x" + code.ToString("X8"));
                }

                // The size and the format came from the game and are kept; the
                // rest is this platform's business, so each accepted shape is
                // tried until one is.
                for (var shape = 1; code != S_OK && shape < Shapes.Length; shape++)
                {
                    var another = ConsoleDescription(
                        Marshal.ReadInt32(desc, 0), Marshal.ReadInt32(desc, 4),
                        Marshal.ReadInt32(desc, 8), 0, shape);
                    code = compose(original, device, another, IntPtr.Zero, result);
                    Note("shape " + shape + ": 0x" + code.ToString("X8"));
                    Marshal.FreeHGlobal(another);
                }



                if (code == S_OK && result != IntPtr.Zero)
                {
                    var chain = Marshal.ReadIntPtr(result);
                    // Counting frames on the way past. A managed step per
                    // frame is sixty a second, which is nothing, and it is the
                    // difference between believing and knowing.
                    presentThrough = Marshal.GetDelegateForFunctionPointer<PresentDelegate>(
                        ComProxy.Method(chain, PresentSlot));
                    present = (self, interval, flags) =>
                    {
                        if (Frames == 0) FirstFrameAt = Environment.TickCount;
                        Frames++;
                        // Taken before the frame goes out: a flip-model chain
                        // rotates its buffers on the way, and what was just
                        // drawn is no longer where it was.
                        if (!Mirroring)
                        {
                            return presentThrough(ComProxy.Original(self), interval, flags);
                        }

                        // The frame is taken here and shown by this application,
                        // so it is never handed to the display system — and it
                        // must not be. A composed chain with nothing attached to
                        // it has no one to consume what it is given: its buffers
                        // fill, and the next call waits for a reader that will
                        // never come. About a second in, measured, and it takes
                        // the whole process with it.
                        FrameMirror.Take();
                        Pace();
                        return S_OK;
                    };

                    // A flip-model chain is often presented through the newer
                    // call instead, and a frame counted on only one of the two
                    // says zero while the screen is busy.
                    presentOneThrough = Marshal.GetDelegateForFunctionPointer<Present1Delegate>(
                        ComProxy.Method(chain, Present1Slot));
                    presentOne = (self, interval, flags, parameters) =>
                    {
                        if (Frames == 0) FirstFrameAt = Environment.TickCount;
                        Frames++;
                        if (Mirroring)
                        {
                            FrameMirror.Take();
                            Pace();
                            return S_OK;
                        }
                        return presentOneThrough(
                            ComProxy.Original(self), interval, flags, parameters);
                    };

                    bufferThrough = Marshal.GetDelegateForFunctionPointer<BufferDelegate>(
                        ComProxy.Method(chain, GetBufferSlot));
                    buffer = (self, index, riid, surface) =>
                    {
                        Buffers++;
                        return bufferThrough(ComProxy.Original(self), index, riid, surface);
                    };

                    var stand = Proxy.Wrap(chain, SwapChainMethods, new Dictionary<int, IntPtr>
                    {
                        { GetBufferSlot, Marshal.GetFunctionPointerForDelegate(buffer) },
                        { PresentSlot, Marshal.GetFunctionPointerForDelegate(present) },
                        { Present1Slot, Marshal.GetFunctionPointerForDelegate(presentOne) },
                        // Going fullscreen is a desktop idea. On a console the
                        // app already owns the screen, so the honest answer to
                        // "make me fullscreen" is that it is done.
                        { SetFullscreenSlot,
                            Marshal.GetFunctionPointerForDelegate(setFullscreen) },
                        { GetFullscreenSlot,
                            Marshal.GetFunctionPointerForDelegate(getFullscreen) },
                        { ResizeTargetSlot,
                            Marshal.GetFunctionPointerForDelegate(resizeTarget) },
                        { GetDescSlot, Marshal.GetFunctionPointerForDelegate(describe) },
                        { QueryInterfaceSlot, Marshal.GetFunctionPointerForDelegate(chainAsk) },
                    });
                    standingChain = stand;
                    Marshal.WriteIntPtr(result, stand);
                    // A chain made for the console's window is already on the
                    // screen; only a composed one needs somewhere to land —
                    // and if it cannot land, the window is taken instead.
                    if (composed)
                    {
                        // Both direct routes were measured refused on this
                        // console, every shape, with and without the app
                        // holding its own screen. So the frames are copied.
                        if (Direct && ShowNative(chain))
                        {
                            Note("direct: frames go straight to the screen");
                            return code;
                        }
                        var going = NoMirror ? false : FrameMirror.Start(
                            device, chain,
                            Marshal.ReadInt32(desc, 0), Marshal.ReadInt32(desc, 4),
                            87, Mirror, OnUi);
                        Note("mirror: " + FrameMirror.Note);
                        Mirroring = going;
                    }
                }
                return code;
            }
            catch (Exception error)
            {
                Note(from + ": " + error.GetType().Name);
                return E_FAIL;
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
                // FakeSwapChain retains its own device reference after Build.
                if (renderDevice != IntPtr.Zero) Marshal.Release(renderDevice);
            }
        }


        /// <summary>
        /// Hands the finished chain to the surface on screen.
        ///
        /// The element is a XAML element, so this has to happen on the thread
        /// that owns XAML, and the game is not on it. The panel's own COM
        /// interface is what accepts a swap chain; C# has no declaration for
        /// it, so it is asked for by identifier and called by slot.
        /// </summary>
        private static bool Show(IntPtr chain)
        {
            var panel = Surface;
            var ui = OnUi;
            if (panel == null || ui == null || chain == IntPtr.Zero)
            {
                Note("no surface to compose into");
                return false;
            }

            var settled = new System.Threading.ManualResetEventSlim(false);
            var attached = false;

            var _ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
            {
                var unknown = IntPtr.Zero;
                try
                {
                    // Asked for by hand rather than by cast. The native
                    // compiler will not generate the stub a cast needs here,
                    // and there are two interfaces a panel might answer for —
                    // the original, and the one that came later. Both are
                    // tried, and what each said is written down, because
                    // "it did not work" is not a thing anyone can act on.
                    unknown = Marshal.GetIUnknownForObject(panel);
                    var names = new[]
                    {
                        "63aad0b8-7c24-40ff-85a8-640d944cc325", // ISwapChainPanelNative
                        "ec7d9b13-79ff-4f1e-b8f1-e0e8e6dd93dc", // ISwapChainPanelNative2
                        // Not to use — to tell two failures apart. If even
                        // this is refused then the pointer is a managed
                        // wrapper and the question was asked of the wrong
                        // thing; if it answers, the pointer is the panel and
                        // this platform simply does not offer the interface.
                        "af86e2e0-b12d-4c6a-9c5a-d7aa65101e90", // IInspectable
                    };

                    foreach (var name in names)
                    {
                        var riid = Marshal.AllocHGlobal(16);
                        var slot = Marshal.AllocHGlobal(IntPtr.Size);
                        try
                        {
                            Marshal.StructureToPtr(new Guid(name), riid, false);
                            Marshal.WriteIntPtr(slot, IntPtr.Zero);
                            var ask = Marshal.GetDelegateForFunctionPointer<
                                QueryInterfaceDelegate>(ComProxy.Method(unknown, 0));
                            var code = ask(unknown, riid, slot);
                            Note("panel " + name.Substring(0, 8) + ": 0x" + code.ToString("X8"));
                            if (code != S_OK) continue;
                            if (name.StartsWith("af86", StringComparison.Ordinal))
                            {
                                Marshal.Release(Marshal.ReadIntPtr(slot));
                                continue;
                            }

                            var native = Marshal.ReadIntPtr(slot);
                            var set = Marshal.GetDelegateForFunctionPointer<
                                SetSwapChainDelegate>(ComProxy.Method(native, 3));
                            var hr = set(native, chain);
                            Note("composed onto the panel: 0x" + hr.ToString("X8"));
                            attached = hr == S_OK;
                            Marshal.Release(native);
                            return;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(riid);
                            Marshal.FreeHGlobal(slot);
                        }
                    }
                }
                catch (Exception error)
                {
                    Note("compose: " + error.GetType().Name + " " + error.Message);
                }
                finally
                {
                    if (unknown != IntPtr.Zero) Marshal.Release(unknown);
                    settled.Set();
                }
            });

            // Waited on, because what happens next depends on the answer and
            // the answer arrives on another thread.
            settled.Wait(3000);
            return attached;
        }

        [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
        private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        /// <summary>Opt-in: present the game's own chain instead of copying it.</summary>
        public static bool Direct;

        /// <summary>
        /// Puts a composed chain on screen through a panel made natively.
        ///
        /// The earlier refusal was measured on the pointer the managed runtime
        /// handed back for a panel it created, and that pointer answering
        /// IInspectable does not prove it is the panel: a runtime wrapper
        /// answers that too. A panel activated through the Windows Runtime
        /// directly is the native object by construction, so its answer to
        /// ISwapChainPanelNative is the console's answer.
        /// </summary>
        private static bool ShowNative(IntPtr chain)
        {
            var image = Mirror;
            var ui = OnUi;
            if (image == null || ui == null || chain == IntPtr.Zero) return false;

            var settled = new System.Threading.ManualResetEventSlim(false);
            var attached = false;
            var _ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
            {
                var name = IntPtr.Zero;
                var instance = IntPtr.Zero;
                var riid = Marshal.AllocHGlobal(16);
                var slot = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    const string ClassName = "Windows.UI.Xaml.Controls.SwapChainPanel";
                    WindowsCreateString(ClassName, ClassName.Length, out name);
                    var made = RoActivateInstance(name, out instance);
                    Note("direct: activate 0x" + made.ToString("X8"));
                    if (made != S_OK || instance == IntPtr.Zero) return;

                    Marshal.StructureToPtr(new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), riid, false);
                    Marshal.WriteIntPtr(slot, IntPtr.Zero);
                    var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                        ComProxy.Method(instance, 0));
                    var code = ask(instance, riid, slot);
                    Note("direct: panel native 0x" + code.ToString("X8"));
                    if (code != S_OK) return;

                    var native = Marshal.ReadIntPtr(slot);
                    var set = Marshal.GetDelegateForFunctionPointer<SetSwapChainDelegate>(
                        ComProxy.Method(native, 3));
                    var hr = set(native, chain);
                    Marshal.Release(native);
                    Note("direct: set chain 0x" + hr.ToString("X8"));
                    if (hr != S_OK) return;

                    var panel = (Windows.UI.Xaml.Controls.SwapChainPanel)Marshal.GetObjectForIUnknown(instance);
                    var parent = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(image)
                        as Windows.UI.Xaml.Controls.Panel;
                    if (parent == null)
                    {
                        Note("direct: mirror has no panel parent");
                        return;
                    }
                    Windows.UI.Xaml.Controls.Grid.SetRow(panel, Windows.UI.Xaml.Controls.Grid.GetRow(image));
                    Windows.UI.Xaml.Controls.Grid.SetColumn(panel, Windows.UI.Xaml.Controls.Grid.GetColumn(image));
                    Windows.UI.Xaml.Controls.Grid.SetRowSpan(panel, Windows.UI.Xaml.Controls.Grid.GetRowSpan(image));
                    Windows.UI.Xaml.Controls.Grid.SetColumnSpan(panel, Windows.UI.Xaml.Controls.Grid.GetColumnSpan(image));
                    panel.HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch;
                    panel.VerticalAlignment = Windows.UI.Xaml.VerticalAlignment.Stretch;
                    parent.Children.Insert(parent.Children.IndexOf(image) + 1, panel);
                    image.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                    attached = true;
                    Note("direct: on screen");
                }
                catch (Exception error)
                {
                    Note("direct: " + error.GetType().Name + " " + error.Message);
                }
                finally
                {
                    if (instance != IntPtr.Zero) Marshal.Release(instance);
                    if (name != IntPtr.Zero) WindowsDeleteString(name);
                    Marshal.FreeHGlobal(riid);
                    Marshal.FreeHGlobal(slot);
                    settled.Set();
                }
            });
            settled.Wait(3000);
            return attached;
        }



        /// <summary>
        /// Wraps an adapter so that asking it who made it answers the
        /// stand-in.
        ///
        /// Otherwise there is a way around the bridge, and an engine will
        /// find it: from a device you can reach its adapter, and from an
        /// adapter its factory — the real one. A game that walks that path
        /// ends up holding the object whose window-shaped calls this console
        /// refuses, and nothing here would ever see it happen.
        /// </summary>
        private static IntPtr WrapAdapter(IntPtr adapter)
        {
            if (adapter == IntPtr.Zero) return adapter;
            try
            {
                return Proxy.Wrap(adapter, AdapterMethods, new Dictionary<int, IntPtr>
                {
                    { GetParentSlot, Marshal.GetFunctionPointerForDelegate(adapterParent) },
                    { EnumOutputsSlot, Marshal.GetFunctionPointerForDelegate(adapterOutputs) },
                    { AdapterDescSlot, Marshal.GetFunctionPointerForDelegate(adapterDesc) },
                    { AdapterDesc1Slot, Marshal.GetFunctionPointerForDelegate(adapterDesc1) },
                });
            }
            catch
            {
                return adapter;
            }
        }

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr levels, uint levelCount, uint sdk,
            out IntPtr device, out int level, out IntPtr context);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory2(
            uint flags, ref Guid riid, out IntPtr factory);

        /// <summary>
        /// Proves the path on its own, before any game uses it.
        ///
        /// Everything between a game and the screen is ours except the parts
        /// that are the console's, and when a frame does not appear there is
        /// no way from outside to tell which half is at fault. So the app makes
        /// a device, a swap chain and a frame by itself, with the same calls
        /// and the same surface. If this works, the bridge works, and whatever
        /// is wrong is on the engine's side of it.
        /// </summary>
        public static string SelfTest()
        {
            try
            {
                // BGRA support is not a preference here: a chain that is
                // composed rather than presented to a window requires it, and
                // without it the chain is refused with the same undifferentiated
                // error as a malformed description.
                var code = D3D11CreateDevice(
                    IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
                    out var device, out var level, out var context);
                if (code != S_OK || device == IntPtr.Zero)
                {
                    return "no device: 0x" + code.ToString("X8");
                }

                var id = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
                code = CreateDXGIFactory2(0, ref id, out var factory);
                if (code != S_OK || factory == IntPtr.Zero)
                {
                    return "device ok (level 0x" + level.ToString("X")
                        + "), no factory: 0x" + code.ToString("X8");
                }

                var desc = ConsoleDescription(1920, 1080, 28, 0);
                var slot = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(slot, IntPtr.Zero);
                    var compose = Marshal.GetDelegateForFunctionPointer<
                        CreateForCompositionDelegate>(
                        ComProxy.Method(factory, CreateForCompositionSlot));
                    var direct = Marshal.GetDelegateForFunctionPointer<
                        CreateForCoreWindowDelegate>(
                        ComProxy.Method(factory, CreateForCoreWindowSlot));
                    code = ConsoleWindow == IntPtr.Zero
                        ? unchecked((int)0x80004005)
                        : direct(factory, device, ConsoleWindow, desc, IntPtr.Zero, slot);
                    if (code == S_OK) Note("the console's own window took it");

                    if (code != S_OK) code = compose(factory, device, desc, IntPtr.Zero, slot);
                    for (var shape = 1; code != S_OK && shape < Shapes.Length; shape++)
                    {
                        var another = ConsoleDescription(1920, 1080, 28, 0, shape);
                        code = compose(factory, device, another, IntPtr.Zero, slot);
                        Marshal.FreeHGlobal(another);
                        if (code == S_OK) Note("the console wanted shape " + shape);
                    }
                    if (code != S_OK)
                    {
                        return "device and factory ok (level 0x" + level.ToString("X")
                            + "), no chain: 0x" + code.ToString("X8");
                    }

                    var chain = Marshal.ReadIntPtr(slot);
                    Show(chain);
                    var present = Marshal.GetDelegateForFunctionPointer<PresentDelegate>(
                        ComProxy.Method(chain, PresentSlot));
                    var shown = present(chain, 1, 0);
                    return "whole path works: level 0x" + level.ToString("X")
                        + ", present 0x" + shown.ToString("X8");
                }
                finally
                {
                    Marshal.FreeHGlobal(slot);
                    Marshal.FreeHGlobal(desc);
                }
            }
            catch (Exception error)
            {
                return error.GetType().Name + ": " + error.Message;
            }
        }


        /// <summary>
        /// Gives up the app's own screen so the game can have the window.
        ///
        /// A window can be drawn into by one thing at a time, and while the
        /// interface framework holds this one, the console refuses to make a
        /// swap chain for it — which is the whole reason the frames were going
        /// through a surface instead. Letting go is not a trick: it is what a
        /// console game does, which is own the screen.
        /// </summary>
        private static Windows.UI.Xaml.UIElement handedOver;

        /// <summary>Gives the app its screen back once the game is finished.</summary>
        public static void RestoreInterface()
        {
            var ui = OnUi;
            if (ui == null || handedOver == null) return;
            var _ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    Windows.UI.Xaml.Window.Current.Content = handedOver;
                    handedOver = null;
                }
                catch
                {
                    // Coming back to nothing is bad; crashing over it is worse.
                }
            });
        }

        private static void ReleaseInterface()
        {
            var ui = OnUi;
            if (ui == null) return;
            var settled = new System.Threading.ManualResetEventSlim(false);
            var _ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
            {
                try
                {
                    // Kept, so the app can have its screen back when the game
                    // is done with it rather than coming back to nothing.
                    handedOver = Windows.UI.Xaml.Window.Current.Content;
                    Windows.UI.Xaml.Window.Current.Content = null;
                    Note("let go of the app's own screen");
                }
                catch (Exception error)
                {
                    Note("let go: " + error.GetType().Name);
                }
                finally
                {
                    settled.Set();
                }
            });
            settled.Wait(3000);
        }

        public static void Install(SystemImports system)
        {
            imports = system;

            setFullscreen = (self, on, target) => S_OK;
            getFullscreen = (self, on, target) =>
            {
                if (on != IntPtr.Zero) Marshal.WriteInt32(on, 0);
                if (target != IntPtr.Zero) Marshal.WriteIntPtr(target, IntPtr.Zero);
                return S_OK;
            };
            resizeTarget = (self, mode) => S_OK;

            // Asked for itself by another name, the chain has to answer the
            // stand-in — otherwise the next frame is presented through the
            // real one and everything measured here says nothing is happening.
            chainAsk = (self, riid, result) =>
            {
                var original = ComProxy.Original(self);
                if (result != IntPtr.Zero && riid != IntPtr.Zero && standingChain != IntPtr.Zero)
                {
                    try
                    {
                        var id = Marshal.PtrToStructure<Guid>(riid).ToString();
                        foreach (var known in ChainIds)
                        {
                            if (!string.Equals(id, known, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            Marshal.WriteIntPtr(result, standingChain);
                            return S_OK;
                        }
                    }
                    catch
                    {
                        // An unreadable identifier is not one of ours.
                    }
                }
                try
                {
                    var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                        ComProxy.Method(original, QueryInterfaceSlot));
                    return ask(original, riid, result);
                }
                catch
                {
                    return E_FAIL;
                }
            };

            // A composed chain honestly reports that it belongs to no window,
            // and a game that reads that field decides its own window is gone.
            // The description is the system's; only the window is ours.
            describe = (self, desc) =>
            {
                if (desc == IntPtr.Zero) return E_FAIL;
                try
                {
                    var original = ComProxy.Original(self);
                    var read = Marshal.GetDelegateForFunctionPointer<SetFullscreenGetDelegate>(
                        ComProxy.Method(original, GetDescSlot));
                    var code = read(original, desc);
                    if (code == S_OK)
                    {
                        Marshal.WriteInt64(desc, 48, 0x00BA5E11);   // OutputWindow
                        Marshal.WriteInt32(desc, 56, 1);            // Windowed
                    }
                    return code;
                }
                catch
                {
                    return E_FAIL;
                }
            };

            // Asked for itself by another name, the stand-in has to hand back
            // the stand-in — otherwise the game walks around it on the next
            // call and lands on the function this console refuses.
            queryInterface = (self, riid, result) =>
            {
                var original = ComProxy.Original(self);
                if (IsFactoryId(riid) && result != IntPtr.Zero)
                {
                    try
                    {
                        var addRef = Marshal.GetDelegateForFunctionPointer<ResizeTargetDelegate>(
                            ComProxy.Method(original, 1));
                        addRef(original, IntPtr.Zero);
                    }
                    catch
                    {
                        // A reference we failed to take leaks; it does not crash.
                    }
                    Marshal.WriteIntPtr(result, self);
                    return S_OK;
                }
                try
                {
                    var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                        ComProxy.Method(original, QueryInterfaceSlot));
                    return ask(original, riid, result);
                }
                catch
                {
                    return E_FAIL;
                }
            };

            // The old call: the description carries the window inside it.
            createSwapChain = (self, device, desc, result) =>
            {
                var width = Marshal.ReadInt32(desc, 0);
                var height = Marshal.ReadInt32(desc, 4);
                var format = Marshal.ReadInt32(desc, 16);
                var flags = Marshal.ReadInt32(desc, 64);
                return MakeChain(
                    self, device,
                    ConsoleDescription(width, height, format, flags),
                    result, "CreateSwapChain");
            };

            // The modern call: a window handle beside a description.
            createForHwnd = (self, device, window, desc, fullscreen, restrict, result) =>
            {
                var width = Marshal.ReadInt32(desc, 0);
                var height = Marshal.ReadInt32(desc, 4);
                var format = Marshal.ReadInt32(desc, 8);
                var flags = Marshal.ReadInt32(desc, 44);
                return MakeChain(
                    self, device,
                    ConsoleDescription(width, height, format, flags),
                    result, "CreateSwapChainForHwnd");
            };

            // A graphics device asked which display device it is. Every
            // version of that question is answered with a stand-in, because
            // answering only the oldest leaves the newer ones as a way out.
            deviceAsk = (self, riid, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                var original = ComProxy.Original(self);
                var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                    ComProxy.Method(original, QueryInterfaceSlot));
                var code = ask(original, riid, result);
                if (code != S_OK) return code;

                int methods;
                if (!IsDisplayDeviceId(riid, out methods)) return code;

                var real = Marshal.ReadIntPtr(result);
                var stood = WrapDisplayDevice(real, methods);
                if (stood != real) Marshal.WriteIntPtr(result, stood);
                Note("a device was asked which screen device it is");
                return S_OK;
            };

            // …and from the display device, its adapter — which has to be the
            // one whose factory is ours, or the walk ends at the real one.
            displayAdapter = (self, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                try
                {
                    var original = ComProxy.Original(self);
                    var call = Marshal.GetDelegateForFunctionPointer<OneOutDelegate>(
                        ComProxy.Method(original, GetAdapterSlot));
                    var code = call(original, result);
                    if (code != S_OK) return code;
                    Marshal.WriteIntPtr(result, WrapAdapter(Marshal.ReadIntPtr(result)));
                    Note("a screen device was asked for its adapter");
                    return S_OK;
                }
                catch
                {
                    return E_FAIL;
                }
            };

            // The three fields that name the part, and nothing else: the
            // description text, the vendor and the device. Memory, revision
            // and the adapter's own identity stay as the console gave them.
            Func<IntPtr, IntPtr, int, int> describeCard = (self, desc, slot) =>
            {
                if (desc == IntPtr.Zero) return E_FAIL;
                try
                {
                    var original = ComProxy.Original(self);
                    var call = Marshal.GetDelegateForFunctionPointer<OneOutDelegate>(
                        ComProxy.Method(original, slot));
                    var code = call(original, desc);
                    if (code == S_OK)
                    {
                        ReportedAdapter = Marshal.PtrToStringUni(desc, 128).TrimEnd('\0');
                        ReportedVendor = unchecked((uint)Marshal.ReadInt32(desc, 256));
                        ReportedDevice = unchecked((uint)Marshal.ReadInt32(desc, 260));
                        ReportedVideoMemory = unchecked((ulong)Marshal.ReadInt64(desc, 272));
                    }
                    if (code != S_OK || !NameTheCard) return code;

                    var name = "AMD Radeon RX 6800 XT";
                    for (var i = 0; i < 128; i++)
                    {
                        Marshal.WriteInt16(desc, i * 2, (short)(i < name.Length ? name[i] : '\0'));
                    }
                    Marshal.WriteInt32(desc, 256, 0x1002);   // AMD
                    Marshal.WriteInt32(desc, 260, 0x73BF);   // Navi 21
                    return S_OK;
                }
                catch
                {
                    return E_FAIL;
                }
            };
            adapterDesc = (self, desc) => describeCard(self, desc, AdapterDescSlot);
            adapterDesc1 = (self, desc) => describeCard(self, desc, AdapterDesc1Slot);

            displayParent = (self, riid, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;

                // Asked for the factory rather than the adapter, answer with
                // ours directly. Walking up used to assume the answer was
                // always an adapter and wrapped it as one — so a caller that
                // asked a display device for the factory got the console's
                // own, dressed as something it is not.
                if (IsFactoryId(riid) && standingFactory != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(result, standingFactory);
                    Note("a screen device was asked who made it");
                    return S_OK;
                }
                try
                {
                    var original = ComProxy.Original(self);
                    var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                        ComProxy.Method(original, GetParentSlot));
                    var code = ask(original, riid, result);
                    if (code != S_OK) return code;
                    Marshal.WriteIntPtr(result, WrapAdapter(Marshal.ReadIntPtr(result)));
                    return S_OK;
                }
                catch
                {
                    return E_FAIL;
                }
            };

            // Both land in the same place as the window-shaped one: whatever
            // the engine asks for, it gets a chain this bridge owns, drawing
            // into a texture this bridge can read.
            createForCoreWindow = (self, device, window, desc, restrict, result) =>
            {
                var width = Marshal.ReadInt32(desc, 4);
                var height = Marshal.ReadInt32(desc, 8);
                var format = Marshal.ReadInt32(desc, 0);
                return MakeChain(
                    self, device,
                    ConsoleDescription(width, height, format, 0),
                    result, "CreateSwapChainForCoreWindow");
            };

            createForComposition = (self, device, desc, restrict, result) =>
            {
                var width = Marshal.ReadInt32(desc, 4);
                var height = Marshal.ReadInt32(desc, 8);
                var format = Marshal.ReadInt32(desc, 0);
                return MakeChain(
                    self, device,
                    ConsoleDescription(width, height, format, 0),
                    result, "CreateSwapChainForComposition");
            };

            adapterParent = (self, riid, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                if (IsFactoryId(riid) && standingFactory != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(result, standingFactory);
                    Note("an adapter was asked who made it");
                    return S_OK;
                }
                try
                {
                    var original = ComProxy.Original(self);
                    var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                        ComProxy.Method(original, GetParentSlot));
                    return ask(original, riid, result);
                }
                catch
                {
                    return E_FAIL;
                }
            };

            // What an engine does between making its device and asking for
            // a swap chain: walk the adapter's screens to learn what they can
            // show. Inside this console's container there are none to walk,
            // and the walk coming back empty is where the engine stopped —
            // measured, twice, at exactly this point.
            adapterOutputs = (self, index, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                Marshal.WriteIntPtr(result, IntPtr.Zero);

                // One screen, and only one. Asked for a second, the answer has
                // to be the one that ends the loop rather than an error.
                if (index != 0) return DXGI_ERROR_NOT_FOUND;

                // The real adapter is asked first, and the answer is written
                // down on both sides of the call: if the console never returns
                // from it, the report says which line was the last one.
                var real = IntPtr.Zero;
                Note("asking the adapter for its screen");
                try
                {
                    var original = ComProxy.Original(self);
                    var call = Marshal.GetDelegateForFunctionPointer<ItemOutDelegate>(
                        ComProxy.Method(original, EnumOutputsSlot));
                    var code = call(original, index, result);
                    Note("the adapter answered 0x" + code.ToString("X8"));
                    if (code == S_OK) real = Marshal.ReadIntPtr(result);
                }
                catch
                {
                    Note("the adapter refused to be asked");
                }
                if (real != IntPtr.Zero) return S_OK;

                var invented = FakeOutput.Build(Proxy, self);
                if (invented == IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(result, IntPtr.Zero);
                    return DXGI_ERROR_NOT_FOUND;
                }
                Marshal.AddRef(invented);
                Marshal.WriteIntPtr(result, invented);
                Note("made up a screen: " + FakeOutput.Note);
                return S_OK;
            };

            enumAdapters = (self, index, result) => Enumerate(self, index, result, EnumAdaptersSlot);
            enumAdapters1 = (self, index, result) => Enumerate(self, index, result, EnumAdapters1Slot);

            factory = (riid, result) => Make("CreateDXGIFactory", riid, result, 0);
            factory1 = (riid, result) => Make("CreateDXGIFactory1", riid, result, 0);
            factory2 = (flags, riid, result) => Make("CreateDXGIFactory2", riid, result, flags);

            // One call that makes the device and the chain together. The
            // device half is the system's; the chain half is ours, reached
            // through the factory the new device came from.
            deviceAndChain = (adapter, driverType, software, flags, levels, levelCount,
                sdk, chainDesc, resultChain, resultDevice, resultLevel, resultContext) =>
            {
                var real = imports.SystemAddress("d3d11.dll", "D3D11CreateDevice");
                if (real == IntPtr.Zero)
                {
                    Note("d3d11 has no device entry point");
                    return E_FAIL;
                }

                var ownDevice = resultDevice;
                var borrowed = IntPtr.Zero;
                if (ownDevice == IntPtr.Zero)
                {
                    borrowed = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(borrowed, IntPtr.Zero);
                    ownDevice = borrowed;
                }

                try
                {
                    var make = Marshal.GetDelegateForFunctionPointer<DeviceDelegate>(real);
                    var code = make(adapter, driverType, software, flags, levels,
                        levelCount, sdk, ownDevice, resultLevel, resultContext);
                    Note("D3D11CreateDevice: 0x" + code.ToString("X8"));
                    if (code == S_OK && resultLevel != IntPtr.Zero)
                        ReportedFeatureLevel = Marshal.ReadInt32(resultLevel);
                    if (code != S_OK || resultChain == IntPtr.Zero || chainDesc == IntPtr.Zero)
                    {
                        return code;
                    }

                    var device = Marshal.ReadIntPtr(ownDevice);
                    var made = FactoryBehind(device);
                    if (made == IntPtr.Zero)
                    {
                        Note("no factory behind the new device");
                        return E_FAIL;
                    }

                    return MakeChainOn(
                        made, device,
                        ConsoleDescription(
                            Marshal.ReadInt32(chainDesc, 0),
                            Marshal.ReadInt32(chainDesc, 4),
                            Marshal.ReadInt32(chainDesc, 16),
                            Marshal.ReadInt32(chainDesc, 64)),
                        resultChain, "D3D11CreateDeviceAndSwapChain");
                }
                catch (Exception error)
                {
                    Note("D3D11CreateDeviceAndSwapChain: " + error.GetType().Name);
                    return E_FAIL;
                }
                finally
                {
                    if (borrowed != IntPtr.Zero) Marshal.FreeHGlobal(borrowed);
                }
            };

            // Passed straight through, but written down. A device that fails
            // to be created is the sort of thing an engine retries quietly
            // forever, and from outside that looks identical to hanging.
            deviceOnly = (adapter, driverType, software, flags, levels, levelCount,
                sdk, resultDevice, resultLevel, resultContext) =>
            {
                if (NoChain)
                {
                    // Under the same switch: the graphics device is the other
                    // thing a game makes that the display system knows about.
                    Note("D3D11CreateDevice: refused on purpose");
                    return E_FAIL;
                }

                var real = imports.SystemAddress("d3d11.dll", "D3D11CreateDevice");
                if (real == IntPtr.Zero)
                {
                    Note("d3d11 has no device entry point");
                    return E_FAIL;
                }
                try
                {
                    // Added whether or not the game asked. A device without it
                    // cannot be composed, and composing is the only way a frame
                    // reaches the screen on this console — so a game that did
                    // not know to ask would simply never draw.
                    var wanted = flags | 0x20u;

                    var make = Marshal.GetDelegateForFunctionPointer<DeviceDelegate>(real);
                    var code = make(adapter, driverType, software, wanted, levels,
                        levelCount, sdk, resultDevice, resultLevel, resultContext);
                    Note("D3D11CreateDevice(type " + driverType + ", flags 0x"
                        + wanted.ToString("X") + "): 0x" + code.ToString("X8"));
                    flags = wanted;

                    // Two things a desktop tolerates and a console does not:
                    // the debug layer, which is not installed here, and a
                    // specific adapter, which on a console is the only one
                    // there is. An engine that asked for either and was
                    // refused would retry forever without learning anything,
                    // so the retry happens here instead, once, quietly.
                    if (code != S_OK && (flags & 0x2) != 0)
                    {
                        code = make(adapter, driverType, software, flags & ~0x2u, levels,
                            levelCount, sdk, resultDevice, resultLevel, resultContext);
                        Note("without the debug layer: 0x" + code.ToString("X8"));
                    }
                    if (code != S_OK)
                    {
                        code = make(IntPtr.Zero, 1, IntPtr.Zero, flags & ~0x2u, IntPtr.Zero,
                            0, sdk, resultDevice, resultLevel, resultContext);
                        Note("on the default adapter: 0x" + code.ToString("X8"));
                    }

                    if (code == S_OK && resultLevel != IntPtr.Zero)
                    {
                        ReportedFeatureLevel = Marshal.ReadInt32(resultLevel);
                        Note("feature level 0x" + Marshal.ReadInt32(resultLevel).ToString("X"));
                    }

                    // The device goes back as a stand-in as well. Without this
                    // the engine reaches the real factory through it and asks
                    // the console for a window-shaped swap chain, which the
                    // console refuses — for ever, quietly, off to one side
                    // where none of this could see it.
                    if (code == S_OK && resultDevice != IntPtr.Zero && !NoDeviceStandIn)
                    {
                        var born = Marshal.ReadIntPtr(resultDevice);
                        var stood = WrapDevice(born);
                        if (stood != born) Marshal.WriteIntPtr(resultDevice, stood);
                    }
                    return code;
                }
                catch (Exception error)
                {
                    Note("D3D11CreateDevice: " + error.GetType().Name);
                    return E_FAIL;
                }
            };

            foreach (var module in new[] { "d3d11.dll", "D3D11.dll" })
            {
                system.Overrides[module + "!D3D11CreateDeviceAndSwapChain"] =
                    Marshal.GetFunctionPointerForDelegate(deviceAndChain);
                system.Overrides[module + "!D3D11CreateDevice"] =
                    Marshal.GetFunctionPointerForDelegate(deviceOnly);
            }

            foreach (var module in new[] { "dxgi.dll", "DXGI.dll" })
            {
                system.Overrides[module + "!CreateDXGIFactory"] =
                    Marshal.GetFunctionPointerForDelegate(factory);
                system.Overrides[module + "!CreateDXGIFactory1"] =
                    Marshal.GetFunctionPointerForDelegate(factory1);
                system.Overrides[module + "!CreateDXGIFactory2"] =
                    Marshal.GetFunctionPointerForDelegate(factory2);
            }
        }



        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DeviceAndChainDelegate(
            IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr levels, uint levelCount, uint sdk, IntPtr chainDesc,
            IntPtr resultChain, IntPtr resultDevice, IntPtr resultLevel,
            IntPtr resultContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DeviceDelegate(
            IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr levels, uint levelCount, uint sdk,
            IntPtr resultDevice, IntPtr resultLevel, IntPtr resultContext);

        private static DeviceAndChainDelegate deviceAndChain;
        private static DeviceDelegate deviceOnly;
        private static ItemOutDelegate enumAdapters;
        private static ItemOutDelegate enumAdapters1;
        private static ItemOutDelegate adapterOutputs;
        private static CreateForCoreWindowDelegate createForCoreWindow;
        private static CreateForCompositionDelegate createForComposition;
        private static QueryInterfaceDelegate deviceAsk;
        private static QueryInterfaceDelegate displayParent;
        private static OneOutDelegate displayAdapter;
        private static OneOutDelegate adapterDesc;
        private static OneOutDelegate adapterDesc1;
        private static QueryInterfaceDelegate adapterParent;
        private static QueryInterfaceDelegate chainAsk;

        /// <summary>The stand-in a chain should give back when asked for itself.</summary>
        private static IntPtr standingChain;

        /// <summary>
        /// Interfaces the chain stand-in admits to being. The newer ones are
        /// deliberately absent: its table is as long as IDXGISwapChain1 and no
        /// longer, and handing it over as something with more methods on it
        /// would have a caller read past the end.
        /// </summary>
        private static readonly string[] ChainIds =
        {
            "00000000-0000-0000-c000-000000000046", // IUnknown
            "aec22fb8-76f3-4639-9be0-28eb43a67a2e", // IDXGIObject
            "3d3e0379-f9de-4d58-bb6c-18d62992f1a6", // IDXGIDeviceSubObject
            "310d36a0-d2e7-4c0a-aa04-6a9d23b8886a", // IDXGISwapChain
            "790a45f7-0d42-4876-983a-0a55cfe6f4aa", // IDXGISwapChain1
        };

        /// <summary>The stand-in every adapter should name as its parent.</summary>
        private static IntPtr standingFactory;

        /// <summary>
        /// The factory that made a device, reached through the device itself.
        ///
        /// Some engines create the device and the swap chain in one call and
        /// never touch a factory, so there is nothing to stand in front of.
        /// But every device knows its adapter and every adapter knows its
        /// factory, so the way in is through the back.
        /// </summary>
        private static IntPtr FactoryBehind(IntPtr device)
        {
            var dxgiDevice = Ask(device, "54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            if (dxgiDevice == IntPtr.Zero) return IntPtr.Zero;

            var adapter = OneOut(dxgiDevice, 7);            // IDXGIDevice::GetAdapter
            if (adapter == IntPtr.Zero) return IntPtr.Zero;

            return Parent(adapter, "50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        }

        private static IntPtr Ask(IntPtr instance, string id)
        {
            if (instance == IntPtr.Zero) return IntPtr.Zero;
            var riid = IntPtr.Zero;
            var slot = IntPtr.Zero;
            try
            {
                riid = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(new Guid(id), riid, false);
                slot = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(slot, IntPtr.Zero);
                var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                    ComProxy.Method(instance, QueryInterfaceSlot));
                return ask(instance, riid, slot) == S_OK
                    ? Marshal.ReadIntPtr(slot)
                    : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                if (riid != IntPtr.Zero) Marshal.FreeHGlobal(riid);
                if (slot != IntPtr.Zero) Marshal.FreeHGlobal(slot);
            }
        }

        private static IntPtr OneOut(IntPtr instance, int slot)
        {
            var target = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(target, IntPtr.Zero);
                var call = Marshal.GetDelegateForFunctionPointer<SetFullscreenGetDelegate>(
                    ComProxy.Method(instance, slot));
                return call(instance, target) == S_OK
                    ? Marshal.ReadIntPtr(target)
                    : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(target);
            }
        }

        /// <summary>IDXGIObject::GetParent, which is slot six on everything.</summary>
        private static IntPtr Parent(IntPtr instance, string id)
        {
            var riid = IntPtr.Zero;
            var slot = IntPtr.Zero;
            try
            {
                riid = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(new Guid(id), riid, false);
                slot = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(slot, IntPtr.Zero);
                var call = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                    ComProxy.Method(instance, 6));
                return call(instance, riid, slot) == S_OK
                    ? Marshal.ReadIntPtr(slot)
                    : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                if (riid != IntPtr.Zero) Marshal.FreeHGlobal(riid);
                if (slot != IntPtr.Zero) Marshal.FreeHGlobal(slot);
            }
        }

        /// <summary>The same object, asked for by its newest interface.</summary>
        private static IntPtr AsFactory2(IntPtr original)
        {
            var riid = IntPtr.Zero;
            var slot = IntPtr.Zero;
            try
            {
                riid = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(
                    new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0"), riid, false);
                slot = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(slot, IntPtr.Zero);

                var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                    ComProxy.Method(original, QueryInterfaceSlot));
                return ask(original, riid, slot) == S_OK
                    ? Marshal.ReadIntPtr(slot)
                    : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                if (riid != IntPtr.Zero) Marshal.FreeHGlobal(riid);
                if (slot != IntPtr.Zero) Marshal.FreeHGlobal(slot);
            }
        }


        /// <summary>Enumerates through to the real factory, then wraps what comes back.</summary>
        private static int Enumerate(IntPtr self, uint index, IntPtr result, int slot)
        {
            if (result == IntPtr.Zero) return E_FAIL;
            try
            {
                var original = ComProxy.Original(self);
                var call = Marshal.GetDelegateForFunctionPointer<ItemOutDelegate>(
                    ComProxy.Method(original, slot));
                var code = call(original, index, result);
                if (code == S_OK)
                {
                    Marshal.WriteIntPtr(result, WrapAdapter(Marshal.ReadIntPtr(result)));
                }
                return code;
            }
            catch
            {
                return E_FAIL;
            }
        }

        /// <summary>Builds the real factory, then the stand-in over it.</summary>
        /// <summary>
        /// Finds the newest factory interface this console actually answers
        /// for, and says how long its table is.
        ///
        /// Asking for the newest on purpose is what makes the stand-in exact:
        /// every older interface is a prefix of it, so one table serves every
        /// version a game might ask for, and the entries the bridge overrides
        /// sit at the same numbers in all of them.
        /// </summary>
        private static IntPtr AsNewestFactory(IntPtr original, out int methods)
        {
            methods = FactoryMethods;
            if (original == IntPtr.Zero) return IntPtr.Zero;

            IntPtr ask;
            try
            {
                ask = ComProxy.Method(original, QueryInterfaceSlot);
            }
            catch
            {
                return IntPtr.Zero;
            }
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(ask);

            foreach (var shape in FactoryShapes)
            {
                var id = new Guid(shape.Item1);
                var riid = Marshal.AllocHGlobal(16);
                var slot = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.StructureToPtr(id, riid, false);
                    Marshal.WriteIntPtr(slot, IntPtr.Zero);
                    if (query(original, riid, slot) != S_OK) continue;
                    var found = Marshal.ReadIntPtr(slot);
                    if (found == IntPtr.Zero) continue;
                    methods = shape.Item2;
                    return found;
                }
                catch
                {
                    // An interface that cannot be asked for is one we do not have.
                }
                finally
                {
                    Marshal.FreeHGlobal(riid);
                    Marshal.FreeHGlobal(slot);
                }
            }
            return IntPtr.Zero;
        }

        private static int Make(string name, IntPtr riid, IntPtr result, uint flags)
        {
            try
            {
                var real = imports.SystemAddress("dxgi.dll", name);
                if (real == IntPtr.Zero)
                {
                    // Every console has DXGI; if this one names it differently,
                    // the newest entry point is the one to fall back on.
                    real = imports.SystemAddress("dxgi.dll", "CreateDXGIFactory2");
                    if (real == IntPtr.Zero)
                    {
                        Note(name + ": dxgi has no factory entry point");
                        return E_FAIL;
                    }
                }

                int code;
                if (name == "CreateDXGIFactory2")
                {
                    code = Marshal.GetDelegateForFunctionPointer<Factory2Delegate>(real)(
                        flags, riid, result);
                }
                else
                {
                    code = Marshal.GetDelegateForFunctionPointer<FactoryDelegate>(real)(
                        riid, result);
                }
                if (code != S_OK || result == IntPtr.Zero)
                {
                    Note(name + ": 0x" + code.ToString("X8"));
                    return code;
                }

                var original = Marshal.ReadIntPtr(result);

                // Which interface came back decides how long its table is, and
                // reading past the end of a table reads whatever happens to be
                // next in memory. The newest interface is asked for explicitly:
                // its table starts with every older one, so handing it back in
                // place of what was asked for is exact, and the entries the
                // bridge needs are inside it.
                var methods = FactoryMethods;
                var newest = AsNewestFactory(original, out methods);
                if (newest == IntPtr.Zero)
                {
                    // Without the newer interface there is no call on this
                    // object that can make a swap chain the console accepts,
                    // and a stand-in that cannot help is only a place to
                    // crash. The real factory goes back untouched.
                    Note(name + ": no modern factory interface, left alone");
                    return S_OK;
                }
                original = newest;

                var stand = Proxy.Wrap(
                    original,
                    methods,
                    new Dictionary<int, IntPtr>
                {
                    { QueryInterfaceSlot, Marshal.GetFunctionPointerForDelegate(queryInterface) },
                    { CreateSwapChainSlot, Marshal.GetFunctionPointerForDelegate(createSwapChain) },
                    { CreateForHwndSlot, Marshal.GetFunctionPointerForDelegate(createForHwnd) },
                    { EnumAdaptersSlot, Marshal.GetFunctionPointerForDelegate(enumAdapters) },
                    { EnumAdapters1Slot, Marshal.GetFunctionPointerForDelegate(enumAdapters1) },
                    { CreateForCoreWindowSlot,
                        Marshal.GetFunctionPointerForDelegate(createForCoreWindow) },
                    { CreateForCompositionSlot,
                        Marshal.GetFunctionPointerForDelegate(createForComposition) },
                });
                standingFactory = stand;
                Marshal.WriteIntPtr(result, stand);
                Note(name + ": standing in for 0x" + original.ToInt64().ToString("X")
                     + " over " + methods + " methods");
                return S_OK;
            }
            catch (Exception error)
            {
                Note(name + ": " + error.GetType().Name + " " + error.Message);
                return E_FAIL;
            }
        }
    }
}
