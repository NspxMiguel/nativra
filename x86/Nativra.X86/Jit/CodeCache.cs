using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nativra.X86.Cpu;

namespace Nativra.X86.Jit
{
    /// <summary>
    /// Packs translated blocks into executable pages. A page is writable only
    /// while a block is copied into its unused tail, then returns to read-execute
    /// before any block runs. Callers lock this cache while executing a block so
    /// another guest thread cannot change that page's protection underneath it.
    /// </summary>
    public sealed class CodeCache : IDisposable
    {
        private const int PageSize = 4096;
        private sealed class Region
        {
            public IntPtr Address;
            public ulong Size;
            public int Used;
            public int Blocks;
        }

        private readonly List<Region> regions = new List<Region>();
        private readonly Dictionary<IntPtr, Region> blocks = new Dictionary<IntPtr, Region>();
        private readonly HostPages.IBackend host = HostPages.Current;

        public long AllocatedBytes { get; private set; }

        public IntPtr Publish(byte[] code)
        {
            if (code == null || code.Length == 0) throw new ArgumentException("empty JIT block", nameof(code));
            lock (this)
            {
                var padded = (code.Length + 15) & ~15;
                var region = regions.Count == 0 ? null : regions[regions.Count - 1];
                if (region == null || region.Size != PageSize || region.Used + padded > PageSize)
                    region = NewRegion(code.Length);
                else if (!host.Protect(region.Address, region.Size, write: true, execute: false))
                    // Some hosts only permit the initial RW-to-RX transition.
                    region = NewRegion(code.Length);

                var pointer = region.Address + region.Used;
                Marshal.Copy(code, 0, pointer, code.Length);
                if (!host.Protect(region.Address, region.Size, write: false, execute: true))
                    throw new InvalidOperationException("JIT code cache: could not make a page executable");
                region.Used += padded;
                region.Blocks++;
                blocks.Add(pointer, region);
                return pointer;
            }
        }

        private Region NewRegion(int length)
        {
            var size = (ulong)((length + PageSize - 1) & ~(PageSize - 1));
            var address = host.Reserve(size);
            if (address == IntPtr.Zero) throw new OutOfMemoryException("JIT code cache: could not reserve a page");
            if (!host.Commit(address, size))
            {
                host.Release(address, size);
                throw new OutOfMemoryException("JIT code cache: could not commit a page");
            }
            var region = new Region { Address = address, Size = size };
            regions.Add(region);
            AllocatedBytes += (long)size;
            JitFaults.RegisterPage(address, size);
            return region;
        }

        public void Release(IntPtr address)
        {
            lock (this)
            {
                if (!blocks.TryGetValue(address, out var region)) return;
                blocks.Remove(address);
                if (--region.Blocks != 0) return;
                regions.Remove(region);
                JitFaults.UnregisterPage(region.Address);
                host.Release(region.Address, region.Size);
                AllocatedBytes -= (long)region.Size;
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                foreach (var region in regions)
                {
                    JitFaults.UnregisterPage(region.Address);
                    host.Release(region.Address, region.Size);
                }
                regions.Clear();
                blocks.Clear();
                AllocatedBytes = 0;
            }
        }
    }
}
