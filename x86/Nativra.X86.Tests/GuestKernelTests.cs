using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// The baseline kernel surface, exercised the way a game reaches it: through
    /// the import table. Calling an import's sentinel directly makes the run loop
    /// dispatch it, so these check the real registered handlers and their stack
    /// conventions, not the C# methods in isolation.
    /// </summary>
    public sealed class GuestKernelTests
    {
        private const uint HeapZeroMemory = 0x8;

        private static GuestProcess NewProcess(out GuestKernel kernel)
        {
            var p = new GuestProcess(new GuestMemory(), useJit: false);
            kernel = new GuestKernel(p);
            kernel.Install();
            return p;
        }

        private static uint CallK(GuestProcess p, string function, params uint[] args)
        {
            var sentinel = p.Imports.Bind("kernel32.dll", function, -1);
            var result = p.Call(sentinel, out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result.Stop} @ 0x{result.FaultAddress:X8}");
            return eax;
        }

        [Fact]
        public void HeapAllocReturnsWritableZeroedGuestMemory()
        {
            var p = NewProcess(out var kernel);
            var heapHandle = CallK(p, "GetProcessHeap");
            var ptr = CallK(p, "HeapAlloc", heapHandle, HeapZeroMemory, 256);

            Assert.NotEqual(0u, ptr);
            Assert.True(kernel.Heap.Owns(ptr));
            Assert.Equal(0u, p.Memory.Read32(ptr));   // HEAP_ZERO_MEMORY honoured
            p.Memory.Write32(ptr, 0xCAFEF00D);
            Assert.Equal(0xCAFEF00Du, p.Memory.Read32(ptr));

            Assert.Equal(1u, CallK(p, "HeapFree", heapHandle, 0, ptr));
        }

        [Fact]
        public void LastErrorRoundTrips()
        {
            var p = NewProcess(out _);
            CallK(p, "SetLastError", 0x57);   // ERROR_INVALID_PARAMETER
            Assert.Equal(0x57u, CallK(p, "GetLastError"));
        }

        [Fact]
        public void TlsAllocGivesAnIndexThatStoresAndReturnsAValue()
        {
            var p = NewProcess(out _);
            var slot = CallK(p, "TlsAlloc");
            Assert.NotEqual(0xFFFFFFFFu, slot);

            Assert.Equal(1u, CallK(p, "TlsSetValue", slot, 0xABCD1234));
            Assert.Equal(0xABCD1234u, CallK(p, "TlsGetValue", slot));
        }

        [Fact]
        public void VirtualAllocMapsUsableGuestPages()
        {
            var p = NewProcess(out _);
            var region = CallK(p, "VirtualAlloc", 0, 0x2000, 0x3000 /*COMMIT|RESERVE*/, 0x04);
            Assert.NotEqual(0u, region);
            p.Memory.Write32(region + 0x1FFC, 0x12345678);
            Assert.Equal(0x12345678u, p.Memory.Read32(region + 0x1FFC));
        }

        [Fact]
        public void GetModuleHandleAnswersForTheLoadedImageAndByName()
        {
            var p = NewProcess(out var kernel);
            var image = p.LoadImage("waveshaper.exe", TestPe32.Minimal());
            kernel.RegisterModule("waveshaper.exe", image.BaseAddress);

            Assert.Equal(image.BaseAddress, CallK(p, "GetModuleHandleA", 0)); // null -> the exe

            var namePtr = kernel.Heap.Alloc(32);
            p.Memory.WriteAnsi(namePtr, "WaveShaper.exe");
            Assert.Equal(image.BaseAddress, CallK(p, "GetModuleHandleA", namePtr)); // case-insensitive
        }

        [Fact]
        public void CommandLineIsReadableInBothWidths()
        {
            var p = NewProcess(out var kernel);
            kernel.SetCommandLine("waveshaper.exe -foo");
            var ansi = CallK(p, "GetCommandLineA");
            var wide = CallK(p, "GetCommandLineW");
            Assert.Equal("waveshaper.exe -foo", p.Memory.ReadAnsi(ansi));
            Assert.Equal("waveshaper.exe -foo", p.Memory.ReadUnicode(wide));
        }
    }
}
