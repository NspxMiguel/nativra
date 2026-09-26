using System;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;

namespace Kiosk.Native
{
    /// <summary>
    /// Direct3D 9 for a 32-bit game: its d3d9.dll imports are served by the
    /// packaged 64-bit d3d9.dll (Direct3D 9 on Direct3D 11) through the COM
    /// bridge, and presented through D3D9Bridge like a 64-bit D3D9 game.
    ///
    /// The layer's lockable memory (CPU copies of textures and buffers the
    /// game locks) comes from a heap inside the guest's space, so every
    /// pointer Lock hands the game is one it can use. The heap commits pages
    /// only as they are used; it is capped at 768 MB of the guest's 2 GB.
    /// </summary>
    internal static class X86Direct3D9
    {
        private const uint LockHeapBase = 0x40000000;
        private const uint LockHeapSize = 0x30000000;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr CreateDelegate(uint sdkVersion);

        private static GuestHeap lockHeap;
        private static long hostBase;

        /// <summary>Bytes the layer's lock heap has handed out (the pulse's memory line).</summary>
        public static long LockBytes;

        public static string Note = "not asked";

        public static void Install(GuestProcess process, GuestKernel kernel, GuestCom com)
        {
            const string d = "d3d9.dll";
            Direct3D9Com.Define(com);
            var imports = process.Imports;

            imports.Register(d, "Direct3DCreate9", CallConv.Stdcall, 1, c =>
            {
                if (!Prepare(process)) return 0;
                var create = D3D9Bridge.Export("Direct3DCreate9");
                if (create == IntPtr.Zero) { Note = "no Direct3DCreate9 in the packaged layer"; return 0; }
                var host = Marshal.GetDelegateForFunctionPointer<CreateDelegate>(create)(c.Arg(0));
                Note = host == IntPtr.Zero ? "Direct3DCreate9 failed" : "IDirect3D9 made";
                return com.Wrap(host, com.Find("IDirect3D9"));
            });
            // No D3D9Ex: a game that asks falls back to plain D3D9.
            imports.Register(d, "Direct3DCreate9Ex", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(1) != 0) process.Memory.Write32(c.Arg(1), 0);
                return 0x8876086A;   // D3DERR_NOTAVAILABLE
            });

            // PIX markers: nothing listens.
            imports.Register(d, "D3DPERF_BeginEvent", CallConv.Stdcall, 2, c => 0);
            imports.Register(d, "D3DPERF_EndEvent", CallConv.Stdcall, 0, c => 0);
            imports.Register(d, "D3DPERF_SetMarker", CallConv.Stdcall, 2, c => 0);
            imports.Register(d, "D3DPERF_SetRegion", CallConv.Stdcall, 2, c => 0);
            imports.Register(d, "D3DPERF_QueryRepeatFrame", CallConv.Stdcall, 0, c => 0);
            imports.Register(d, "D3DPERF_SetOptions", CallConv.Stdcall, 1, c => 0);
            imports.Register(d, "D3DPERF_GetStatus", CallConv.Stdcall, 0, c => 0);
        }

        /// <summary>Loads the layer and points its lock memory into the guest, once.</summary>
        private static bool Prepare(GuestProcess process)
        {
            if (lockHeap != null) return true;
            D3D9Bridge.Install();
            if (!D3D9Bridge.Active) { Note = "the packaged d3d9.dll is missing"; return false; }

            var memory = process.Memory;
            hostBase = memory.HostBase.ToInt64();
            lockHeap = new GuestHeap(memory, LockHeapBase, LockHeapSize);
            var ok = D3D9Bridge.SetAllocator(
                size =>
                {
                    var bytes = (uint)Math.Min(size.ToUInt64(), uint.MaxValue);
                    var guest = lockHeap.Alloc(bytes == 0 ? 1u : bytes);
                    if (guest == 0) return IntPtr.Zero;
                    System.Threading.Interlocked.Add(ref LockBytes, lockHeap.SizeOf(guest));
                    return new IntPtr(hostBase + guest);
                },
                pointer =>
                {
                    if (pointer == IntPtr.Zero) return;
                    var guest = (uint)(pointer.ToInt64() - hostBase);
                    System.Threading.Interlocked.Add(ref LockBytes, -(long)lockHeap.SizeOf(guest));
                    lockHeap.Free(guest);
                });
            if (!ok) Note = "the packaged d3d9.dll has no allocator hook";
            return ok;
        }
    }
}
