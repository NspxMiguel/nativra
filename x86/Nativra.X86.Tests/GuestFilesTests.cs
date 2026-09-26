using System;
using System.IO;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// kernel32 file calls against a temporary folder standing in for the
    /// game's: the guest sees it as C:\game.
    /// </summary>
    public sealed class GuestFilesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "nativra-files-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestFilesTests()
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllBytes(Path.Combine(root, "data", "level1.pak"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            File.WriteAllText(Path.Combine(root, "data", "readme.txt"), "hi");

            p = new GuestProcess(new GuestMemory(), useJit: false);
            k = new GuestKernel(p) { ExePath = "C:\\game\\waveshaper.exe", Files = new HostFolderFiles("C:\\game", root) };
            k.Install();
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        private uint K(string function, params uint[] args)
        {
            var sentinel = p.Imports.Bind("kernel32.dll", function, -1);
            var result = p.Call(sentinel, out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private uint A(string text)
        {
            var ptr = k.Heap.Alloc((uint)text.Length + 1);
            p.Memory.WriteAnsi(ptr, text);
            return ptr;
        }

        private uint W(string text)
        {
            var ptr = k.Heap.Alloc((uint)(text.Length + 1) * 2);
            p.Memory.WriteUnicode(ptr, text);
            return ptr;
        }

        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint OpenExisting = 3, CreateAlways = 2;

        [Fact]
        public void RelativeOpenReadsFromTheGameFolderAndSeeks()
        {
            var h = K("CreateFileA", A("data/level1.pak"), GenericRead, 1, 0, OpenExisting, 0x80, 0);
            Assert.NotEqual(0xFFFFFFFFu, h);
            Assert.Equal(8u, K("GetFileSize", h, 0));

            var buffer = k.Heap.Alloc(16);
            var read = k.Heap.Alloc(4);
            Assert.Equal(1u, K("ReadFile", h, buffer, 3, read, 0));
            Assert.Equal(3u, p.Memory.Read32(read));
            Assert.Equal(new byte[] { 1, 2, 3 }, p.Memory.ReadBytes(buffer, 3));

            Assert.Equal(6u, K("SetFilePointer", h, 0xFFFFFFFE, 0, 2 /*FILE_END*/));   // -2 from the end
            Assert.Equal(1u, K("ReadFile", h, buffer, 16, read, 0));
            Assert.Equal(2u, p.Memory.Read32(read));   // short read at end of file
            Assert.Equal(new byte[] { 7, 8 }, p.Memory.ReadBytes(buffer, 2));

            Assert.Equal(1u, K("CloseHandle", h));
        }

        [Fact]
        public void MissingFileFailsWithFileNotFoundAndIsRecorded()
        {
            Assert.Equal(0xFFFFFFFFu, K("CreateFileW", W("C:\\game\\data\\missing.ogg"), GenericRead, 1, 0, OpenExisting, 0, 0));
            Assert.Equal(2u, K("GetLastError"));
            Assert.Contains("C:\\game\\data\\missing.ogg", k.FilesNotFound);

            Assert.Equal(0xFFFFFFFFu, K("GetFileAttributesW", W("C:\\game\\nodir\\x.ogg")));
            Assert.Equal(3u, K("GetLastError"));   // ERROR_PATH_NOT_FOUND
        }

        [Fact]
        public void WrittenFileCanBeReadBack()
        {
            var h = K("CreateFileW", W("save.dat"), GenericRead | GenericWrite, 0, 0, CreateAlways, 0x80, 0);
            Assert.NotEqual(0xFFFFFFFFu, h);
            var data = k.Heap.Alloc(4);
            p.Memory.Write32(data, 0xC0FFEE11);
            var written = k.Heap.Alloc(4);
            Assert.Equal(1u, K("WriteFile", h, data, 4, written, 0));
            Assert.Equal(4u, p.Memory.Read32(written));
            K("CloseHandle", h);

            Assert.Equal(new byte[] { 0x11, 0xEE, 0xFF, 0xC0 }, File.ReadAllBytes(Path.Combine(root, "save.dat")));
            Assert.Equal(1u, K("DeleteFileA", A("save.dat")));
            Assert.False(File.Exists(Path.Combine(root, "save.dat")));
        }

        [Fact]
        public void FindFilesMatchesTheWildcard()
        {
            var data = k.Heap.Alloc(600);
            var h = K("FindFirstFileW", W("C:\\game\\data\\*.pak"), data);
            Assert.NotEqual(0xFFFFFFFFu, h);
            Assert.Equal("level1.pak", p.Memory.ReadUnicode(data + 44));
            Assert.Equal(8u, p.Memory.Read32(data + 32));   // nFileSizeLow
            Assert.Equal(0u, K("FindNextFileW", h, data));
            Assert.Equal(18u, K("GetLastError"));           // ERROR_NO_MORE_FILES
            Assert.Equal(1u, K("FindClose", h));

            var all = K("FindFirstFileA", A("data\\*.*"), data);
            var count = 1;
            while (K("FindNextFileA", all, data) != 0) count++;
            Assert.Equal(2, count);
        }

        [Fact]
        public void AttributesTellFilesFromFolders()
        {
            Assert.Equal(0x10u, K("GetFileAttributesA", A("C:\\game\\data")) & 0x10);   // FILE_ATTRIBUTE_DIRECTORY
            Assert.Equal(0u, K("GetFileAttributesA", A("data\\readme.txt")) & 0x10);

            var info = k.Heap.Alloc(36);
            Assert.Equal(1u, K("GetFileAttributesExW", W("data\\readme.txt"), 0, info));
            Assert.Equal(2u, p.Memory.Read32(info + 32));
        }

        [Fact]
        public void PathsResolveAgainstTheCurrentDirectory()
        {
            var buffer = k.Heap.Alloc(520);
            Assert.Equal(7u, K("GetCurrentDirectoryA", 260, buffer));
            Assert.Equal("C:\\game", p.Memory.ReadAnsi(buffer));

            Assert.Equal(1u, K("SetCurrentDirectoryW", W("data")));
            var part = k.Heap.Alloc(4);
            var n = K("GetFullPathNameW", W("..\\data\\.\\level1.pak"), 260, buffer, part);
            Assert.Equal("C:\\game\\data\\level1.pak", p.Memory.ReadUnicode(buffer));
            Assert.Equal((uint)"C:\\game\\data\\level1.pak".Length, n);
            Assert.Equal("level1.pak", p.Memory.ReadUnicode(p.Memory.Read32(part)));

            Assert.Equal(0u, K("SetCurrentDirectoryA", A("C:\\nowhere")));
        }

        [Fact]
        public void StandardOutputGoesToTheLog()
        {
            var lines = new System.Collections.Generic.List<string>();
            k.Log = lines.Add;
            var h = K("GetStdHandle", 0xFFFFFFF5);   // STD_OUTPUT_HANDLE
            var text = A("frame 1\r\n");
            Assert.Equal(1u, K("WriteFile", h, text, 9, 0, 0));
            Assert.Equal(new[] { "frame 1" }, lines);
            Assert.Equal(2u, K("GetFileType", h));   // FILE_TYPE_CHAR
        }

        [Theory]
        [InlineData("*.pak", "level1.pak", true)]
        [InlineData("LEVEL?.PAK", "level1.pak", true)]
        [InlineData("*.pak", "readme.txt", false)]
        [InlineData("*.*", "noext", true)]
        [InlineData("level*", "level1.pak", true)]
        public void WildcardFollowsDosRules(string pattern, string name, bool match) =>
            Assert.Equal(match, GuestKernel.Wildcard(pattern, name));
    }
}
