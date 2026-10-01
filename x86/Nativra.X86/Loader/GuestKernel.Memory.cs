using System;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // The virtual-memory API over the address-space model in GuestMemory: reserve
    // and commit are different things (a reservation is address space only, with
    // no memory behind it until a commit), allocation bases sit on 64 KB, a free
    // really frees, VirtualQuery describes what is there and VirtualProtect
    // remembers what it was told. Protections are recorded, not enforced (see
    // GuestMemory.Regions.cs).
    public sealed partial class GuestKernel
    {
        private const uint ErrorBadLength = 24, ErrorInvalidAddress = 487, ErrorNoAccess = 998;

        private const uint Granularity = 0x00010000;
        private const uint LowestUserAddress = 0x00010000;
        private const uint BasicInformationSize = 28;   // MEMORY_BASIC_INFORMATION on x86

        // Where VirtualAlloc(NULL) places things: first fit from the floor up to the
        // ceiling, then the gap below the floor. Above the ceiling live the PEB and
        // TEBs, the stand-in modules for system DLLs and the import sentinels, none
        // of which an anonymous allocation may land on.
        private const uint AnonymousFloor = 0x20000000;
        private const uint AnonymousCeiling = 0x7E000000;

        /// <summary>
        /// The last usable byte of the guest's address space: 2 GB for most programs,
        /// 4 GB less 64 KB for one that says it is large-address-aware.
        /// </summary>
        private uint HighestUserAddress =>
            process.MainImage != null && process.MainImage.LargeAddressAware ? 0xFFFEFFFF : 0x7FFEFFFF;

        private uint Fail(uint error)
        {
            process.LastError = error;
            return 0;
        }

        /// <summary>The *Ex forms take a process handle; only this process can be reached.</summary>
        private bool OwnProcess(uint handle)
        {
            if (IsThisProcess(handle)) return true;
            process.LastError = ErrorInvalidHandle;
            return false;
        }

        private void InstallVirtualMemory(GuestImports i)
        {
            const string k = "kernel32.dll";
            i.Register(k, "VirtualAlloc", CallConv.Stdcall, 4, c => Allocate(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), Win32Memory.MemPrivate));
            i.Register(k, "VirtualAllocEx", CallConv.Stdcall, 5, c =>
                OwnProcess(c.Arg(0)) ? Allocate(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), Win32Memory.MemPrivate) : 0u);
            i.Register(k, "VirtualAllocExNuma", CallConv.Stdcall, 6, c =>
                OwnProcess(c.Arg(0)) ? Allocate(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), Win32Memory.MemPrivate) : 0u);
            i.Register(k, "VirtualFree", CallConv.Stdcall, 3, c => Free(c.Arg(0), c.Arg(1), c.Arg(2)));
            i.Register(k, "VirtualFreeEx", CallConv.Stdcall, 4, c => OwnProcess(c.Arg(0)) ? Free(c.Arg(1), c.Arg(2), c.Arg(3)) : 0u);
            i.Register(k, "VirtualQuery", CallConv.Stdcall, 3, c => Query(c.Arg(0), c.Arg(1), c.Arg(2)));
            i.Register(k, "VirtualQueryEx", CallConv.Stdcall, 4, c => OwnProcess(c.Arg(0)) ? Query(c.Arg(1), c.Arg(2), c.Arg(3)) : 0u);
            i.Register(k, "VirtualProtect", CallConv.Stdcall, 4, c => Protect(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(k, "VirtualProtectEx", CallConv.Stdcall, 5, c =>
                OwnProcess(c.Arg(0)) ? Protect(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)) : 0u);
        }

        /// <summary>
        /// Reserves and commits read-write memory for the host's own needs (DIB sections,
        /// file-mapping views): address 0 picks a 64 KB-aligned spot, anything else asks
        /// for that very address. Zero, with the error set, when it does not fit.
        /// </summary>
        private uint VirtualAlloc(uint address, uint size, uint regionType = Win32Memory.MemPrivate) =>
            Allocate(address, size, Win32Memory.MemReserve | Win32Memory.MemCommit, Win32Memory.PageReadWrite, regionType);

        // --- VirtualAlloc ---------------------------------------------------------------

        private uint Allocate(uint address, uint size, uint type, uint protect, uint regionType)
        {
            const uint Known = Win32Memory.MemCommit | Win32Memory.MemReserve | Win32Memory.MemReset |
                Win32Memory.MemTopDown | Win32Memory.MemWriteWatch | Win32Memory.MemPhysical |
                Win32Memory.MemResetUndo | Win32Memory.MemLargePages;
            var action = type & (Win32Memory.MemCommit | Win32Memory.MemReserve | Win32Memory.MemReset | Win32Memory.MemResetUndo);
            if ((type & ~Known) != 0 || action == 0) return Fail(ErrorInvalidParameter);
            // RESET stands alone; large pages need a minimum size GetLargePageMinimum reports as none.
            if ((action & (Win32Memory.MemReset | Win32Memory.MemResetUndo)) != 0 && type != action) return Fail(ErrorInvalidParameter);
            if ((type & Win32Memory.MemLargePages) != 0) return Fail(ErrorInvalidParameter);
            if (!Win32Memory.IsValidProtection(protect) || size == 0) return Fail(ErrorInvalidParameter);

            if ((action & (Win32Memory.MemReset | Win32Memory.MemResetUndo)) != 0) return ResetPages(address, size);
            return address == 0
                ? AllocateAnywhere(size, type, protect, regionType)
                : AllocateAt(address, size, type, protect, regionType);
        }

        /// <summary>
        /// MEM_RESET (and its undo): the program says it no longer needs the contents
        /// of committed pages. Nothing is dropped; the call only has to answer as
        /// Windows does: the pages must be committed.
        /// </summary>
        private uint ResetPages(uint address, uint size)
        {
            if (address == 0) return Fail(ErrorInvalidParameter);
            var end = (ulong)address + size;
            if (end > (ulong)HighestUserAddress + 1) return Fail(ErrorInvalidParameter);
            for (var at = address & ~(uint)(GuestMemory.PageSize - 1); at < end; at += GuestMemory.PageSize)
                if (!memory.IsMapped(at)) return Fail(ErrorInvalidAddress);
            return address & ~(uint)(GuestMemory.PageSize - 1);
        }

        private uint AllocateAnywhere(uint size, uint type, uint protect, uint regionType)
        {
            var topDown = (type & Win32Memory.MemTopDown) != 0;
            uint at;
            if (topDown)
            {
                at = FindAvoidingHeap(size, LowestUserAddress, AnonymousCeiling, true);
            }
            else
            {
                at = FindAvoidingHeap(size, AnonymousFloor, AnonymousCeiling, false);
                if (at == 0) at = FindAvoidingHeap(size, LowestUserAddress, AnonymousFloor, false);
            }
            if (at == 0) return Fail(ErrorNotEnoughMemory);
            return Claim(at, size, type, protect, regionType);
        }

        /// <summary>
        /// The 64 KB-aligned window the model finds, minus the guest heap's region: the
        /// heap maps its region only as it grows, so its unmapped part looks free to the
        /// model, and anything handed out there would later be the heap's too.
        /// </summary>
        private uint FindAvoidingHeap(uint size, uint low, uint high, bool topDown)
        {
            var at = memory.FindRange(size, Granularity, low, high, topDown);
            if (at == 0 || !heap.Overlaps(at, size)) return at;
            return topDown
                ? memory.FindRange(size, Granularity, low, Math.Min(high, heap.Base), true)
                : memory.FindRange(size, Granularity, Math.Max(low, heap.RegionEnd), high, false);
        }

        private uint AllocateAt(uint address, uint size, uint type, uint protect, uint regionType)
        {
            var reserving = (type & Win32Memory.MemReserve) != 0;
            // A reservation starts on a 64 KB boundary; a commit inside one on a page.
            var start = address & ~(reserving ? Granularity - 1 : (uint)(GuestMemory.PageSize - 1));
            var end = (ulong)address + size;
            if (start < LowestUserAddress) return Fail(ErrorInvalidAddress);
            if (end > (ulong)HighestUserAddress + 1) return Fail(ErrorInvalidParameter);
            var length = (uint)(end - start);
            // The heap's region is the heap's, mapped yet or not.
            if (heap.Overlaps(start, length)) return Fail(ErrorInvalidAddress);

            if (!reserving)
            {
                var error = memory.Commit(start, length, protect);
                return error == 0 ? start : Fail(error);
            }
            if (!memory.Reserve(start, length, regionType, protect)) return Fail(ErrorInvalidAddress);
            return Claim(start, length, type, protect, regionType, reserved: true);
        }

        /// <summary>Reserves a found window (unless it already is) and commits it when the call asked for that.</summary>
        private uint Claim(uint at, uint size, uint type, uint protect, uint regionType, bool reserved = false)
        {
            if (!reserved && !memory.Reserve(at, size, regionType, protect)) return Fail(ErrorNotEnoughMemory);
            if ((type & Win32Memory.MemCommit) != 0)
            {
                var error = memory.Commit(at, size, protect, anyType: true);
                if (error != 0)
                {
                    memory.Unmap(at, size);
                    return Fail(error);
                }
            }
            return at;
        }

        // --- VirtualFree ----------------------------------------------------------------

        private uint Free(uint address, uint size, uint type)
        {
            // Exactly one of the two, and nothing else.
            if (type != Win32Memory.MemDecommit && type != Win32Memory.MemRelease) return Fail(ErrorInvalidParameter);

            if (type == Win32Memory.MemRelease)
            {
                // Release frees what VirtualAlloc reserved, all of it, named by its base.
                if (size != 0) return Fail(ErrorInvalidParameter);
                if (heap.Overlaps(address, 1)) return Fail(ErrorInvalidParameter);
                var released = memory.Release(address);
                return released == 0 ? 1u : Fail(released);
            }

            // Decommit: the pages go back to reserved, and size 0 means the whole allocation.
            if (size == 0)
            {
                if (!memory.GetAllocation(address, out var allocationBase, out var pages, out _, out _)) return Fail(ErrorInvalidAddress);
                if (allocationBase != address) return Fail(ErrorInvalidParameter);
                size = pages << GuestMemory.PageShift;
            }
            if (heap.Overlaps(address, size)) return Fail(ErrorInvalidParameter);
            var error = memory.Decommit(address, size);
            return error == 0 ? 1u : Fail(error);
        }

        // --- VirtualQuery ---------------------------------------------------------------

        private uint Query(uint address, uint buffer, uint length)
        {
            if (length < BasicInformationSize) return Fail(ErrorBadLength);
            if (address > HighestUserAddress) return Fail(ErrorInvalidParameter);
            if (!Readable(buffer, BasicInformationSize)) return Fail(ErrorNoAccess);

            var region = memory.Query(address);
            var size = region.RegionSize;
            // Free space ends where the user range does.
            if (region.State == Win32Memory.MemFree)
                size = (uint)Math.Min(size, (ulong)HighestUserAddress + 1 - region.BaseAddress);
            memory.Write32(buffer + 0, region.BaseAddress);
            memory.Write32(buffer + 4, region.AllocationBase);
            memory.Write32(buffer + 8, region.AllocationProtect);
            memory.Write32(buffer + 12, size);
            memory.Write32(buffer + 16, region.State);
            memory.Write32(buffer + 20, region.Protect);
            memory.Write32(buffer + 24, region.Type);
            return BasicInformationSize;
        }

        // --- VirtualProtect ---------------------------------------------------------------

        private uint Protect(uint address, uint size, uint protect, uint oldOut)
        {
            if (oldOut == 0 || !Readable(oldOut, 4)) return Fail(ErrorNoAccess);
            if (!Win32Memory.IsValidProtection(protect) || size == 0) return Fail(ErrorInvalidParameter);
            if ((ulong)address + size > (ulong)HighestUserAddress + 1) return Fail(ErrorInvalidParameter);

            var error = memory.Protect(address, size, protect, out var old);
            if (error != 0) return Fail(error);
            memory.Write32(oldOut, old);
            return 1;
        }
    }
}
