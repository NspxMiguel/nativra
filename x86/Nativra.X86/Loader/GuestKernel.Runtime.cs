using System;
using System.Collections.Generic;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// A software exception no frame handled (or one the dispatcher could not
    /// continue): the run ends with its code, as the process would crash.
    /// </summary>
    public sealed class GuestRaisedException : Exception
    {
        public uint Code { get; }

        public GuestRaisedException(uint code) : base($"guest raised exception 0x{code:X8}")
        {
            Code = code;
        }
    }

    // The kernel32 surface the Microsoft C runtime (ucrtbase, vcruntime140,
    // msvcp140 and the static /MT runtime) reaches during start-up and in
    // ordinary use: pointer encoding, the environment, code pages and string
    // conversion, locale queries, time, fiber-local storage, one-time
    // initialisation, the slim locks, and the debug output channel.
    public sealed partial class GuestKernel
    {
        private const uint ErrorEnvVarNotFound = 203;
        private const uint ErrorInsufficientBuffer = 122;
        private const uint ErrorTimeout = 1460;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 0x102;
        private const uint Infinite = 0xFFFFFFFF;

        // Standard handles: fixed values, never confused with a file handle.
        private const uint StdInput = 0x10, StdOutput = 0x14, StdError = 0x18;

        private readonly Dictionary<string, string> environment =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OS"] = "Windows_NT",
                ["PROCESSOR_ARCHITECTURE"] = "x86",
                ["NUMBER_OF_PROCESSORS"] = "4",
                ["SystemRoot"] = "C:\\Windows",
                ["windir"] = "C:\\Windows",
                ["TEMP"] = "C:\\Temp",
                ["TMP"] = "C:\\Temp",
            };

        private readonly Dictionary<uint, uint> fls = new Dictionary<uint, uint>();
        private readonly bool[] flsUsed = new bool[128];
        private readonly Dictionary<uint, GuestEvent> events = new Dictionary<uint, GuestEvent>();
        private uint unhandledFilter;
        private uint nextHandle = 0x100;

        private sealed class GuestEvent
        {
            public bool ManualReset;
            public bool Signaled;
        }

        /// <summary>Where OutputDebugString and writes to the standard handles go.</summary>
        public Action<string> Log { get; set; }

        /// <summary>A guest environment variable (the defaults describe a plain Windows install).</summary>
        public void SetEnvironment(string name, string value)
        {
            if (value == null) environment.Remove(name); else environment[name] = value;
        }

        private uint NewHandle()
        {
            var handle = nextHandle;
            nextHandle += 4;
            return handle;
        }

        private void InstallRuntime(GuestImports i)
        {
            const string k = "kernel32.dll";

            i.Register(k, "EncodePointer", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(k, "DecodePointer", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(k, "EncodeSystemPointer", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(k, "DecodeSystemPointer", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(k, "IsDebuggerPresent", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "SetUnhandledExceptionFilter", CallConv.Stdcall, 1, c =>
            {
                var previous = unhandledFilter;
                unhandledFilter = c.Arg(0);
                return previous;
            });
            i.Register(k, "UnhandledExceptionFilter", CallConv.Stdcall, 1, c => 0 /* EXCEPTION_CONTINUE_SEARCH */);
            i.Register(k, "SetErrorMode", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "GetErrorMode", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "IsWow64Process", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0);
                return 1;
            });
            i.Register(k, "FlushInstructionCache", CallConv.Stdcall, 3, c => 1);
            i.Register(k, "GetCurrentProcessorNumber", CallConv.Stdcall, 0, c => 0);

            // Interlocked singly-linked lists: { Next, Depth:16, Sequence:16 }.
            i.Register(k, "InitializeSListHead", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), 0);
                return 0;
            });
            i.Register(k, "InterlockedPushEntrySList", CallConv.Stdcall, 2, c =>
            {
                var head = c.Arg(0);
                var first = memory.Read32(head);
                memory.Write32(c.Arg(1), first);
                memory.Write32(head, c.Arg(1));
                memory.Write16(head + 4, (ushort)(memory.Read16(head + 4) + 1));
                return first;
            });
            i.Register(k, "InterlockedPopEntrySList", CallConv.Stdcall, 1, c =>
            {
                var head = c.Arg(0);
                var first = memory.Read32(head);
                if (first == 0) return 0;
                memory.Write32(head, memory.Read32(first));
                memory.Write16(head + 4, (ushort)(memory.Read16(head + 4) - 1));
                return first;
            });
            i.Register(k, "InterlockedFlushSList", CallConv.Stdcall, 1, c =>
            {
                var first = memory.Read32(c.Arg(0));
                memory.Write64(c.Arg(0), 0);
                return first;
            });
            i.Register(k, "QueryDepthSList", CallConv.Stdcall, 1, c => memory.Read16(c.Arg(0) + 4));

            // Version checks (IsWindows8OrGreater and friends): the guest is on
            // Windows 10/11, so any "at least" test passes.
            i.Register(k, "VerSetConditionMask", CallConv.Stdcall, 4, c =>
            {
                var mask = c.Arg64(0);
                uint types = c.Arg(2), condition = c.Arg(3) & 7;
                for (var bit = 0; bit < 8; bit++)
                    if ((types & (1u << bit)) != 0) mask |= (ulong)condition << (bit * 3);
                return mask;
            });
            i.Register(k, "VerifyVersionInfoW", CallConv.Stdcall, 4, c => 1);
            i.Register(k, "VerifyVersionInfoA", CallConv.Stdcall, 4, c => 1);
            i.Register(k, "IsThreadAFiber", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "FlushProcessWriteBuffers", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "HeapValidate", CallConv.Stdcall, 3, c => 1);
            i.Register(k, "HeapCompact", CallConv.Stdcall, 2, c => 0x100000);
            i.Register(k, "HeapQueryInformation", CallConv.Stdcall, 5, c =>
            {
                // HeapCompatibilityInformation: 2, the low-fragmentation heap.
                if (c.Arg(1) != 0 || c.Arg(3) < 4) { process.LastError = ErrorInsufficientBuffer; return 0; }
                memory.Write32(c.Arg(2), 2);
                if (c.Arg(4) != 0) memory.Write32(c.Arg(4), 4);
                return 1;
            });
            i.Register(k, "HeapSetInformation", CallConv.Stdcall, 4, c => 1);

            // Start-up information and the environment.
            i.Register(k, "GetStartupInfoA", CallConv.Stdcall, 1, c => { StartupInfo(c.Arg(0)); return 0; });
            i.Register(k, "GetStartupInfoW", CallConv.Stdcall, 1, c => { StartupInfo(c.Arg(0)); return 0; });
            i.Register(k, "GetEnvironmentStrings", CallConv.Stdcall, 0, c => EnvironmentBlock(false));
            i.Register(k, "GetEnvironmentStringsA", CallConv.Stdcall, 0, c => EnvironmentBlock(false));
            i.Register(k, "GetEnvironmentStringsW", CallConv.Stdcall, 0, c => EnvironmentBlock(true));
            i.Register(k, "FreeEnvironmentStringsA", CallConv.Stdcall, 1, c => heap.Free(c.Arg(0)) ? 1u : 0u);
            i.Register(k, "FreeEnvironmentStringsW", CallConv.Stdcall, 1, c => heap.Free(c.Arg(0)) ? 1u : 0u);
            i.Register(k, "GetEnvironmentVariableA", CallConv.Stdcall, 3, c =>
                GetEnvironmentVariable(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(k, "GetEnvironmentVariableW", CallConv.Stdcall, 3, c =>
                GetEnvironmentVariable(c.Arg(0), c.Arg(1), c.Arg(2), true));
            i.Register(k, "SetEnvironmentVariableA", CallConv.Stdcall, 2, c =>
            {
                SetEnvironment(ReadText(c.Arg(0), false), c.Arg(1) == 0 ? null : ReadText(c.Arg(1), false));
                return 1;
            });
            i.Register(k, "SetEnvironmentVariableW", CallConv.Stdcall, 2, c =>
            {
                SetEnvironment(ReadText(c.Arg(0), true), c.Arg(1) == 0 ? null : ReadText(c.Arg(1), true));
                return 1;
            });

            // Standard handles and the console: a game has no console, so the
            // console calls fail as they do in a GUI process and anything
            // written to the standard handles goes to the log.
            i.Register(k, "GetStdHandle", CallConv.Stdcall, 1, c =>
            {
                switch (c.Arg(0))
                {
                    case 0xFFFFFFF6: return StdInput;
                    case 0xFFFFFFF5: return StdOutput;
                    case 0xFFFFFFF4: return StdError;
                    default: return 0xFFFFFFFF;
                }
            });
            i.Register(k, "SetStdHandle", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "GetConsoleMode", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "GetConsoleCP", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "GetConsoleOutputCP", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "SetConsoleCtrlHandler", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "WriteConsoleA", CallConv.Stdcall, 5, c => WriteConsole(c, false));
            i.Register(k, "WriteConsoleW", CallConv.Stdcall, 5, c => WriteConsole(c, true));
            i.Register(k, "OutputDebugStringA", CallConv.Stdcall, 1, c => { Say(ReadText(c.Arg(0), false)); return 0; });
            i.Register(k, "OutputDebugStringW", CallConv.Stdcall, 1, c => { Say(ReadText(c.Arg(0), true)); return 0; });

            // Code pages and conversion.
            i.Register(k, "GetACP", CallConv.Stdcall, 0, c => 1252);
            i.Register(k, "GetOEMCP", CallConv.Stdcall, 0, c => 437);
            i.Register(k, "IsValidCodePage", CallConv.Stdcall, 1, c => KnownCodePage(c.Arg(0)) ? 1u : 0u);
            i.Register(k, "GetCPInfo", CallConv.Stdcall, 2, c => CodePageInfo(c.Arg(0), c.Arg(1)));
            i.Register(k, "MultiByteToWideChar", CallConv.Stdcall, 6, c => MultiByteToWideChar(c));
            i.Register(k, "WideCharToMultiByte", CallConv.Stdcall, 8, c => WideCharToMultiByte(c));

            // Locale: one locale, en-US, as the CRT's "C" locale expects of it.
            i.Register(k, "GetUserDefaultLCID", CallConv.Stdcall, 0, c => 0x409);
            i.Register(k, "GetSystemDefaultLCID", CallConv.Stdcall, 0, c => 0x409);
            i.Register(k, "GetThreadLocale", CallConv.Stdcall, 0, c => 0x409);
            i.Register(k, "GetUserDefaultLangID", CallConv.Stdcall, 0, c => 0x409);
            i.Register(k, "GetSystemDefaultLangID", CallConv.Stdcall, 0, c => 0x409);
            i.Register(k, "GetUserDefaultUILanguage", CallConv.Stdcall, 0, c => 0x409);
            i.Register(k, "GetSystemDefaultUILanguage", CallConv.Stdcall, 0, c => 0x409);
            i.Register(k, "IsValidLocale", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "IsValidLocaleName", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "LocaleNameToLCID", CallConv.Stdcall, 2, c => 0x409);
            i.Register(k, "LCIDToLocaleName", CallConv.Stdcall, 4, c => CopyOut("en-US", c.Arg(1), c.Arg(2), true));
            i.Register(k, "GetUserDefaultLocaleName", CallConv.Stdcall, 2, c => CopyOut("en-US", c.Arg(0), c.Arg(1), true));
            i.Register(k, "GetLocaleInfoA", CallConv.Stdcall, 4, c => LocaleInfo(c.Arg(1), c.Arg(2), c.Arg(3), false));
            i.Register(k, "GetLocaleInfoW", CallConv.Stdcall, 4, c => LocaleInfo(c.Arg(1), c.Arg(2), c.Arg(3), true));
            i.Register(k, "GetLocaleInfoEx", CallConv.Stdcall, 4, c => LocaleInfo(c.Arg(1), c.Arg(2), c.Arg(3), true));
            i.Register(k, "LCMapStringW", CallConv.Stdcall, 6, c =>
                MapString(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
            i.Register(k, "LCMapStringEx", CallConv.Stdcall, 9, c =>
                MapString(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
            i.Register(k, "CompareStringW", CallConv.Stdcall, 6, c =>
                CompareStrings(c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), (c.Arg(1) & 1) != 0));
            i.Register(k, "CompareStringEx", CallConv.Stdcall, 9, c =>
                CompareStrings(c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), (c.Arg(1) & 1) != 0));
            i.Register(k, "CompareStringOrdinal", CallConv.Stdcall, 5, c =>
                CompareStrings(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4) != 0));
            i.Register(k, "GetStringTypeW", CallConv.Stdcall, 4, c => StringType(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));

            // System and time.
            i.Register(k, "GetNativeSystemInfo", CallConv.Stdcall, 1, c => { FillSystemInfo(c.Arg(0)); return 0; });
            i.Register(k, "GetVersionExA", CallConv.Stdcall, 1, c => VersionInfo(c.Arg(0)));
            i.Register(k, "GetVersionExW", CallConv.Stdcall, 1, c => VersionInfo(c.Arg(0)));
            i.Register(k, "GetSystemTimePreciseAsFileTime", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), (ulong)DateTime.UtcNow.ToFileTimeUtc());
                return 0;
            });
            i.Register(k, "GetSystemTime", CallConv.Stdcall, 1, c => { WriteSystemTime(c.Arg(0), DateTime.UtcNow); return 0; });
            i.Register(k, "GetLocalTime", CallConv.Stdcall, 1, c => { WriteSystemTime(c.Arg(0), DateTime.Now); return 0; });
            i.Register(k, "FileTimeToSystemTime", CallConv.Stdcall, 2, c =>
            {
                WriteSystemTime(c.Arg(1), DateTime.FromFileTimeUtc((long)memory.Read64(c.Arg(0))));
                return 1;
            });
            i.Register(k, "SystemTimeToFileTime", CallConv.Stdcall, 2, c =>
            {
                memory.Write64(c.Arg(1), (ulong)ReadSystemTime(c.Arg(0)).ToFileTimeUtc());
                return 1;
            });
            i.Register(k, "FileTimeToLocalFileTime", CallConv.Stdcall, 2, c =>
            {
                var utc = DateTime.FromFileTimeUtc((long)memory.Read64(c.Arg(0)));
                var local = DateTime.SpecifyKind(utc.ToLocalTime(), DateTimeKind.Utc);
                memory.Write64(c.Arg(1), (ulong)local.ToFileTimeUtc());
                return 1;
            });
            i.Register(k, "GetTimeZoneInformation", CallConv.Stdcall, 1, c =>
            {
                // UTC with no daylight saving: TIME_ZONE_ID_UNKNOWN.
                memory.WriteBytes(c.Arg(0), new byte[172]);
                return 0;
            });
            i.Register(k, "GlobalMemoryStatusEx", CallConv.Stdcall, 1, c => { MemoryStatusEx(c.Arg(0)); return 1; });
            i.Register(k, "GlobalMemoryStatus", CallConv.Stdcall, 1, c => { MemoryStatus(c.Arg(0)); return 0; });

            // The Global/Local allocators, over the process heap. Handles are
            // the pointers themselves, so Lock/Unlock are identities.
            i.Register(k, "GlobalAlloc", CallConv.Stdcall, 2, c => heap.Alloc(Math.Max(c.Arg(1), 1u), (c.Arg(0) & 0x40) != 0));
            i.Register(k, "LocalAlloc", CallConv.Stdcall, 2, c => heap.Alloc(Math.Max(c.Arg(1), 1u), (c.Arg(0) & 0x40) != 0));
            i.Register(k, "GlobalFree", CallConv.Stdcall, 1, c => heap.Free(c.Arg(0)) || c.Arg(0) == 0 ? 0u : c.Arg(0));
            i.Register(k, "LocalFree", CallConv.Stdcall, 1, c => heap.Free(c.Arg(0)) || c.Arg(0) == 0 ? 0u : c.Arg(0));
            i.Register(k, "GlobalReAlloc", CallConv.Stdcall, 3, c => heap.ReAlloc(c.Arg(0), c.Arg(1)));
            i.Register(k, "LocalReAlloc", CallConv.Stdcall, 3, c => heap.ReAlloc(c.Arg(0), c.Arg(1)));
            i.Register(k, "GlobalLock", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(k, "GlobalUnlock", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "GlobalSize", CallConv.Stdcall, 1, c => heap.SizeOf(c.Arg(0)));
            i.Register(k, "LocalSize", CallConv.Stdcall, 1, c => heap.SizeOf(c.Arg(0)));

            i.Register(k, "GetModuleHandleExA", CallConv.Stdcall, 3, c => ModuleHandleEx(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(k, "GetModuleHandleExW", CallConv.Stdcall, 3, c => ModuleHandleEx(c.Arg(0), c.Arg(1), c.Arg(2), true));

            // Fiber-local storage: one fiber, so it is TLS with a destructor
            // the guest never needs called.
            i.Register(k, "FlsAlloc", CallConv.Stdcall, 1, c =>
            {
                for (uint idx = 0; idx < flsUsed.Length; idx++)
                    if (!flsUsed[idx]) { flsUsed[idx] = true; fls[idx] = 0; return idx; }
                return TlsOutOfIndexes;
            });
            i.Register(k, "FlsFree", CallConv.Stdcall, 1, c =>
            {
                var idx = c.Arg(0);
                if (idx >= flsUsed.Length || !flsUsed[idx]) return 0;
                flsUsed[idx] = false;
                fls.Remove(idx);
                return 1;
            });
            i.Register(k, "FlsGetValue", CallConv.Stdcall, 1, c => fls.TryGetValue(c.Arg(0), out var v) ? v : 0);
            i.Register(k, "FlsSetValue", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(0) >= flsUsed.Length || !flsUsed[c.Arg(0)]) return 0;
                fls[c.Arg(0)] = c.Arg(1);
                return 1;
            });

            // One-time initialisation. INIT_ONCE holds 0 until done, then 2.
            i.Register(k, "InitOnceExecuteOnce", CallConv.Stdcall, 4, c => InitOnceExecuteOnce(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(k, "InitOnceBeginInitialize", CallConv.Stdcall, 4, c =>
            {
                var done = memory.Read32(c.Arg(0)) == 2;
                if (c.Arg(2) != 0) memory.Write32(c.Arg(2), done ? 0u : 1u);   // fPending
                if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0);
                return 1;
            });
            i.Register(k, "InitOnceComplete", CallConv.Stdcall, 3, c =>
            {
                if ((c.Arg(1) & 4) == 0) memory.Write32(c.Arg(0), 2);   // not INIT_ONCE_INIT_FAILED
                else memory.Write32(c.Arg(0), 0);
                return 1;
            });

            // Slim locks and condition variables on one thread: acquiring never
            // waits, and a wait on a condition nobody else can signal times out.
            i.Register(k, "TryEnterCriticalSection", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "InitializeSRWLock", CallConv.Stdcall, 1, c => { memory.Write32(c.Arg(0), 0); return 0; });
            i.Register(k, "AcquireSRWLockExclusive", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "AcquireSRWLockShared", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "ReleaseSRWLockExclusive", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "ReleaseSRWLockShared", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "TryAcquireSRWLockExclusive", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "TryAcquireSRWLockShared", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "InitializeConditionVariable", CallConv.Stdcall, 1, c => { memory.Write32(c.Arg(0), 0); return 0; });
            i.Register(k, "WakeConditionVariable", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "WakeAllConditionVariable", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "SleepConditionVariableCS", CallConv.Stdcall, 3, c => { process.LastError = ErrorTimeout; return 0; });
            i.Register(k, "SleepConditionVariableSRW", CallConv.Stdcall, 4, c => { process.LastError = ErrorTimeout; return 0; });

            // Events and waits, single-threaded: a wait succeeds on a signalled
            // event and otherwise times out, since nothing else could signal it.
            i.Register(k, "CreateEventA", CallConv.Stdcall, 4, c => CreateEvent(c.Arg(1) != 0, c.Arg(2) != 0));
            i.Register(k, "CreateEventW", CallConv.Stdcall, 4, c => CreateEvent(c.Arg(1) != 0, c.Arg(2) != 0));
            i.Register(k, "CreateEventExW", CallConv.Stdcall, 4, c => CreateEvent((c.Arg(2) & 1) != 0, (c.Arg(2) & 2) != 0));
            i.Register(k, "SetEvent", CallConv.Stdcall, 1, c => SignalEvent(c.Arg(0), true));
            i.Register(k, "ResetEvent", CallConv.Stdcall, 1, c => SignalEvent(c.Arg(0), false));
            i.Register(k, "WaitForSingleObject", CallConv.Stdcall, 2, c => Wait(c.Arg(0), c.Arg(1)));
            i.Register(k, "WaitForSingleObjectEx", CallConv.Stdcall, 3, c => Wait(c.Arg(0), c.Arg(1)));
            i.Register(k, "SleepEx", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "SwitchToThread", CallConv.Stdcall, 0, c => 0);

            // lstr*: the Win32 string helpers older games still import.
            i.Register(k, "lstrlenA", CallConv.Stdcall, 1, c => c.Arg(0) == 0 ? 0u : (uint)ReadText(c.Arg(0), false).Length);
            i.Register(k, "lstrlenW", CallConv.Stdcall, 1, c => c.Arg(0) == 0 ? 0u : (uint)ReadText(c.Arg(0), true).Length);
            i.Register(k, "lstrcpyA", CallConv.Stdcall, 2, c => { WriteText(c.Arg(0), ReadText(c.Arg(1), false), false); return c.Arg(0); });
            i.Register(k, "lstrcpyW", CallConv.Stdcall, 2, c => { WriteText(c.Arg(0), ReadText(c.Arg(1), true), true); return c.Arg(0); });
            i.Register(k, "lstrcatA", CallConv.Stdcall, 2, c =>
            {
                WriteText(c.Arg(0), ReadText(c.Arg(0), false) + ReadText(c.Arg(1), false), false);
                return c.Arg(0);
            });
            i.Register(k, "lstrcatW", CallConv.Stdcall, 2, c =>
            {
                WriteText(c.Arg(0), ReadText(c.Arg(0), true) + ReadText(c.Arg(1), true), true);
                return c.Arg(0);
            });
            i.Register(k, "lstrcmpA", CallConv.Stdcall, 2, c => Sign(string.CompareOrdinal(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false))));
            i.Register(k, "lstrcmpW", CallConv.Stdcall, 2, c => Sign(string.CompareOrdinal(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true))));
            i.Register(k, "lstrcmpiA", CallConv.Stdcall, 2, c =>
                Sign(string.Compare(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false), StringComparison.OrdinalIgnoreCase)));
            i.Register(k, "lstrcmpiW", CallConv.Stdcall, 2, c =>
                Sign(string.Compare(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true), StringComparison.OrdinalIgnoreCase)));

            // winmm's clock, which most games of the era pace their frames by.
            const string w = "winmm.dll";
            i.Register(w, "timeGetTime", CallConv.Stdcall, 0, c => (uint)Milliseconds);
            i.Register(w, "timeBeginPeriod", CallConv.Stdcall, 1, c => 0);
            i.Register(w, "timeEndPeriod", CallConv.Stdcall, 1, c => 0);
        }

        // --- helpers ------------------------------------------------------

        private void Say(string text)
        {
            text = text?.TrimEnd('\r', '\n');
            if (!string.IsNullOrEmpty(text)) Log?.Invoke(text);
        }

        private static uint Sign(int comparison) => comparison < 0 ? 0xFFFFFFFF : comparison > 0 ? 1u : 0u;

        private uint WriteConsole(GuestCall c, bool wide)
        {
            var count = c.Arg(2);
            Say(wide ? ReadWide(c.Arg(1), (int)count) : Ansi.Decode(memory.ReadBytes(c.Arg(1), (int)count)));
            if (c.Arg(3) != 0) memory.Write32(c.Arg(3), count);
            return 1;
        }

        private void StartupInfo(uint p)
        {
            memory.WriteBytes(p, new byte[68]);
            memory.Write32(p, 68);   // cb
        }

        private uint EnvironmentBlock(bool wide)
        {
            var text = new StringBuilder();
            foreach (var pair in environment) text.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            text.Append('\0');
            var block = text.ToString();
            if (wide)
            {
                var p = heap.Alloc((uint)block.Length * 2);
                for (var n = 0; n < block.Length; n++) memory.Write16(p + (uint)n * 2, block[n]);
                return p;
            }
            var bytes = Ansi.Encode(block);
            var q = heap.Alloc((uint)bytes.Length);
            memory.WriteBytes(q, bytes);
            return q;
        }

        private uint GetEnvironmentVariable(uint name, uint buffer, uint size, bool wide)
        {
            if (!environment.TryGetValue(ReadText(name, wide), out var value))
            {
                process.LastError = ErrorEnvVarNotFound;
                return 0;
            }
            return CopyOut(value, buffer, size, wide) is var n && n <= size ? n - 1 : n;
        }

        /// <summary>
        /// Writes <paramref name="text"/> with its NUL when it fits and returns
        /// the characters written including the NUL; when it does not fit,
        /// writes nothing and returns the size needed, including the NUL.
        /// </summary>
        private uint CopyOut(string text, uint buffer, uint size, bool wide)
        {
            var needed = (uint)(wide ? text.Length : Ansi.Encode(text).Length) + 1;
            if (buffer == 0 || size < needed)
            {
                if (buffer != 0) process.LastError = ErrorInsufficientBuffer;
                return needed;
            }
            WriteText(buffer, text, wide);
            return needed;
        }

        /// <summary>Guest text in the A (code page 1252) or W (UTF-16) form.</summary>
        internal string ReadText(uint address, bool wide) =>
            address == 0 ? "" : wide ? memory.ReadUnicode(address, 32768) : Ansi.Decode(ReadAnsiBytes(address));

        internal void WriteText(uint address, string text, bool wide)
        {
            if (wide) { memory.WriteUnicode(address, text); return; }
            var bytes = Ansi.Encode(text);
            memory.WriteBytes(address, bytes);
            memory.Write8(address + (uint)bytes.Length, 0);
        }

        private byte[] ReadAnsiBytes(uint address)
        {
            var bytes = new List<byte>();
            for (uint n = 0; n < 32768; n++)
            {
                var b = memory.Read8(address + n);
                if (b == 0) break;
                bytes.Add(b);
            }
            return bytes.ToArray();
        }

        private string ReadWide(uint address, int count)
        {
            var chars = new char[count];
            for (var n = 0; n < count; n++) chars[n] = (char)memory.Read16(address + (uint)n * 2);
            return new string(chars);
        }

        // --- code pages ---------------------------------------------------

        private static bool KnownCodePage(uint cp) =>
            cp == 0 || cp == 1 || cp == 3 || cp == 437 || cp == 1252 || cp == 20127 || cp == 28591 || cp == 65001;

        private uint CodePageInfo(uint cp, uint info)
        {
            if (!KnownCodePage(cp)) { process.LastError = 87; return 0; }
            memory.WriteBytes(info, new byte[20]);
            memory.Write32(info, cp == 65001 ? 4u : 1u);   // MaxCharSize
            memory.Write8(info + 4, (byte)'?');           // DefaultChar
            return 1;
        }

        private static string Decode(uint cp, byte[] bytes) =>
            cp == 65001 ? Encoding.UTF8.GetString(bytes) : Ansi.Decode(bytes);

        private static byte[] Encode(uint cp, string text) =>
            cp == 65001 ? Encoding.UTF8.GetBytes(text) : Ansi.Encode(text);

        private uint MultiByteToWideChar(GuestCall c)
        {
            uint cp = c.Arg(0), src = c.Arg(2), dst = c.Arg(4), dstChars = c.Arg(5);
            var srcLen = (int)c.Arg(3);
            byte[] bytes;
            if (srcLen < 0)
            {
                var body = ReadAnsiBytes(src);
                bytes = new byte[body.Length + 1];   // -1 converts the terminator too
                Array.Copy(body, bytes, body.Length);
            }
            else bytes = memory.ReadBytes(src, srcLen);

            var text = Decode(cp, bytes);
            if (dstChars == 0) return (uint)text.Length;
            if (dstChars < text.Length) { process.LastError = ErrorInsufficientBuffer; return 0; }
            for (var n = 0; n < text.Length; n++) memory.Write16(dst + (uint)n * 2, text[n]);
            return (uint)text.Length;
        }

        private uint WideCharToMultiByte(GuestCall c)
        {
            uint cp = c.Arg(0), src = c.Arg(2), dst = c.Arg(4), dstBytes = c.Arg(5), usedDefault = c.Arg(7);
            var srcLen = (int)c.Arg(3);
            var text = srcLen < 0 ? memory.ReadUnicode(src, 32768) + "\0" : ReadWide(src, srcLen);

            var bytes = Encode(cp, text);
            if (usedDefault != 0) memory.Write32(usedDefault, Ansi.Lossy(text) && cp != 65001 ? 1u : 0u);
            if (dstBytes == 0) return (uint)bytes.Length;
            if (dstBytes < bytes.Length) { process.LastError = ErrorInsufficientBuffer; return 0; }
            memory.WriteBytes(dst, bytes);
            return (uint)bytes.Length;
        }

        // --- locale -------------------------------------------------------

        private uint LocaleInfo(uint type, uint buffer, uint size, bool wide)
        {
            const uint ReturnNumber = 0x20000000;
            var number = (type & ReturnNumber) != 0;
            string value;
            switch (type & 0xFFFF)
            {
                case 0x0001: value = "0409"; break;          // LOCALE_ILANGUAGE
                case 0x0002: value = "English (United States)"; break;
                case 0x0003: value = "ENU"; break;           // LOCALE_SABBREVLANGNAME
                case 0x000B: value = "437"; break;           // LOCALE_IDEFAULTCODEPAGE
                case 0x000C: value = ","; break;             // LOCALE_SLIST
                case 0x000E: value = "."; break;             // LOCALE_SDECIMAL
                case 0x000F: value = ","; break;             // LOCALE_STHOUSAND
                case 0x0010: value = "3;0"; break;           // LOCALE_SGROUPING
                case 0x0014: value = "$"; break;             // LOCALE_SCURRENCY
                case 0x0016: value = "."; break;             // LOCALE_SMONDECIMALSEP
                case 0x0017: value = ","; break;             // LOCALE_SMONTHOUSANDSEP
                case 0x0018: value = "3;0"; break;           // LOCALE_SMONGROUPING
                case 0x0059: value = "en"; break;            // LOCALE_SISO639LANGNAME
                case 0x005A: value = "US"; break;            // LOCALE_SISO3166CTRYNAME
                case 0x005C: value = "en-US"; break;         // LOCALE_SNAME
                case 0x1001: value = "English"; break;       // LOCALE_SENGLISHLANGUAGENAME
                case 0x1002: value = "United States"; break; // LOCALE_SENGLISHCOUNTRYNAME
                case 0x1004: value = "1252"; break;          // LOCALE_IDEFAULTANSICODEPAGE
                default: value = ""; break;
            }

            if (number)
            {
                // The value as a DWORD in the buffer; size counts characters.
                uint.TryParse(value, out var n);
                if ((type & 0xFFFF) == 0x0001) n = 0x409;
                if (buffer != 0 && size >= (wide ? 2u : 4u)) memory.Write32(buffer, n);
                return wide ? 2u : 4u;
            }
            if (size == 0) return (uint)value.Length + 1;
            var written = CopyOut(value, buffer, size, wide);
            return written <= size ? written : 0;
        }

        private uint MapString(uint flags, uint src, uint srcLen, uint dst, uint dstLen)
        {
            const uint LowerCase = 0x100, UpperCase = 0x200, SortKey = 0x400;
            var text = (int)srcLen < 0 ? memory.ReadUnicode(src, 32768) + "\0" : ReadWide(src, (int)srcLen);
            if ((flags & LowerCase) != 0) text = text.ToLowerInvariant();
            else if ((flags & UpperCase) != 0) text = text.ToUpperInvariant();

            if ((flags & SortKey) != 0)
            {
                // A byte string that sorts as the text does, case-folded.
                var key = new List<byte>();
                foreach (var ch in text.TrimEnd('\0').ToUpperInvariant()) { key.Add((byte)(ch >> 8)); key.Add((byte)ch); }
                key.Add(0);
                if (dstLen == 0) return (uint)key.Count;
                if (dstLen < key.Count) { process.LastError = ErrorInsufficientBuffer; return 0; }
                memory.WriteBytes(dst, key.ToArray());
                return (uint)key.Count;
            }

            if (dstLen == 0) return (uint)text.Length;
            if (dstLen < text.Length) { process.LastError = ErrorInsufficientBuffer; return 0; }
            for (var n = 0; n < text.Length; n++) memory.Write16(dst + (uint)n * 2, text[n]);
            return (uint)text.Length;
        }

        private uint CompareStrings(uint a, uint aLen, uint b, uint bLen, bool ignoreCase)
        {
            var x = (int)aLen < 0 ? memory.ReadUnicode(a, 32768) : ReadWide(a, (int)aLen);
            var y = (int)bLen < 0 ? memory.ReadUnicode(b, 32768) : ReadWide(b, (int)bLen);
            var result = string.Compare(x, y, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return result < 0 ? 1u : result > 0 ? 3u : 2u;   // CSTR_LESS_THAN / EQUAL / GREATER_THAN
        }

        private uint StringType(uint infoType, uint src, uint srcLen, uint output)
        {
            if (infoType != 1) { process.LastError = 87; return 0; }   // only CT_CTYPE1
            var text = (int)srcLen < 0 ? memory.ReadUnicode(src, 32768) + "\0" : ReadWide(src, (int)srcLen);
            for (var n = 0; n < text.Length; n++) memory.Write16(output + (uint)n * 2, CharType(text[n]));
            return 1;
        }

        private static ushort CharType(char ch)
        {
            ushort t = 0;
            if (char.IsUpper(ch)) t |= 0x001;
            if (char.IsLower(ch)) t |= 0x002;
            if (ch >= '0' && ch <= '9') t |= 0x004;
            if (char.IsWhiteSpace(ch)) t |= 0x008;
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) t |= 0x010;
            if (char.IsControl(ch)) t |= 0x020;
            if (ch == ' ' || ch == '\t') t |= 0x040;
            if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')) t |= 0x080;
            if (char.IsLetter(ch)) t |= 0x100;
            return (ushort)(t | 0x200);   // C1_DEFINED
        }

        // --- system, time, memory -----------------------------------------

        private uint VersionInfo(uint p)
        {
            // Windows 10/11, the same as GetVersion reports.
            memory.Write32(p + 4, 10);
            memory.Write32(p + 8, 0);
            memory.Write32(p + 12, 26100);
            memory.Write32(p + 16, 2);   // VER_PLATFORM_WIN32_NT
            memory.Write8(p + 20, 0);    // szCSDVersion: ""
            memory.Write8(p + 21, 0);
            return 1;
        }

        private void WriteSystemTime(uint p, DateTime t)
        {
            memory.Write16(p + 0, (ushort)t.Year);
            memory.Write16(p + 2, (ushort)t.Month);
            memory.Write16(p + 4, (ushort)t.DayOfWeek);
            memory.Write16(p + 6, (ushort)t.Day);
            memory.Write16(p + 8, (ushort)t.Hour);
            memory.Write16(p + 10, (ushort)t.Minute);
            memory.Write16(p + 12, (ushort)t.Second);
            memory.Write16(p + 14, (ushort)t.Millisecond);
        }

        private DateTime ReadSystemTime(uint p) =>
            new DateTime(memory.Read16(p), memory.Read16(p + 2), memory.Read16(p + 6),
                memory.Read16(p + 8), memory.Read16(p + 10), memory.Read16(p + 12), memory.Read16(p + 14),
                DateTimeKind.Utc);

        // What a 32-bit process on a console with memory to spare sees: 8 GB
        // of RAM, a 2 GB address space with most of it free.
        private const ulong TotalPhysical = 8UL << 30, AvailablePhysical = 6UL << 30;
        private const ulong TotalVirtual = 0x7FFE0000, AvailableVirtual = 0x70000000;

        private void MemoryStatusEx(uint p)
        {
            memory.Write32(p + 0, 64);
            memory.Write32(p + 4, 25);   // dwMemoryLoad
            memory.Write64(p + 8, TotalPhysical);
            memory.Write64(p + 16, AvailablePhysical);
            memory.Write64(p + 24, TotalPhysical * 2);
            memory.Write64(p + 32, AvailablePhysical * 2);
            memory.Write64(p + 40, TotalVirtual);
            memory.Write64(p + 48, AvailableVirtual);
            memory.Write64(p + 56, 0);
        }

        private void MemoryStatus(uint p)
        {
            // The legacy form clamps to 2 GB, as Windows does for 32-bit callers.
            const uint Clamp = 0x7FFFFFFF;
            memory.Write32(p + 0, 32);
            memory.Write32(p + 4, 25);
            memory.Write32(p + 8, Clamp);
            memory.Write32(p + 12, Clamp);
            memory.Write32(p + 16, Clamp);
            memory.Write32(p + 20, Clamp);
            memory.Write32(p + 24, (uint)TotalVirtual);
            memory.Write32(p + 28, (uint)AvailableVirtual);
        }

        private uint ModuleHandleEx(uint flags, uint nameOrAddress, uint output, bool wide)
        {
            const uint FromAddress = 0x4;
            uint module = 0;
            if ((flags & FromAddress) != 0)
            {
                foreach (var image in process.Images)
                    if (nameOrAddress >= image.BaseAddress && nameOrAddress - image.BaseAddress < image.ImageSize)
                    {
                        module = image.BaseAddress;
                        break;
                    }
                if (module == 0) process.LastError = ErrorModNotFound;
            }
            else module = ModuleHandle(nameOrAddress, wide);

            if (output != 0) memory.Write32(output, module);
            return module != 0 ? 1u : 0u;
        }

        // --- one-time init, events -----------------------------------------

        private uint InitOnceExecuteOnce(uint initOnce, uint function, uint parameter, uint context)
        {
            if (memory.Read32(initOnce) == 2) return 1;
            // BOOL CALLBACK InitOnceCallback(PINIT_ONCE, PVOID, PVOID*): a nested
            // run on the same stack, like DllMain from LoadLibrary.
            var saved = SaveRegisters();
            var result = process.Call(function, out var ok, 50_000_000, initOnce, parameter, context);
            RestoreRegisters(saved);
            if (!result.Ok || ok == 0) return 0;
            memory.Write32(initOnce, 2);
            return 1;
        }

        private uint CreateEvent(bool manualReset, bool signaled)
        {
            var handle = NewHandle();
            events[handle] = new GuestEvent { ManualReset = manualReset, Signaled = signaled };
            return handle;
        }

        private uint SignalEvent(uint handle, bool signaled)
        {
            if (!events.TryGetValue(handle, out var e)) { process.LastError = 6; return 0; }
            e.Signaled = signaled;
            return 1;
        }

        private uint Wait(uint handle, uint timeout)
        {
            if (events.TryGetValue(handle, out var e))
            {
                if (e.Signaled)
                {
                    if (!e.ManualReset) e.Signaled = false;
                    return WaitObject0;
                }
                if (timeout == Infinite) Say($"x86: infinite wait on unsignalled event 0x{handle:X} (single thread)");
                return WaitTimeout;
            }
            // Anything else (a file, a finished thread): already signalled.
            return WaitObject0;
        }

        private bool CloseRuntimeHandle(uint handle) => events.Remove(handle);

        /// <summary>
        /// Code page 1252, the ANSI code page of a US/Western Windows: Latin-1
        /// with typographic characters in 0x80–0x9F. Written out so it does not
        /// depend on the platform's legacy encoding provider.
        /// </summary>
        internal static class Ansi
        {
            private static readonly char[] High =
            {
                '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
                '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
                '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
                '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
            };

            private static readonly Dictionary<char, byte> Reverse = BuildReverse();

            private static Dictionary<char, byte> BuildReverse()
            {
                var map = new Dictionary<char, byte>();
                for (var n = 0; n < High.Length; n++) map[High[n]] = (byte)(0x80 + n);
                return map;
            }

            public static string Decode(byte[] bytes)
            {
                var chars = new char[bytes.Length];
                for (var n = 0; n < bytes.Length; n++)
                {
                    var b = bytes[n];
                    chars[n] = b >= 0x80 && b < 0xA0 ? High[b - 0x80] : (char)b;
                }
                return new string(chars);
            }

            public static byte[] Encode(string text)
            {
                var bytes = new byte[text.Length];
                for (var n = 0; n < text.Length; n++) bytes[n] = EncodeChar(text[n]);
                return bytes;
            }

            public static bool Lossy(string text)
            {
                foreach (var ch in text) if (EncodeChar(ch) == (byte)'?' && ch != '?') return true;
                return false;
            }

            private static byte EncodeChar(char ch)
            {
                if (ch < 0x80 || (ch >= 0xA0 && ch <= 0xFF)) return (byte)ch;
                return Reverse.TryGetValue(ch, out var b) ? b : (byte)'?';
            }
        }
    }
}
