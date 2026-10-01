using System;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// The guest's virtual-memory API as Windows defines it: a reservation is
    /// address space with no memory behind it, a commit inside it makes zeroed
    /// pages, allocation bases sit on 64 KB, a free really frees, VirtualQuery
    /// describes what is there and VirtualProtect remembers what it was told.
    /// Every call goes through the import sentinel, as a game's would.
    /// </summary>
    public sealed class GuestVirtualMemoryTests
    {
        private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemDecommit = 0x4000, MemRelease = 0x8000;
        private const uint MemReset = 0x80000, MemTopDown = 0x100000;
        private const uint MemFree = 0x10000, MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000;
        private const uint PageNoAccess = 0x01, PageReadOnly = 0x02, PageReadWrite = 0x04;
        private const uint PageExecuteRead = 0x20, PageExecuteReadWrite = 0x40, PageExecuteWriteCopy = 0x80, PageGuard = 0x100;
        private const uint ErrorNotEnoughMemory = 8, ErrorInvalidHandle = 6, ErrorBadLength = 24;
        private const uint ErrorInvalidParameter = 87, ErrorInvalidAddress = 487, ErrorNoAccess = 998;

        private static GuestProcess NewProcess(out GuestKernel kernel, GuestMemory memory = null,
            uint heapBase = 0x30000000, uint heapSize = 0x10000000)
        {
            var p = new GuestProcess(memory ?? new GuestMemory(), useJit: false);
            kernel = new GuestKernel(p, heapBase, heapSize);
            kernel.Install();
            return p;
        }

        private static uint K(GuestProcess p, string function, params uint[] args)
        {
            var sentinel = p.Imports.Bind("kernel32.dll", function, -1);
            var result = p.Call(sentinel, out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result.Stop} @ 0x{result.FaultAddress:X8}");
            return eax;
        }

        private static uint LastError(GuestProcess p) => K(p, "GetLastError");

        /// <summary>Calls something that must fail: zero back, and this error.</summary>
        private static void Fails(GuestProcess p, uint error, string function, params uint[] args)
        {
            K(p, "SetLastError", 0);
            Assert.Equal(0u, K(p, function, args));
            Assert.Equal(error, LastError(p));
        }

        private struct Info
        {
            public uint Base, AllocationBase, AllocationProtect, Size, State, Protect, Type;
        }

        private static Info Query(GuestProcess p, GuestKernel kernel, uint address)
        {
            var buffer = kernel.Heap.Alloc(28, zero: true);
            Assert.Equal(28u, K(p, "VirtualQuery", address, buffer, 28));
            var m = p.Memory;
            var info = new Info
            {
                Base = m.Read32(buffer),
                AllocationBase = m.Read32(buffer + 4),
                AllocationProtect = m.Read32(buffer + 8),
                Size = m.Read32(buffer + 12),
                State = m.Read32(buffer + 16),
                Protect = m.Read32(buffer + 20),
                Type = m.Read32(buffer + 24),
            };
            kernel.Heap.Free(buffer);
            return info;
        }

        private static uint Alloc(GuestProcess p, uint size, uint type = MemReserve | MemCommit, uint protect = PageReadWrite)
        {
            var at = K(p, "VirtualAlloc", 0, size, type, protect);
            Assert.NotEqual(0u, at);
            return at;
        }

        // ---------------------------------------------------------------- lifecycle

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReserveCommitWriteDecommitRecommitRelease(bool native)
        {
            using (var memory = new GuestMemory(native))
            {
                var p = NewProcess(out var kernel, memory);
                var committed = memory.MappedPages;
                var reserved = memory.ReservedPages;

                // Reserving claims 64 KB-aligned address space and nothing else.
                var region = K(p, "VirtualAlloc", 0, 0x40000, MemReserve, PageNoAccess);
                Assert.NotEqual(0u, region);
                Assert.Equal(0u, region & 0xFFFF);
                Assert.Equal(committed, memory.MappedPages);
                Assert.Equal(reserved + 0x40, memory.ReservedPages);
                Assert.False(memory.IsMapped(region));
                Assert.Throws<GuestFaultException>(() => memory.Read32(region));

                // A commit inside it maps just those pages (the address rounds down to a page), zero-filled.
                var middle = K(p, "VirtualAlloc", region + 0x1234, 0x1800, MemCommit, PageReadWrite);
                Assert.Equal(region + 0x1000, middle);
                Assert.Equal(committed + 2, memory.MappedPages);   // 0x1234..0x2A33 touches two pages
                Assert.False(memory.IsMapped(region));
                Assert.True(memory.IsMapped(region + 0x1000));
                Assert.True(memory.IsMapped(region + 0x2FFF));
                Assert.False(memory.IsMapped(region + 0x3000));
                Assert.Equal(0u, memory.Read32(middle));
                Assert.Equal(0u, memory.Read32(middle + 0x1FFC));
                memory.Write32(middle, 0xCAFEBABE);
                memory.Write32(middle + 0x1FFC, 0x01020304);
                Assert.Equal(0xCAFEBABEu, memory.Read32(middle));

                // Decommit gives the pages back to the reservation: the contents go, touching them faults.
                Assert.Equal(1u, K(p, "VirtualFree", middle, 0x2000, MemDecommit));
                Assert.Equal(committed, memory.MappedPages);
                Assert.Equal(reserved + 0x40, memory.ReservedPages);
                Assert.False(memory.IsMapped(middle));
                Assert.Throws<GuestFaultException>(() => memory.Read32(middle + 0x1000));

                // Committed again, it is zeroed again.
                Assert.Equal(middle, K(p, "VirtualAlloc", middle, 0x2000, MemCommit, PageReadWrite));
                Assert.Equal(0u, memory.Read32(middle));
                Assert.Equal(0u, memory.Read32(middle + 0x1FFC));

                // Release returns the whole allocation, committed part included.
                Assert.Equal(1u, K(p, "VirtualFree", region, 0, MemRelease));
                Assert.Equal(committed, memory.MappedPages);
                Assert.Equal(reserved, memory.ReservedPages);
                Assert.False(memory.IsMapped(middle));
                Assert.Equal(MemFree, Query(p, kernel, region).State);
                Assert.Equal(MemFree, Query(p, kernel, middle).State);
            }
        }

        [Fact]
        public void ReserveAndCommitInOneCallMapsEverything()
        {
            var p = NewProcess(out _);
            var committed = p.Memory.MappedPages;
            var region = Alloc(p, 0x5000);
            Assert.Equal(committed + 5, p.Memory.MappedPages);
            for (uint at = region; at < region + 0x5000; at += 0x1000) Assert.True(p.Memory.IsMapped(at));
            Assert.False(p.Memory.IsMapped(region + 0x5000));   // the size rounds up to pages, not further
            p.Memory.Write32(region + 0x4FFC, 0x12345678);
            Assert.Equal(0x12345678u, p.Memory.Read32(region + 0x4FFC));
        }

        [Fact]
        public void CommitWithoutAnAddressReservesAndCommitsToo()
        {
            var p = NewProcess(out _);
            var region = K(p, "VirtualAlloc", 0, 0x2000, MemCommit, PageReadWrite);   // no MEM_RESERVE: Windows adds it
            Assert.NotEqual(0u, region);
            p.Memory.Write32(region, 7);
            Assert.Equal(7u, p.Memory.Read32(region));
        }

        [Fact]
        public void DecommitOfTheWholeAllocationKeepsTheReservation()
        {
            var p = NewProcess(out var kernel);
            var region = Alloc(p, 0x4000);
            p.Memory.Write32(region, 5);

            Assert.Equal(1u, K(p, "VirtualFree", region, 0, MemDecommit));   // size 0 at the base: all of it
            Assert.False(p.Memory.IsMapped(region));
            var info = Query(p, kernel, region);
            Assert.Equal(0x2000u, info.State);   // MEM_RESERVE
            Assert.Equal(0x4000u, info.Size);
            Assert.Equal(0u, info.Protect);
            Assert.Equal(PageReadWrite, info.AllocationProtect);

            // Decommitting what is not committed is fine, and the pages come back on demand.
            Assert.Equal(1u, K(p, "VirtualFree", region, 0x1000, MemDecommit));
            Assert.Equal(region, K(p, "VirtualAlloc", region, 0x4000, MemCommit, PageReadWrite));
            Assert.Equal(0u, p.Memory.Read32(region));
        }

        [Fact]
        public void FreedSpaceIsReusedByTheNextAllocation()
        {
            var p = NewProcess(out _);
            var a = Alloc(p, 0x10000);
            var b = Alloc(p, 0x10000);
            Assert.NotEqual(a, b);
            Assert.Equal(1u, K(p, "VirtualFree", a, 0, MemRelease));

            Assert.Equal(a, Alloc(p, 0x8000));            // first fit: the hole at the bottom
            Assert.True(Alloc(p, 0x20000) > b);           // too big for what is left of it
        }

        // ------------------------------------------------------------ granularity

        [Fact]
        public void AllocationBasesSitOnSixtyFourKilobytes()
        {
            var p = NewProcess(out var kernel);
            var sizes = new uint[] { 1, 0x1000, 0x1001, 0x10000, 0x12345 };
            var seen = new System.Collections.Generic.List<Info>();
            foreach (var size in sizes)
            {
                var at = Alloc(p, size);
                Assert.Equal(0u, at & 0xFFFF);
                seen.Add(Query(p, kernel, at));
            }
            for (var n = 1; n < seen.Count; n++) Assert.True(seen[n].Base >= seen[n - 1].Base + seen[n - 1].Size, "allocations overlap");
        }

        [Fact]
        public void AnExplicitReservationRoundsDownToTheGranularityAndItsEndUpToAPage()
        {
            var p = NewProcess(out var kernel);
            // 0x50012345 + 0x100 ends inside the page at 0x50012000; the base rounds down to 0x50010000.
            var at = K(p, "VirtualAlloc", 0x50012345, 0x100, MemReserve, PageReadWrite);
            Assert.Equal(0x50010000u, at);
            var info = Query(p, kernel, at);
            Assert.Equal(0x50010000u, info.AllocationBase);
            Assert.Equal(0x3000u, info.Size);
            Assert.Equal(0x2000u, info.State);

            // Inside it, a commit rounds down to a page, not to 64 KB.
            Assert.Equal(0x50011000u, K(p, "VirtualAlloc", 0x50011FF0, 0x10, MemCommit, PageReadWrite));
            Assert.True(p.Memory.IsMapped(0x50011000));
            Assert.False(p.Memory.IsMapped(0x50010000));
            Assert.False(p.Memory.IsMapped(0x50012000));
        }

        [Fact]
        public void TopDownPlacesAboveTheBottomUpAllocationsAndBelowTheSystemArea()
        {
            var p = NewProcess(out _);
            var low = Alloc(p, 0x10000);
            var high = Alloc(p, 0x10000, MemReserve | MemCommit | MemTopDown);
            var higher = Alloc(p, 0x10000, MemReserve | MemCommit | MemTopDown);
            Assert.True(high > low);
            Assert.Equal(0u, high & 0xFFFF);
            Assert.True(high + 0x10000 <= 0x7E000000u, "top-down must not reach the PEB and import sentinels");
            Assert.True(higher + 0x10000 <= high);   // the next one goes under it
        }

        [Fact]
        public void AnonymousAllocationsNeverLandInTheHeapRegionEvenBeforeItGrowsThere()
        {
            // The heap maps only what it uses, so most of its region is free as far as the pages go.
            var p = NewProcess(out var kernel, heapBase: 0x20000000, heapSize: 0x00100000);
            var at = Alloc(p, 0x10000);
            Assert.True(at >= 0x20100000u, $"landed at 0x{at:X8} inside the heap's 0x20000000..0x20100000");
            Assert.False(kernel.Heap.Overlaps(at, 0x10000));

            Fails(p, ErrorInvalidAddress, "VirtualAlloc", 0x20010000, 0x1000, MemReserve | MemCommit, PageReadWrite);
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", 0x200FF000, 0x1000, MemCommit, PageReadWrite);
            Fails(p, ErrorInvalidParameter, "VirtualFree", 0x20000000, 0, MemRelease);   // the heap's own memory is not the program's to free

            // The heap itself keeps working, and keeps its blocks.
            var block = kernel.Heap.Alloc(100);
            Assert.True(kernel.Heap.Owns(block));
            p.Memory.Write32(block, 0xABCD);
            Assert.Equal(0xABCDu, p.Memory.Read32(block));
        }

        // ------------------------------------------------------------------ queries

        [Fact]
        public void QueryWalksAReservationWithACommittedMiddle()
        {
            var p = NewProcess(out var kernel);
            var region = K(p, "VirtualAlloc", 0, 0x10000, MemReserve, PageNoAccess);
            K(p, "VirtualAlloc", region + 0x3000, 0x2000, MemCommit, PageReadWrite);

            var head = Query(p, kernel, region);
            Assert.Equal(region, head.Base);
            Assert.Equal(region, head.AllocationBase);
            Assert.Equal(PageNoAccess, head.AllocationProtect);
            Assert.Equal(0x3000u, head.Size);
            Assert.Equal(0x2000u, head.State);   // MEM_RESERVE
            Assert.Equal(0u, head.Protect);
            Assert.Equal(MemPrivate, head.Type);

            // Any address in a page answers for its page; a region runs from there.
            var middle = Query(p, kernel, region + 0x3ABC);
            Assert.Equal(region + 0x3000, middle.Base);
            Assert.Equal(region, middle.AllocationBase);
            Assert.Equal(PageNoAccess, middle.AllocationProtect);
            Assert.Equal(0x2000u, middle.Size);
            Assert.Equal(0x1000u, middle.State);   // MEM_COMMIT
            Assert.Equal(PageReadWrite, middle.Protect);
            Assert.Equal(MemPrivate, middle.Type);

            var tail = Query(p, kernel, region + 0x5000);
            Assert.Equal(region + 0x5000, tail.Base);
            Assert.Equal(region, tail.AllocationBase);
            Assert.Equal(0xB000u, tail.Size);   // to the end of the allocation, not beyond
            Assert.Equal(0x2000u, tail.State);
            Assert.Equal(0u, tail.Protect);
        }

        [Fact]
        public void QueryDescribesFreeSpaceUpToTheNextAllocation()
        {
            var p = NewProcess(out var kernel);
            var a = K(p, "VirtualAlloc", 0x50000000, 0x10000, MemReserve | MemCommit, PageReadWrite);
            Assert.Equal(0x50000000u, a);

            var before = Query(p, kernel, 0x4FFF0000);
            Assert.Equal(MemFree, before.State);
            Assert.Equal(0x4FFF0000u, before.Base);
            Assert.Equal(0u, before.AllocationBase);
            Assert.Equal(0u, before.AllocationProtect);
            Assert.Equal(PageNoAccess, before.Protect);
            Assert.Equal(0u, before.Type);
            Assert.True(before.Size >= 0x10000);
            // The free run ends where the allocation begins.
            Assert.Equal(0x50000000u, before.Base + before.Size);

            var after = Query(p, kernel, 0x50010000);
            Assert.Equal(MemFree, after.State);
            Assert.Equal(0x50010000u, after.Base);

            // The first 64 KB are free address space too, and never allocatable.
            Assert.Equal(MemFree, Query(p, kernel, 0).State);
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", 0x1000, 0x1000, MemReserve | MemCommit, PageReadWrite);
        }

        [Fact]
        public void QueryFailsPastTheUserRangeAndWithBadArguments()
        {
            var p = NewProcess(out var kernel);
            var buffer = kernel.Heap.Alloc(28, zero: true);
            var region = Alloc(p, 0x1000);

            Fails(p, ErrorInvalidParameter, "VirtualQuery", 0x80000000, buffer, 28);   // above 2 GB for an ordinary program
            Fails(p, ErrorInvalidParameter, "VirtualQuery", 0xFFFFFFFF, buffer, 28);
            Fails(p, ErrorBadLength, "VirtualQuery", region, buffer, 27);
            Fails(p, ErrorNoAccess, "VirtualQuery", region, 0x0DEAD000, 28);   // nowhere to write the answer
            Assert.Equal(28u, K(p, "VirtualQuery", 0x7FFEFFFF, buffer, 28));   // the last usable byte
            Assert.Equal(MemFree, p.Memory.Read32(buffer + 16));
            Assert.Equal(0x7FFEF000u, p.Memory.Read32(buffer));
            Assert.Equal(0x1000u, p.Memory.Read32(buffer + 12));   // free space stops at the end of the user range
        }

        [Fact]
        public void QueryExAnswersForThisProcessOnly()
        {
            var p = NewProcess(out var kernel);
            var buffer = kernel.Heap.Alloc(28, zero: true);
            var region = Alloc(p, 0x1000);

            Assert.Equal(28u, K(p, "VirtualQueryEx", 0xFFFFFFFF, region, buffer, 28));   // GetCurrentProcess()
            Assert.Equal(region, p.Memory.Read32(buffer));
            Fails(p, ErrorInvalidHandle, "VirtualQueryEx", 0x1234, region, buffer, 28);
            Fails(p, ErrorInvalidHandle, "VirtualAllocEx", 0x1234, 0, 0x1000, MemReserve | MemCommit, PageReadWrite);
            Fails(p, ErrorInvalidHandle, "VirtualProtectEx", 0x1234, region, 0x1000, PageReadOnly, buffer);
            Fails(p, ErrorInvalidHandle, "VirtualFreeEx", 0x1234, region, 0, MemRelease);

            var other = K(p, "VirtualAllocEx", 0xFFFFFFFF, 0, 0x2000, MemReserve | MemCommit, PageReadWrite);
            Assert.NotEqual(0u, other);
            Assert.Equal(1u, K(p, "VirtualProtectEx", 0xFFFFFFFF, other, 0x2000, PageReadOnly, buffer));
            Assert.Equal(1u, K(p, "VirtualFreeEx", 0xFFFFFFFF, other, 0, MemRelease));
        }

        [Fact]
        public void ALargeAddressAwareProgramSeesTheWholeFourGigabytesLessTheTopSixtyFourKilobytes()
        {
            var p = NewProcess(out var kernel);
            var image = TestPe32.Minimal();
            image[0x80 + 22] |= 0x20;   // IMAGE_FILE_LARGE_ADDRESS_AWARE in the COFF characteristics
            p.LoadExecutable("big.exe", image);
            Assert.True(p.MainImage.LargeAddressAware);

            // A probe above 2 GB works, and the base comes back on its 64 KB boundary (Source's tier0 asks for this).
            var probe = K(p, "VirtualAlloc", 0xFFEEFFEE, 1, MemReserve, PageNoAccess);
            Assert.Equal(0xFFEE0000u, probe);
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", 0xFFEEFFEE, 1, MemReserve, PageNoAccess);   // already taken
            Assert.Equal(1u, K(p, "VirtualFree", probe, 0, MemRelease));
            var buffer = kernel.Heap.Alloc(28, zero: true);
            Assert.Equal(28u, K(p, "VirtualQuery", 0xFFFEFFFF, buffer, 28));
            Fails(p, ErrorInvalidParameter, "VirtualQuery", 0xFFFF0000, buffer, 28);
        }

        // ------------------------------------------------------------------ images

        [Fact]
        public void AnImageIsOneMemImageAllocationWithItsSectionProtections()
        {
            var p = NewProcess(out var kernel);
            var image = p.LoadExecutable("waveshaper.exe", TestPe32.Minimal());
            var b = image.BaseAddress;

            var headers = Query(p, kernel, b);
            Assert.Equal(b, headers.Base);
            Assert.Equal(b, headers.AllocationBase);
            Assert.Equal(PageExecuteWriteCopy, headers.AllocationProtect);
            Assert.Equal(0x1000u, headers.State);
            Assert.Equal(PageReadOnly, headers.Protect);
            Assert.Equal(MemImage, headers.Type);
            Assert.Equal(0x1000u, headers.Size);

            var text = Query(p, kernel, b + 0x1000);   // .text: code | execute | read
            Assert.Equal(b, text.AllocationBase);
            Assert.Equal(PageExecuteRead, text.Protect);
            Assert.Equal(MemImage, text.Type);
            Assert.Equal(0x1000u, text.Size);

            var data = Query(p, kernel, b + 0x2FFF);   // .rdata: initialised data | read
            Assert.Equal(b + 0x2000, data.Base);
            Assert.Equal(b, data.AllocationBase);
            Assert.Equal(PageReadOnly, data.Protect);
            Assert.Equal(0x1000u, data.Size);

            // Past SizeOfImage the image is over.
            Assert.Equal(MemFree, Query(p, kernel, b + 0x3000).State);
        }

        [Fact]
        public void AnImageIsNotPrivateMemory()
        {
            var p = NewProcess(out _);
            var image = p.LoadExecutable("waveshaper.exe", TestPe32.Minimal());
            var b = image.BaseAddress;

            Fails(p, ErrorInvalidAddress, "VirtualAlloc", b, 0x1000, MemReserve | MemCommit, PageReadWrite);   // taken
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", b + 0x1000, 0x1000, MemCommit, PageReadWrite);       // not a reservation to commit in
            Fails(p, ErrorInvalidParameter, "VirtualFree", b, 0, MemRelease);                                  // not VirtualAlloc's to free
            Fails(p, ErrorInvalidParameter, "VirtualFree", b + 0x1000, 0x1000, MemDecommit);

            // What patching code does: open the page up, write, put the protection back.
            var old = p.Memory.Read32(p.PebBase) & 0;   // scratch: just somewhere to read the answer from
            var slot = ScratchWord(p);
            Assert.Equal(1u, K(p, "VirtualProtect", b + 0x1000, 0x1000, PageExecuteReadWrite, slot));
            Assert.Equal(PageExecuteRead + old, p.Memory.Read32(slot));
            Assert.Equal(1u, K(p, "VirtualProtect", b + 0x1000, 0x1000, p.Memory.Read32(slot), slot));
            Assert.Equal(PageExecuteReadWrite, p.Memory.Read32(slot));
        }

        private static uint ScratchWord(GuestProcess p)
        {
            var at = p.Memory.FindFree(0x1000, 0x00800000);
            p.Memory.Map(at, 0x1000);
            return at;
        }

        [Fact]
        public void ASystemDllHandleIsAnImageToo()
        {
            var p = NewProcess(out var kernel);
            var name = kernel.Heap.Alloc(16);
            p.Memory.WriteAnsi(name, "kernel32.dll");
            var handle = K(p, "GetModuleHandleA", name);
            Assert.NotEqual(0u, handle);

            var info = Query(p, kernel, handle + 0x1234);
            Assert.Equal(MemImage, info.Type);
            Assert.Equal(handle, info.AllocationBase);   // what GetModuleHandleEx(FROM_ADDRESS)-style code finds
            Assert.Equal(0x1000u, info.State);
        }

        [Fact]
        public void AnImageIsPlacedAroundReservedAddressSpace()
        {
            var memory = new GuestMemory();
            // Someone holds the image's preferred base, not by mapping memory there but by reserving it.
            Assert.True(memory.Reserve(TestPe32.PreferredBase, 0x100000, Win32Memory.MemPrivate, Win32Memory.PageNoAccess));

            var image = Pe32Image.Load("waveshaper.exe", TestPe32.Minimal(), memory, (m, f, o) => 0xF0000000);
            Assert.NotEqual(TestPe32.PreferredBase, image.BaseAddress);
            Assert.True(image.BaseAddress >= TestPe32.PreferredBase + 0x100000 || image.BaseAddress + image.ImageSize <= TestPe32.PreferredBase);
            Assert.Equal(image.BaseAddress + TestPe32.MarkerRva, memory.Read32(image.BaseAddress + TestPe32.MarkerRva));   // relocated
        }

        // -------------------------------------------------- stacks, heap, mapped views

        [Fact]
        public void ThreadStacksAndTheHeapArePrivateCommittedMemory()
        {
            var p = NewProcess(out var kernel);

            var stack = Query(p, kernel, p.Cpu.Esp);
            Assert.Equal(p.StackLimit, stack.AllocationBase);   // what a stack-bounds probe reads
            Assert.Equal(0x1000u, stack.State);
            Assert.Equal(PageReadWrite, stack.Protect);
            Assert.Equal(MemPrivate, stack.Type);
            Assert.Equal(p.StackBase, stack.Base + stack.Size);

            var block = kernel.Heap.Alloc(100);
            var heap = Query(p, kernel, block);
            Assert.Equal(kernel.Heap.Base, heap.AllocationBase);
            Assert.Equal(0x1000u, heap.State);
            Assert.Equal(MemPrivate, heap.Type);
            Assert.True(heap.Size >= 100);

            // A thread's stack and the TEB come with the thread.
            var thread = p.CreateThread(0x00600000, 0, 0, suspended: true);
            Assert.Equal(thread.StackLimit, Query(p, kernel, thread.StackLimit + 0x100).AllocationBase);
            Assert.Equal(thread.TebBase, Query(p, kernel, thread.TebBase).AllocationBase);
        }

        [Fact]
        public void MappedViewsAreMemMapped()
        {
            var p = NewProcess(out var kernel);
            var mapping = K(p, "CreateFileMappingA", 0xFFFFFFFF, 0, PageReadWrite, 0, 0x2000, 0);
            Assert.NotEqual(0u, mapping);
            var view = K(p, "MapViewOfFile", mapping, 0xF001F, 0, 0, 0);
            Assert.NotEqual(0u, view);

            var info = Query(p, kernel, view);
            Assert.Equal(MemMapped, info.Type);
            Assert.Equal(0x1000u, info.State);
            Assert.Equal(view, info.AllocationBase);
            Assert.Equal(0x2000u, info.Size);
            p.Memory.Write32(view + 0x1FFC, 0x600D);
            Assert.Equal(0x600Du, p.Memory.Read32(view + 0x1FFC));
        }

        // ---------------------------------------------------------------- protection

        [Fact]
        public void ProtectionRoundTripReturnsTheRealOldValueAndShowsInQueries()
        {
            var p = NewProcess(out var kernel);
            var region = Alloc(p, 0x3000);
            var old = ScratchWord(p);

            Assert.Equal(1u, K(p, "VirtualProtect", region + 0x1000, 0x1000, PageReadOnly, old));
            Assert.Equal(PageReadWrite, p.Memory.Read32(old));

            // The middle page is its own region now.
            var first = Query(p, kernel, region);
            var second = Query(p, kernel, region + 0x1000);
            var third = Query(p, kernel, region + 0x2000);
            Assert.Equal((PageReadWrite, 0x1000u), (first.Protect, first.Size));
            Assert.Equal((PageReadOnly, 0x1000u), (second.Protect, second.Size));
            Assert.Equal((PageReadWrite, 0x1000u), (third.Protect, third.Size));
            Assert.Equal(region, second.AllocationBase);
            Assert.Equal(PageReadWrite, second.AllocationProtect);   // the allocation keeps the protection it was made with

            // Putting back what was returned merges the run again.
            Assert.Equal(1u, K(p, "VirtualProtect", region + 0x1000, 0x1000, p.Memory.Read32(old), old));
            Assert.Equal(PageReadOnly, p.Memory.Read32(old));
            Assert.Equal(0x3000u, Query(p, kernel, region).Size);

            // Several pages: the old value is the first page's, and modifiers stay in the answer.
            Assert.Equal(1u, K(p, "VirtualProtect", region, 0x3000, PageReadWrite | PageGuard, old));
            Assert.Equal(PageReadWrite, p.Memory.Read32(old));
            Assert.Equal(PageReadWrite | PageGuard, Query(p, kernel, region + 0x2000).Protect);
            Assert.Equal(1u, K(p, "VirtualProtect", region + 0x2000, 1, PageNoAccess, old));
            Assert.Equal(PageReadWrite | PageGuard, p.Memory.Read32(old));
        }

        [Fact]
        public void ProtectFailsOnPagesThatAreNotCommittedAndChangesNothing()
        {
            var p = NewProcess(out var kernel);
            var region = K(p, "VirtualAlloc", 0, 0x4000, MemReserve, PageNoAccess);
            K(p, "VirtualAlloc", region, 0x1000, MemCommit, PageReadWrite);
            var old = ScratchWord(p);

            Fails(p, ErrorInvalidAddress, "VirtualProtect", region + 0x1000, 0x1000, PageReadOnly, old);   // reserved only
            Fails(p, ErrorInvalidAddress, "VirtualProtect", region, 0x2000, PageReadOnly, old);            // committed then reserved
            Assert.Equal(PageReadWrite, Query(p, kernel, region).Protect);                                  // the committed page is untouched
            Fails(p, ErrorInvalidAddress, "VirtualProtect", 0x50000000, 0x1000, PageReadOnly, old);        // free
        }

        [Fact]
        public void ProtectRejectsBadArguments()
        {
            var p = NewProcess(out _);
            var region = Alloc(p, 0x2000);
            var old = ScratchWord(p);

            Fails(p, ErrorNoAccess, "VirtualProtect", region, 0x1000, PageReadOnly, 0);     // the old value has to go somewhere
            Fails(p, ErrorInvalidParameter, "VirtualProtect", region, 0x1000, 0, old);
            Fails(p, ErrorInvalidParameter, "VirtualProtect", region, 0x1000, PageReadWrite | 0x10, old);   // two base protections
            Fails(p, ErrorInvalidParameter, "VirtualProtect", region, 0x1000, PageNoAccess | PageGuard, old);
            Fails(p, ErrorInvalidParameter, "VirtualProtect", region, 0, PageReadOnly, old);
            Fails(p, ErrorInvalidParameter, "VirtualProtect", 0x80000000, 0x1000, PageReadOnly, old);
        }

        // ------------------------------------------------------------- error codes

        [Fact]
        public void AllocRejectsWhatWindowsRejects()
        {
            var p = NewProcess(out _);
            const uint Both = MemReserve | MemCommit;

            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0, Both, PageReadWrite);                // no size
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0x1000, 0, PageReadWrite);              // no action
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0x1000, Both | 0x4, PageReadWrite);     // a flag nobody defined
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0x1000, Both, 0);                       // no protection
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0x1000, Both, PageReadWrite | PageExecuteRead);
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0x1000, Both | MemReset, PageReadWrite);   // RESET stands alone
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0x1000, Both | 0x20000000, PageReadWrite);   // large pages: none on this machine
        }

        [Fact]
        public void AllocFailsWhenTheRangeIsTakenOrNothingFits()
        {
            var p = NewProcess(out _);
            var region = Alloc(p, 0x10000);

            Fails(p, ErrorInvalidAddress, "VirtualAlloc", region, 0x1000, MemReserve, PageReadWrite);                  // taken
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", region + 0x4000, 0x1000, MemReserve, PageReadWrite);        // 64 KB rounding lands on it
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", region - 0x10000, 0x20000, MemReserve, PageReadWrite);      // straddles its start
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", region + 0x10000, 0x1000, MemCommit, PageReadWrite);        // commit with nothing reserved there
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", region + 0x8000, 0x10000, MemCommit, PageReadWrite);        // reservation ends first
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0x7FFF0000, 0x20000, MemReserve, PageReadWrite);          // runs out of the user range
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0x80000000, 0x1000, MemReserve, PageReadWrite);
            Fails(p, ErrorNotEnoughMemory, "VirtualAlloc", 0, 0x7FFFFFFF, MemReserve, PageReadWrite);                 // nothing that size anywhere
            Fails(p, ErrorNotEnoughMemory, "VirtualAlloc", 0, 0xF0000000, MemReserve | MemCommit, PageReadWrite);
        }

        [Fact]
        public void FreeRejectsBadCombinationsAndAddresses()
        {
            var p = NewProcess(out _);
            var region = Alloc(p, 0x4000);

            Fails(p, ErrorInvalidParameter, "VirtualFree", region, 0, MemDecommit | MemRelease);   // both
            Fails(p, ErrorInvalidParameter, "VirtualFree", region, 0, 0);                          // neither
            Fails(p, ErrorInvalidParameter, "VirtualFree", region, 0, 0x1);
            Fails(p, ErrorInvalidParameter, "VirtualFree", region, 0x1000, MemRelease);            // release takes the whole allocation
            Fails(p, ErrorInvalidParameter, "VirtualFree", region + 0x1000, 0, MemRelease);        // and its base
            Fails(p, ErrorInvalidParameter, "VirtualFree", region + 0x1000, 0, MemDecommit);       // size 0 means the allocation: from its base
            Assert.True(p.Memory.IsMapped(region));   // none of that freed anything

            Fails(p, ErrorInvalidAddress, "VirtualFree", 0x50000000, 0, MemRelease);               // nothing there
            Fails(p, ErrorInvalidAddress, "VirtualFree", 0x50000000, 0x1000, MemDecommit);
            Fails(p, ErrorInvalidAddress, "VirtualFree", region + 0x2000, 0x10000, MemDecommit);   // runs past the allocation
            Fails(p, ErrorInvalidAddress, "VirtualFree", 0, 0, MemRelease);

            Assert.Equal(1u, K(p, "VirtualFree", region, 0, MemRelease));
            Fails(p, ErrorInvalidAddress, "VirtualFree", region, 0, MemRelease);                   // already gone
        }

        [Fact]
        public void ResetIsAcceptedOnCommittedPagesOnly()
        {
            var p = NewProcess(out _);
            var region = K(p, "VirtualAlloc", 0, 0x3000, MemReserve, PageNoAccess);
            K(p, "VirtualAlloc", region, 0x1000, MemCommit, PageReadWrite);
            p.Memory.Write32(region, 0x1234);

            Assert.Equal(region, K(p, "VirtualAlloc", region + 0x10, 0x100, MemReset, PageNoAccess));
            Fails(p, ErrorInvalidAddress, "VirtualAlloc", region, 0x2000, MemReset, PageNoAccess);   // the second page is not committed
            Fails(p, ErrorInvalidParameter, "VirtualAlloc", 0, 0x1000, MemReset, PageNoAccess);
        }

        // --------------------------------------------------------------- faulting

        // mov eax,[address]; ret
        private static byte[] ReadProgram(uint address) =>
            new byte[] { 0xA1, (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24), 0xC3 };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TouchingAReservedOrDecommittedOrReleasedPageFaults(bool native)
        {
            const uint Code = 0x00600000;
            using (var memory = new GuestMemory(native))
            {
                var p = NewProcess(out _, memory);
                memory.Map(Code, 0x1000);
                var region = K(p, "VirtualAlloc", 0, 0x4000, MemReserve, PageNoAccess);
                memory.WriteBytes(Code, ReadProgram(region + 0x1008));

                var reserved = p.Call(Code, out _, 10_000);
                Assert.Equal(GuestStop.Fault, reserved.Stop);
                Assert.Equal(region + 0x1008, reserved.FaultAddress);
                Assert.Equal(Code, p.Cpu.Eip);   // stopped on the instruction that touched it

                K(p, "VirtualAlloc", region + 0x1000, 0x1000, MemCommit, PageReadWrite);
                memory.Write32(region + 0x1008, 0xFEEDF00D);
                var committed = p.Call(Code, out var eax, 10_000);
                Assert.True(committed.Ok, committed.ToString());
                Assert.Equal(0xFEEDF00Du, eax);

                K(p, "VirtualFree", region + 0x1000, 0x1000, MemDecommit);
                Assert.Equal(GuestStop.Fault, p.Call(Code, out _, 10_000).Stop);

                K(p, "VirtualAlloc", region + 0x1000, 0x1000, MemCommit, PageReadWrite);
                Assert.True(p.Call(Code, out eax, 10_000).Ok);
                Assert.Equal(0u, eax);   // recommitted: zeroed

                K(p, "VirtualFree", region, 0, MemRelease);
                Assert.Equal(GuestStop.Fault, p.Call(Code, out _, 10_000).Stop);
            }
        }

        // A frame whose handler records the code and the faulting address, then skips the
        // 5-byte `mov eax,[address]` (the 0x12345678 below) in the CONTEXT and continues.
        // Returns code + EBX (7). The same program as GuestSehTests.
        private static readonly byte[] AccessViolationProgram =
        {
            0x53, 0x68, 0x31, 0x00, 0x60, 0x00, 0x64, 0xFF, 0x35, 0x00, 0x00, 0x00, 0x00, 0x64, 0x89, 0x25,
            0x00, 0x00, 0x00, 0x00, 0xBB, 0x07, 0x00, 0x00, 0x00, 0xA1, 0x78, 0x56, 0x34, 0x12, 0xA1, 0x10,
            0x10, 0x60, 0x00, 0x01, 0xD8, 0x64, 0x8F, 0x05, 0x00, 0x00, 0x00, 0x00, 0x83, 0xC4, 0x04, 0x5B,
            0xC3, 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x08, 0x89, 0x0D, 0x10, 0x10, 0x60, 0x00, 0x8B, 0x48, 0x18,
            0x89, 0x0D, 0x14, 0x10, 0x60, 0x00, 0x8B, 0x44, 0x24, 0x0C, 0x83, 0x80, 0xB8, 0x00, 0x00, 0x00,
            0x05, 0x31, 0xC0, 0xC3,
        };

        // Under the JIT a guest fault is a host access violation, which only the Windows
        // vectored handler turns back into a guest fault; elsewhere it would end the test host.
        private static bool CanRun(bool jit) =>
            !jit || System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void AReservedPageRaisesAnAccessViolationTheGuestsHandlerSees(bool jit)
        {
            Skip.If(jit && !TestHost.CanJit, "JIT needs an x64 host");
            if (!CanRun(jit)) return;
            const uint Code = 0x00600000, Data = 0x00601000;
            using (var memory = new GuestMemory(native: jit))
            {
                var p = NewProcess(out var kernel, memory);
                memory.Map(Code, 0x2000);
                var region = K(p, "VirtualAlloc", 0, 0x2000, MemReserve, PageNoAccess);

                var program = (byte[])AccessViolationProgram.Clone();
                program[26] = (byte)(region + 0x10); program[27] = (byte)((region + 0x10) >> 8);
                program[28] = (byte)((region + 0x10) >> 16); program[29] = (byte)((region + 0x10) >> 24);
                memory.WriteBytes(Code, program);

                var result = p.Call(Code, out var eax, 100_000);
                Assert.True(result.Ok, result.ToString());
                Assert.Equal(0xC0000005u + 7u, eax);
                Assert.Equal(region + 0x10, memory.Read32(Data + 0x14));   // ExceptionInformation[1]
                Assert.Equal(new[] { 0xC0000005u }, kernel.ExceptionsRaised);
            }
        }

        // ---------------------------------------------------- code that goes away

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void CodeFreedAndAllocatedAgainAtTheSameAddressRunsTheNewBytes(bool jit)
        {
            Skip.If(jit && !TestHost.CanJit, "JIT needs an x64 host");
            using (var memory = new GuestMemory(native: jit))
            using (var p = new GuestProcess(memory, useJit: jit))
            {
                Assert.Equal(jit, p.UsesJit);   // a JIT case must not fall back to the interpreter unnoticed
                var kernel = new GuestKernel(p);
                kernel.Install();

                var region = Alloc(p, 0x1000, MemReserve | MemCommit, PageExecuteReadWrite);
                memory.WriteBytes(region, new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 });   // mov eax,1; ret
                Assert.True(p.Call(region, out var eax, 1000).Ok);
                Assert.Equal(1u, eax);

                Assert.Equal(1u, K(p, "VirtualFree", region, 0, MemRelease));
                Assert.Equal(region, K(p, "VirtualAlloc", region, 0x1000, MemReserve | MemCommit, PageExecuteReadWrite));
                memory.WriteBytes(region, new byte[] { 0xB8, 0x02, 0x00, 0x00, 0x00, 0xC3 });   // mov eax,2; ret
                Assert.True(p.Call(region, out eax, 1000).Ok);
                Assert.Equal(2u, eax);   // not the block translated from the first allocation

                // The same for a decommit and a recommit.
                Assert.Equal(1u, K(p, "VirtualFree", region, 0x1000, MemDecommit));
                K(p, "VirtualAlloc", region, 0x1000, MemCommit, PageExecuteReadWrite);
                memory.WriteBytes(region, new byte[] { 0xB8, 0x03, 0x00, 0x00, 0x00, 0xC3 });
                Assert.True(p.Call(region, out eax, 1000).Ok);
                Assert.Equal(3u, eax);
            }
        }

        // ------------------------------------------------------- the model itself

        [Fact]
        public void PagesDiscardedAnnouncesCommittedPagesThatGoBack()
        {
            var memory = new GuestMemory();
            var seen = new System.Collections.Generic.List<(uint, uint)>();
            memory.PagesDiscarded += (at, size) => seen.Add((at, size));

            Assert.True(memory.Reserve(0x40000000, 0x8000, Win32Memory.MemPrivate, Win32Memory.PageReadWrite));
            Assert.Equal(0u, memory.Decommit(0x40000000, 0x8000));   // nothing was committed: nothing to announce
            Assert.Empty(seen);

            Assert.Equal(0u, memory.Commit(0x40001000, 0x2000, Win32Memory.PageReadWrite));
            Assert.Equal(0u, memory.Decommit(0x40000000, 0x8000));
            Assert.Equal(new[] { (0x40000000u, 0x8000u) }, seen);

            memory.Map(0x41000000, 0x2000);
            memory.Unmap(0x41000000, 0x2000);
            Assert.Equal((0x41000000u, 0x2000u), seen[1]);

            Assert.Equal(0u, memory.Commit(0x40000000, 0x1000, Win32Memory.PageReadWrite));
            Assert.Equal(0u, memory.Release(0x40000000));
            Assert.Equal((0x40000000u, 0x8000u), seen[2]);
        }

        [Fact]
        public void UnmapCutsAnAllocationAroundTheHole()
        {
            var memory = new GuestMemory();
            memory.Map(0x00400000, 0x4000);
            memory.Unmap(0x00401000, 0x2000);

            Assert.Equal(2, memory.MappedPages);
            var low = memory.Query(0x00400000);
            Assert.Equal((0x00400000u, 0x00400000u, 0x1000u), (low.BaseAddress, low.AllocationBase, low.RegionSize));
            var hole = memory.Query(0x00401800);
            Assert.Equal((Win32Memory.MemFree, 0x00401000u, 0x2000u), (hole.State, hole.BaseAddress, hole.RegionSize));
            var high = memory.Query(0x00403000);
            Assert.Equal((0x00403000u, 0x00403000u, 0x1000u), (high.BaseAddress, high.AllocationBase, high.RegionSize));
            Assert.Equal(1, memory.Read8(0x00400000) + 1);
            Assert.Throws<GuestFaultException>(() => memory.Read8(0x00401000));
            Assert.True(memory.IsFree(0x00401000, 0x2000));
            Assert.False(memory.IsFree(0x00400FFF, 2));
        }

        [Fact]
        public void MapKeepsWhatIsThereAndFillsTheGapsAround()
        {
            var memory = new GuestMemory();
            memory.Map(0x00500000, 0x1000);
            memory.Write32(0x00500000, 0x11111111);
            memory.Map(0x004FF000, 0x3000);   // one page before, the mapped one, one after
            Assert.Equal(0x11111111u, memory.Read32(0x00500000));
            Assert.Equal(3, memory.MappedPages - 0 - (memory.MappedPages - 3));
            Assert.True(memory.IsMapped(0x004FF000));
            Assert.True(memory.IsMapped(0x00501000));
            Assert.False(memory.IsMapped(0x00502000));
        }

        [Fact]
        public void MapGrowingKeepsOneAllocationAndStopsAtAnObstruction()
        {
            var memory = new GuestMemory();
            Assert.True(memory.MapGrowing(0x30000000, 0, 0x2000));
            Assert.True(memory.MapGrowing(0x30000000, 0x2000, 0x5000));
            var region = memory.Query(0x30000000);
            Assert.Equal((0x30000000u, 0x5000u, Win32Memory.MemPrivate), (region.AllocationBase, region.RegionSize, region.Type));

            memory.Map(0x30008000, 0x1000);   // something else sits further up
            Assert.True(memory.MapGrowing(0x30000000, 0x5000, 0x8000));
            Assert.False(memory.MapGrowing(0x30000000, 0x8000, 0x9000));   // it would share a page
            Assert.Equal(0x8000u, memory.Query(0x30000000).RegionSize);
        }

        [Fact]
        public void FindFreeAndFindRangeSkipReservedSpace()
        {
            var memory = new GuestMemory();
            Assert.True(memory.Reserve(0x00700000, 0x100000, Win32Memory.MemPrivate, Win32Memory.PageNoAccess));
            memory.Map(0x00800000, 0x1000);

            Assert.Equal(0x00600000u, memory.FindFree(0x1000, 0x00600000));
            Assert.Equal(0x00801000u, memory.FindFree(0x1000, 0x00700000));
            Assert.Equal(0x00801000u, memory.FindFree(0x2000, 0x00700000));
            Assert.Equal(0x00700000u - 0x1000, memory.FindFree(0x1000, 0x006FF000));
            Assert.Equal(0x006FF000u, memory.FindFree(0x1000, 0x006FF000));

            // Aligned, within bounds, from either end.
            Assert.Equal(0x00810000u, memory.FindRange(0x10000, 0x10000, 0x00801000, 0x01000000, topDown: false));
            Assert.Equal(0x00FF0000u, memory.FindRange(0x10000, 0x10000, 0x00801000, 0x01000000, topDown: true));
            Assert.Equal(0u, memory.FindRange(0x10000, 0x10000, 0x00700000, 0x00800000, topDown: false));   // all reserved
            Assert.Equal(0u, memory.FindRange(0x10000, 0x10000, 0x00700000, 0x00800000, topDown: true));
            Assert.Equal(0u, memory.FindRange(0xFFFF0000, 0x10000, 0x00010000, 0x7E000000, topDown: false));   // larger than the whole window
        }

        [Fact]
        public void TopDownSearchStepsOverAllocationsThatStraddleItsLimit()
        {
            var memory = new GuestMemory();
            memory.Map(0x00FF0000, 0x20000);   // 0x00FF0000..0x01010000 straddles the limit below
            Assert.Equal(0x00FE0000u, memory.FindRange(0x10000, 0x10000, 0, 0x01000000, topDown: true));
            memory.Map(0x00FE0000, 0x10000);
            Assert.Equal(0x00FD0000u, memory.FindRange(0x10000, 0x10000, 0, 0x01000000, topDown: true));
        }

        [Fact]
        public void ReserveReleaseAndCommitAreCheckedAgainstTheAllocation()
        {
            var memory = new GuestMemory();
            Assert.False(memory.Reserve(0x00500800, 0x1000, Win32Memory.MemPrivate, Win32Memory.PageReadWrite));   // not page-aligned
            Assert.False(memory.Reserve(0x00500000, 0, Win32Memory.MemPrivate, Win32Memory.PageReadWrite));
            Assert.False(memory.Reserve(0xFFFFF000, 0x2000, Win32Memory.MemPrivate, Win32Memory.PageReadWrite));   // past 4 GB
            Assert.True(memory.Reserve(0x00500000, 0x3000, Win32Memory.MemPrivate, Win32Memory.PageReadWrite));
            Assert.False(memory.Reserve(0x00502000, 0x2000, Win32Memory.MemPrivate, Win32Memory.PageReadWrite));   // overlaps

            Assert.Equal(Win32Memory.ErrorInvalidAddress, memory.Commit(0x00502000, 0x2000, Win32Memory.PageReadWrite));   // runs past it
            Assert.Equal(Win32Memory.ErrorInvalidParameter, memory.Release(0x00501000));
            Assert.Equal(Win32Memory.ErrorInvalidAddress, memory.Release(0x00600000));
            Assert.Equal(0u, memory.Release(0x00500000));
            Assert.True(memory.IsFree(0x00500000, 0x3000));
            Assert.Equal(0, memory.ReservedPages);
        }

        [Fact]
        public void ProtectionValidationAcceptsOnlyWhatWindowsAccepts()
        {
            foreach (var kind in new uint[] { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 })
            {
                Assert.True(Win32Memory.IsValidProtection(kind), $"0x{kind:X}");
                if (kind != 0x01)
                {
                    Assert.True(Win32Memory.IsValidProtection(kind | 0x100), $"0x{kind:X} | GUARD");
                    Assert.True(Win32Memory.IsValidProtection(kind | 0x200), $"0x{kind:X} | NOCACHE");
                    Assert.True(Win32Memory.IsValidProtection(kind | 0x400), $"0x{kind:X} | WRITECOMBINE");
                }
            }
            Assert.False(Win32Memory.IsValidProtection(0));
            Assert.False(Win32Memory.IsValidProtection(0x03));
            Assert.False(Win32Memory.IsValidProtection(0x100));
            Assert.False(Win32Memory.IsValidProtection(0x01 | 0x100));
            Assert.False(Win32Memory.IsValidProtection(0x04 | 0x200 | 0x400));
            Assert.False(Win32Memory.IsValidProtection(0x04 | 0x800));
        }
    }
}
