using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// A first-fit heap carved out of the guest address space, backing the
    /// HeapAlloc/malloc family. It commits guest pages as it grows and keeps a
    /// free list so freed blocks are reused, which a real game's startup — which
    /// allocates and frees constantly — needs in order not to run the region dry.
    ///
    /// Bookkeeping lives host-side (this class); only the payload bytes live in
    /// guest memory, so the guest sees ordinary pointers it can read and write.
    /// </summary>
    public sealed class GuestHeap
    {
        private const uint Align = 16;

        private readonly GuestMemory memory;
        private readonly uint regionBase;
        private readonly uint regionEnd;
        private uint committed;   // high-water mark of mapped bytes from regionBase
        private uint brk;         // next never-yet-allocated address

        private struct Block { public uint Size; public bool Free; }
        // Allocated/freed blocks keyed by payload address, in address order.
        private readonly SortedDictionary<uint, Block> blocks = new SortedDictionary<uint, Block>();

        public uint Base => regionBase;

        public GuestHeap(GuestMemory memory, uint regionBase, uint size)
        {
            this.memory = memory;
            this.regionBase = regionBase;
            regionEnd = regionBase + size;
            brk = regionBase;
        }

        /// <summary>Allocates <paramref name="size"/> bytes (optionally zeroed); 0 on exhaustion.</summary>
        public uint Alloc(uint size, bool zero = false)
        {
            var need = Round(size == 0 ? 1 : size);

            // Reuse the first free block big enough.
            foreach (var pair in blocks)
            {
                var block = pair.Value;
                if (block.Free && block.Size >= need)
                {
                    blocks[pair.Key] = new Block { Size = block.Size, Free = false };
                    if (zero) Zero(pair.Key, block.Size);
                    return pair.Key;
                }
            }

            // Otherwise grow the break.
            var address = brk;
            if ((ulong)address + need > regionEnd) return 0;
            if (!EnsureCommitted(address + need)) return 0;
            brk = address + need;
            blocks[address] = new Block { Size = need, Free = false };
            if (zero) Zero(address, need);
            return address;
        }

        /// <summary>Frees a block. Unknown or already-free pointers are ignored, as Win32 tolerates.</summary>
        public bool Free(uint address)
        {
            if (address == 0) return true;
            if (!blocks.TryGetValue(address, out var block) || block.Free) return false;
            blocks[address] = new Block { Size = block.Size, Free = true };
            return true;
        }

        /// <summary>Grows or shrinks a block, copying the payload when it must move.</summary>
        public uint ReAlloc(uint address, uint size)
        {
            if (address == 0) return Alloc(size);
            if (!blocks.TryGetValue(address, out var block) || block.Free) return 0;
            var need = Round(size == 0 ? 1 : size);
            if (need <= block.Size) return address;

            var moved = Alloc(size);
            if (moved == 0) return 0;
            for (uint i = 0; i < block.Size; i++) memory.Write8(moved + i, memory.Read8(address + i));
            Free(address);
            return moved;
        }

        /// <summary>The usable size of a live block, or 0 if it is not one.</summary>
        public uint SizeOf(uint address) =>
            blocks.TryGetValue(address, out var block) && !block.Free ? block.Size : 0;

        public bool Owns(uint address) => address >= regionBase && address < regionEnd;

        private bool EnsureCommitted(uint upTo)
        {
            if (upTo <= regionBase + committed) return true;
            var target = Round(upTo - regionBase, GuestMemory.PageSize);
            memory.Map(regionBase + committed, target - committed);
            committed = target;
            return true;
        }

        private void Zero(uint address, uint size)
        {
            for (uint i = 0; i < size; i++) memory.Write8(address + i, 0);
        }

        private static uint Round(uint value) => Round(value, Align);

        private static uint Round(uint value, uint to) => (value + to - 1) / to * to;
    }
}
