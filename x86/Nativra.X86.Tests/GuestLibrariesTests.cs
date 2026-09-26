using System;
using System.IO;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>gdi32, the rest of user32, winmm, advapi32, shell32, version, oleaut32 and the absent network.</summary>
    public sealed class GuestLibrariesTests : IDisposable
    {
        private const uint Code = 0x00600000, Data = 0x00601000;
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-libs-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestLibrariesTests()
        {
            Directory.CreateDirectory(work);
            p = new GuestProcess(new GuestMemory(native: true), useJit: false);
            k = New(p);
            p.Memory.Map(Code, 0x3000);
        }

        private GuestKernel New(GuestProcess process)
        {
            var kernel = new GuestKernel(process) { ExePath = "C:\\game\\game.exe", Files = new HostFolderFiles("C:\\game", work) };
            kernel.Install();
            return kernel;
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        private uint Call(string module, string function, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind(module, function, -1), out var eax, 50_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private uint G(string f, params uint[] a) => Call("gdi32.dll", f, a);

        private uint Str(string text)
        {
            var ptr = k.Heap.Alloc((uint)text.Length + 1, zero: true);
            p.Memory.WriteAnsi(ptr, text);
            return ptr;
        }

        private uint Dib(int width, int height, out uint bits)
        {
            var info = k.Heap.Alloc(40, zero: true);
            p.Memory.Write32(info, 40);
            p.Memory.Write32(info + 4, (uint)width);
            p.Memory.Write32(info + 8, unchecked((uint)-height));   // top-down
            p.Memory.Write16(info + 12, 1);
            p.Memory.Write16(info + 14, 32);
            var slot = k.Heap.Alloc(4);
            var bitmap = G("CreateDIBSection", 0, info, 0, slot, 0, 0);
            Assert.NotEqual(0u, bitmap);
            bits = p.Memory.Read32(slot);
            return bitmap;
        }

        [Fact]
        public void GdiMovesPixelsBetweenMemoryDcs()
        {
            var a = G("CreateCompatibleDC", 0);
            var b = G("CreateCompatibleDC", 0);
            var bmpA = Dib(4, 4, out var bitsA);
            var bmpB = Dib(4, 4, out var bitsB);
            var oldA = G("SelectObject", a, bmpA);
            G("SelectObject", b, bmpB);
            Assert.NotEqual(0u, oldA);

            G("PatBlt", a, 0, 0, 4, 4, 0x00FF0062);                  // WHITENESS
            G("SetPixel", a, 1, 2, 0x000000FF);                        // red COLORREF
            Assert.Equal(0x000000FFu, G("GetPixel", a, 1, 2));
            Assert.Equal(0x00FF0000u, p.Memory.Read32(bitsA + (2 * 4 + 1) * 4));   // BGRA in memory
            Assert.Equal(1u, G("BitBlt", b, 0, 0, 4, 4, a, 0, 0, 0x00CC0020));
            Assert.Equal(0x00FF0000u, p.Memory.Read32(bitsB + (2 * 4 + 1) * 4));
            Assert.Equal(0x00FFFFFFu, p.Memory.Read32(bitsB));

            // A brush fill through user32.
            var rect = k.Heap.Alloc(16);
            Call("user32.dll", "SetRect", rect, 0, 0, 2, 1);
            Call("user32.dll", "FillRect", b, rect, G("CreateSolidBrush", 0x0000FF00));
            Assert.Equal(0x0000FF00u, p.Memory.Read32(bitsB + 4));

            // GetDIBits into a bottom-up 24-bit buffer.
            var info = k.Heap.Alloc(40, zero: true);
            p.Memory.Write32(info, 40);
            p.Memory.Write32(info + 4, 4);
            p.Memory.Write32(info + 8, 4);
            p.Memory.Write16(info + 12, 1);
            p.Memory.Write16(info + 14, 24);
            var out24 = k.Heap.Alloc(64, zero: true);
            Assert.Equal(4u, G("GetDIBits", b, bmpB, 0, 4, out24, info, 0));
            // Row 2 from the top is row 1 from the bottom: 12 bytes a row.
            Assert.Equal(new byte[] { 0, 0, 0xFF }, p.Memory.ReadBytes(out24 + 12 + 3, 3));

            var bm = k.Heap.Alloc(24);
            Assert.Equal(24u, G("GetObjectA", bmpA, 24, bm));
            Assert.Equal(4u, p.Memory.Read32(bm + 4));
            Assert.Equal(32, p.Memory.Read16(bm + 18));
            Assert.Equal(0u, G("DeleteObject", bmpA));   // still selected
            G("SelectObject", a, oldA);
            Assert.Equal(1u, G("DeleteObject", bmpA));
        }

        [Fact]
        public void FontsAnswerWithConsistentMetrics()
        {
            var dc = G("CreateCompatibleDC", 0);
            var font = Call("gdi32.dll", "CreateFontA", unchecked((uint)-20), 0, 0, 0, 700, 0, 0, 0, 0, 0, 0, 0, 0, Str("Verdana"));
            G("SelectObject", dc, font);
            var tm = k.Heap.Alloc(56);
            Assert.Equal(1u, G("GetTextMetricsA", dc, tm));
            var height = p.Memory.Read32(tm);
            Assert.True(height > 20);
            Assert.Equal(height, p.Memory.Read32(tm + 4) + p.Memory.Read32(tm + 8));
            var size = k.Heap.Alloc(8);
            G("GetTextExtentPoint32A", dc, Str("Hello"), 5, size);
            Assert.True(p.Memory.Read32(size) > 20);
            Assert.Equal(height, p.Memory.Read32(size + 4));
            var metrics = k.Heap.Alloc(20);
            var bytes = G("GetGlyphOutlineW", dc, 'A', 6, metrics, 0, 0, 0);   // GGO_GRAY8_BITMAP, size query
            Assert.Equal(p.Memory.Read32(metrics) + 3 & ~3u, bytes / p.Memory.Read32(metrics + 4));
            var face = k.Heap.Alloc(32);
            G("GetTextFaceA", dc, 32, face);
            Assert.Equal("Verdana", p.Memory.ReadAnsi(face));

            var buffer = k.Heap.Alloc(64);
            Assert.Equal(7u, Call("user32.dll", "wsprintfA", buffer, Str("%s-%04d"), Str("ab"), 42));
            Assert.Equal("ab-0042", p.Memory.ReadAnsi(buffer));
        }

        [Fact]
        public void WaveOutPlaysInRealTimeAndSignalsItsEvent()
        {
            var format = k.Heap.Alloc(18, zero: true);
            p.Memory.Write16(format, 1);            // PCM
            p.Memory.Write16(format + 2, 2);
            p.Memory.Write32(format + 4, 44100);
            p.Memory.Write32(format + 8, 176400);
            p.Memory.Write16(format + 12, 4);
            p.Memory.Write16(format + 14, 16);
            var evt = Call("kernel32.dll", "CreateEventA", 0, 0, 0, 0);
            var slot = k.Heap.Alloc(4);
            Assert.Equal(0u, Call("winmm.dll", "waveOutOpen", slot, 0xFFFFFFFF, format, evt, 0, 0x50000));   // CALLBACK_EVENT
            var wave = p.Memory.Read32(slot);
            Assert.Equal(0u, Call("kernel32.dll", "WaitForSingleObject", evt, 0));   // WOM_OPEN signals it, as on Windows
            Call("kernel32.dll", "ResetEvent", evt);
            var header = k.Heap.Alloc(32, zero: true);
            p.Memory.Write32(header, k.Heap.Alloc(1764));
            p.Memory.Write32(header + 4, 1764);    // 10 ms
            Call("winmm.dll", "waveOutPrepareHeader", wave, header, 32);
            Assert.Equal(0u, Call("winmm.dll", "waveOutWrite", wave, header, 32));
            Assert.Equal(0x10u, p.Memory.Read32(header + 16) & 0x11);   // queued, not done
            Assert.Equal(33u, Call("winmm.dll", "waveOutClose", wave));   // still playing
            Assert.Equal(0u, Call("kernel32.dll", "WaitForSingleObject", evt, 2000));
            Assert.Equal(1u, p.Memory.Read32(header + 16) & 0x11);      // done
            var time = k.Heap.Alloc(12, zero: true);
            p.Memory.Write32(time, 4);                                   // TIME_BYTES
            Call("winmm.dll", "waveOutGetPosition", wave, time, 12);
            Assert.Equal(1764u, p.Memory.Read32(time + 4));
            Assert.Equal(0u, Call("winmm.dll", "waveOutClose", wave));
        }

        [Fact]
        public void MultimediaTimerCallsTheGuestOnItsOwnThread()
        {
            // TimeProc(id, msg, user, dw1, dw2): inc dword [user]; ret 20
            p.Memory.WriteBytes(Code, new byte[] { 0x8B, 0x44, 0x24, 0x0C, 0xFF, 0x00, 0xC2, 0x14, 0x00 });
            var id = Call("winmm.dll", "timeSetEvent", 5, 1, Code, Data, 1);   // TIME_PERIODIC
            Assert.NotEqual(0u, id);
            Call("kernel32.dll", "Sleep", 80);
            Assert.True(p.Memory.Read32(Data) >= 3, $"fired {p.Memory.Read32(Data)} times");
            Assert.Equal(0u, Call("winmm.dll", "timeKillEvent", id));
            var fired = p.Memory.Read32(Data);
            Call("kernel32.dll", "Sleep", 30);
            Assert.True(p.Memory.Read32(Data) <= fired + 1);
        }

        [Fact]
        public void RegistryPersistsInTheGameFolder()
        {
            var key = k.Heap.Alloc(4);
            var disposition = k.Heap.Alloc(4);
            Assert.Equal(0u, Call("advapi32.dll", "RegCreateKeyExA", 0x80000001, Str("Software\\Studio\\Game"), 0, 0, 0, 0xF003F, 0, key, disposition));
            Assert.Equal(1u, p.Memory.Read32(disposition));
            var dword = k.Heap.Alloc(4);
            p.Memory.Write32(dword, 1280);
            Assert.Equal(0u, Call("advapi32.dll", "RegSetValueExA", p.Memory.Read32(key), Str("Width"), 0, 4, dword, 4));
            Assert.Equal(0u, Call("advapi32.dll", "RegSetValueExA", p.Memory.Read32(key), Str("Name"), 0, 1, Str("Ada"), 4));
            Call("advapi32.dll", "RegCloseKey", p.Memory.Read32(key));
            Assert.True(File.Exists(Path.Combine(work, "nativra-registry.txt")));

            // A fresh process reads what this one wrote.
            using (var q = new GuestProcess(new GuestMemory(native: true), useJit: false))
            {
                var kq = New(q);
                uint Q(string f, params uint[] a) { Assert.True(q.Call(q.Imports.Bind("advapi32.dll", f, -1), out var eax, 1_000_000, a).Ok); return eax; }
                uint S(string t) { var at = kq.Heap.Alloc((uint)t.Length + 1, zero: true); q.Memory.WriteAnsi(at, t); return at; }
                var k2 = kq.Heap.Alloc(4);
                Assert.Equal(0u, Q("RegOpenKeyExA", 0x80000001, S("Software\\Studio\\Game"), 0, 0x20019, k2));
                var type = kq.Heap.Alloc(4);
                var data = kq.Heap.Alloc(16);
                var size = kq.Heap.Alloc(4);
                q.Memory.Write32(size, 16);
                Assert.Equal(0u, Q("RegQueryValueExA", q.Memory.Read32(k2), S("Width"), 0, type, data, size));
                Assert.Equal(4u, q.Memory.Read32(type));
                Assert.Equal(1280u, q.Memory.Read32(data));
                q.Memory.Write32(size, 16);
                Assert.Equal(0u, Q("RegQueryValueExA", q.Memory.Read32(k2), S("Name"), 0, type, data, size));
                Assert.Equal("Ada", q.Memory.ReadAnsi(data));
                Assert.Equal(4u, q.Memory.Read32(size));
                q.Memory.Write32(size, 1);
                Assert.Equal(234u, Q("RegQueryValueExA", q.Memory.Read32(k2), S("Name"), 0, type, data, size));   // ERROR_MORE_DATA
                Assert.Equal(2u, Q("RegOpenKeyExA", 0x80000001, S("Software\\Nope"), 0, 0x20019, k2));
            }
        }

        [Fact]
        public void VersionInfoForASystemDll()
        {
            var size = Call("version.dll", "GetFileVersionInfoSizeA", Str("d3d9.dll"), 0);
            Assert.True(size > 0);
            var block = k.Heap.Alloc(size);
            Assert.Equal(1u, Call("version.dll", "GetFileVersionInfoA", Str("d3d9.dll"), 0, size, block));
            var value = k.Heap.Alloc(4);
            var length = k.Heap.Alloc(4);
            Assert.Equal(1u, Call("version.dll", "VerQueryValueA", block, Str("\\"), value, length));
            Assert.Equal(0xFEEF04BDu, p.Memory.Read32(p.Memory.Read32(value)));
            Assert.Equal(0x000A0000u, p.Memory.Read32(p.Memory.Read32(value) + 8));   // 10.0
        }

        [Fact]
        public void WalkingAProfilePathFromTheDriveRootNeverReachesTheHost()
        {
            // As a CRT's mkdir -p does: every part from C:\ down.
            Assert.Equal(0u, Call("kernel32.dll", "CreateDirectoryA", Str("C:\\users"), 0));
            Assert.Equal(183u, Call("kernel32.dll", "GetLastError"));            // ERROR_ALREADY_EXISTS
            Assert.Equal(0x10u, Call("kernel32.dll", "GetFileAttributesA", Str("C:\\")) & 0x10);
            foreach (var part in new[] { "C:\\users\\Player", "C:\\users\\Player\\Documents", "C:\\users\\Player\\Documents\\Studio" })
                Call("kernel32.dll", "CreateDirectoryA", Str(part), 0);
            Assert.True(Directory.Exists(Path.Combine(work, "nativra-user", "Documents", "Studio")));
            var f = Call("msvcrt.dll", "fopen", Str("C:\\users\\Player\\Documents\\Studio\\save.dat"), Str("wb"));
            Assert.NotEqual(0u, f);
            Call("msvcrt.dll", "fclose", f);
            Assert.True(File.Exists(Path.Combine(work, "nativra-user", "Documents", "Studio", "save.dat")));
            var env = k.Heap.Alloc(260);
            Call("kernel32.dll", "GetEnvironmentVariableA", Str("APPDATA"), env, 260);
            Assert.Equal("C:\\users\\Player\\AppData\\Roaming", p.Memory.ReadAnsi(env));
        }

        private sealed class RefusingFiles : IGuestFiles
        {
            public Stream Open(string path, FileMode mode, FileAccess access) => throw new UnauthorizedAccessException(path);
            public GuestFileEntry Stat(string path) => throw new UnauthorizedAccessException(path);
            public System.Collections.Generic.IEnumerable<GuestFileEntry> List(string folder) => throw new UnauthorizedAccessException(folder);
            public bool CreateDirectory(string path) => throw new UnauthorizedAccessException(path);
            public bool Delete(string path) => throw new PathTooLongException(path);
        }

        [Fact]
        public void AHostThatRefusesEverythingGivesWin32ErrorsNotExceptions()
        {
            k.Files = new RefusingFiles();
            Assert.Equal(0xFFFFFFFFu, Call("kernel32.dll", "GetFileAttributesA", Str("C:\\game\\data.pak")));
            Assert.Equal(0xFFFFFFFFu, Call("kernel32.dll", "CreateFileA", Str("C:\\game\\data.pak"), 0x80000000, 0, 0, 3, 0, 0));
            Assert.Equal(0u, Call("kernel32.dll", "CreateDirectoryA", Str("C:\\game\\new"), 0));
            Assert.Equal(0u, Call("kernel32.dll", "DeleteFileA", Str("C:\\game\\old")));
            Assert.Equal(0u, Call("msvcrt.dll", "fopen", Str("x.txt"), Str("r")));
            var path = k.Heap.Alloc(260);
            Assert.Equal(0u, Call("shell32.dll", "SHGetFolderPathA", 0, 0x8005, 0, 0, path));   // the path is still handed out
        }

        [Fact]
        public void AHandlerThatThrowsStopsTheRunNamingItsImport()
        {
            p.Imports.Register("test.dll", "Broken", CallConv.Stdcall, 0, c => throw new InvalidOperationException("boom"));
            var result = p.Call(p.Imports.Bind("test.dll", "Broken", -1), out _, 1000);
            Assert.Equal(GuestStop.HostError, result.Stop);
            Assert.Contains("test.dll!Broken", result.ToString());
            Assert.Contains("boom", result.Detail);
        }

        [Fact]
        public void BstrsNetworkAndFolders()
        {
            var wide = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteUnicode(wide, "abc");
            var bstr = Call("oleaut32.dll", "SysAllocString", wide);
            Assert.Equal(3u, Call("oleaut32.dll", "SysStringLen", bstr));
            Assert.Equal(6u, p.Memory.Read32(bstr - 4));
            Call("oleaut32.dll", "SysFreeString", bstr);

            Assert.Equal(0u, Call("ws2_32.dll", "WSAStartup", 0x0202, k.Heap.Alloc(400)));
            Assert.Equal(0xFFFFFFFFu, Call("ws2_32.dll", "socket", 2, 1, 6));
            Assert.Equal(10050u, Call("ws2_32.dll", "WSAGetLastError"));
            Assert.Equal(0x3412u, Call("ws2_32.dll", "htons", 0x1234));
            Assert.Equal(0u, Call("wininet.dll", "InternetGetConnectedState", k.Heap.Alloc(4), 0));

            var path = k.Heap.Alloc(260);
            Assert.Equal(0u, Call("shell32.dll", "SHGetFolderPathA", 0, 0x8005, 0, 0, path));   // CSIDL_PERSONAL | CREATE
            Assert.Equal("C:\\users\\Player\\Documents", p.Memory.ReadAnsi(path));   // the guest never sees host paths
            Assert.True(Directory.Exists(Path.Combine(work, "nativra-user", "Documents")));
        }
    }
}
