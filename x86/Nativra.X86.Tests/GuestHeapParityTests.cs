using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestHeapParityTests
    {
        [Fact]
        public void OversizedAllocationCannotWrapOrReplaceAnExistingBlock()
        {
            using var memory = new GuestMemory();
            var heap = new GuestHeap(memory, 0x30000000, 0x10000);
            var block = heap.Alloc(4);
            memory.Write32(block, 0x12345678);
            Assert.Equal(0u, heap.Alloc(uint.MaxValue));
            Assert.Equal(0u, heap.ReAlloc(block, uint.MaxValue));
            Assert.Equal(4u, heap.RequestedSizeOf(block));
            Assert.Equal(0x12345678u, memory.Read32(block));
        }

        [Fact]
        public void ZeroedGrowthUsesRequestedSizeEvenWithinAlignmentPadding()
        {
            using var memory = new GuestMemory();
            var heap = new GuestHeap(memory, 0x30000000, 0x10000);
            var block = heap.Alloc(3);
            for (uint i = 0; i < 16; i++) memory.Write8(block + i, 0xAB);
            Assert.Equal(block, heap.ReAlloc(block, 8, zero: true));
            for (uint i = 0; i < 3; i++) Assert.Equal(0xAB, memory.Read8(block + i));
            for (uint i = 3; i < 8; i++) Assert.Equal(0, memory.Read8(block + i));
            Assert.Equal(0xAB, memory.Read8(block + 8));
            var reused = heap.Alloc(64);
            for (uint i = 0; i < 64; i++) memory.Write8(reused + i, 0xCD);
            heap.Free(reused);
            var moved = heap.ReAlloc(block, 64, zero: true);
            Assert.Equal(reused, moved);
            for (uint i = 8; i < 64; i++) Assert.Equal(0, memory.Read8(moved + i));
        }

        [Fact]
        public void InPlaceFailurePreservesTheOriginalAllocation()
        {
            using var memory = new GuestMemory();
            var heap = new GuestHeap(memory, 0x30000000, 0x10000);
            var block = heap.Alloc(16);
            memory.Write32(block, 0x12345678);
            Assert.Equal(0u, heap.ReAlloc(block, 64, inPlaceOnly: true));
            Assert.Equal(16u, heap.RequestedSizeOf(block));
            Assert.Equal(0x12345678u, memory.Read32(block));
        }

        [Fact]
        public void Kernel32AndNtdllOperateOnTheSameAllocations()
        {
            using var process = new GuestProcess(new GuestMemory(), useJit: false);
            var kernel = new GuestKernel(process);
            kernel.Install();
            uint Call(string dll, string name, params uint[] args)
            {
                var result = process.Call(process.Imports.Bind(dll, name, -1), out var value, 100000, args);
                Assert.True(result.Ok, name + ": " + result);
                return value;
            }
            var handle = Call("kernel32.dll", "GetProcessHeap");
            var block = Call("ntdll.dll", "RtlAllocateHeap", handle, 8, 7);
            Assert.NotEqual(0u, block);
            Assert.Equal(7u, Call("kernel32.dll", "HeapSize", handle, 0, block));
            Assert.Equal(1u, Call("ntdll.dll", "RtlValidateHeap", handle, 0, block));
            Assert.Equal(0u, Call("ntdll.dll", "RtlReAllocateHeap", handle, 0x10, block, 256));
            Assert.Equal(7u, Call("ntdll.dll", "RtlSizeHeap", handle, 0, block));
            Assert.Equal(0u, Call("kernel32.dll", "HeapReAlloc", handle, 0, 0, 8));
            Assert.Equal(1u, Call("kernel32.dll", "HeapFree", handle, 0, block));
            Assert.Equal(uint.MaxValue, Call("ntdll.dll", "RtlSizeHeap", handle, 0, block));
            Assert.Equal(0u, Call("kernel32.dll", "HeapValidate", handle, 0, block));
            Assert.Equal(0u, Call("ntdll.dll", "RtlAllocateHeap", 0xBAD, 0, 8));
            var output = kernel.Heap.Alloc(8, zero: true);
            Assert.Equal(1u, Call("ntdll.dll", "RtlGetProcessHeaps", 0, output));
            Assert.Equal(0u, process.Memory.Read32(output));
            Assert.Equal(1u, Call("kernel32.dll", "GetProcessHeaps", 2, output));
            Assert.Equal(handle, process.Memory.Read32(output));
            Assert.Equal(0u, process.Memory.Read32(output + 4));
        }
    }
}
