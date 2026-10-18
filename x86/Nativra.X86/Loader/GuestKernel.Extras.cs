using System;

namespace Nativra.X86.Loader
{
    // Corners of the system DLLs that a game reaches once, at start-up or on a
    // settings screen, and that decide whether it carries on: text-input
    // probes (imm32), UUIDs (rpcrt4), drop-target registration (ole32), the
    // audio mixer (winmm), the services database (ws2_32), one multibyte
    // string call (msvcrt), the Toolhelp process list and SHDeleteKey over
    // the emulated registry. Where the console lacks the capability (no input
    // method, no mixer device, no services file) the call fails the way
    // Windows fails for its absence, never succeeding with nothing behind it.
    public sealed partial class GuestKernel
    {
        private void InstallExtras(GuestImports i)
        {
            InstallImm32(i);
            InstallRpcrt4(i);
            InstallDragDrop(i);
            InstallMixer(i);
            InstallServiceLookup(i);
            InstallMultibyte(i);
            InstallProcessList(i);
            InstallShlwapi(i);
        }

        // --- imm32 -----------------------------------------------------------------
        // The console has no input method editor, so no window has an input
        // context and nothing is ever being composed. These are the answers
        // Windows gives with no IME installed; engines that probe them at
        // start-up (SDL does) find nothing to compose.

        private void InstallImm32(GuestImports i)
        {
            const string m = "imm32.dll";
            i.Register(m, "ImmGetContext", CallConv.Stdcall, 1, c => 0);
            i.Register(m, "ImmReleaseContext", CallConv.Stdcall, 2, c => 1);
            i.Register(m, "ImmAssociateContext", CallConv.Stdcall, 2, c => 0);   // the context it replaced: none
            i.Register(m, "ImmGetConversionStatus", CallConv.Stdcall, 3, c => 0);
            i.Register(m, "ImmSetConversionStatus", CallConv.Stdcall, 3, c => 0);
            i.Register(m, "ImmNotifyIME", CallConv.Stdcall, 4, c => 0);
            i.Register(m, "ImmSetCandidateWindow", CallConv.Stdcall, 2, c => 0);
            i.Register(m, "ImmGetProperty", CallConv.Stdcall, 2, c => 0);
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var x = wide ? "W" : "A";
                i.Register(m, "ImmGetCompositionString" + x, CallConv.Stdcall, 4, c => 0);   // nothing composed
                i.Register(m, "ImmSetCompositionString" + x, CallConv.Stdcall, 6, c => 0);
                i.Register(m, "ImmGetCandidateList" + x, CallConv.Stdcall, 4, c => 0);
                i.Register(m, "ImmGetCandidateListCount" + x, CallConv.Stdcall, 2, c =>
                {
                    if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0);
                    return 0;
                });
                i.Register(m, "ImmIsUIMessage" + x, CallConv.Stdcall, 4, c => 0);
                i.Register(m, "ImmGetIMEFileName" + x, CallConv.Stdcall, 3, c =>
                {
                    // No IME file to name: an empty string, not whatever the buffer held.
                    if (c.Arg(1) != 0 && c.Arg(2) != 0) WriteText(c.Arg(1), "", w);
                    return 0;
                });
            }
        }

        // --- rpcrt4: UUIDs ----------------------------------------------------------
        // A UUID is the GUID layout: Data1, Data2 and Data3 little-endian, then
        // eight bytes. Its text is lowercase 8-4-4-4-12, in a block from the guest
        // heap that RpcStringFree gives back.

        private void InstallRpcrt4(GuestImports i)
        {
            const string r = "rpcrt4.dll";
            i.Register(r, "UuidCreate", CallConv.Stdcall, 1, c => UuidCreate(c.Arg(0)));
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var x = wide ? "W" : "A";
                i.Register(r, "UuidToString" + x, CallConv.Stdcall, 2, c => UuidToString(c.Arg(0), c.Arg(1), w));
                i.Register(r, "UuidFromString" + x, CallConv.Stdcall, 2, c => UuidFromString(c.Arg(0), c.Arg(1), w));
                i.Register(r, "RpcStringFree" + x, CallConv.Stdcall, 1, c => RpcStringFree(c.Arg(0)));
            }
        }

        private uint UuidCreate(uint uuid)
        {
            var bytes = new byte[16];
            random.GetBytes(bytes);
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);   // version 4: the top nibble of Data3
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant: the top bits of Data4[0]
            memory.WriteBytes(uuid, bytes);
            return 0;   // RPC_S_OK
        }

        private uint UuidToString(uint uuid, uint result, bool wide)
        {
            // A null UUID reads as the nil UUID.
            var text = (uuid == 0 ? Guid.Empty : new Guid(memory.ReadBytes(uuid, 16))).ToString("D");
            var buffer = heap.Alloc((uint)(text.Length + 1) * (wide ? 2u : 1u));
            if (buffer == 0) return 14;   // RPC_S_OUT_OF_MEMORY
            WriteText(buffer, text, wide);
            memory.Write32(result, buffer);
            return 0;
        }

        private uint UuidFromString(uint text, uint uuid, bool wide)
        {
            if (text == 0) { memory.WriteBytes(uuid, new byte[16]); return 0; }   // a null string is the nil UUID
            var s = ReadText(text, wide);
            if (!IsUuidText(s)) return 1705;   // RPC_S_INVALID_STRING_UUID
            memory.WriteBytes(uuid, new Guid(s).ToByteArray());
            return 0;
        }

        /// <summary>The one form UuidFromString takes: 8-4-4-4-12 hex digits, no braces and no spaces.</summary>
        private static bool IsUuidText(string s)
        {
            if (s.Length != 36) return false;
            for (var n = 0; n < 36; n++)
            {
                if (n == 8 || n == 13 || n == 18 || n == 23) { if (s[n] != '-') return false; }
                else if (!Uri.IsHexDigit(s[n])) return false;
            }
            return true;
        }

        private uint RpcStringFree(uint slot)
        {
            heap.Free(memory.Read32(slot));
            memory.Write32(slot, 0);
            return 0;   // RPC_S_OK
        }

        // --- ole32: drag and drop -----------------------------------------------------
        // Nothing on the console can start a drag, so a registered target is never
        // called: registering succeeds and no drop ever arrives, which is the true
        // state and not a stand-in. A medium arrives with a drop or a paste and
        // neither happens here; one the guest filled itself is left alone, not freed.

        private void InstallDragDrop(GuestImports i)
        {
            const string o = "ole32.dll";
            i.Register(o, "RegisterDragDrop", CallConv.Stdcall, 2, c => 0);   // S_OK
            i.Register(o, "RevokeDragDrop", CallConv.Stdcall, 1, c => 0);
            i.Register(o, "ReleaseStgMedium", CallConv.Stdcall, 1, c => 0);
        }

        // --- winmm: the mixer ----------------------------------------------------------
        // There is no mixer device (mixerGetNumDevs answers 0 and mixerOpen
        // MMSYSERR_BADDEVICEID, in GuestKernel.Libraries.cs): an ID names nothing,
        // and with no handle ever open every call on one is a bad handle.

        private void InstallMixer(GuestImports i)
        {
            const string w = "winmm.dll";
            foreach (var x in new[] { "A", "W" })
            {
                i.Register(w, "mixerGetDevCaps" + x, CallConv.Stdcall, 3, c => 2);   // MMSYSERR_BADDEVICEID
                i.Register(w, "mixerGetLineInfo" + x, CallConv.Stdcall, 3, c => 5);   // MMSYSERR_INVALHANDLE
                i.Register(w, "mixerGetLineControls" + x, CallConv.Stdcall, 3, c => 5);
                i.Register(w, "mixerGetControlDetails" + x, CallConv.Stdcall, 3, c => 5);
            }
            i.Register(w, "mixerClose", CallConv.Stdcall, 1, c => 5);
            i.Register(w, "mixerSetControlDetails", CallConv.Stdcall, 3, c => 5);
        }

        // --- ws2_32: the services database -----------------------------------------------
        // The console has no services file, so no port has a name: NULL with
        // WSANO_DATA, the answer for a port the database does not list.

        private void InstallServiceLookup(GuestImports i)
        {
            const uint NoData = 11004;   // WSANO_DATA
            HostCall none = c => { socketError = NoData; return 0; };
            foreach (var dll in new[] { "ws2_32.dll", "wsock32.dll" })
            {
                i.Register(dll, "getservbyport", CallConv.Stdcall, 2, none);
                i.RegisterOrdinal(dll, 56, CallConv.Stdcall, 2, none);
            }
        }

        // --- msvcrt: multibyte strings -----------------------------------------------------

        private void InstallMultibyte(GuestImports i)
        {
            // The "C" locale's code page is single-byte here (as for mbstowcs), so a
            // multibyte string is a byte string and _mbsrchr is strrchr, with the
            // CRT's check for a null string.
            C(i, "_mbsrchr", 2, c =>
            {
                if (c.Arg(0) == 0) { SetErrno(Einval); return 0; }
                var ch = (byte)c.Arg(1);
                uint found = 0;
                for (var p = c.Arg(0); ; p++)
                {
                    var b = memory.Read8(p);
                    if (b == ch) found = p;
                    if (b == 0) return found;
                }
            });
        }

        // --- Toolhelp: the process list ------------------------------------------------------
        // The console runs this one process, so a snapshot that asks for processes
        // lists exactly it. The snapshot itself is made in GuestKernel.SystemInfo.cs.

        private void InstallProcessList(GuestImports i)
        {
            const string k = "kernel32.dll";
            i.Register(k, "Process32First", CallConv.Stdcall, 2, c => NextProcessEntry(c.Arg(0), c.Arg(1), true, false));
            i.Register(k, "Process32Next", CallConv.Stdcall, 2, c => NextProcessEntry(c.Arg(0), c.Arg(1), false, false));
            i.Register(k, "Process32FirstW", CallConv.Stdcall, 2, c => NextProcessEntry(c.Arg(0), c.Arg(1), true, true));
            i.Register(k, "Process32NextW", CallConv.Stdcall, 2, c => NextProcessEntry(c.Arg(0), c.Arg(1), false, true));
        }

        // PROCESSENTRY32 (A: 296 bytes, W: 556): dwSize +0, cntUsage +4,
        // th32ProcessID +8, th32DefaultHeapID +0xC, th32ModuleID +0x10,
        // cntThreads +0x14, th32ParentProcessID +0x18, pcPriClassBase +0x1C,
        // dwFlags +0x20, szExeFile[260] +0x24.
        private uint NextProcessEntry(uint snapshot, uint entry, bool first, bool wide)
        {
            const uint HeaderBytes = 0x24, MaxPath = 260;
            const int Processes = 1;
            if (!threadSnapshots.ContainsKey(snapshot)) { process.LastError = ErrorInvalidHandle; return 0; }
            if (entry == 0 || memory.Read32(entry) < HeaderBytes + MaxPath * (wide ? 2u : 1u))
            {
                process.LastError = ErrorInvalidParameter;
                return 0;
            }
            // A snapshot made without processes has none to walk.
            if (!processSnapshots.TryGetValue(snapshot, out var list)) { process.LastError = ErrorNoMoreFiles; return 0; }
            var at = first ? 0 : list.Cursor;
            if (at >= Processes) { process.LastError = ErrorNoMoreFiles; return 0; }

            memory.Write32(entry + 0x04, 0);                // cntUsage
            memory.Write32(entry + 0x08, CurrentProcessId);
            memory.Write32(entry + 0x0C, 0);                // th32DefaultHeapID
            memory.Write32(entry + 0x10, 0);                // th32ModuleID
            memory.Write32(entry + 0x14, list.Threads);
            memory.Write32(entry + 0x18, 0);                // th32ParentProcessID: nothing launched it here
            memory.Write32(entry + 0x1C, 8);                // pcPriClassBase: NORMAL_PRIORITY_CLASS
            memory.Write32(entry + 0x20, 0);
            var name = ExePath.Substring(Folder(ExePath).Length);   // the file name alone, as Toolhelp reports it
            WriteText(entry + 0x24, name.Length < MaxPath ? name : name.Substring(0, (int)MaxPath - 1), wide);
            list.Cursor = at + 1;
            return 1;
        }

        // --- shlwapi ---------------------------------------------------------------------------

        private void InstallShlwapi(GuestImports i)
        {
            const string s = "shlwapi.dll";
            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                i.Register(s, "SHDeleteKey" + (wide ? "W" : "A"), CallConv.Stdcall, 2, c => DeleteKeyTree(c.Arg(0), c.Arg(1), w));
                i.Register(s, "PathCanonicalize" + (wide ? "W" : "A"), CallConv.Stdcall, 2, c => CanonicalizePath(c.Arg(0), c.Arg(1), w));
            }
        }

        // SHDeleteKey: the named subkey and everything beneath it, in the registry
        // kept in the game folder. A missing name is refused instead of read as the
        // key itself, which would let one stray call empty a whole hive.
        private uint DeleteKeyTree(uint key, uint subKey, bool wide)
        {
            if (ReadText(subKey, wide).Trim('\\').Length == 0) return ErrorInvalidParameter;
            return DeleteKey(RegPath(key, subKey, wide));
        }
    }
}
