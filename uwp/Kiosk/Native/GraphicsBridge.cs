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
        private const int GetParentSlot = 6;
        private const int AdapterMethods = 11;
        private const int CreateSwapChainSlot = 10;
        private const int CreateForHwndSlot = 15;
        private const int CreateForCoreWindowSlot = 16;
        private const int CreateForCompositionSlot = 24;
        private const int FactoryMethods = 25;

        // Slots in IDXGISwapChain1.
        private const int PresentSlot = 8;
        private const int SetFullscreenSlot = 10;
        private const int GetFullscreenSlot = 11;
        private const int ResizeTargetSlot = 14;
        private const int SwapChainMethods = 29;

        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);

        /// <summary>
        /// The console's window, as COM sees it. Taken on the interface thread
        /// at startup, because that is the only thread that can ask for it.
        /// </summary>
        public static IntPtr ConsoleWindow;

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
        private delegate int QueryInterfaceDelegate(IntPtr self, IntPtr riid, IntPtr result);

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
        private delegate int SetFullscreenGetDelegate(IntPtr self, IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PresentDelegate(IntPtr self, uint interval, uint flags);

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

        /// <summary>The interface identifiers a factory stand-in answers for.</summary>
        private static readonly string[] FactoryIds =
        {
            "00000000-0000-0000-c000-000000000046", // IUnknown
            "aec22fb8-76f3-4639-9be0-28eb43a67a2e", // IDXGIObject
            "7b7166ec-21c7-44ae-b21a-c9ae321ae369", // IDXGIFactory
            "770aae78-f26f-4dba-a829-253c83d1b387", // IDXGIFactory1
            "50c83a1c-e072-4c48-87b0-3630fa36a6d0", // IDXGIFactory2
        };

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
        private static IntPtr ConsoleDescription(int width, int height, int format, int flags)
        {
            // Zero means "the size of the window" when there is a window. A
            // composed surface has no window to measure, so the screen is the
            // honest answer.
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;

            // Mode switching and tearing are things you ask a display for. A
            // composed surface is not a display, and asking refuses the whole
            // chain rather than just the flag.
            flags = 0;

            var desc = Marshal.AllocHGlobal(48);
            Marshal.WriteInt32(desc, 0, width);
            Marshal.WriteInt32(desc, 4, height);
            Marshal.WriteInt32(desc, 8, Supported(format));
            Marshal.WriteInt32(desc, 12, 0);        // Stereo
            Marshal.WriteInt32(desc, 16, 1);        // SampleDesc.Count
            Marshal.WriteInt32(desc, 20, 0);        // SampleDesc.Quality
            Marshal.WriteInt32(desc, 24, 0x20);     // DXGI_USAGE_RENDER_TARGET_OUTPUT
            Marshal.WriteInt32(desc, 28, 2);        // BufferCount
            Marshal.WriteInt32(desc, 32, 1);        // DXGI_SCALING_NONE
            Marshal.WriteInt32(desc, 36, 4);        // DXGI_SWAP_EFFECT_FLIP_DISCARD
            Marshal.WriteInt32(desc, 40, 1);        // DXGI_ALPHA_MODE_IGNORE
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
            if (original == IntPtr.Zero)
            {
                Note(from + ": no factory to ask");
                Marshal.FreeHGlobal(desc);
                return E_FAIL;
            }

            try
            {
                // Composition, not the console's window directly. A XAML
                // application does not own its CoreWindow — the framework
                // does — so the supported way in is a surface the framework
                // composes, which is also what keeps our own screen alive
                // behind the game.
                var compose = Marshal.GetDelegateForFunctionPointer<CreateForCompositionDelegate>(
                    ComProxy.Method(original, CreateForCompositionSlot));
                var code = compose(original, device, desc, IntPtr.Zero, result);
                Note(from + " composition: 0x" + code.ToString("X8"));

                if (code != S_OK)
                {
                    // Some hosts do hand over the window. Worth one attempt.
                    var create = Marshal.GetDelegateForFunctionPointer<
                        CreateForCoreWindowDelegate>(
                        ComProxy.Method(original, CreateForCoreWindowSlot));
                    code = create(original, device, ConsoleWindow, desc, IntPtr.Zero, result);
                    Note(from + " core window: 0x" + code.ToString("X8"));
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
                        return presentThrough(ComProxy.Original(self), interval, flags);
                    };

                    var stand = Proxy.Wrap(chain, SwapChainMethods, new Dictionary<int, IntPtr>
                    {
                        { PresentSlot, Marshal.GetFunctionPointerForDelegate(present) },
                        // Going fullscreen is a desktop idea. On a console the
                        // app already owns the screen, so the honest answer to
                        // "make me fullscreen" is that it is done.
                        { SetFullscreenSlot,
                            Marshal.GetFunctionPointerForDelegate(setFullscreen) },
                        { GetFullscreenSlot,
                            Marshal.GetFunctionPointerForDelegate(getFullscreen) },
                        { ResizeTargetSlot,
                            Marshal.GetFunctionPointerForDelegate(resizeTarget) },
                    });
                    Marshal.WriteIntPtr(result, stand);
                    Show(chain);
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
        private static void Show(IntPtr chain)
        {
            var panel = Surface;
            var ui = OnUi;
            if (panel == null || ui == null || chain == IntPtr.Zero)
            {
                Note("no surface to compose into");
                return;
            }

            var _ = ui.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, () =>
            {
                var unknown = IntPtr.Zero;
                var native = IntPtr.Zero;
                try
                {
                    unknown = Marshal.GetIUnknownForObject(panel);
                    var id = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
                    var riid = Marshal.AllocHGlobal(16);
                    Marshal.StructureToPtr(id, riid, false);
                    var slot = Marshal.AllocHGlobal(IntPtr.Size);
                    var ask = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(
                        ComProxy.Method(unknown, 0));
                    var code = ask(unknown, riid, slot);
                    Marshal.FreeHGlobal(riid);
                    if (code != S_OK)
                    {
                        Note("panel refused its own interface: 0x" + code.ToString("X8"));
                        Marshal.FreeHGlobal(slot);
                        return;
                    }
                    native = Marshal.ReadIntPtr(slot);
                    Marshal.FreeHGlobal(slot);

                    var set = Marshal.GetDelegateForFunctionPointer<SetSwapChainDelegate>(
                        ComProxy.Method(native, 3));
                    // The panel is in the tree from the start — an element
                    // that is not laid out has no surface to hand a swap chain
                    // to, and it draws nothing until there is one anyway.
                    Note("compose: 0x" + set(native, chain).ToString("X8"));
                }
                catch (Exception error)
                {
                    Note("compose: " + error.GetType().Name);
                }
                finally
                {
                    if (native != IntPtr.Zero) Marshal.Release(native);
                    if (unknown != IntPtr.Zero) Marshal.Release(unknown);
                }
            });
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
                var code = D3D11CreateDevice(
                    IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7,
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
                    code = compose(factory, device, desc, IntPtr.Zero, slot);
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
                var real = imports.SystemAddress("d3d11.dll", "D3D11CreateDevice");
                if (real == IntPtr.Zero)
                {
                    Note("d3d11 has no device entry point");
                    return E_FAIL;
                }
                try
                {
                    var make = Marshal.GetDelegateForFunctionPointer<DeviceDelegate>(real);
                    var code = make(adapter, driverType, software, flags, levels,
                        levelCount, sdk, resultDevice, resultLevel, resultContext);
                    Note("D3D11CreateDevice(type " + driverType + ", flags 0x"
                        + flags.ToString("X") + "): 0x" + code.ToString("X8"));

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
                        Note("feature level 0x" + Marshal.ReadInt32(resultLevel).ToString("X"));
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
        private static QueryInterfaceDelegate adapterParent;

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
                var newest = AsFactory2(original);
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
                    FactoryMethods,
                    new Dictionary<int, IntPtr>
                {
                    { QueryInterfaceSlot, Marshal.GetFunctionPointerForDelegate(queryInterface) },
                    { CreateSwapChainSlot, Marshal.GetFunctionPointerForDelegate(createSwapChain) },
                    { CreateForHwndSlot, Marshal.GetFunctionPointerForDelegate(createForHwnd) },
                    { EnumAdaptersSlot, Marshal.GetFunctionPointerForDelegate(enumAdapters) },
                    { EnumAdapters1Slot, Marshal.GetFunctionPointerForDelegate(enumAdapters1) },
                });
                standingFactory = stand;
                Marshal.WriteIntPtr(result, stand);
                Note(name + ": standing in for 0x" + original.ToInt64().ToString("X"));
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
