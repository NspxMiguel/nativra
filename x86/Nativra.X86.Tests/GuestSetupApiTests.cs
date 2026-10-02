using System;
using System.IO;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>setupapi's device enumeration.</summary>
    public sealed class GuestSetupApiTests : IDisposable
    {
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-setup-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestSetupApiTests()
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

        private uint Setup(string f, params uint[] a)
        {
            var result = p.Call(p.Imports.Bind("setupapi.dll", f, -1), out var eax, 50_000_000, a);
            Assert.True(result.Ok, $"{f} stopped as {result}");
            return eax;
        }

        private uint LastError() => p.Call(p.Imports.Bind("kernel32.dll", "GetLastError", -1), out var eax, 1_000_000).Ok ? eax : 0;

        private uint Str(string text)
        {
            var ptr = k.Heap.Alloc((uint)text.Length + 1, zero: true);
            p.Memory.WriteAnsi(ptr, text);
            return ptr;
        }

        [Fact]
        public void ClassNamesMapToTheirGuids()
        {
            var guids = k.Heap.Alloc(32, zero: true);
            var required = k.Heap.Alloc(4, zero: true);
            Assert.Equal(1u, Setup("SetupDiClassGuidsFromNameA", Str("net"), guids, 2, required));   // names ignore case
            Assert.Equal(1u, p.Memory.Read32(required));
            Assert.Equal(new Guid("4D36E972-E325-11CE-BFC1-08002BE10318").ToByteArray(), p.Memory.ReadBytes(guids, 16));
            Assert.Equal(0u, Setup("SetupDiClassGuidsFromNameA", Str("Net"), guids, 0, required));
            Assert.Equal(122u, LastError());                                                           // no room, size reported
            Assert.Equal(1u, p.Memory.Read32(required));
            Assert.Equal(1u, Setup("SetupDiClassGuidsFromNameA", Str("NoSuchClass"), guids, 2, required));
            Assert.Equal(0u, p.Memory.Read32(required));                                               // none, as Windows says it
        }

        [Fact]
        public void ClassNameFromGuidIsTheInverse()
        {
            var guid = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteBytes(guid, new Guid("745A17A0-74D3-11D0-B6FE-00A0C90F57DA").ToByteArray());
            var name = k.Heap.Alloc(32, zero: true);
            var required = k.Heap.Alloc(4, zero: true);
            Assert.Equal(1u, Setup("SetupDiClassNameFromGuidA", guid, name, 32, required));
            Assert.Equal("HIDClass", p.Memory.ReadAnsi(name));
            Assert.Equal(9u, p.Memory.Read32(required));
            Assert.Equal(0u, Setup("SetupDiClassNameFromGuidA", guid, name, 4, required));
            Assert.Equal(122u, LastError());
            p.Memory.WriteBytes(guid, new byte[16]);
            Assert.Equal(0u, Setup("SetupDiClassNameFromGuidA", guid, name, 32, required));
            Assert.Equal(0xE0000203u, LastError());                                                    // ERROR_INVALID_CLASS
        }

        [Fact]
        public void AnEnumerationOfAnEmptyDeviceSetEndsAtOnce()
        {
            var guid = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteBytes(guid, new Guid("4D36E972-E325-11CE-BFC1-08002BE10318").ToByteArray());
            var set = Setup("SetupDiGetClassDevsA", guid, 0, 0, 2);   // DIGCF_PRESENT
            Assert.NotEqual(0xFFFFFFFFu, set);
            var data = k.Heap.Alloc(28, zero: true);
            Assert.Equal(0u, Setup("SetupDiEnumDeviceInfo", set, 0, data));
            Assert.Equal(1784u, LastError());                           // cbSize not set: ERROR_INVALID_USER_BUFFER
            p.Memory.Write32(data, 28);
            Assert.Equal(0u, Setup("SetupDiEnumDeviceInfo", set, 0, data));
            Assert.Equal(259u, LastError());                            // ERROR_NO_MORE_ITEMS
            Assert.Equal(0u, Setup("SetupDiGetDeviceInstanceIdA", set, data, k.Heap.Alloc(64), 64, 0));
            Assert.Equal(87u, LastError());                             // that data names no device
            var iface = k.Heap.Alloc(28, zero: true);
            p.Memory.Write32(iface, 28);
            Assert.Equal(0u, Setup("SetupDiEnumDeviceInterfaces", set, 0, guid, 0, iface));
            Assert.Equal(259u, LastError());
            Assert.Equal(1u, Setup("SetupDiDestroyDeviceInfoList", set));
            Assert.Equal(0u, Setup("SetupDiEnumDeviceInfo", set, 0, data));
            Assert.Equal(6u, LastError());                              // destroyed: ERROR_INVALID_HANDLE
            Assert.Equal(0u, Setup("SetupDiDestroyDeviceInfoList", set));
        }

        [Fact]
        public void GetClassDevsNeedsAClassOrAllClasses()
        {
            Assert.Equal(0xFFFFFFFFu, Setup("SetupDiGetClassDevsA", 0, 0, 0, 2));
            Assert.Equal(87u, LastError());
            Assert.NotEqual(0xFFFFFFFFu, Setup("SetupDiGetClassDevsA", 0, 0, 0, 4));   // DIGCF_ALLCLASSES
        }
    }
}
