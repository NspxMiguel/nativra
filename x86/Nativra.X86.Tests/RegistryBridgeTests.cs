using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Kiosk.Native;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class RegistryBridgeTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "nativra-registry-" + Guid.NewGuid().ToString("N"));
        private readonly IntPtr buffer = Marshal.AllocHGlobal(512);
        private static readonly IntPtr CurrentUser = new IntPtr(unchecked((long)(int)0x80000001));
        private string FileName => Path.Combine(folder, "registry.txt");

        public void Dispose()
        {
            Marshal.FreeHGlobal(buffer);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        private IntPtr Text(string text, int offset = 128)
        {
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            Marshal.Copy(bytes, 0, buffer + offset, bytes.Length);
            return buffer + offset;
        }

        private IntPtr Open(RegistryBridge registry, bool create = true)
        {
            Assert.Equal(0, registry.Open(CurrentUser, Text(@"Software\Studio\Game"), buffer, buffer + 8, create, true));
            return Marshal.ReadIntPtr(buffer);
        }

        [Fact]
        public void UnicodePreferencesSurviveClosingHandlesAndRestartingTheRegistry()
        {
            var registry = new RegistryBridge(FileName);
            var key = Open(registry);
            Assert.Equal(1, Marshal.ReadInt32(buffer + 8));
            var name = Text("Settings", 256);
            var value = Encoding.Unicode.GetBytes("ação 🎮\0");
            Marshal.Copy(value, 0, buffer + 32, value.Length);
            Assert.Equal(0, registry.Set(key, name, 0, 1, buffer + 32, (uint)value.Length, true));
            Assert.Equal(0, registry.Close(key));
            Assert.Equal(6, registry.Query(key, name, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, buffer + 16, true));
            registry = new RegistryBridge(FileName);
            key = Open(registry, false);
            Assert.Equal(2, Marshal.ReadInt32(buffer + 8));
            Assert.Equal(0, registry.Query(key, name, IntPtr.Zero, buffer + 12, IntPtr.Zero, buffer + 16, true));
            Assert.Equal(1, Marshal.ReadInt32(buffer + 12));
            Assert.Equal(value.Length, Marshal.ReadInt32(buffer + 16));
            Assert.Equal(0, registry.Query(key, name, IntPtr.Zero, IntPtr.Zero, buffer + 32, buffer + 16, true));
            Assert.Equal("ação 🎮", Marshal.PtrToStringUni(buffer + 32));
        }

        [Fact]
        public void QueryReportsRequiredBytesWithoutWritingAShortBuffer()
        {
            var registry = new RegistryBridge(FileName);
            var key = Open(registry);
            Marshal.WriteInt32(buffer + 32, 0x12345678);
            Assert.Equal(0, registry.Set(key, IntPtr.Zero, 0, 4, buffer + 32, 4, true));
            Marshal.WriteInt64(buffer + 32, 0x2233445566778899);
            Marshal.WriteInt32(buffer + 16, 3);
            Assert.Equal(234, registry.Query(key, IntPtr.Zero, IntPtr.Zero, buffer + 12, buffer + 32, buffer + 16, true));
            Assert.Equal(4, Marshal.ReadInt32(buffer + 16));
            Assert.Equal(4, Marshal.ReadInt32(buffer + 12));
            Assert.Equal(0x2233445566778899, Marshal.ReadInt64(buffer + 32));
            Assert.Equal(0, registry.Query(key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, buffer + 32, buffer + 16, true));
            Assert.Equal(0x2233445512345678, Marshal.ReadInt64(buffer + 32));
        }

        [Fact]
        public void AnsiAndWideValueQueriesUseByteLengthsAndWindows1252()
        {
            var registry = new RegistryBridge(FileName);
            var key = Open(registry);
            Marshal.Copy(new byte[] { 0x80, 0xE9, 0 }, 0, buffer + 32, 3);
            Assert.Equal(0, registry.Set(key, IntPtr.Zero, 0, 1, buffer + 32, 3, false));
            Marshal.WriteInt32(buffer + 16, 64);
            Assert.Equal(0, registry.Query(key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, buffer + 32, buffer + 16, true));
            Assert.Equal(6, Marshal.ReadInt32(buffer + 16));
            Assert.Equal("€é", Marshal.PtrToStringUni(buffer + 32));
            Assert.Equal(0, registry.Query(key, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, buffer + 32, buffer + 16, false));
            Assert.Equal(3, Marshal.ReadInt32(buffer + 16));
            Assert.Equal(0x80, Marshal.ReadByte(buffer + 32));
            Assert.Equal(0xE9, Marshal.ReadByte(buffer + 33));
        }

        [Fact]
        public void MissingKeysStayMissingAndValuesCanBeDeletedPersistently()
        {
            var registry = new RegistryBridge(FileName);
            Assert.Equal(2, registry.Open(CurrentUser, Text("Missing"), buffer, IntPtr.Zero, false, true));
            var key = Open(registry);
            var name = Text("value", 256);
            Assert.Equal(0, registry.Set(key, name, 0, 3, IntPtr.Zero, 0, true));
            Assert.Equal(0, registry.Delete(key, name, true, true));
            Assert.Equal(2, registry.Query(key, name, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, buffer + 16, true));
            Assert.Equal(5, registry.Delete(CurrentUser, Text(@"Software\Studio"), false, true));
            Assert.Equal(0, registry.Delete(CurrentUser, Text(@"Software\Studio\Game"), false, true));
            registry = new RegistryBridge(FileName);
            Assert.Equal(2, registry.Open(CurrentUser, Text(@"Software\Studio\Game"), buffer, IntPtr.Zero, false, true));
        }

        [Fact]
        public void PersistenceFailuresAreNotReportedAsSuccessfulWrites()
        {
            Directory.CreateDirectory(FileName);
            var registry = new RegistryBridge(FileName);
            Assert.Equal(29, registry.Open(CurrentUser, Text(@"Software\Studio\Game"), buffer, IntPtr.Zero, true, true));
        }
    }
}
