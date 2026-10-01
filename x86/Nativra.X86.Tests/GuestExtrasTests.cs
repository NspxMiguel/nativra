using System;
using System.Collections.Generic;
using System.IO;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;
using Xunit;

namespace Nativra.X86.Tests
{
    /// <summary>
    /// imm32 with no IME, rpcrt4's UUIDs, drop-target registration, the absent
    /// mixer, the empty services database, _mbsrchr, the Toolhelp process list
    /// and SHDeleteKey, each reached through its import sentinel the way a game
    /// reaches it.
    /// </summary>
    public sealed class GuestExtrasTests : IDisposable
    {
        private const uint CurrentUser = 0x80000001;   // HKEY_CURRENT_USER
        private readonly string work = Path.Combine(Path.GetTempPath(), "nativra-extras-" + Guid.NewGuid().ToString("N"));
        private readonly GuestProcess p;
        private readonly GuestKernel k;

        public GuestExtrasTests()
        {
            Directory.CreateDirectory(work);
            p = new GuestProcess(new GuestMemory(), useJit: false);
            k = new GuestKernel(p) { ExePath = "C:\\game\\game.exe", Files = new HostFolderFiles("C:\\game", work) };
            k.Install();
        }

        public void Dispose()
        {
            p.Dispose();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }

        private uint Call(string module, string function, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind(module, function, -1), out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{function} stopped as {result}");
            return eax;
        }

        private uint CallOrdinal(string module, int ordinal, params uint[] args)
        {
            var result = p.Call(p.Imports.Bind(module, null, ordinal), out var eax, 1_000_000, args);
            Assert.True(result.Ok, $"{module}#{ordinal} stopped as {result}");
            return eax;
        }

        private uint LastError() => Call("kernel32.dll", "GetLastError");

        private uint Str(string text)
        {
            var ptr = k.Heap.Alloc((uint)text.Length + 1, zero: true);
            p.Memory.WriteAnsi(ptr, text);
            return ptr;
        }

        private uint WStr(string text)
        {
            var ptr = k.Heap.Alloc((uint)(text.Length + 1) * 2, zero: true);
            p.Memory.WriteUnicode(ptr, text);
            return ptr;
        }

        private static byte[] Filled(byte value, int count)
        {
            var bytes = new byte[count];
            for (var n = 0; n < count; n++) bytes[n] = value;
            return bytes;
        }

        // Data1 0x00112233, Data2 0x4455, Data3 0x6677, then 88 99 aa bb cc dd ee ff.
        private static readonly byte[] KnownUuid =
        {
            0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
        };
        private const string KnownUuidText = "00112233-4455-6677-8899-aabbccddeeff";

        private uint KnownUuidAt()
        {
            var uuid = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteBytes(uuid, KnownUuid);
            return uuid;
        }

        // --- the Windows signatures ------------------------------------------------------

        // A handler that pops the wrong number of dwords corrupts the guest's
        // stack, and Call unwinds its own frame whatever the callee did, so the
        // behavioural tests cannot see it: check the table against the headers.
        private static readonly (string Module, string Function, CallConv Conv, int Args)[] Signatures =
        {
            ("imm32.dll", "ImmGetContext", CallConv.Stdcall, 1),               // (hWnd)
            ("imm32.dll", "ImmReleaseContext", CallConv.Stdcall, 2),           // (hWnd, hIMC)
            ("imm32.dll", "ImmAssociateContext", CallConv.Stdcall, 2),         // (hWnd, hIMC)
            ("imm32.dll", "ImmGetConversionStatus", CallConv.Stdcall, 3),      // (hIMC, &conversion, &sentence)
            ("imm32.dll", "ImmSetConversionStatus", CallConv.Stdcall, 3),      // (hIMC, conversion, sentence)
            ("imm32.dll", "ImmNotifyIME", CallConv.Stdcall, 4),                // (hIMC, action, index, value)
            ("imm32.dll", "ImmSetCandidateWindow", CallConv.Stdcall, 2),       // (hIMC, &form)
            ("imm32.dll", "ImmGetProperty", CallConv.Stdcall, 2),              // (hKL, index)
            ("imm32.dll", "ImmGetCompositionStringA", CallConv.Stdcall, 4),    // (hIMC, index, buffer, length)
            ("imm32.dll", "ImmGetCompositionStringW", CallConv.Stdcall, 4),
            ("imm32.dll", "ImmSetCompositionStringA", CallConv.Stdcall, 6),    // (hIMC, index, comp, compLen, read, readLen)
            ("imm32.dll", "ImmSetCompositionStringW", CallConv.Stdcall, 6),
            ("imm32.dll", "ImmGetCandidateListA", CallConv.Stdcall, 4),        // (hIMC, index, list, length)
            ("imm32.dll", "ImmGetCandidateListW", CallConv.Stdcall, 4),
            ("imm32.dll", "ImmGetCandidateListCountA", CallConv.Stdcall, 2),   // (hIMC, &count)
            ("imm32.dll", "ImmGetCandidateListCountW", CallConv.Stdcall, 2),
            ("imm32.dll", "ImmIsUIMessageA", CallConv.Stdcall, 4),             // (hWnd, msg, wParam, lParam)
            ("imm32.dll", "ImmIsUIMessageW", CallConv.Stdcall, 4),
            ("imm32.dll", "ImmGetIMEFileNameA", CallConv.Stdcall, 3),          // (hKL, buffer, length)
            ("imm32.dll", "ImmGetIMEFileNameW", CallConv.Stdcall, 3),
            ("rpcrt4.dll", "UuidCreate", CallConv.Stdcall, 1),                 // (&uuid)
            ("rpcrt4.dll", "UuidToStringA", CallConv.Stdcall, 2),              // (&uuid, &string)
            ("rpcrt4.dll", "UuidToStringW", CallConv.Stdcall, 2),
            ("rpcrt4.dll", "UuidFromStringA", CallConv.Stdcall, 2),            // (string, &uuid)
            ("rpcrt4.dll", "UuidFromStringW", CallConv.Stdcall, 2),
            ("rpcrt4.dll", "RpcStringFreeA", CallConv.Stdcall, 1),             // (&string)
            ("rpcrt4.dll", "RpcStringFreeW", CallConv.Stdcall, 1),
            ("ole32.dll", "RegisterDragDrop", CallConv.Stdcall, 2),            // (hWnd, target)
            ("ole32.dll", "RevokeDragDrop", CallConv.Stdcall, 1),              // (hWnd)
            ("ole32.dll", "ReleaseStgMedium", CallConv.Stdcall, 1),            // (&medium)
            ("winmm.dll", "mixerGetDevCapsA", CallConv.Stdcall, 3),            // (id, &caps, size)
            ("winmm.dll", "mixerGetDevCapsW", CallConv.Stdcall, 3),
            ("winmm.dll", "mixerClose", CallConv.Stdcall, 1),                  // (hmx)
            ("winmm.dll", "mixerGetLineInfoA", CallConv.Stdcall, 3),           // (hmxobj, &line, flags)
            ("winmm.dll", "mixerGetLineInfoW", CallConv.Stdcall, 3),
            ("winmm.dll", "mixerGetLineControlsA", CallConv.Stdcall, 3),       // (hmxobj, &controls, flags)
            ("winmm.dll", "mixerGetLineControlsW", CallConv.Stdcall, 3),
            ("winmm.dll", "mixerGetControlDetailsA", CallConv.Stdcall, 3),     // (hmxobj, &details, flags)
            ("winmm.dll", "mixerGetControlDetailsW", CallConv.Stdcall, 3),
            ("winmm.dll", "mixerSetControlDetails", CallConv.Stdcall, 3),      // (hmxobj, &details, flags)
            ("ws2_32.dll", "getservbyport", CallConv.Stdcall, 2),              // (port, proto)
            ("wsock32.dll", "getservbyport", CallConv.Stdcall, 2),
            ("msvcrt.dll", "_mbsrchr", CallConv.Cdecl, 2),                     // (str, c)
            ("kernel32.dll", "Process32First", CallConv.Stdcall, 2),           // (snapshot, &entry)
            ("kernel32.dll", "Process32Next", CallConv.Stdcall, 2),
            ("kernel32.dll", "Process32FirstW", CallConv.Stdcall, 2),
            ("kernel32.dll", "Process32NextW", CallConv.Stdcall, 2),
            ("shlwapi.dll", "SHDeleteKeyA", CallConv.Stdcall, 2),              // (hkey, subkey)
            ("shlwapi.dll", "SHDeleteKeyW", CallConv.Stdcall, 2),
        };

        [Fact]
        public void EveryNewImportPopsTheArgumentsOfItsWindowsSignature()
        {
            foreach (var s in Signatures)
            {
                var sentinel = p.Imports.Bind(s.Module, s.Function, -1);
                Assert.True(p.Imports.TryResolve(sentinel, out var import), s.Module + "!" + s.Function);
                Assert.True(import.Handler != null, s.Module + "!" + s.Function + " has no handler");
                Assert.True(import.Handler.Conv == s.Conv, s.Module + "!" + s.Function + " calling convention");
                Assert.True(import.Handler.ArgDwords == s.Args, s.Module + "!" + s.Function + " takes " + s.Args + " arguments");
            }

            // The ordinal imports of getservbyport (ws2_32 and wsock32 number it 56).
            foreach (var module in new[] { "ws2_32.dll", "wsock32.dll" })
            {
                Assert.True(p.Imports.TryResolve(p.Imports.Bind(module, null, 56), out var byOrdinal));
                Assert.NotNull(byOrdinal.Handler);
                Assert.Equal(2, byOrdinal.Handler.ArgDwords);
            }
        }

        // --- imm32 ----------------------------------------------------------------------

        [Fact]
        public void Imm32AnswersAsWindowsDoesWithNoImeInstalled()
        {
            const uint window = 0x1234;
            Assert.Equal(0u, Call("imm32.dll", "ImmGetContext", window));
            Assert.Equal(1u, Call("imm32.dll", "ImmReleaseContext", window, 0));
            Assert.Equal(0u, Call("imm32.dll", "ImmAssociateContext", window, 0));

            // Nothing composed, no candidates.
            var buffer = k.Heap.Alloc(32, zero: true);
            Assert.Equal(0u, Call("imm32.dll", "ImmGetCompositionStringW", 0, 8 /* GCS_RESULTSTR */, buffer, 32));
            Assert.Equal(0u, Call("imm32.dll", "ImmGetCandidateListW", 0, 0, buffer, 32));
            var count = k.Heap.Alloc(4);
            p.Memory.Write32(count, 0xDEAD);
            Assert.Equal(0u, Call("imm32.dll", "ImmGetCandidateListCountW", 0, count));
            Assert.Equal(0u, p.Memory.Read32(count));
            Assert.Equal(0u, Call("imm32.dll", "ImmGetCandidateListCountA", 0, 0));   // no out parameter: fine

            // FALSE (or 0) for everything that would have to act on a context.
            Assert.Equal(0u, Call("imm32.dll", "ImmGetConversionStatus", 0, count, buffer));
            Assert.Equal(0u, Call("imm32.dll", "ImmSetConversionStatus", 0, 0, 0));
            Assert.Equal(0u, Call("imm32.dll", "ImmSetCompositionStringW", 0, 0, 0, 0, 0, 0));
            Assert.Equal(0u, Call("imm32.dll", "ImmNotifyIME", 0, 1, 0, 0));
            Assert.Equal(0u, Call("imm32.dll", "ImmSetCandidateWindow", 0, buffer));
            Assert.Equal(0u, Call("imm32.dll", "ImmIsUIMessageA", 0, 0x10F, 0, 0));
            Assert.Equal(0u, Call("imm32.dll", "ImmGetProperty", 0, 4));

            // No IME file to name: the buffer holds an empty string afterwards.
            p.Memory.WriteBytes(buffer, Filled((byte)'X', 8));
            Assert.Equal(0u, Call("imm32.dll", "ImmGetIMEFileNameA", 0, buffer, 32));
            Assert.Equal("", p.Memory.ReadAnsi(buffer));
            p.Memory.WriteBytes(buffer, Filled((byte)'X', 8));
            Assert.Equal(0u, Call("imm32.dll", "ImmGetIMEFileNameW", 0, buffer, 16));
            Assert.Equal("", p.Memory.ReadUnicode(buffer));
        }

        [Fact]
        public void Imm32FunctionsCanBeLookedUpByNameAtRuntime()
        {
            // How SDL reaches them: LoadLibrary, then GetProcAddress per function.
            var module = Call("kernel32.dll", "LoadLibraryA", Str("imm32.dll"));
            Assert.NotEqual(0u, module);
            foreach (var name in new[]
            {
                "ImmGetContext", "ImmReleaseContext", "ImmAssociateContext", "ImmGetCompositionStringW",
                "ImmGetCandidateListW", "ImmGetCandidateListCountW", "ImmSetCandidateWindow", "ImmNotifyIME",
                "ImmIsUIMessageA", "ImmGetProperty", "ImmGetIMEFileNameA", "ImmGetConversionStatus",
                "ImmSetConversionStatus", "ImmSetCompositionStringW",
            })
                Assert.True(GuestImports.InRegion(Call("kernel32.dll", "GetProcAddress", module, Str(name))), name);
        }

        // --- rpcrt4 ---------------------------------------------------------------------

        [Fact]
        public void UuidCreateMakesAVersion4UuidThatRoundTripsThroughItsString()
        {
            var first = k.Heap.Alloc(16, zero: true);
            var second = k.Heap.Alloc(16, zero: true);
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidCreate", first));
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidCreate", second));
            var bytes = p.Memory.ReadBytes(first, 16);
            Assert.Equal(4, bytes[7] >> 4);   // version 4: the top nibble of Data3, whose high byte is byte 7
            Assert.Equal(2, bytes[8] >> 6);   // RFC 4122 variant: the top bits of Data4[0]
            Assert.NotEqual(bytes, p.Memory.ReadBytes(second, 16));

            var slot = k.Heap.Alloc(4, zero: true);
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidToStringA", first, slot));
            var text = p.Memory.Read32(slot);
            Assert.True(k.Heap.Owns(text));
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", p.Memory.ReadAnsi(text));

            var back = k.Heap.Alloc(16, zero: true);
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidFromStringA", text, back));
            Assert.Equal(bytes, p.Memory.ReadBytes(back, 16));

            // RpcStringFree gives the block back and clears the caller's pointer.
            Assert.True(k.Heap.SizeOf(text) > 0);
            Assert.Equal(0u, Call("rpcrt4.dll", "RpcStringFreeA", slot));
            Assert.Equal(0u, p.Memory.Read32(slot));
            Assert.Equal(0u, k.Heap.SizeOf(text));
            Assert.Equal(0u, Call("rpcrt4.dll", "RpcStringFreeA", slot));   // a cleared pointer frees nothing and is fine
        }

        [Fact]
        public void UuidStringsFollowTheGuidFieldOrderInEitherCase()
        {
            var uuid = KnownUuidAt();
            var slot = k.Heap.Alloc(4, zero: true);
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidToStringA", uuid, slot));
            Assert.Equal(KnownUuidText, p.Memory.ReadAnsi(p.Memory.Read32(slot)));

            var parsed = k.Heap.Alloc(16, zero: true);
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidFromStringA", Str(KnownUuidText.ToUpperInvariant()), parsed));
            Assert.Equal(KnownUuid, p.Memory.ReadBytes(parsed, 16));

            // A null UUID reads as the nil UUID.
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidToStringA", 0, slot));
            Assert.Equal("00000000-0000-0000-0000-000000000000", p.Memory.ReadAnsi(p.Memory.Read32(slot)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("00112233-4455-6677-8899-aabbccddeef")]        // one digit short
        [InlineData("00112233-4455-6677-8899-aabbccddeeff0")]      // one digit long
        [InlineData("{00112233-4455-6677-8899-aabbccddeeff}")]     // braces are GUID text, not UUID text
        [InlineData(" 00112233-4455-6677-8899-aabbccddeef")]       // right length, a space in front
        [InlineData("00112233 4455-6677-8899-aabbccddeeff")]       // not a dash
        [InlineData("0011223-34455-6677-8899-aabbccddeeff")]       // dash in the wrong place
        [InlineData("0011223g-4455-6677-8899-aabbccddeeff")]       // not a hex digit
        public void UuidFromStringRefusesAnythingButTheDashedForm(string text)
        {
            var uuid = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteBytes(uuid, Filled(0xEE, 16));
            Assert.Equal(1705u, Call("rpcrt4.dll", "UuidFromStringA", Str(text), uuid));   // RPC_S_INVALID_STRING_UUID
            Assert.Equal(Filled(0xEE, 16), p.Memory.ReadBytes(uuid, 16));                 // nothing written
        }

        [Fact]
        public void UuidFromStringTakesANullStringAsTheNilUuid()
        {
            var uuid = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteBytes(uuid, Filled(0xEE, 16));
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidFromStringA", 0, uuid));
            Assert.Equal(new byte[16], p.Memory.ReadBytes(uuid, 16));
        }

        [Fact]
        public void TheWideUuidCallsAgreeWithTheNarrowOnes()
        {
            var uuid = KnownUuidAt();
            var slot = k.Heap.Alloc(4, zero: true);
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidToStringW", uuid, slot));
            var text = p.Memory.Read32(slot);
            Assert.Equal(KnownUuidText, p.Memory.ReadUnicode(text));

            var back = k.Heap.Alloc(16, zero: true);
            Assert.Equal(0u, Call("rpcrt4.dll", "UuidFromStringW", text, back));
            Assert.Equal(KnownUuid, p.Memory.ReadBytes(back, 16));
            Assert.Equal(1705u, Call("rpcrt4.dll", "UuidFromStringW", WStr("not a uuid"), back));

            Assert.Equal(0u, Call("rpcrt4.dll", "RpcStringFreeW", slot));
            Assert.Equal(0u, p.Memory.Read32(slot));
            Assert.Equal(0u, k.Heap.SizeOf(text));
        }

        // --- ole32 ----------------------------------------------------------------------

        [Fact]
        public void DragDropRegistrationSucceedsAndReleasingAMediumTouchesNothing()
        {
            var target = k.Heap.Alloc(16, zero: true);
            Assert.Equal(0u, Call("ole32.dll", "RegisterDragDrop", 0x1234, target));   // S_OK
            Assert.Equal(0u, Call("ole32.dll", "RevokeDragDrop", 0x1234));

            var medium = k.Heap.Alloc(16, zero: true);
            p.Memory.WriteBytes(medium, Filled(0x5A, 16));
            Call("ole32.dll", "ReleaseStgMedium", medium);
            Assert.Equal(Filled(0x5A, 16), p.Memory.ReadBytes(medium, 16));
        }

        // --- winmm ----------------------------------------------------------------------

        [Fact]
        public void TheMixerHasNoDeviceAndNoHandleCanBeOpen()
        {
            // The two calls Libraries.cs answers, and the rest agreeing with them.
            Assert.Equal(0u, Call("winmm.dll", "mixerGetNumDevs"));
            var handle = k.Heap.Alloc(4, zero: true);
            Assert.Equal(2u, Call("winmm.dll", "mixerOpen", handle, 0, 0, 0, 0));   // MMSYSERR_BADDEVICEID
            Assert.Equal(0u, p.Memory.Read32(handle));

            var caps = k.Heap.Alloc(64, zero: true);
            Assert.Equal(2u, Call("winmm.dll", "mixerGetDevCapsA", 0, caps, 64));
            Assert.Equal(2u, Call("winmm.dll", "mixerGetDevCapsW", 0, caps, 64));
            foreach (var name in new[]
            {
                "mixerGetLineInfoA", "mixerGetLineInfoW", "mixerGetLineControlsA", "mixerGetLineControlsW",
                "mixerGetControlDetailsA", "mixerGetControlDetailsW", "mixerSetControlDetails",
            })
                Assert.True(Call("winmm.dll", name, 0x1234, caps, 0) == 5u, name + " should say MMSYSERR_INVALHANDLE");
            Assert.Equal(5u, Call("winmm.dll", "mixerClose", 0x1234));

            Assert.Equal(new byte[64], p.Memory.ReadBytes(caps, 64));   // no device description was made up
        }

        // --- ws2_32 ---------------------------------------------------------------------

        [Fact]
        public void GetservbyportKnowsWellKnownPortsAndSaysWsaNoDataOtherwise()
        {
            const uint port80 = 0x5000, port12345 = 0x3930;   // in network byte order
            // A program starts Winsock first; without it the answer is WSANOTINITIALISED.
            Assert.Equal(0u, Call("ws2_32.dll", "WSAStartup", 0x0202, k.Heap.Alloc(400, zero: true)));
            var http = Call("ws2_32.dll", "getservbyport", port80, Str("tcp"));
            Assert.NotEqual(0u, http);
            Assert.Equal("http", p.Memory.ReadAnsi(p.Memory.Read32(http)));   // servent.s_name

            Assert.Equal(0u, Call("ws2_32.dll", "getservbyport", port12345, Str("tcp")));
            Assert.Equal(11004u, Call("ws2_32.dll", "WSAGetLastError"));   // WSANO_DATA

            // wsock32 and the import by ordinal 56 answer the same, through the same last-error slot.
            Call("ws2_32.dll", "WSASetLastError", 0);
            Assert.Equal(0u, Call("wsock32.dll", "getservbyport", port12345, 0));
            Assert.Equal(11004u, Call("wsock32.dll", "WSAGetLastError"));
            Call("ws2_32.dll", "WSASetLastError", 0);
            Assert.Equal(0u, CallOrdinal("ws2_32.dll", 56, port12345, 0));
            Assert.Equal(11004u, Call("ws2_32.dll", "WSAGetLastError"));
        }

        // --- msvcrt ---------------------------------------------------------------------

        [Fact]
        public void MbsrchrFindsTheLastOccurrenceAsStrrchrDoes()
        {
            const string text = "C:\\game\\data\\save.dat";
            var s = Str(text);
            Assert.Equal(s + (uint)text.LastIndexOf('\\'), Call("msvcrt.dll", "_mbsrchr", s, '\\'));
            Assert.Equal(s + (uint)text.LastIndexOf('a'), Call("msvcrt.dll", "_mbsrchr", s, 'a'));
            Assert.Equal(s + 3, Call("msvcrt.dll", "_mbsrchr", s, 'g'));   // a single occurrence
            Assert.Equal(0u, Call("msvcrt.dll", "_mbsrchr", s, 'z'));
            Assert.Equal(s + (uint)text.Length, Call("msvcrt.dll", "_mbsrchr", s, 0));   // the terminator counts

            // Bytes above 0x7F compare as unsigned bytes, and c is cut to a byte, so a
            // char widened through a sign-extending int still finds itself.
            var high = k.Heap.Alloc(8, zero: true);
            p.Memory.WriteBytes(high, new byte[] { (byte)'a', 0xE9, (byte)'b', 0xE9, (byte)'c' });
            Assert.Equal(high + 3, Call("msvcrt.dll", "_mbsrchr", high, 0xE9));
            Assert.Equal(high + 3, Call("msvcrt.dll", "_mbsrchr", high, 0xFFFFFFE9));
        }

        [Fact]
        public void MbsrchrOfANullStringIsNullWithEinval()
        {
            var errno = Call("msvcrt.dll", "_errno");
            p.Memory.Write32(errno, 0);
            Assert.Equal(0u, Call("msvcrt.dll", "_mbsrchr", 0, '\\'));
            Assert.Equal(22u, p.Memory.Read32(errno));   // EINVAL
        }

        // --- Toolhelp -------------------------------------------------------------------

        [Fact]
        public void Process32ListsThisProcessOnceThenReportsNoMoreFiles()
        {
            var snapshot = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x2 /* TH32CS_SNAPPROCESS */, 0);
            Assert.NotEqual(0xFFFFFFFFu, snapshot);
            var entry = k.Heap.Alloc(296, zero: true);
            p.Memory.Write32(entry, 296);

            Assert.Equal(1u, Call("kernel32.dll", "Process32First", snapshot, entry));
            Assert.Equal(296u, p.Memory.Read32(entry));                       // dwSize is the caller's
            Assert.Equal(0u, p.Memory.Read32(entry + 0x04));                  // cntUsage
            Assert.Equal(GuestProcess.ProcessId, p.Memory.Read32(entry + 0x08));
            Assert.Equal(0u, p.Memory.Read32(entry + 0x0C));                  // th32DefaultHeapID
            Assert.Equal(0u, p.Memory.Read32(entry + 0x10));                  // th32ModuleID
            Assert.Equal(1u, p.Memory.Read32(entry + 0x14));                  // cntThreads: the main thread alone
            Assert.Equal(0u, p.Memory.Read32(entry + 0x18));                  // th32ParentProcessID
            Assert.Equal(8u, p.Memory.Read32(entry + 0x1C));                  // pcPriClassBase
            Assert.Equal(0u, p.Memory.Read32(entry + 0x20));                  // dwFlags
            Assert.Equal("game.exe", p.Memory.ReadAnsi(entry + 0x24));        // the file name, not the path

            Assert.Equal(0u, Call("kernel32.dll", "Process32Next", snapshot, entry));
            Assert.Equal(18u, LastError());                                   // ERROR_NO_MORE_FILES
            Assert.Equal(0u, Call("kernel32.dll", "Process32Next", snapshot, entry));
            Assert.Equal(18u, LastError());

            // First starts the walk over.
            Assert.Equal(1u, Call("kernel32.dll", "Process32First", snapshot, entry));
            Assert.Equal(0u, Call("kernel32.dll", "Process32Next", snapshot, entry));
        }

        [Fact]
        public void Process32WFillsTheWideEntry()
        {
            var snapshot = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x2, 0);
            var entry = k.Heap.Alloc(556, zero: true);
            p.Memory.Write32(entry, 556);

            Assert.Equal(1u, Call("kernel32.dll", "Process32FirstW", snapshot, entry));
            Assert.Equal(GuestProcess.ProcessId, p.Memory.Read32(entry + 0x08));
            Assert.Equal(1u, p.Memory.Read32(entry + 0x14));
            Assert.Equal("game.exe", p.Memory.ReadUnicode(entry + 0x24));
            Assert.Equal(0u, Call("kernel32.dll", "Process32NextW", snapshot, entry));
            Assert.Equal(18u, LastError());
        }

        [Theory]
        [InlineData(false, 296u)]
        [InlineData(true, 556u)]
        public void ALongFileNameIsCutToTheFieldAndNeverWrittenPastIt(bool wide, uint size)
        {
            k.ExePath = "C:\\game\\" + new string('a', 300) + ".exe";
            var snapshot = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x2, 0);
            var entry = k.Heap.Alloc(size + 16, zero: true);
            p.Memory.Write32(entry, size);
            p.Memory.WriteBytes(entry + size, Filled(0xAA, 16));

            Assert.Equal(1u, Call("kernel32.dll", wide ? "Process32FirstW" : "Process32First", snapshot, entry));
            var name = wide ? p.Memory.ReadUnicode(entry + 0x24, 1000) : p.Memory.ReadAnsi(entry + 0x24, 1000);
            Assert.Equal(259, name.Length);                                      // MAX_PATH - 1, then the NUL
            Assert.Equal(Filled(0xAA, 16), p.Memory.ReadBytes(entry + size, 16));
        }

        [Fact]
        public void Process32RefusesAnEntryTooSmallAndAHandleThatIsNotASnapshot()
        {
            var snapshot = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x2, 0);
            var entry = k.Heap.Alloc(560, zero: true);

            p.Memory.Write32(entry, 295);
            Assert.Equal(0u, Call("kernel32.dll", "Process32First", snapshot, entry));
            Assert.Equal(87u, LastError());                                      // ERROR_INVALID_PARAMETER
            p.Memory.Write32(entry, 296);
            Assert.Equal(0u, Call("kernel32.dll", "Process32FirstW", snapshot, entry));   // the A size is short for the W entry
            Assert.Equal(87u, LastError());
            Assert.Equal(0u, Call("kernel32.dll", "Process32First", snapshot, 0));
            Assert.Equal(87u, LastError());

            Assert.Equal(0u, Call("kernel32.dll", "Process32First", 0x4444, entry));
            Assert.Equal(6u, LastError());                                       // ERROR_INVALID_HANDLE

            // A closed snapshot is no snapshot any more, and the next one is unaffected.
            Assert.Equal(1u, Call("kernel32.dll", "CloseHandle", snapshot));
            Assert.Equal(0u, Call("kernel32.dll", "Process32First", snapshot, entry));
            Assert.Equal(6u, LastError());
            var next = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x2, 0);
            Assert.NotEqual(snapshot, next);
            Assert.Equal(1u, Call("kernel32.dll", "Process32First", next, entry));
            Assert.Equal(0u, Call("kernel32.dll", "Process32First", snapshot, entry));
            Assert.Equal(6u, LastError());
        }

        [Fact]
        public void ACombinedSnapshotKeepsListingThreadsAndAlsoListsTheProcess()
        {
            var worker = p.CreateThread(0x00600000, 0, 0, suspended: true);   // a second live thread, never run
            var both = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x6 /* THREAD | PROCESS */, 0);
            Assert.NotEqual(0xFFFFFFFFu, both);

            var thread = k.Heap.Alloc(28, zero: true);
            p.Memory.Write32(thread, 28);
            var ids = new List<uint>();
            var more = Call("kernel32.dll", "Thread32First", both, thread);
            while (more == 1)
            {
                Assert.Equal(GuestProcess.ProcessId, p.Memory.Read32(thread + 0x0C));
                ids.Add(p.Memory.Read32(thread + 0x08));
                more = Call("kernel32.dll", "Thread32Next", both, thread);
            }
            Assert.Equal(18u, LastError());
            Assert.Equal(new[] { GuestProcess.MainThreadId, worker.Id }, ids);

            var entry = k.Heap.Alloc(296, zero: true);
            p.Memory.Write32(entry, 296);
            Assert.Equal(1u, Call("kernel32.dll", "Process32First", both, entry));
            Assert.Equal(2u, p.Memory.Read32(entry + 0x14));                   // both threads are live
            Assert.Equal(0u, Call("kernel32.dll", "Process32Next", both, entry));
            Assert.Equal(18u, LastError());

            // TH32CS_SNAPALL asks for heaps and modules too, which are not listed: the rest still works.
            var all = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0xF, 0);
            Assert.NotEqual(0xFFFFFFFFu, all);
            Assert.Equal(1u, Call("kernel32.dll", "Process32First", all, entry));
            Assert.Equal(1u, Call("kernel32.dll", "Thread32First", all, thread));
        }

        [Fact]
        public void ASnapshotListsOnlyWhatItWasAskedFor()
        {
            var entry = k.Heap.Alloc(296, zero: true);
            p.Memory.Write32(entry, 296);
            var thread = k.Heap.Alloc(28, zero: true);
            p.Memory.Write32(thread, 28);

            var threadsOnly = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x4, 0);
            Assert.Equal(1u, Call("kernel32.dll", "Thread32First", threadsOnly, thread));
            Assert.Equal(0u, Call("kernel32.dll", "Process32First", threadsOnly, entry));
            Assert.Equal(18u, LastError());                                    // no process information in it

            var processesOnly = Call("kernel32.dll", "CreateToolhelp32Snapshot", 0x2, 0);
            Assert.Equal(1u, Call("kernel32.dll", "Process32First", processesOnly, entry));
            Assert.Equal(0u, Call("kernel32.dll", "Thread32First", processesOnly, thread));
            Assert.Equal(18u, LastError());                                    // no thread information in it
        }

        [Theory]
        [InlineData(0x0u)]
        [InlineData(0x1u)]    // TH32CS_SNAPHEAPLIST
        [InlineData(0x8u)]    // TH32CS_SNAPMODULE
        [InlineData(0x10u)]   // TH32CS_SNAPMODULE32
        public void ASnapshotOfOnlyWhatIsNotListedFailsAsNotSupported(uint flags)
        {
            Assert.Equal(0xFFFFFFFFu, Call("kernel32.dll", "CreateToolhelp32Snapshot", flags, 0));
            Assert.Equal(50u, LastError());                                    // ERROR_NOT_SUPPORTED
        }

        // --- shlwapi --------------------------------------------------------------------

        [Fact]
        public void SHDeleteKeyRemovesTheKeyAndEverythingBeneathItFromTheRegistry()
        {
            var key = k.Heap.Alloc(4, zero: true);
            foreach (var path in new[] { "Software\\Studio\\Game\\Video", "Software\\Studio\\Audio", "Software\\Other\\Sub\\Deep" })
                Assert.Equal(0u, Call("advapi32.dll", "RegCreateKeyExA", CurrentUser, Str(path), 0, 0, 0, 0xF003F, 0, key, 0));
            uint Open(string path) => Call("advapi32.dll", "RegOpenKeyExA", CurrentUser, Str(path), 0, 0x20019, key);
            Assert.Equal(0u, Open("Software\\Studio\\Game\\Video"));

            Assert.Equal(0u, Call("shlwapi.dll", "SHDeleteKeyA", CurrentUser, Str("Software\\Studio")));
            Assert.Equal(2u, Open("Software\\Studio\\Game\\Video"));           // gone with its subkeys
            Assert.Equal(2u, Open("Software\\Studio\\Audio"));
            Assert.Equal(2u, Open("Software\\Studio"));
            Assert.Equal(0u, Open("Software\\Other\\Sub\\Deep"));              // a neighbour is untouched
            Assert.Equal(0u, Open("Software"));

            // The deletion reached the file the registry lives in, not only memory.
            var saved = File.ReadAllText(Path.Combine(work, "nativra-registry.txt"));
            Assert.DoesNotContain("HKCU\\Software\\Studio", saved);
            Assert.Contains("HKCU\\Software\\Other\\Sub\\Deep", saved);

            Assert.Equal(2u, Call("shlwapi.dll", "SHDeleteKeyA", CurrentUser, Str("Software\\Studio")));   // ERROR_FILE_NOT_FOUND
        }

        [Fact]
        public void SHDeleteKeyWorksFromAnOpenKeyAndInTheWideForm()
        {
            var key = k.Heap.Alloc(4, zero: true);
            Assert.Equal(0u, Call("advapi32.dll", "RegCreateKeyExA", CurrentUser, Str("Software\\Other\\Sub\\Deep"), 0, 0, 0, 0xF003F, 0, key, 0));
            Assert.Equal(0u, Call("advapi32.dll", "RegOpenKeyExA", CurrentUser, Str("Software\\Other"), 0, 0x20019, key));
            var other = p.Memory.Read32(key);

            Assert.Equal(0u, Call("shlwapi.dll", "SHDeleteKeyW", other, WStr("Sub")));
            Assert.Equal(2u, Call("advapi32.dll", "RegOpenKeyExA", CurrentUser, Str("Software\\Other\\Sub\\Deep"), 0, 0x20019, key));
            Assert.Equal(0u, Call("advapi32.dll", "RegOpenKeyExA", CurrentUser, Str("Software\\Other"), 0, 0x20019, key));   // the open key stays
        }

        [Fact]
        public void SHDeleteKeyRefusesNoNameAndAnInvalidHandleAndDeletesNothing()
        {
            var key = k.Heap.Alloc(4, zero: true);
            Assert.Equal(0u, Call("advapi32.dll", "RegCreateKeyExA", CurrentUser, Str("Software\\Studio"), 0, 0, 0, 0xF003F, 0, key, 0));

            // A missing or empty name is not "this key": it must not empty a hive.
            Assert.Equal(87u, Call("shlwapi.dll", "SHDeleteKeyA", CurrentUser, 0));      // ERROR_INVALID_PARAMETER
            Assert.Equal(87u, Call("shlwapi.dll", "SHDeleteKeyA", CurrentUser, Str("")));
            Assert.Equal(87u, Call("shlwapi.dll", "SHDeleteKeyW", CurrentUser, WStr("\\")));
            Assert.Equal(0u, Call("advapi32.dll", "RegOpenKeyExA", CurrentUser, Str("Software\\Studio"), 0, 0x20019, key));

            Assert.Equal(6u, Call("shlwapi.dll", "SHDeleteKeyA", 0xDEAD, Str("Software")));   // ERROR_INVALID_HANDLE
        }
    }
}
