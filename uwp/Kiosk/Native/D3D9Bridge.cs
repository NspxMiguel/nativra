using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Connects the packaged d3d9.dll (native/directx-redist/d3d9: Direct3D 9
    /// on Direct3D 11) to the app's screen.
    ///
    /// A D3D9 game never touches DXGI, so none of GraphicsBridge's swap-chain
    /// interception sees it. Instead the d3d9 layer draws into a D3D11 back
    /// buffer and, on Present, hands that texture to a callback installed here,
    /// which mirrors it to the screen exactly as a fake swap chain's back buffer
    /// is. The layer's own diagnostics land in GraphicsBridge's notes.
    ///
    /// Nothing happens unless the package carries our d3d9.dll: the hooks are
    /// found by name, so a system d3d9 (without them) is left alone.
    /// </summary>
    internal static class D3D9Bridge
    {
        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", SetLastError = true,
            CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PresentCallback(IntPtr context, IntPtr texture, uint width, uint height);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void LogCallback(IntPtr line);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetPresentDelegate(IntPtr callback, IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetLogDelegate(IntPtr callback);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SetDefaultSizeDelegate(uint width, uint height);

        // ID3D11DeviceChild::GetDevice.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetDeviceDelegate(IntPtr self, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr self);

        // Kept alive for as long as native code may call them.
        private static PresentCallback present;
        private static LogCallback log;

        /// <summary>Whether the packaged D3D9 layer was found and hooked.</summary>
        public static bool Active { get; private set; }

        /// <summary>Hooks the packaged d3d9.dll, if there is one. Safe to call more than once.</summary>
        public static void Install(uint defaultWidth = 1920, uint defaultHeight = 1080)
        {
            if (Active) return;
            var module = LoadPackagedLibrary("d3d9.dll", 0);
            if (module == IntPtr.Zero) return;
            var setPresent = GetProcAddress(module, "NativraD3D9SetPresent");
            var setLog = GetProcAddress(module, "NativraD3D9SetLog");
            var setSize = GetProcAddress(module, "NativraD3D9SetDefaultSize");
            if (setPresent == IntPtr.Zero) return;   // not ours

            present = OnPresent;
            Marshal.GetDelegateForFunctionPointer<SetPresentDelegate>(setPresent)(
                Marshal.GetFunctionPointerForDelegate(present), IntPtr.Zero);
            if (setLog != IntPtr.Zero)
            {
                log = OnLog;
                Marshal.GetDelegateForFunctionPointer<SetLogDelegate>(setLog)(Marshal.GetFunctionPointerForDelegate(log));
            }
            if (setSize != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<SetDefaultSizeDelegate>(setSize)(defaultWidth, defaultHeight);
            Active = true;
        }

        private static void OnPresent(IntPtr context, IntPtr texture, uint width, uint height)
        {
            try
            {
                // The mirror needs the device the texture lives on.
                var vtable = Marshal.ReadIntPtr(texture);
                var getDevice = Marshal.GetDelegateForFunctionPointer<GetDeviceDelegate>(
                    Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                getDevice(texture, out var device);
                // 87 = DXGI_FORMAT_B8G8R8A8_UNORM, the back buffer's view format.
                GraphicsBridge.HandFrame(device, texture, (int)width, (int)height, 87);
                if (device != IntPtr.Zero)
                {
                    var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(device), 2 * IntPtr.Size));
                    release(device);
                }
            }
            catch
            {
                // A frame that cannot be shown is dropped, not a crash in the game's thread.
            }
        }

        private static void OnLog(IntPtr line)
        {
            var text = Marshal.PtrToStringAnsi(line);
            lock (GraphicsBridge.Notes)
            {
                if (GraphicsBridge.Notes.Count < 60) GraphicsBridge.Notes.Add("d3d9: " + text);
            }
        }
    }
}
