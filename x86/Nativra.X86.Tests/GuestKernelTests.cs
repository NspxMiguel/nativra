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

        private static uint Ansi(GuestKernel kernel, GuestProcess p, string text)
        {
            var ptr = kernel.Heap.Alloc((uint)text.Length + 1);
            p.Memory.WriteAnsi(ptr, text);
            return ptr;
        }

        [Fact]
        public void GetModuleHandleAnswersForTheLoadedImageAndByName()
        {
            var p = NewProcess(out var kernel);
            var image = p.LoadExecutable("waveshaper.exe", TestPe32.Minimal());

            Assert.Equal(image.BaseAddress, CallK(p, "GetModuleHandleA", 0)); // null -> the exe
            Assert.Equal(image.BaseAddress,
                CallK(p, "GetModuleHandleA", Ansi(kernel, p, "C:\\Games\\WaveShaper.exe"))); // path, any case

            // kernel32 is in every process; a DLL nothing references is not.
            Assert.NotEqual(0u, CallK(p, "GetModuleHandleA", Ansi(kernel, p, "KERNEL32")));
            Assert.Equal(0u, CallK(p, "GetModuleHandleA", Ansi(kernel, p, "nothere.dll")));
            Assert.Equal(126u, CallK(p, "GetLastError"));   // ERROR_MOD_NOT_FOUND
        }

        [Fact]
        public void ExitProcessEndsTheRunWithItsCode()
        {
            var p = NewProcess(out _);
            var sentinel = p.Imports.Bind("kernel32.dll", "ExitProcess", -1);
            var result = p.Call(sentinel, out _, 1000, 42);
            Assert.Equal(GuestStop.Exited, result.Stop);
            Assert.Equal(42u, result.ExitCode);
        }

        [Fact]
        public void LoadLibraryMapsAGameDllRunsDllMainAndGetProcAddressFindsItsExport()
        {
            var p = NewProcess(out var kernel);
            var dllBytes = TestPe32.Minimal(dll: true, dllMain: true);
            p.ModuleSource = name => name == "fmod.dll" ? dllBytes : null;
            p.LoadExecutable("waveshaper.exe", TestPe32.Minimal());

            var module = CallK(p, "LoadLibraryA", Ansi(kernel, p, "fmod"));   // ".dll" implied
            var dll = p.FindModule("fmod.dll");
            Assert.NotNull(dll);
            Assert.Equal(dll.BaseAddress, module);
            Assert.Equal(TestPe32.DllMainMark, p.Memory.Read32(dll.BaseAddress + TestPe32.DllMainMarkRva));

            Assert.Equal(dll.Export("Start"), CallK(p, "GetProcAddress", module, Ansi(kernel, p, "Start")));
            Assert.Equal(dll.Export("Start"), CallK(p, "GetProcAddress", module, 1));   // by ordinal
        }

        [Fact]
        public void GetProcAddressOnASystemDllServesOnlyWhatTheHostImplements()
        {
            var p = NewProcess(out var kernel);
            var kernel32 = CallK(p, "LoadLibraryA", Ansi(kernel, p, "kernel32.dll"));
            Assert.NotEqual(0u, kernel32);

            // HeapAlloc has a handler: the address it hands back is callable.
            var heapAlloc = CallK(p, "GetProcAddress", kernel32, Ansi(kernel, p, "HeapAlloc"));
            Assert.True(GuestImports.InRegion(heapAlloc));
            var result = p.Call(heapAlloc, out var ptr, 1000, 0, 0, 64);
            Assert.True(result.Ok);
            Assert.True(kernel.Heap.Owns(ptr));

            // CreateToolhelp32Snapshot has none: the program is told it does not exist, and we note it.
            Assert.Equal(0u, CallK(p, "GetProcAddress", kernel32, Ansi(kernel, p, "CreateToolhelp32Snapshot")));
            Assert.Equal(127u, CallK(p, "GetLastError"));   // ERROR_PROC_NOT_FOUND
            Assert.Contains("kernel32.dll!CreateToolhelp32Snapshot", kernel.ProbedAbsent);
        }

        [Fact]
        public void GetModuleFileNameReportsTheProgramPathTruncatedToTheBuffer()
        {
            var p = NewProcess(out var kernel);
            kernel.ExePath = "E:\\Games\\WAVESHAPER\\WAVESHAPER.exe";
            p.LoadExecutable("waveshaper.exe", TestPe32.Minimal());

            var buffer = kernel.Heap.Alloc(260);
            Assert.Equal((uint)kernel.ExePath.Length, CallK(p, "GetModuleFileNameA", 0, buffer, 260));
            Assert.Equal(kernel.ExePath, p.Memory.ReadAnsi(buffer));

            Assert.Equal(7u, CallK(p, "GetModuleFileNameA", 0, buffer, 8));   // 7 chars + NUL
            Assert.Equal("E:\\Game", p.Memory.ReadAnsi(buffer));
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
