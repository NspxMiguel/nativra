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
        private const int CreateSwapChainSlot = 10;
        private const int CreateForHwndSlot = 15;
        private const int CreateForCoreWindowSlot = 16;
        private const int CreateForCompositionSlot = 24;
        private const int FactoryMethods = 25;

        // Slots in IDXGISwapChain1.
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
        private static SetFullscreenDelegate setFullscreen;
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
            IntPtr self, IntPtr device, IntPtr desc, IntPtr result, string from)
        {
            var original = ComProxy.Original(self);
            if (original == IntPtr.Zero || ConsoleWindow == IntPtr.Zero)
            {
                Note(from + ": no console window to draw into");
                return E_FAIL;
            }

            try
            {
                var create = Marshal.GetDelegateForFunctionPointer<CreateForCoreWindowDelegate>(
                    ComProxy.Method(original, CreateForCoreWindowSlot));
                var code = create(original, device, ConsoleWindow, desc, IntPtr.Zero, result);
                Note(from + ": 0x" + code.ToString("X8"));

                if (code == S_OK && result != IntPtr.Zero)
                {
                    var chain = Marshal.ReadIntPtr(result);
                    var stand = Proxy.Wrap(chain, SwapChainMethods, new Dictionary<int, IntPtr>
                    {
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

            factory = (riid, result) => Make("CreateDXGIFactory", riid, result, 0);
            factory1 = (riid, result) => Make("CreateDXGIFactory1", riid, result, 0);
            factory2 = (flags, riid, result) => Make("CreateDXGIFactory2", riid, result, flags);

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
                var stand = Proxy.Wrap(original, FactoryMethods, new Dictionary<int, IntPtr>
                {
                    { QueryInterfaceSlot, Marshal.GetFunctionPointerForDelegate(queryInterface) },
                    { CreateSwapChainSlot, Marshal.GetFunctionPointerForDelegate(createSwapChain) },
                    { CreateForHwndSlot, Marshal.GetFunctionPointerForDelegate(createForHwnd) },
                });
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
