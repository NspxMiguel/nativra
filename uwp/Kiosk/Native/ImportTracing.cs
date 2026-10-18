using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>Data exports retain their address; only executable exports may use a call thunk.</summary>
    internal static class ImportTracing
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;
        }

        [DllImport("api-ms-win-core-memory-l1-1-0.dll", EntryPoint = "VirtualQuery")]
        private static extern UIntPtr QueryMemory(IntPtr address, out MemoryBasicInformation info, UIntPtr length);

        private static uint Protection(IntPtr address)
        {
            try
            {
                if (QueryMemory(address, out var info, (UIntPtr)(uint)Marshal.SizeOf<MemoryBasicInformation>()) == UIntPtr.Zero || info.State != 0x1000)
                    return 0;
                return info.Protect;
            }
            catch
            {
                // An unclassified export must retain its original address.
                return 0;
            }
        }

        internal static IntPtr Resolve(IntPtr address, bool trace, Func<IntPtr, IntPtr> wrap,
            Func<IntPtr, uint> query = null)
        {
            if (!trace || address == IntPtr.Zero) return address;
            var protect = (query ?? Protection)(address);
            // PAGE_EXECUTE, EXECUTE_READ, EXECUTE_READWRITE and EXECUTE_WRITECOPY.
            if ((protect & 0xF0) == 0 || (protect & 0x101) != 0) return address;
            return wrap(address);
        }
    }
}
