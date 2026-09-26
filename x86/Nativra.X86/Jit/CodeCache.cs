using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;
using System.Runtime.InteropServices;

namespace Nativra.X86.Jit
{
    /// <summary>
    /// Executable memory for translated blocks. Each block gets its own
    /// page-aligned region: committed writable, filled, then flipped to
    /// read-execute. That RW-then-RX flip is what the console's W^X policy
    /// demands — allocating RWX up front is refused there (measured by
    /// JitProbe), and it is the route every real dynarec uses.
    /// </summary>
    public sealed class CodeCache : IDisposable
    {
        private const int PageSize = 4096;
        private readonly List<(IntPtr addr, ulong size)> regions = new List<(IntPtr, ulong)>();
        private readonly HostPages.IBackend host = HostPages.Current;

        public IntPtr Publish(byte[] code)
        {
            var size = (ulong)((code.Length + PageSize - 1) & ~(PageSize - 1));
            var region = host.Reserve(size);
            if (region == IntPtr.Zero) throw new OutOfMemoryException("JIT code cache: could not reserve a page");
            if (!host.Commit(region, size))
            {
                host.Release(region, size);
                throw new OutOfMemoryException("JIT code cache: could not commit a page");
            }
            Marshal.Copy(code, 0, region, code.Length);
            if (!host.Protect(region, size, write: false, execute: true))
            {
                host.Release(region, size);
                throw new InvalidOperationException(
                    "JIT code cache: the platform refused to make the page executable " +
                    "(the codeGeneration capability is required on the console)");
            }
            regions.Add((region, size));
            return region;
        }

        public void Dispose()
        {
            foreach (var (addr, size) in regions) host.Release(addr, size);
            regions.Clear();
        }
    }
}
