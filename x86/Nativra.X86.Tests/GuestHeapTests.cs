using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>The guest heap allocator on its own: distinct blocks, reuse, grow, zero.</summary>
    public sealed class GuestHeapTests
    {
        private const uint Base = 0x30000000;

        private static GuestHeap Fresh(out GuestMemory memory)
        {
            memory = new GuestMemory();
            return new GuestHeap(memory, Base, 0x00100000);
        }

        [Fact]
        public void AllocationsAreDistinctAndWritable()
        {
            var heap = Fresh(out var memory);
            var a = heap.Alloc(32);
            var b = heap.Alloc(32);
            Assert.NotEqual(0u, a);
            Assert.NotEqual(a, b);
            Assert.True(b >= a + 32, "blocks overlap");

            memory.Write32(a, 0x11223344);
            memory.Write32(b, 0x55667788);
            Assert.Equal(0x11223344u, memory.Read32(a));
            Assert.Equal(0x55667788u, memory.Read32(b));
        }

        [Fact]
        public void FreedBlocksAreReused()
        {
            var heap = Fresh(out _);
            var a = heap.Alloc(48);
            Assert.True(heap.Free(a));
            var b = heap.Alloc(48);
            Assert.Equal(a, b);   // first-fit reuse of the freed block
        }

        [Fact]
        public void ZeroInitialisesOnRequest()
        {
            var heap = Fresh(out var memory);
            var a = heap.Alloc(16);
            memory.Write32(a, 0xFFFFFFFF);
            heap.Free(a);
            var b = heap.Alloc(16, zero: true);
            Assert.Equal(a, b);
            Assert.Equal(0u, memory.Read32(b));
        }

        [Fact]
        public void ReAllocGrowsAndKeepsContents()
        {
            var heap = Fresh(out var memory);
            var a = heap.Alloc(16);
            for (uint i = 0; i < 16; i++) memory.Write8(a + i, (byte)(i + 1));

            var grown = heap.ReAlloc(a, 128);
            Assert.NotEqual(0u, grown);
            for (uint i = 0; i < 16; i++) Assert.Equal((byte)(i + 1), memory.Read8(grown + i));
            Assert.True(heap.SizeOf(grown) >= 128);
        }

        [Fact]
        public void ReturnsZeroWhenExhausted()
        {
            var memory = new GuestMemory();
            var heap = new GuestHeap(memory, Base, 0x1000);
            Assert.NotEqual(0u, heap.Alloc(0x800));
            Assert.Equal(0u, heap.Alloc(0x4000));   // larger than the whole region
        }
    }
}
