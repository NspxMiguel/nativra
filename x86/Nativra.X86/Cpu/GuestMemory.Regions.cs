using System;
using System.Collections.Generic;

namespace Nativra.X86.Cpu
{
    /// <summary>The Win32 virtual-memory vocabulary the guest address space and the kernel layer share.</summary>
    public static class Win32Memory
    {
        // Page protections.
        public const uint PageNoAccess = 0x01, PageReadOnly = 0x02, PageReadWrite = 0x04, PageWriteCopy = 0x08;
        public const uint PageExecute = 0x10, PageExecuteRead = 0x20, PageExecuteReadWrite = 0x40, PageExecuteWriteCopy = 0x80;
        public const uint PageGuard = 0x100, PageNoCache = 0x200, PageWriteCombine = 0x400;

        // Allocation types (VirtualAlloc / VirtualFree).
        public const uint MemCommit = 0x1000, MemReserve = 0x2000, MemDecommit = 0x4000, MemRelease = 0x8000;
        public const uint MemReset = 0x80000, MemTopDown = 0x100000, MemWriteWatch = 0x200000;
        public const uint MemPhysical = 0x400000, MemResetUndo = 0x1000000, MemLargePages = 0x20000000;

        // Region states and types (MEMORY_BASIC_INFORMATION).
        public const uint MemFree = 0x10000;
        public const uint MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;

        // The Win32 errors the model reports.
        public const uint ErrorNotEnoughMemory = 8, ErrorInvalidParameter = 87, ErrorInvalidAddress = 487;

        /// <summary>
        /// A protection VirtualAlloc/VirtualProtect accepts: exactly one of the eight
        /// base values, optionally with GUARD, NOCACHE or WRITECOMBINE (never with
        /// NOACCESS, and NOCACHE with WRITECOMBINE not together) or the CFG hint bit.
        /// </summary>
        public static bool IsValidProtection(uint protect)
        {
            const uint CfgHint = 0x40000000;
            var kind = protect & 0xFF;
            if (kind == 0 || (kind & (kind - 1)) != 0) return false;
            var modifiers = protect & ~0xFFu & ~CfgHint;
            if ((modifiers & ~(PageGuard | PageNoCache | PageWriteCombine)) != 0) return false;
            if (kind == PageNoAccess && modifiers != 0) return false;
            if ((modifiers & PageNoCache) != 0 && (modifiers & PageWriteCombine) != 0) return false;
            return true;
        }
    }

    /// <summary>One MEMORY_BASIC_INFORMATION: a run of pages with identical attributes.</summary>
    public struct GuestRegion
    {
        public uint BaseAddress;
        public uint AllocationBase;
        public uint AllocationProtect;
        public uint RegionSize;
        /// <summary>MEM_COMMIT, MEM_RESERVE or MEM_FREE.</summary>
        public uint State;
        public uint Protect;
        /// <summary>MEM_PRIVATE, MEM_MAPPED or MEM_IMAGE (0 for free memory).</summary>
        public uint Type;
    }

    // The guest's address space as Windows keeps it: allocations (a base, a size,
    // the protection they were made with, private / mapped / image) and, per
    // page, free / reserved / committed with a protection. Only committed pages
    // exist in the backing; a reserved page is address space and nothing else,
    // so touching it faults exactly as touching unmapped memory always did, in
    // the interpreter and (through the host's own page faults) in the JIT.
    //
    // Protections are recorded and reported, not enforced: the host's own
    // handlers write through the same pages the guest sees, and the JIT touches
    // guest memory directly, so a read-only page would have to be read-only on
    // the host too, which would crash the layer whenever a handler fills a
    // buffer the guest has write-protected. The model is the source of truth
    // for what is free, reserved or committed, and for what a query answers.
    public sealed unsafe partial class GuestMemory
    {
        private const int StateShift = 12;
        private const ushort StateMask = 0x3000;
        private const ushort ProtectionMask = 0x7FF;
        private const ushort StateReserved = 1 << StateShift;
        private const ushort StateCommitted = 2 << StateShift;
        private const uint NotFound = uint.MaxValue;

        private sealed class Allocation
        {
            public uint First;       // first page
            public uint Count;       // pages
            public uint Type;        // MEM_PRIVATE / MEM_MAPPED / MEM_IMAGE
            public uint Protect;     // the protection the allocation was created with
            public uint End => First + Count;
        }

        // Per page: state in bits 12-13 (0 free, 1 reserved, 2 committed), the
        // protection in the low bits. Free pages are all zero.
        private readonly ushort[] attributes = new ushort[PageCount];

        // Sorted by First, never overlapping: a page is free exactly when no
        // allocation holds it.
        private readonly List<Allocation> allocations = new List<Allocation>();

        /// <summary>Guest pages that are address space only (reserved, not committed).</summary>
        public long ReservedPages { get; private set; }

        /// <summary>
        /// Raised with the byte range whenever committed pages go back (decommit,
        /// release, unmap): whatever was translated from there is stale. The JIT
        /// drops its cached blocks.
        /// </summary>
        public event Action<uint, uint> PagesDiscarded;

        // --- lookups --------------------------------------------------------

        /// <summary>The allocation holding <paramref name="page"/>, or the complement of where one would go.</summary>
        private int FindAllocation(uint page)
        {
            int low = 0, high = allocations.Count - 1;
            while (low <= high)
            {
                var mid = (low + high) >> 1;
                var a = allocations[mid];
                if (page < a.First) high = mid - 1;
                else if (page >= a.End) low = mid + 1;
                else return mid;
            }
            return ~low;
        }

        /// <summary>The page after the last one touched by [address, address+size).</summary>
        private static ulong PageSpan(uint address, uint size) =>
            ((ulong)address + size + PageMask) >> PageShift;

        private bool RangeFree(uint first, uint count)
        {
            if ((ulong)first + count > PageCount) return false;
            var at = FindAllocation(first);
            if (at >= 0) return false;
            var next = ~at;
            return next >= allocations.Count || allocations[next].First >= first + count;
        }

        /// <summary>True when every page of the range is free and the range fits the 4 GB space.</summary>
        public bool IsFree(uint address, uint size)
        {
            if (size == 0) return true;
            var first = address >> PageShift;
            var end = PageSpan(address, size);
            return end <= PageCount && RangeFree(first, (uint)(end - first));
        }

        private ushort StateOf(uint page) => (ushort)(attributes[page] & StateMask);

        private void SetAttributes(uint page, ushort value)
        {
            if ((attributes[page] & StateMask) == StateReserved) ReservedPages--;
            if ((value & StateMask) == StateReserved) ReservedPages++;
            attributes[page] = value;
        }

        // --- the backing ------------------------------------------------------

        /// <summary>Commits a run of pages in the backing, zero-filled. False when the host refuses.</summary>
        private bool CommitRun(uint firstPage, uint count)
        {
            if (host != null)
            {
                if (!HostPages.Current.Commit((IntPtr)(host + ((ulong)firstPage << PageShift)), (ulong)count << PageShift))
                    return false;
                for (uint i = 0; i < count; i++) committed[firstPage + i] = true;
            }
            else
            {
                for (uint i = 0; i < count; i++) pages[firstPage + i] = new byte[PageSize];
            }
            MappedPages += count;
            return true;
        }

        /// <summary>Gives a run back: the next commit finds it zeroed, and touching it faults meanwhile.</summary>
        private void DecommitRun(uint firstPage, uint count)
        {
            if (host != null)
            {
                HostPages.Current.Decommit((IntPtr)(host + ((ulong)firstPage << PageShift)), (ulong)count << PageShift);
                for (uint i = 0; i < count; i++) committed[firstPage + i] = false;
            }
            else
            {
                for (uint i = 0; i < count; i++) pages[firstPage + i] = null;
            }
            MappedPages -= count;
        }

        // --- reserving ----------------------------------------------------------

        /// <summary>
        /// Claims the free range [address, address+size) as one allocation in the
        /// reserved state. The address must be page-aligned; false when any page of
        /// the range is already taken or the range leaves the 4 GB space.
        /// </summary>
        public bool Reserve(uint address, uint size, uint type, uint protect)
        {
            if (size == 0 || (address & PageMask) != 0) return false;
            var first = address >> PageShift;
            var end = PageSpan(address, size);
            if (end > PageCount) return false;
            var count = (uint)(end - first);
            if (!RangeFree(first, count)) return false;

            Insert(new Allocation { First = first, Count = count, Type = type, Protect = protect });
            for (var page = first; page < first + count; page++) SetAttributes(page, StateReserved);
            return true;
        }

        /// <summary>Claims the range for a PE image: the loader then commits and protects its sections.</summary>
        public bool ReserveImage(uint address, uint size) =>
            Reserve(address, size, Win32Memory.MemImage, Win32Memory.PageExecuteWriteCopy);

        private void Insert(Allocation a)
        {
            var at = FindAllocation(a.First);
            allocations.Insert(at >= 0 ? at : ~at, a);
        }

        /// <summary>
        /// The free window of <paramref name="size"/> bytes inside
        /// [<paramref name="lowest"/>, <paramref name="highest"/>) whose start is a
        /// multiple of <paramref name="alignment"/>: the lowest one, or the highest
        /// when <paramref name="topDown"/>. A <paramref name="highest"/> of 0 means
        /// the top of the 4 GB space. Zero when nothing fits; nothing is reserved.
        /// </summary>
        public uint FindRange(uint size, uint alignment, uint lowest, uint highest, bool topDown)
        {
            if (size == 0) return 0;
            var pages = (uint)PageSpan(0, size);
            var align = Math.Max(1u, alignment >> PageShift);
            var low = lowest >> PageShift;
            var high = highest == 0 ? (uint)PageCount : highest >> PageShift;
            var found = topDown ? FindDown(pages, align, low, high) : FindUp(pages, align, low, high);
            return found == NotFound ? 0 : found << PageShift;
        }

        private uint FindUp(uint pages, uint align, uint low, uint high)
        {
            if (high <= low || pages > high - low) return NotFound;
            var cursor = (low + align - 1) / align * align;
            var at = FindAllocation(cursor);
            var i = at >= 0 ? at : ~at;
            while (true)
            {
                var limit = i < allocations.Count ? Math.Min(allocations[i].First, high) : high;
                if (cursor <= limit && limit - cursor >= pages) return cursor;
                if (i >= allocations.Count) return NotFound;
                cursor = (Math.Max(cursor, allocations[i].End) + align - 1) / align * align;
                if (cursor >= high) return NotFound;
                i++;
            }
        }

        private uint FindDown(uint pages, uint align, uint low, uint high)
        {
            if (high <= low || pages > high - low) return NotFound;
            var top = high;
            var at = FindAllocation(top - 1);
            var i = at >= 0 ? at : ~at - 1;   // the last allocation that starts below the top
            while (true)
            {
                var floor = i >= 0 ? allocations[i].End : 0;
                if (floor > top)
                {
                    // That allocation straddles the top: the window has to end below it.
                    top = allocations[i].First;
                    i--;
                    continue;
                }
                floor = Math.Max(floor, low);
                if (top >= floor + pages)
                {
                    var start = top - pages;
                    start -= start % align;
                    if (start >= floor) return start;
                }
                if (i < 0) return NotFound;
                top = allocations[i].First;
                i--;
                if (top <= low) return NotFound;
            }
        }

        // --- committing -----------------------------------------------------------

        /// <summary>
        /// Commits the pages of [address, address+size) inside one private
        /// allocation: reserved pages become zeroed memory, committed ones keep
        /// their contents, and every page takes <paramref name="protect"/>.
        /// Returns 0, or the Win32 error (487 outside an allocation or in one that is
        /// not private memory, 8 when the host has no memory left; nothing is committed then).
        /// A caller that has just made the allocation itself passes
        /// <paramref name="anyType"/>: a mapped view is committed as it is created.
        /// </summary>
        public uint Commit(uint address, uint size, uint protect, bool anyType = false)
        {
            if (size == 0) return Win32Memory.ErrorInvalidParameter;
            var first = address >> PageShift;
            var end = PageSpan(address, size);
            if (end > PageCount) return Win32Memory.ErrorInvalidAddress;
            var last = (uint)(end - 1);
            var at = FindAllocation(first);
            if (at < 0 || allocations[at].End <= last || (!anyType && allocations[at].Type != Win32Memory.MemPrivate))
                return Win32Memory.ErrorInvalidAddress;
            return CommitPages(first, last, (ushort)(protect & ProtectionMask)) ? 0 : Win32Memory.ErrorNotEnoughMemory;
        }

        /// <summary>Commits the reserved pages of [first, last] and gives every page of it the protection.</summary>
        private bool CommitPages(uint first, uint last, ushort protect)
        {
            // Physical commit first, run by run; the attributes change only once all
            // of it worked, so a failure just gives the runs back.
            List<uint> runs = null;
            for (var page = first; page <= last;)
            {
                if (StateOf(page) != StateReserved) { page++; continue; }
                var start = page;
                while (page <= last && StateOf(page) == StateReserved) page++;
                if (!CommitRun(start, page - start))
                {
                    if (runs != null)
                        for (var r = 0; r < runs.Count; r += 2) DecommitRun(runs[r], runs[r + 1]);
                    return false;
                }
                if (runs == null) runs = new List<uint>();
                runs.Add(start);
                runs.Add(page - start);
            }
            for (var page = first; page <= last; page++) SetAttributes(page, (ushort)(StateCommitted | protect));
            return true;
        }

        /// <summary>
        /// Gives committed pages of one allocation back to the reserved state; their
        /// contents are gone and a later commit finds zeros. Returns 0, 487 when the
        /// range is not inside one allocation, 87 when that allocation is not private memory.
        /// </summary>
        public uint Decommit(uint address, uint size)
        {
            if (size == 0) return Win32Memory.ErrorInvalidParameter;
            var first = address >> PageShift;
            var end = PageSpan(address, size);
            if (end > PageCount) return Win32Memory.ErrorInvalidAddress;
            var last = (uint)(end - 1);
            var at = FindAllocation(first);
            if (at < 0 || allocations[at].End <= last) return Win32Memory.ErrorInvalidAddress;
            if (allocations[at].Type != Win32Memory.MemPrivate) return Win32Memory.ErrorInvalidParameter;

            DropPages(first, last);
            return 0;
        }

        /// <summary>Committed pages of the range go back to the reserved state; the rest is left alone.</summary>
        private void DropPages(uint first, uint last)
        {
            var any = false;
            for (var page = first; page <= last;)
            {
                if (StateOf(page) != StateCommitted) { page++; continue; }
                var start = page;
                while (page <= last && StateOf(page) == StateCommitted) page++;
                DecommitRun(start, page - start);
                for (var p = start; p < page; p++) SetAttributes(p, StateReserved);
                any = true;
            }
            if (any) PagesDiscarded?.Invoke(first << PageShift, (last - first + 1) << PageShift);
        }

        /// <summary>
        /// Frees the whole private allocation that starts at <paramref name="allocationBase"/>.
        /// Returns 0, 487 when nothing is allocated there, 87 when the address is not the
        /// allocation's base or the allocation is not private memory.
        /// </summary>
        public uint Release(uint allocationBase)
        {
            var at = FindAllocation(allocationBase >> PageShift);
            if (at < 0) return Win32Memory.ErrorInvalidAddress;
            var a = allocations[at];
            if ((a.First << PageShift) != allocationBase || a.Type != Win32Memory.MemPrivate)
                return Win32Memory.ErrorInvalidParameter;

            DropPages(a.First, a.End - 1);
            for (var page = a.First; page < a.End; page++) SetAttributes(page, 0);
            allocations.RemoveAt(at);
            return 0;
        }

        // --- protecting and describing ------------------------------------------------

        /// <summary>
        /// Records a new protection for committed pages (they may span adjacent
        /// allocations) and gives back the old one of the first page. Returns 0 or
        /// 487 when a page of the range is not committed. The protection is recorded,
        /// not enforced.
        /// </summary>
        public uint Protect(uint address, uint size, uint protect, out uint old)
        {
            old = 0;
            if (size == 0) return Win32Memory.ErrorInvalidParameter;
            var first = address >> PageShift;
            var end = PageSpan(address, size);
            if (end > PageCount) return Win32Memory.ErrorInvalidAddress;
            var last = (uint)(end - 1);
            for (var page = first; page <= last; page++)
                if (StateOf(page) != StateCommitted) return Win32Memory.ErrorInvalidAddress;

            old = (uint)(attributes[first] & ProtectionMask);
            var value = (ushort)(StateCommitted | (protect & ProtectionMask));
            for (var page = first; page <= last; page++) attributes[page] = value;
            return 0;
        }

        /// <summary>The allocation holding an address: where it starts, its size in pages, its type and creation protection.</summary>
        public bool GetAllocation(uint address, out uint allocationBase, out uint pages, out uint type, out uint protect)
        {
            allocationBase = pages = type = protect = 0;
            var at = FindAllocation(address >> PageShift);
            if (at < 0) return false;
            var a = allocations[at];
            allocationBase = a.First << PageShift;
            pages = a.Count;
            type = a.Type;
            protect = a.Protect;
            return true;
        }

        /// <summary>
        /// VirtualQuery for one address: the run of pages from its page on that share
        /// an allocation, a state and a protection. A free run reaches the next allocation.
        /// </summary>
        public GuestRegion Query(uint address)
        {
            var page = address >> PageShift;
            var at = FindAllocation(page);
            if (at < 0)
            {
                var next = ~at;
                var end = next < allocations.Count ? allocations[next].First : PageCount;
                return new GuestRegion
                {
                    BaseAddress = page << PageShift,
                    RegionSize = (uint)Math.Min((ulong)(end - page) << PageShift, uint.MaxValue),
                    State = Win32Memory.MemFree,
                    Protect = Win32Memory.PageNoAccess,
                };
            }

            var a = allocations[at];
            var value = attributes[page];
            var stop = page + 1;
            while (stop < a.End && attributes[stop] == value) stop++;
            var isCommitted = (value & StateMask) == StateCommitted;
            return new GuestRegion
            {
                BaseAddress = page << PageShift,
                AllocationBase = a.First << PageShift,
                AllocationProtect = a.Protect,
                RegionSize = (stop - page) << PageShift,
                State = isCommitted ? Win32Memory.MemCommit : Win32Memory.MemReserve,
                Protect = isCommitted ? (uint)(value & ProtectionMask) : 0,
                Type = a.Type,
            };
        }

        // --- the plain mapping calls ------------------------------------------------------

        /// <summary>
        /// Makes a range addressable, zero-filled, read-write. Already-committed pages
        /// keep their contents; reserved pages inside an allocation are committed in
        /// it; free pages become one new private allocation per free run. This is how
        /// the loader and the tests lay down their own memory.
        /// </summary>
        public void Map(uint address, uint size)
        {
            if (size == 0) return;
            var first = address >> PageShift;
            var last = (uint)Math.Min(PageSpan(address, size) - 1, (ulong)PageCount - 1);
            const ushort ReadWrite = (ushort)Win32Memory.PageReadWrite;

            for (var page = first; page <= last;)
            {
                var state = StateOf(page);
                if (state == StateCommitted) { page++; continue; }

                var start = page;
                while (page <= last && StateOf(page) == state) page++;
                if (state == 0)
                {
                    Insert(new Allocation
                    {
                        First = start,
                        Count = page - start,
                        Type = Win32Memory.MemPrivate,
                        Protect = Win32Memory.PageReadWrite,
                    });
                    for (var p = start; p < page; p++) SetAttributes(p, StateReserved);
                }
                if (!CommitRun(start, page - start))
                    throw new OutOfMemoryException($"could not commit guest pages at 0x{start << PageShift:X8}");
                for (var p = start; p < page; p++) SetAttributes(p, (ushort)(StateCommitted | ReadWrite));
            }
        }

        /// <summary>
        /// Like <see cref="Map"/> for memory that grows in place (the guest heap): the
        /// bytes [<paramref name="currentBytes"/>, <paramref name="newBytes"/>) after
        /// <paramref name="allocationBase"/> are committed, in one private allocation that
        /// is created on the first call and extended on the next. False when something
        /// else holds the pages the growth needs, or the host has no memory.
        /// </summary>
        public bool MapGrowing(uint allocationBase, uint currentBytes, uint newBytes)
        {
            if (newBytes <= currentBytes) return true;
            var first = allocationBase >> PageShift;
            var from = first + (currentBytes >> PageShift);
            var end = PageSpan(allocationBase, newBytes);
            if (end > PageCount) return false;

            var at = FindAllocation(first);
            if (at < 0)
            {
                if (!Reserve(allocationBase, newBytes, Win32Memory.MemPrivate, Win32Memory.PageReadWrite)) return false;
            }
            else
            {
                var a = allocations[at];
                if (a.First != first || a.Type != Win32Memory.MemPrivate) return false;
                if (a.End < end)
                {
                    var added = a.End;
                    if (!RangeFree(added, (uint)end - added)) return false;
                    a.Count = (uint)end - first;
                    for (var page = added; page < end; page++) SetAttributes(page, StateReserved);
                }
            }
            return CommitPages(from, (uint)end - 1, (ushort)Win32Memory.PageReadWrite);
        }

        /// <summary>
        /// Takes the range out of the address space altogether (decommit and release):
        /// pages go back to free, and an allocation is cut down or split around the hole.
        /// </summary>
        public void Unmap(uint address, uint size)
        {
            if (size == 0) return;
            var first = address >> PageShift;
            var last = (uint)Math.Min(PageSpan(address, size) - 1, (ulong)PageCount - 1);

            DropPages(first, last);
            for (var page = first; page <= last; page++) SetAttributes(page, 0);

            var at = FindAllocation(first);
            var i = at >= 0 ? at : ~at;
            while (i < allocations.Count && allocations[i].First <= last)
            {
                var a = allocations[i];
                allocations.RemoveAt(i);
                if (a.First < first)
                {
                    allocations.Insert(i, new Allocation { First = a.First, Count = first - a.First, Type = a.Type, Protect = a.Protect });
                    i++;
                }
                if (a.End > last + 1)
                {
                    allocations.Insert(i, new Allocation { First = last + 1, Count = a.End - (last + 1), Type = a.Type, Protect = a.Protect });
                    i++;
                }
            }
        }

        /// <summary>
        /// Finds a free, page-aligned range at or above <paramref name="hint"/>.
        /// Used for stacks, heaps and TEB/PEB blocks; returns 0 when nothing fits.
        /// Reserved address space counts as taken.
        /// </summary>
        public uint FindFree(uint size, uint hint = 0x00100000) => FindRange(size, PageSize, hint, 0, false);
    }
}
