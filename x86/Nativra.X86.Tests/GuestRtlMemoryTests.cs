using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestRtlMemoryTests
    {
        [Fact]
        public void ComparisonReturnsMatchingPrefixLengthRatherThanSortOrder()
        {
            using var process = new GuestProcess(new GuestMemory(), useJit: false);
            var kernel = new GuestKernel(process);
            kernel.Install();
            var left = kernel.Heap.Alloc(8);
            var right = kernel.Heap.Alloc(8);
            process.Memory.WriteBytes(left, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            process.Memory.WriteBytes(right, new byte[] { 1, 2, 3, 9, 5, 6, 7, 8 });
            Assert.Equal(3u, Call(process, "RtlCompareMemory", left, right, 8));
            Assert.Equal(2u, Call(process, "RtlCompareMemory", left, right, 2));
            Assert.Equal(8u, Call(process, "RtlCompareMemory", left, left, 8));
            Assert.Equal(0u, Call(process, "RtlCompareMemory", 0, 0, 0));
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(3u)]
        [InlineData(4u)]
        [InlineData(7u)]
        [InlineData(8u)]
        [InlineData(11u)]
        public void UlongOperationsIgnoreTrailingBytesAndPreserveCanaries(uint length)
        {
            using var process = new GuestProcess(new GuestMemory(), useJit: false);
            var kernel = new GuestKernel(process);
            kernel.Install();
            var block = kernel.Heap.Alloc(16);
            for (uint offset = 0; offset < 16; offset++) process.Memory.Write8(block + offset, 0xCC);
            Call(process, "RtlFillMemoryUlong", block + 1, length, 0x12345678);
            var complete = length & ~3u;
            Assert.Equal(0xCC, process.Memory.Read8(block));
            for (uint offset = complete + 1; offset < 16; offset++)
                Assert.Equal(0xCC, process.Memory.Read8(block + offset));
            Assert.Equal(complete, Call(process, "RtlCompareMemoryUlong", block + 1, length, 0x12345678));
            if (complete >= 4)
            {
                process.Memory.Write32(block + 1, 0x12345679);
                Assert.Equal(0u, Call(process, "RtlCompareMemoryUlong", block + 1, length, 0x12345678));
            }
            Assert.Equal(0u, Call(process, "RtlCompareMemoryUlong", 0, 3, 0));
            Call(process, "RtlFillMemoryUlong", 0, 3, 0);
        }

        private static uint Call(GuestProcess process, string name, params uint[] args)
        {
            var result = process.Call(process.Imports.Bind("ntdll.dll", name, -1), out var value, 100000, args);
            Assert.True(result.Ok, name + ": " + result);
            return value;
        }
    }
}
