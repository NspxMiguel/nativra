using System.Collections.Generic;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>The kernel32 calls the C runtime makes during start-up and in ordinary use.</summary>
    public sealed class GuestRuntimeTests
    {
        private static GuestProcess NewProcess(out GuestKernel kernel)
        {
            var p = new GuestProcess(new GuestMemory(), useJit: false);
            kernel = new GuestKernel(p);
            kernel.Install();
            return p;
        }

        private static uint Call(GuestProcess p, string module, string function, params uint[] args)
        {
            var sentinel = p.Imports.Bind(module, function, -1);
            var result = p.Call(sentinel, out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private static uint K(GuestProcess p, string function, params uint[] args) =>
            Call(p, "kernel32.dll", function, args);

        private static uint Wide(GuestKernel k, GuestProcess p, string text)
        {
            var ptr = k.Heap.Alloc((uint)(text.Length + 1) * 2);
            p.Memory.WriteUnicode(ptr, text);
            return ptr;
        }

        private static uint Bytes(GuestKernel k, GuestProcess p, byte[] bytes)
        {
            var ptr = k.Heap.Alloc((uint)bytes.Length + 1);
            p.Memory.WriteBytes(ptr, bytes);
            p.Memory.Write8(ptr + (uint)bytes.Length, 0);
            return ptr;
        }

        [Fact]
        public void ApiSetNamesReachTheKernel32Handlers()
        {
            var p = NewProcess(out _);
            Assert.Equal(1252u, Call(p, "api-ms-win-core-localization-l1-2-1.dll", "GetACP"));
            Assert.Equal(0x1234u, Call(p, "KERNELBASE.dll", "GetCurrentProcessId"));
        }

        [Fact]
        public void MultiByteToWideCharConvertsCodePage1252AndUtf8()
        {
            var p = NewProcess(out var k);
            // "€é" in 1252 is 0x80 0xE9; with -1 the terminator is converted too.
            var src = Bytes(k, p, new byte[] { 0x80, 0xE9 });
            Assert.Equal(3u, K(p, "MultiByteToWideChar", 1252, 0, src, 0xFFFFFFFF, 0, 0));
            var dst = k.Heap.Alloc(16);
            Assert.Equal(3u, K(p, "MultiByteToWideChar", 1252, 0, src, 0xFFFFFFFF, dst, 8));
            Assert.Equal("€é", p.Memory.ReadUnicode(dst));

            var utf8 = Bytes(k, p, new byte[] { 0xE2, 0x82, 0xAC });   // "€"
            Assert.Equal(1u, K(p, "MultiByteToWideChar", 65001, 0, utf8, 3, dst, 8));
            Assert.Equal(0x20ACu, (uint)p.Memory.Read16(dst));
        }

        [Fact]
        public void WideCharToMultiByteReportsTheSizeThenConverts()
        {
            var p = NewProcess(out var k);
            var src = Wide(k, p, "a€");
            Assert.Equal(5u, K(p, "WideCharToMultiByte", 65001, 0, src, 0xFFFFFFFF, 0, 0, 0, 0));   // 1 + 3 + NUL
            var dst = k.Heap.Alloc(16);
            Assert.Equal(3u, K(p, "WideCharToMultiByte", 1252, 0, src, 0xFFFFFFFF, dst, 16, 0, 0));
            Assert.Equal(new byte[] { (byte)'a', 0x80, 0 }, p.Memory.ReadBytes(dst, 3));
        }

        [Fact]
        public void EnvironmentVariablesFollowTheWin32SizeRules()
        {
            var p = NewProcess(out var k);
            k.SetEnvironment("GAME_MODE", "demo");
            var name = Wide(k, p, "game_mode");   // case-insensitive
            Assert.Equal(5u, K(p, "GetEnvironmentVariableW", name, 0, 0));   // size needed incl. NUL
            var buffer = k.Heap.Alloc(32);
            Assert.Equal(4u, K(p, "GetEnvironmentVariableW", name, buffer, 16));   // length written
            Assert.Equal("demo", p.Memory.ReadUnicode(buffer));

            Assert.Equal(0u, K(p, "GetEnvironmentVariableW", Wide(k, p, "NOPE"), buffer, 16));
            Assert.Equal(203u, K(p, "GetLastError"));   // ERROR_ENVVAR_NOT_FOUND

            // The block is KEY=VALUE\0...\0\0.
            var block = K(p, "GetEnvironmentStringsW");
            var seen = new List<string>();
            for (var at = block; p.Memory.Read16(at) != 0;)
            {
                var entry = p.Memory.ReadUnicode(at);
                seen.Add(entry);
                at += (uint)(entry.Length + 1) * 2;
            }
            Assert.Contains("GAME_MODE=demo", seen);
            Assert.Equal(1u, K(p, "FreeEnvironmentStringsW", block));
        }

        [Fact]
        public void FlsSlotsStoreValuesLikeTls()
        {
            var p = NewProcess(out _);
            var slot = K(p, "FlsAlloc", 0);
            Assert.NotEqual(0xFFFFFFFFu, slot);
            Assert.Equal(1u, K(p, "FlsSetValue", slot, 0x1234));
            Assert.Equal(0x1234u, K(p, "FlsGetValue", slot));
            Assert.Equal(1u, K(p, "FlsFree", slot));
        }

        [Fact]
        public void SListPushesAndPopsInLifoOrder()
        {
            var p = NewProcess(out var k);
            var head = k.Heap.Alloc(8);
            uint a = k.Heap.Alloc(8), b = k.Heap.Alloc(8);
            K(p, "InitializeSListHead", head);
            K(p, "InterlockedPushEntrySList", head, a);
            K(p, "InterlockedPushEntrySList", head, b);
            Assert.Equal(2u, K(p, "QueryDepthSList", head));
            Assert.Equal(b, K(p, "InterlockedPopEntrySList", head));
            Assert.Equal(a, K(p, "InterlockedPopEntrySList", head));
            Assert.Equal(0u, K(p, "InterlockedPopEntrySList", head));
        }

        [Fact]
        public void LocaleQueriesAnswerForEnglishUs()
        {
            var p = NewProcess(out var k);
            var buffer = k.Heap.Alloc(64);
            Assert.Equal(5u, K(p, "GetLocaleInfoW", 0x409, 0x1004, buffer, 32));   // "1252" + NUL
            Assert.Equal("1252", p.Memory.ReadUnicode(buffer));
            Assert.Equal(2u, K(p, "GetLocaleInfoW", 0x409, 0x20001004, buffer, 32)); // as a number
            Assert.Equal(1252u, p.Memory.Read32(buffer));

            Assert.Equal(2u, K(p, "CompareStringW", 0x409, 1 /*NORM_IGNORECASE*/, Wide(k, p, "ABC"), 0xFFFFFFFF, Wide(k, p, "abc"), 0xFFFFFFFF));
            Assert.Equal(1u, K(p, "CompareStringW", 0x409, 0, Wide(k, p, "a"), 0xFFFFFFFF, Wide(k, p, "b"), 0xFFFFFFFF));

            var upper = k.Heap.Alloc(16);
            Assert.Equal(3u, K(p, "LCMapStringW", 0x409, 0x200 /*LCMAP_UPPERCASE*/, Wide(k, p, "wav"), 3, upper, 8));
            Assert.Equal('W', (char)p.Memory.Read16(upper));

            var types = k.Heap.Alloc(8);
            K(p, "GetStringTypeW", 1, Wide(k, p, "A1"), 2, types);
            Assert.Equal(0x0301, p.Memory.Read16(types) & 0x0301);   // upper, alpha, defined
            Assert.Equal(0x0204, p.Memory.Read16(types + 2) & 0x0204);   // digit, defined
        }

        [Fact]
        public void InitOnceRunsTheCallbackOnceThroughTheGuest()
        {
            var p = NewProcess(out var k);
            // A stdcall callback: inc dword [counter]; mov eax,1; ret 12
            var counter = k.Heap.Alloc(4);
            var code = p.Memory.FindFree(0x1000, 0x00600000);
            p.Memory.Map(code, 0x1000);
            var bytes = new List<byte> { 0xFF, 0x05 };
            bytes.AddRange(System.BitConverter.GetBytes(counter));
            bytes.AddRange(new byte[] { 0xB8, 1, 0, 0, 0, 0xC2, 12, 0 });
            p.Memory.WriteBytes(code, bytes.ToArray());

            var once = k.Heap.Alloc(4, zero: true);
            Assert.Equal(1u, K(p, "InitOnceExecuteOnce", once, code, 0, 0));
            Assert.Equal(1u, K(p, "InitOnceExecuteOnce", once, code, 0, 0));
            Assert.Equal(1u, p.Memory.Read32(counter));
        }

        [Fact]
        public void AutoResetEventSatisfiesOneWait()
        {
            var p = NewProcess(out _);
            var e = K(p, "CreateEventW", 0, 0 /*auto*/, 1 /*signalled*/, 0);
            Assert.Equal(0u, K(p, "WaitForSingleObject", e, 0xFFFFFFFF));
            Assert.Equal(0x102u, K(p, "WaitForSingleObject", e, 0));
            K(p, "SetEvent", e);
            Assert.Equal(0u, K(p, "WaitForSingleObject", e, 10));
            Assert.Equal(1u, K(p, "CloseHandle", e));
        }

        [Fact]
        public void RaiseExceptionStopsTheRunWithItsCode()
        {
            var p = NewProcess(out _);
            var sentinel = p.Imports.Bind("kernel32.dll", "RaiseException", -1);
            var result = p.Call(sentinel, out _, 1000, 0xE06D7363, 1, 0, 0);   // a C++ throw
            Assert.Equal(GuestStop.Raised, result.Stop);
            Assert.Equal(0xE06D7363u, result.ExitCode);
        }

        [Fact]
        public void DebugOutputReachesTheLog()
        {
            var p = NewProcess(out var k);
            var log = new List<string>();
            k.Log = log.Add;
            K(p, "OutputDebugStringW", Wide(k, p, "hello from the guest\n"));
            Assert.Equal(new[] { "hello from the guest" }, log);
        }

        [Fact]
        public void WinmmClockAdvancesWithRealTime()
        {
            var p = NewProcess(out _);
            var first = Call(p, "winmm.dll", "timeGetTime");
            System.Threading.Thread.Sleep(20);
            var second = Call(p, "winmm.dll", "timeGetTime");
            Assert.InRange(second - first, 10u, 5000u);
        }
    }
}
