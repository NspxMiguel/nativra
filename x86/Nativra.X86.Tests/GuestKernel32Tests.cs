using System;
using System.IO;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// The kernel32 families added for 32-bit games: Interlocked, the Rtl
    /// memory helpers, profile files, file mappings, atoms, FormatMessage and
    /// the date/time pictures. Each runs through the import sentinel, as a
    /// game's call would.
    /// </summary>
    public sealed class GuestKernel32Tests : IDisposable
    {
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-k32-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestKernel32Tests()
        {
            Directory.CreateDirectory(work);
            // Native memory: the Interlocked calls are real locked operations there.
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            k = new GuestKernel(p) { ExePath = "C:\\game\\game.exe", Files = new HostFolderFiles("C:\\game", work), DeterministicTime = true };
            k.Install();
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        private uint K(string function, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind("kernel32.dll", function, -1), out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private uint Ansi(string text)
        {
            var ptr = k.Heap.Alloc((uint)text.Length + 1);
            p.Memory.WriteAnsi(ptr, text);
            return ptr;
        }

        [Fact]
        public void InterlockedFamilyWorksOnTheGuestAddress()
        {
            var v = k.Heap.Alloc(8, zero: true);
            Assert.Equal(1u, K("InterlockedIncrement", v));
            Assert.Equal(2u, K("InterlockedIncrement", v));
            Assert.Equal(1u, K("InterlockedDecrement", v));
            Assert.Equal(1u, K("InterlockedExchangeAdd", v, 10));
            Assert.Equal(11u, p.Memory.Read32(v));
            Assert.Equal(11u, K("InterlockedExchange", v, 5));
            Assert.Equal(5u, K("InterlockedCompareExchange", v, 9, 4));   // no match: unchanged
            Assert.Equal(5u, p.Memory.Read32(v));
            Assert.Equal(5u, K("InterlockedCompareExchange", v, 9, 5));
            Assert.Equal(9u, p.Memory.Read32(v));
            Assert.Equal(0xFFFFFFFFu, K("InterlockedDecrement", k.Heap.Alloc(4, zero: true)));

            // 64-bit: exchange and comparand are passed as two DWORDs each; the old value comes back in EDX:EAX.
            p.Memory.Write64(v, 0x1_0000_0002);
            Assert.Equal(2u, K("InterlockedCompareExchange64", v, 7, 8, 2, 1));
            Assert.Equal(1u, p.Cpu.Edx);
            Assert.Equal(0x8_0000_0007UL, p.Memory.Read64(v));
        }

        [Fact]
        public void RtlMoveMemoryHandlesOverlapAndMulDivRounds()
        {
            var b = k.Heap.Alloc(16);
            p.Memory.WriteBytes(b, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            K("RtlMoveMemory", b + 2, b, 6);
            Assert.Equal(new byte[] { 1, 2, 1, 2, 3, 4, 5, 6 }, p.Memory.ReadBytes(b, 8));
            K("RtlFillMemory", b, 3, 0xAA);
            K("RtlZeroMemory", b + 3, 2);
            Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA, 0, 0, 4, 5, 6 }, p.Memory.ReadBytes(b, 8));

            Assert.Equal(3u, K("MulDiv", 5, 3, 5));
            Assert.Equal(2u, K("MulDiv", 3, 1, 2));                 // 1.5 rounds away from zero
            Assert.Equal(unchecked((uint)-2), K("MulDiv", unchecked((uint)-3), 1, 2));
            Assert.Equal(0xFFFFFFFFu, K("MulDiv", 1, 1, 0));
        }

        [Fact]
        public void PrivateProfileRoundTripsThroughTheGameFolder()
        {
            var file = Ansi("settings.ini");   // a bare name: the game's folder
            Assert.Equal(1u, K("WritePrivateProfileStringA", Ansi("Video"), Ansi("Width"), Ansi("1280"), file));
            Assert.Equal(1u, K("WritePrivateProfileStringA", Ansi("Video"), Ansi("Mode"), Ansi("\"windowed\""), file));
            Assert.Equal(1u, K("WritePrivateProfileStringA", Ansi("Audio"), Ansi("Volume"), Ansi("0x40"), file));
            Assert.True(File.Exists(Path.Combine(work, "settings.ini")));

            Assert.Equal(1280u, K("GetPrivateProfileIntA", Ansi("video"), Ansi("width"), 7, file));
            Assert.Equal(64u, K("GetPrivateProfileIntA", Ansi("Audio"), Ansi("Volume"), 7, file));
            Assert.Equal(7u, K("GetPrivateProfileIntA", Ansi("Audio"), Ansi("Missing"), 7, file));

            var buffer = k.Heap.Alloc(64);
            Assert.Equal(8u, K("GetPrivateProfileStringA", Ansi("Video"), Ansi("Mode"), Ansi("x"), buffer, 64, file));
            Assert.Equal("windowed", p.Memory.ReadAnsi(buffer));
            Assert.Equal(3u, K("GetPrivateProfileStringA", Ansi("Video"), Ansi("Nope"), Ansi("def"), buffer, 64, file));
            Assert.Equal("def", p.Memory.ReadAnsi(buffer));

            // NULL section: every section name, NUL-separated, double-NUL-terminated.
            Assert.Equal(12u, K("GetPrivateProfileStringA", 0, 0, Ansi(""), buffer, 64, file));
            Assert.Equal("Video", p.Memory.ReadAnsi(buffer));
            Assert.Equal("Audio", p.Memory.ReadAnsi(buffer + 6));
            Assert.Equal(0, p.Memory.Read8(buffer + 12));
        }

        [Fact]
        public void FileMappingReadsTheFileAndWritesBackOnUnmap()
        {
            File.WriteAllBytes(Path.Combine(work, "data.bin"), new byte[] { 10, 20, 30, 40 });
            var file = K("CreateFileA", Ansi("C:\\game\\data.bin"), 0xC0000000, 0, 0, 3, 0, 0);
            var mapping = K("CreateFileMappingA", file, 0, 0x04, 0, 0, 0);
            Assert.NotEqual(0u, mapping);
            var view = K("MapViewOfFile", mapping, 0x2, 0, 0, 0);
            Assert.Equal(new byte[] { 10, 20, 30, 40 }, p.Memory.ReadBytes(view, 4));
            p.Memory.Write8(view + 1, 99);
            Assert.Equal(1u, K("UnmapViewOfFile", view));
            K("CloseHandle", mapping);
            K("CloseHandle", file);
            Assert.Equal(new byte[] { 10, 99, 30, 40 }, File.ReadAllBytes(Path.Combine(work, "data.bin")));

            // A named page-file mapping is shared by every view and found by name.
            var shared = K("CreateFileMappingA", 0xFFFFFFFF, 0, 0x04, 0, 4096, Ansi("Local\\Shared"));
            var a = K("MapViewOfFile", shared, 0xF001F, 0, 0, 0);
            var b = K("MapViewOfFile", K("OpenFileMappingA", 0xF001F, 0, Ansi("Local\\Shared")), 0xF001F, 0, 0, 0);
            p.Memory.Write32(a, 0x12345678);
            Assert.Equal(0x12345678u, p.Memory.Read32(b));
        }

        [Fact]
        public void AtomsAreFoundByNameAndInteger()
        {
            var atom = K("GlobalAddAtomA", Ansi("MyClass"));
            Assert.True(atom >= 0xC000);
            Assert.Equal(atom, K("GlobalFindAtomA", Ansi("myclass")));
            Assert.Equal(123u, K("AddAtomA", Ansi("#123")));
            var buffer = k.Heap.Alloc(32);
            Assert.Equal(7u, K("GlobalGetAtomNameA", atom, buffer, 32));
            Assert.Equal("MyClass", p.Memory.ReadAnsi(buffer));
        }

        [Fact]
        public void FormatMessageExpandsInsertsAndSystemMessages()
        {
            var args = k.Heap.Alloc(8);
            p.Memory.Write32(args, Ansi("disk"));
            p.Memory.Write32(args + 4, 42);
            var buffer = k.Heap.Alloc(128);
            // FORMAT_MESSAGE_FROM_STRING | ARGUMENT_ARRAY
            var n = K("FormatMessageA", 0x2400, Ansi("%1 has %2!d! files%n"), 0, 0, buffer, 128, args);
            Assert.Equal("disk has 42 files\r\n", p.Memory.ReadAnsi(buffer));
            Assert.Equal(19u, n);

            // FROM_SYSTEM | ALLOCATE_BUFFER | IGNORE_INSERTS
            var slot = k.Heap.Alloc(4);
            Assert.NotEqual(0u, K("FormatMessageA", 0x1300, 0, 2, 0, slot, 0, 0));
            Assert.Equal("The system cannot find the file specified.\r\n", p.Memory.ReadAnsi(p.Memory.Read32(slot)));
        }

        [Fact]
        public void DateAndTimePicturesFollowWindowsSyntax()
        {
            var st = k.Heap.Alloc(16, zero: true);
            p.Memory.Write16(st, 2026);
            p.Memory.Write16(st + 2, 3);
            p.Memory.Write16(st + 6, 7);
            p.Memory.Write16(st + 8, 14);
            p.Memory.Write16(st + 10, 5);
            var buffer = k.Heap.Alloc(64);
            Assert.Equal(12u, K("GetDateFormatA", 0x409, 0, st, Ansi("dd MMM yyyy"), buffer, 64));
            Assert.Equal("07 Mar 2026", p.Memory.ReadAnsi(buffer));
            K("GetTimeFormatA", 0x409, 0, st, Ansi("hh':'mm tt"), buffer, 64);
            Assert.Equal("02:05 PM", p.Memory.ReadAnsi(buffer));
            K("GetDateFormatA", 0x409, 0, st, 0, buffer, 64);
            Assert.Equal("3/7/2026", p.Memory.ReadAnsi(buffer));
        }

        [Fact]
        public void WaitableTimerSignalsAfterItsDueTime()
        {
            // A process of its own whose clock moves.
            var q = new GuestProcess(new GuestMemory(), useJit: false);
            var kq = new GuestKernel(q);
            kq.Install();
            uint Q(string f, params uint[] a)
            {
                Assert.True(q.Call(q.Imports.Bind("kernel32.dll", f, -1), out var eax, 100_000_000, a).Ok);
                return eax;
            }
            var timer = Q("CreateWaitableTimerW", 0, 1, 0);
            var due = kq.Heap.Alloc(8);
            q.Memory.Write64(due, unchecked((ulong)-20 * 10_000));   // 20 ms from now
            Assert.Equal(1u, Q("SetWaitableTimer", timer, due, 0, 0, 0, 0));
            Assert.Equal(0x102u, Q("WaitForSingleObject", timer, 0));   // WAIT_TIMEOUT: not yet
            Assert.Equal(0u, Q("WaitForSingleObject", timer, 5000));    // signalled once due
            q.Dispose();
        }
    }
}
