using System;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestRtlStringTests : IDisposable
    {
        private readonly GuestProcess process = new GuestProcess(new GuestMemory(), useJit: false);
        private readonly GuestKernel kernel;
        public GuestRtlStringTests()
        {
            kernel = new GuestKernel(process);
            kernel.Install();
        }
        public void Dispose() => process.Dispose();
        private GuestMemory Memory => process.Memory;
        private uint Allocate(uint size) => kernel.Heap.Alloc(size, zero: true);
        private uint Call(string name, params uint[] args)
        {
            var result = process.Call(process.Imports.Bind("ntdll.dll", name, -1), out var value, 100000, args);
            Assert.True(result.Ok, name + ": " + result);
            return value;
        }
        private uint Descriptor(uint buffer, ushort length, ushort capacity)
        {
            var descriptor = Allocate(8);
            Memory.Write16(descriptor, length);
            Memory.Write16(descriptor + 2, capacity);
            Memory.Write32(descriptor + 4, buffer);
            return descriptor;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void InitializationBorrowsTheSourceAndCountsBytes(bool wide)
        {
            var source = Allocate(16);
            if (wide) Memory.WriteUnicode(source, "AB");
            else Memory.WriteBytes(source, new byte[] { 65, 66, 0 });
            var target = Allocate(8);
            var name = "RtlInit" + (wide ? "Unicode" : "Ansi") + "String";
            Call(name, target, source);
            Assert.Equal(wide ? 4 : 2, Memory.Read16(target));
            Assert.Equal(wide ? 6 : 3, Memory.Read16(target + 2));
            Assert.Equal(source, Memory.Read32(target + 4));
            Call(name, target, 0);
            Assert.Equal(0ul, Memory.Read64(target));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExtendedInitializationRejectsOverflowWithoutChangingTheDescriptor(bool wide)
        {
            var source = Allocate(65536);
            for (uint offset = 0; offset < 65535; offset++) Memory.Write8(source + offset, 65);
            var target = Allocate(8);
            Memory.Write64(target, 0x1122334455667788);
            Assert.Equal(0xC0000106u, Call("RtlInit" + (wide ? "Unicode" : "Ansi") + "StringEx", target, source));
            Assert.Equal(0x1122334455667788ul, Memory.Read64(target));
        }

        [Fact]
        public void AllocatedConversionPreservesEmbeddedNullsAndUsesTheGuestHeap()
        {
            var source = Allocate(4);
            Memory.WriteBytes(source, new byte[] { 0x80, 0, 0xE9, 0xFF });
            var ansi = Descriptor(source, 3, 3);
            var unicode = Allocate(8);
            Assert.Equal(0u, Call("RtlAnsiStringToUnicodeString", unicode, ansi, 1));
            Assert.Equal(6, Memory.Read16(unicode));
            Assert.Equal(8, Memory.Read16(unicode + 2));
            var text = Memory.Read32(unicode + 4);
            Assert.Equal(new byte[] { 0xAC, 0x20, 0, 0, 0xE9, 0, 0, 0 }, Memory.ReadBytes(text, 8));
            Assert.Equal(8u, kernel.Heap.RequestedSizeOf(text));
            var back = Allocate(8);
            Assert.Equal(0u, Call("RtlUnicodeStringToAnsiString", back, unicode, 1));
            var backBuffer = Memory.Read32(back + 4);
            Assert.Equal(new byte[] { 0x80, 0, 0xE9, 0 }, Memory.ReadBytes(backBuffer, 4));
            Call("RtlFreeUnicodeString", unicode);
            Call("RtlFreeAnsiString", back);
            Assert.Equal(0ul, Memory.Read64(unicode));
            Assert.Equal(0ul, Memory.Read64(back));
            Assert.Equal(uint.MaxValue, kernel.Heap.RequestedSizeOf(text));
            Assert.Equal(uint.MaxValue, kernel.Heap.RequestedSizeOf(backBuffer));
            Assert.Equal(4u, kernel.Heap.RequestedSizeOf(source));
        }

        [Fact]
        public void ShortUnicodeDestinationIsUntouchedButAnsiReceivesATerminatedPrefix()
        {
            var text = Allocate(16);
            Memory.WriteUnicode(text, "ABCD");
            var unicode = Descriptor(text, 8, 10);
            var bytes = Allocate(16);
            Memory.Write64(bytes, 0xCCCCCCCCCCCCCCCC);
            var ansi = Descriptor(bytes, 0, 3);
            Assert.Equal(0x80000005u, Call("RtlUnicodeStringToAnsiString", ansi, unicode, 0));
            Assert.Equal(2, Memory.Read16(ansi));
            Assert.Equal(new byte[] { 65, 66, 0, 0xCC }, Memory.ReadBytes(bytes, 4));
            Memory.Write64(text, 0xCCCCCCCCCCCCCCCC);
            Memory.Write16(unicode + 2, 4);
            Assert.Equal(0x80000005u, Call("RtlAnsiStringToUnicodeString", unicode, ansi, 0x100));
            Assert.Equal(4, Memory.Read16(unicode));
            Assert.Equal(0xCCCCCCCCCCCCCCCCul, Memory.Read64(text));
        }

        [Fact]
        public void EmptyCountedSourceNeedsOnlyATerminatorAndNoReadableSourcePointer()
        {
            var source = Descriptor(0, 0, 0);
            var destination = Allocate(8);
            Assert.Equal(0u, Call("RtlAnsiStringToUnicodeString", destination, source, 1));
            Assert.Equal(0, Memory.Read16(destination));
            Assert.Equal(2, Memory.Read16(destination + 2));
            Assert.Equal(0, Memory.Read16(Memory.Read32(destination + 4)));
        }
    }
}
