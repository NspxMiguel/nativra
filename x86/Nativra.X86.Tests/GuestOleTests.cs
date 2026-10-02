using System;
using System.IO;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>ole32's GUID text conversions.</summary>
    public sealed class GuestOleTests : IDisposable
    {
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-ole-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestOleTests()
        {
            Directory.CreateDirectory(work);
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            k = new GuestKernel(p) { ExePath = "C:\\game\\game.exe", Files = new HostFolderFiles("C:\\game", work) };
            k.Install();
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        private uint Ole(string f, params uint[] a)
        {
            var result = p.Call(p.Imports.Bind("ole32.dll", f, -1), out var eax, 50_000_000, a);
            Assert.True(result.Ok, $"{f} stopped as {result}");
            return eax;
        }

        private uint Wide(string text)
        {
            var ptr = k.Heap.Alloc((uint)text.Length * 2 + 2, zero: true);
            p.Memory.WriteUnicode(ptr, text);
            return ptr;
        }

        [Fact]
        public void ClsidFromStringReadsTheRegistryForm()
        {
            var guid = k.Heap.Alloc(16, zero: true);
            Assert.Equal(0u, Ole("CLSIDFromString", Wide("{D6C5C8D4-3E5B-4A3C-9C1E-0123456789ab}"), guid));
            Assert.Equal(new Guid("D6C5C8D4-3E5B-4A3C-9C1E-0123456789AB").ToByteArray(), p.Memory.ReadBytes(guid, 16));
            Assert.Equal(0x800401F3u, Ole("CLSIDFromString", Wide("D6C5C8D4-3E5B-4A3C-9C1E-0123456789AB"), guid));   // braces are required
            Assert.Equal(0x800401F3u, Ole("CLSIDFromString", Wide("{not a guid}"), guid));
            p.Memory.WriteBytes(guid, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
            Assert.Equal(0u, Ole("CLSIDFromString", 0, guid));                                                           // NULL is CLSID_NULL
            Assert.Equal(new byte[16], p.Memory.ReadBytes(guid, 16));
        }

        [Fact]
        public void IidFromStringAndBackRoundTrip()
        {
            var guid = k.Heap.Alloc(16, zero: true);
            Assert.Equal(0u, Ole("IIDFromString", Wide("{00000000-0000-0000-C000-000000000046}"), guid));   // IUnknown
            Assert.Equal(0xC0, p.Memory.Read8(guid + 8));
            var slot = k.Heap.Alloc(4, zero: true);
            Assert.Equal(0u, Ole("StringFromIID", guid, slot));
            Assert.Equal("{00000000-0000-0000-C000-000000000046}", p.Memory.ReadUnicode(p.Memory.Read32(slot)));
            Assert.Equal(0u, Ole("StringFromCLSID", guid, slot));
            Assert.Equal(0x800401F4u, Ole("IIDFromString", Wide("nonsense"), guid));
            Assert.Equal(0x800401F4u, Ole("IIDFromString", 0, guid));
        }
    }
}
