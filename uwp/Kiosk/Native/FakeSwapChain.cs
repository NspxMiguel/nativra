using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// A swap chain that is not one.
    ///
    /// Everything a game does to draw is ordinary Direct3D and works here.
    /// The one thing that does not is the swap chain: it is the object the
    /// display system knows about, and an application that holds one on this
    /// console stops being the application on screen about a second later —
    /// measured, at every frame rate, with every other cause ruled out.
    ///
    /// So the game is given an object that answers every question a swap chain
    /// answers and owns no display at all. Its back buffer is a plain texture.
    /// Handing a finished frame over does not reach a screen; it copies the
    /// texture, which is how the picture gets to the player anyway.
    ///
    /// The game cannot tell. It asks for buffer zero, draws into it, presents,
    /// and reads back a description that says what it expects to hear.
    /// </summary>
    public static class FakeSwapChain
    {
        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_NOINTERFACE = unchecked((int)0x80004002);

        // ID3D11Device
        private const int CreateTexture2DSlot = 5;

        /// <summary>Formats and flags a back buffer is made with.</summary>
        private const int RenderTarget = 0x20;
        private const int ShaderResource = 0x8;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MakeTextureDelegate(
            IntPtr self, IntPtr desc, IntPtr initial, IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OneOut(IntPtr self, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TwoOut(IntPtr self, IntPtr first, IntPtr second);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ThreeIn(IntPtr self, IntPtr a, IntPtr b, IntPtr c);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BufferDelegate(
            IntPtr self, uint index, IntPtr riid, IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PresentDelegate(IntPtr self, uint interval, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Present1Delegate(
            IntPtr self, uint interval, uint flags, IntPtr parameters);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ResizeDelegate(
            IntPtr self, uint count, uint width, uint height, int format, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FullscreenDelegate(IntPtr self, int on, IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ColourDelegate(IntPtr self, IntPtr colour);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RotationDelegate(IntPtr self, uint rotation);

        // Held in fields so the collector cannot take them while the game holds
        // their addresses.
        private static TwoOut device;
        private static TwoOut parent;
        private static PresentDelegate present;
        private static BufferDelegate buffer;
        private static FullscreenDelegate setFullscreen;
        private static TwoOut getFullscreen;
        private static OneOut describe;
        private static ResizeDelegate resize;
        private static OneOut resizeTarget;
        private static OneOut containing;
        private static OneOut statistics;
        private static OneOut lastPresent;
        private static OneOut describe1;
        private static OneOut fullscreenDesc;
        private static OneOut hwnd;
        private static TwoOut coreWindow;
        private static Present1Delegate present1;
        private static OneOut mono;
        private static OneOut restrict;
        private static ColourDelegate setColour;
        private static ColourDelegate getColour;
        private static RotationDelegate setRotation;
        private static OneOut getRotation;
        private static ThreeIn privateData;

        private static IntPtr held;          // the game's back buffer
        private static IntPtr ownerDevice;
        private static int width;
        private static int height;
        private static int format;

        /// <summary>What happened while building it, for the report.</summary>
        public static string Note = "not built";

        /// <summary>The back buffer the game draws into.</summary>
        public static IntPtr BackBuffer => held;

        /// <summary>Called on every present, with the frame ready to be read.</summary>
        public static Action OnPresent;

        /// <summary>
        /// Makes the texture the game will draw into, and the object it will
        /// think is a swap chain.
        /// </summary>
        public static IntPtr Build(
            ComProxy proxy, IntPtr d3dDevice, int pixelsWide, int pixelsHigh, int pixelFormat)
        {
            try
            {
                ownerDevice = d3dDevice;
                width = pixelsWide;
                height = pixelsHigh;
                format = pixelFormat;

                held = MakeTexture(d3dDevice, width, height, format);
                if (held == IntPtr.Zero)
                {
                    Note = "no back buffer";
                    return IntPtr.Zero;
                }

                // The chain retains the device independently of its caller.
                Marshal.AddRef(ownerDevice);

                Fill();

                var made = proxy.Create(
                    new[]
                    {
                        // IDXGIObject
                        Marshal.GetFunctionPointerForDelegate(privateData),   // SetPrivateData
                        Marshal.GetFunctionPointerForDelegate(privateData),   // …Interface
                        Marshal.GetFunctionPointerForDelegate(privateData),   // GetPrivateData
                        Marshal.GetFunctionPointerForDelegate(parent),        // GetParent
                        // IDXGIDeviceSubObject
                        Marshal.GetFunctionPointerForDelegate(device),        // GetDevice
                        // IDXGISwapChain
                        Marshal.GetFunctionPointerForDelegate(present),
                        Marshal.GetFunctionPointerForDelegate(buffer),
                        Marshal.GetFunctionPointerForDelegate(setFullscreen),
                        Marshal.GetFunctionPointerForDelegate(getFullscreen),
                        Marshal.GetFunctionPointerForDelegate(describe),
                        Marshal.GetFunctionPointerForDelegate(resize),
                        Marshal.GetFunctionPointerForDelegate(resizeTarget),
                        Marshal.GetFunctionPointerForDelegate(containing),
                        Marshal.GetFunctionPointerForDelegate(statistics),
                        Marshal.GetFunctionPointerForDelegate(lastPresent),
                        // IDXGISwapChain1
                        Marshal.GetFunctionPointerForDelegate(describe1),
                        Marshal.GetFunctionPointerForDelegate(fullscreenDesc),
                        Marshal.GetFunctionPointerForDelegate(hwnd),
                        Marshal.GetFunctionPointerForDelegate(coreWindow),
                        Marshal.GetFunctionPointerForDelegate(present1),
                        Marshal.GetFunctionPointerForDelegate(mono),
                        Marshal.GetFunctionPointerForDelegate(restrict),
                        Marshal.GetFunctionPointerForDelegate(setColour),
                        Marshal.GetFunctionPointerForDelegate(getColour),
                        Marshal.GetFunctionPointerForDelegate(setRotation),
                        Marshal.GetFunctionPointerForDelegate(getRotation),
                    },
                    new[]
                    {
                        "aec22fb8-76f3-4639-9be0-28eb43a67a2e", // IDXGIObject
                        "3d3e0379-f9de-4d58-bb6c-18d62992f1a6", // IDXGIDeviceSubObject
                        "310d36a0-d2e7-4c0a-aa04-6a9d23b8886a", // IDXGISwapChain
                        "790a45f7-0d42-4876-983a-0a55cfe6f4aa", // IDXGISwapChain1
                    });

                Note = "built " + width + "x" + height;
                return made;
            }
            catch (Exception error)
            {
                Note = error.GetType().Name + ": " + error.Message;
                return IntPtr.Zero;
            }
        }

        /// <summary>A texture a game can draw into and this app can read.</summary>
        private static IntPtr MakeTexture(IntPtr d3dDevice, int w, int h, int f)
        {
            var desc = Marshal.AllocHGlobal(44);
            var slot = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteInt32(desc, 0, w);
                Marshal.WriteInt32(desc, 4, h);
                Marshal.WriteInt32(desc, 8, 1);       // MipLevels
                Marshal.WriteInt32(desc, 12, 1);      // ArraySize
                Marshal.WriteInt32(desc, 16, f);
                Marshal.WriteInt32(desc, 20, 1);      // SampleDesc.Count
                Marshal.WriteInt32(desc, 24, 0);
                Marshal.WriteInt32(desc, 28, 0);      // D3D11_USAGE_DEFAULT
                Marshal.WriteInt32(desc, 32, RenderTarget | ShaderResource);
                Marshal.WriteInt32(desc, 36, 0);      // no CPU access
                Marshal.WriteInt32(desc, 40, 0);
                Marshal.WriteIntPtr(slot, IntPtr.Zero);

                var make = Marshal.GetDelegateForFunctionPointer<MakeTextureDelegate>(
                    ComProxy.Method(d3dDevice, CreateTexture2DSlot));
                return make(d3dDevice, desc, IntPtr.Zero, slot) == S_OK
                    ? Marshal.ReadIntPtr(slot)
                    : IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(desc);
                Marshal.FreeHGlobal(slot);
            }
        }

        /// <summary>
        /// The answers. Most are "yes, fine" — a swap chain is asked a lot of
        /// questions whose answers a game only checks for failure.
        /// </summary>
        private static void Fill()
        {
            privateData = (self, a, b, c) => S_OK;

            parent = (self, riid, result) =>
            {
                if (result == IntPtr.Zero) return E_FAIL;
                Marshal.WriteIntPtr(result, IntPtr.Zero);
                return E_NOINTERFACE;
            };
            coreWindow = parent;
            device = (self, riid, result) => Query(ownerDevice, riid, result);

            buffer = (self, index, riid, surface) =>
            {
                if (surface == IntPtr.Zero) return E_FAIL;
                Marshal.WriteIntPtr(surface, IntPtr.Zero);
                // Only buffer zero exists, which is all the flip model ever
                // hands out anyway.
                if (index != 0) return E_FAIL;
                // QueryInterface both validates the requested interface and
                // gives the caller its own reference to the texture.
                return Query(held, riid, surface);
            };

            present = (self, interval, flags) =>
            {
                OnPresent?.Invoke();
                return S_OK;
            };
            present1 = (self, interval, flags, parameters) =>
            {
                OnPresent?.Invoke();
                return S_OK;
            };

            setFullscreen = (self, on, target) => S_OK;
            getFullscreen = (self, first, second) =>
            {
                if (first != IntPtr.Zero) Marshal.WriteInt32(first, 0);
                if (second != IntPtr.Zero) Marshal.WriteIntPtr(second, IntPtr.Zero);
                return S_OK;
            };

            // DXGI_SWAP_CHAIN_DESC, the older shape: a mode, then the buffers.
            describe = (self, target) =>
            {
                if (target == IntPtr.Zero) return E_FAIL;
                Marshal.WriteInt32(target, 0, width);
                Marshal.WriteInt32(target, 4, height);
                Marshal.WriteInt32(target, 8, 60);     // refresh numerator
                Marshal.WriteInt32(target, 12, 1);     // refresh denominator
                Marshal.WriteInt32(target, 16, format);
                Marshal.WriteInt32(target, 20, 0);     // scanline ordering
                Marshal.WriteInt32(target, 24, 0);     // scaling
                Marshal.WriteInt32(target, 28, 1);     // sample count
                Marshal.WriteInt32(target, 32, 0);     // sample quality
                Marshal.WriteInt32(target, 36, RenderTarget);
                Marshal.WriteInt32(target, 40, 2);     // buffer count
                Marshal.WriteInt64(target, 48, 0x00BA5E11);
                Marshal.WriteInt32(target, 56, 1);     // windowed
                Marshal.WriteInt32(target, 60, 4);     // flip discard
                Marshal.WriteInt32(target, 64, 0);
                return S_OK;
            };

            // DXGI_SWAP_CHAIN_DESC1, the newer one: size and format first.
            describe1 = (self, target) =>
            {
                if (target == IntPtr.Zero) return E_FAIL;
                Marshal.WriteInt32(target, 0, width);
                Marshal.WriteInt32(target, 4, height);
                Marshal.WriteInt32(target, 8, format);
                Marshal.WriteInt32(target, 12, 0);     // not stereo
                Marshal.WriteInt32(target, 16, 1);     // sample count
                Marshal.WriteInt32(target, 20, 0);
                Marshal.WriteInt32(target, 24, RenderTarget);
                Marshal.WriteInt32(target, 28, 2);
                Marshal.WriteInt32(target, 32, 0);     // stretch
                Marshal.WriteInt32(target, 36, 4);     // flip discard
                Marshal.WriteInt32(target, 40, 1);     // ignore alpha
                Marshal.WriteInt32(target, 44, 0);
                return S_OK;
            };

            // A resize the game asks for is a new texture at the new size.
            resize = (self, count, w, h, f, flags) =>
            {
                if (w == 0 || h == 0) return S_OK;
                var fresh = MakeTexture(ownerDevice, (int)w, (int)h, f == 0 ? format : f);
                if (fresh == IntPtr.Zero) return E_FAIL;
                var old = held;
                held = fresh;
                width = (int)w;
                height = (int)h;
                if (f != 0) format = f;
                if (old != IntPtr.Zero) Marshal.Release(old);
                Note = "resized to " + width + "x" + height;
                return S_OK;
            };

            resizeTarget = (self, mode) => S_OK;
            containing = (self, result) => E_FAIL;
            statistics = (self, result) => E_FAIL;
            lastPresent = (self, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteInt32(result, 0);
                return S_OK;
            };
            fullscreenDesc = (self, result) => S_OK;
            hwnd = (self, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteInt64(result, 0x00BA5E11);
                return S_OK;
            };
            mono = (self, result) => 0;
            restrict = (self, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteIntPtr(result, IntPtr.Zero);
                return S_OK;
            };
            setColour = (self, colour) => S_OK;
            getColour = (self, colour) => S_OK;
            setRotation = (self, rotation) => S_OK;
            getRotation = (self, result) =>
            {
                if (result != IntPtr.Zero) Marshal.WriteInt32(result, 0);
                return S_OK;
            };
        }

        private static int Query(IntPtr instance, IntPtr riid, IntPtr result)
        {
            if (result == IntPtr.Zero) return E_FAIL;
            Marshal.WriteIntPtr(result, IntPtr.Zero);
            if (instance == IntPtr.Zero || riid == IntPtr.Zero) return E_NOINTERFACE;
            var query = Marshal.GetDelegateForFunctionPointer<TwoOut>(
                ComProxy.Method(instance, 0));
            return query(instance, riid, result);
        }
    }
}
