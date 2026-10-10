using System;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class GuestWinrtTests
    {
        private const string Strings = "api-ms-win-core-winrt-string-l1-1-0.dll";
        private const string Runtime = "api-ms-win-core-winrt-l1-1-0.dll";
        private static uint Call(GuestProcess p, string module, string name, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind(module, name, -1), out var eax, 100000, args);
            Assert.True(result.Ok, result.ToString());
            return eax;
        }

        [Fact]
        public void WinrtApiSetsUseCombaseAndBalanceTheComApartment()
        {
            using (var p = new GuestProcess(new GuestMemory(), useJit: false))
            {
                new GuestKernel(p).Install();
                Assert.Equal("combase.dll", GuestImports.Canonical(Runtime));
                Assert.Equal("combase.dll", GuestImports.Canonical(Strings));
                Assert.Equal("kernel32.dll", GuestImports.Canonical("api-ms-win-core-synch-l1-2-0.dll"));
                Assert.Equal(0x80070057u, Call(p, Runtime, "RoInitialize", 2));
                Assert.Equal(0u, Call(p, Runtime, "RoInitialize", 1));
                Assert.Equal(1u, Call(p, "ole32.dll", "CoInitializeEx", 0, 0));
                Assert.Equal(0x80010106u, Call(p, Runtime, "RoInitialize", 0));
                Call(p, Runtime, "RoUninitialize");
                Assert.Equal(0x80010106u, Call(p, Runtime, "RoInitialize", 0));
                Call(p, "ole32.dll", "CoUninitialize");
                Assert.Equal(0u, Call(p, Runtime, "RoInitialize", 0));
                Assert.Equal(1u, Call(p, "ole32.dll", "CoInitialize", 0));
            }
        }

        [Fact]
        public void GuestThreadsHaveIndependentApartmentModes()
        {
            using (var p = new GuestProcess(new GuestMemory(), useJit: false))
            {
                new GuestKernel(p).Install();
                Assert.Equal(0u, Call(p, Runtime, "RoInitialize", 0));
                const uint code = 0x00600000, data = 0x00601000;
                p.Memory.Map(code, 0x2000);
                p.Memory.Write32(data, p.Imports.Bind(Runtime, "RoInitialize", -1));
                p.Memory.Write32(data + 4, 0xFFFFFFFF);
                // worker: RoInitialize(MTA); store HRESULT; return.
                p.Memory.WriteBytes(code, new byte[] { 0x6A, 1, 0xFF, 0x15, 0, 0x10, 0x60, 0, 0xA3, 4, 0x10, 0x60, 0, 0xC2, 4, 0 });
                var thread = Call(p, "kernel32.dll", "CreateThread", 0, 0, code, 0, 0, 0);
                Assert.NotEqual(0u, thread);
                Assert.Equal(0u, Call(p, "kernel32.dll", "WaitForSingleObject", thread, 0xFFFFFFFF));
                Assert.Equal(0u, p.Memory.Read32(data + 4));
                Assert.Equal(1u, Call(p, Runtime, "RoInitialize", 0));
            }
        }

        [Fact]
        public void OwnedStringsPreserveEmbeddedNullsAndStayAliveUntilLastRelease()
        {
            using (var p = new GuestProcess(new GuestMemory(), useJit: false))
            {
                var k = new GuestKernel(p);
                k.Install();
                var source = k.Heap.Alloc(16);
                var output = k.Heap.Alloc(32);
                p.Memory.WriteUnicode(source, "A\0B");
                Assert.Equal(0u, Call(p, Strings, "WindowsCreateString", source, 3, output));
                var value = p.Memory.Read32(output);
                var buffer = Call(p, Strings, "WindowsGetStringRawBuffer", value, output + 8);
                Assert.NotEqual(source, buffer);
                Assert.Equal(3u, p.Memory.Read32(output + 8));
                Assert.Equal((ushort)'B', p.Memory.Read16(buffer + 4));
                Assert.Equal(0, p.Memory.Read16(buffer + 6));
                Assert.Equal(0u, Call(p, Strings, "WindowsDuplicateString", value, output + 4));
                Assert.Equal(value, p.Memory.Read32(output + 4));
                Assert.Equal(0u, Call(p, Strings, "WindowsDeleteString", value));
                Assert.True(k.Heap.Owns(buffer));
                Assert.Equal(3u, Call(p, Strings, "WindowsGetStringLen", value));
                Assert.Equal(0u, Call(p, Strings, "WindowsDeleteString", value));
                Assert.Equal(0u, k.Heap.SizeOf(buffer));
                Assert.Equal(0u, k.Heap.SizeOf(value));
                Assert.Equal(0u, Call(p, Strings, "WindowsCreateString", 0, 0, output));
                Assert.Equal(0u, p.Memory.Read32(output));
                Assert.Equal(0x80004003u, Call(p, Strings, "WindowsCreateString", 0, 1, output));
                Assert.Equal(0x80070057u, Call(p, Strings, "WindowsCreateString", source, 1, 0));
            }
        }

        [Fact]
        public void FastPassDuplicatesCopyTheBackingBufferAndActivationReturnsNoObject()
        {
            using (var p = new GuestProcess(new GuestMemory(), useJit: false))
            {
                var k = new GuestKernel(p);
                k.Install();
                var source = k.Heap.Alloc(32);
                var header = k.Heap.Alloc(24);
                var output = k.Heap.Alloc(32);
                p.Memory.WriteUnicode(source, "Pad");
                p.Memory.Write32(header + 20, 0xC0FFEE);
                Assert.Equal(0u, Call(p, Strings, "WindowsCreateStringReference", source, 3, header, output));
                var reference = p.Memory.Read32(output);
                Assert.Equal(header, reference);
                Assert.Equal(source, Call(p, Strings, "WindowsGetStringRawBuffer", reference, 0));
                Assert.Equal(0xC0FFEEu, p.Memory.Read32(header + 20));
                Assert.Equal(0u, Call(p, Strings, "WindowsDuplicateString", reference, output + 4));
                var owned = p.Memory.Read32(output + 4);
                var buffer = Call(p, Strings, "WindowsGetStringRawBuffer", owned, 0);
                Assert.NotEqual(source, buffer);
                Assert.Equal(0u, Call(p, Strings, "WindowsCompareStringOrdinal", reference, owned, output + 8));
                Assert.Equal(0u, p.Memory.Read32(output + 8));
                p.Memory.WriteUnicode(source, "Bad");
                Assert.Equal("Pad", p.Memory.ReadUnicode(buffer));
                Assert.Equal(0u, Call(p, Strings, "WindowsDeleteString", reference));
                Assert.True(k.Heap.Owns(source));
                Assert.True(k.Heap.Owns(header));
                p.Memory.Write16(source + 6, (ushort)'X');
                Assert.Equal(0x80070057u, Call(p, Strings, "WindowsCreateStringReference", source, 3, header, output));
                Assert.Equal(0u, p.Memory.Read32(output));
                Assert.Equal(0x80040154u, Call(p, Runtime, "RoGetActivationFactory", owned, 0, output));
                Assert.Equal(0u, p.Memory.Read32(output));
                Assert.Equal(0x80040154u, Call(p, Runtime, "RoActivateInstance", owned, output));
                Call(p, Strings, "WindowsDeleteString", owned);
            }
        }
    }
}
