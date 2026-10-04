using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // The process looking at itself: its modules (psapi and kernel32's K32
    // forms), where each was loaded from, NTSTATUS to Win32 errors, and the
    // newer processor-topology query. Crash handlers and integrity checks
    // walk their own modules this way.
    public sealed partial class GuestKernel
    {
        private readonly Dictionary<string, string> modulePaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The guest path a mapped DLL came from, when it was found in the guest's files.</summary>
        private string ModulePath(string key) =>
            modulePaths.TryGetValue(key, out var path) ? path : Folder(ExePath) + key;

        private void InstallProcess(GuestImports i)
        {
            // C++/WinRT resolves these from combase at start-up even when no
            // Windows Runtime objects are created afterwards.
            i.Register("combase.dll", "RoInitialize", CallConv.Stdcall, 1, c => 0);
            i.Register("combase.dll", "RoUninitialize", CallConv.Stdcall, 0, c => 0);
            foreach (var module in new[] { "psapi.dll", "kernel32.dll" })
            {
                var k32 = module == "kernel32.dll" ? "K32" : "";
                i.Register(module, k32 + "EnumProcessModules", CallConv.Stdcall, 4, c => EnumModules(c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(module, k32 + "EnumProcessModulesEx", CallConv.Stdcall, 5, c => EnumModules(c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(module, k32 + "GetModuleBaseNameA", CallConv.Stdcall, 4, c => ModuleBaseName(c.Arg(1), c.Arg(2), c.Arg(3), false));
                i.Register(module, k32 + "GetModuleBaseNameW", CallConv.Stdcall, 4, c => ModuleBaseName(c.Arg(1), c.Arg(2), c.Arg(3), true));
                i.Register(module, k32 + "GetModuleFileNameExA", CallConv.Stdcall, 4, c => ModuleFileName(c.Arg(1), c.Arg(2), c.Arg(3), false));
                i.Register(module, k32 + "GetModuleFileNameExW", CallConv.Stdcall, 4, c => ModuleFileName(c.Arg(1), c.Arg(2), c.Arg(3), true));
                i.Register(module, k32 + "GetModuleInformation", CallConv.Stdcall, 4, c => ModuleInformation(c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(module, k32 + "GetProcessMemoryInfo", CallConv.Stdcall, 3, c => MemoryCounters(c.Arg(1), c.Arg(2)));
                i.Register(module, k32 + "EnumProcesses", CallConv.Stdcall, 3, c =>
                {
                    if (c.Arg(1) >= 4 && c.Arg(0) != 0) memory.Write32(c.Arg(0), GuestProcess.ProcessId);
                    if (c.Arg(2) != 0) memory.Write32(c.Arg(2), c.Arg(1) >= 4 ? 4u : 0u);
                    return 1;
                });
            }

            i.Register("ntdll.dll", "RtlNtStatusToDosError", CallConv.Stdcall, 1, c => DosError(c.Arg(0)));
            // NtSetInformationThread(thread, class, info, length). Games call it with
            // ThreadHideFromDebugger (17) through GetProcAddress and call the result
            // unchecked (Castle Crashers jumped to address zero). No debugger can
            // attach here, so hiding succeeds; other classes are accepted as Windows
            // accepts them for a thread the caller owns.
            foreach (var name in new[] { "NtSetInformationThread", "ZwSetInformationThread" })
                i.Register("ntdll.dll", name, CallConv.Stdcall, 4, c =>
                {
                    const uint StatusSuccess = 0, StatusInvalidHandle = 0xC0000008, StatusInfoLengthMismatch = 0xC0000004;
                    const uint ThreadHideFromDebugger = 17;
                    if (ThreadFor(c.Arg(0)) == null) return StatusInvalidHandle;
                    if (c.Arg(1) == ThreadHideFromDebugger && c.Arg(3) != 0) return StatusInfoLengthMismatch;
                    return StatusSuccess;
                });
            i.Register("kernel32.dll", "GetTempPath2A", CallConv.Stdcall, 2, c => CopyPath(Folder(ExePath), c.Arg(1), c.Arg(0), false));
            i.Register("kernel32.dll", "GetTempPath2W", CallConv.Stdcall, 2, c => CopyPath(Folder(ExePath), c.Arg(1), c.Arg(0), true));
            // EnumSystemLocalesEx(proc, flags, lParam, reserved): proc(name, LOCALE_WINDOWS, lParam).
            i.Register("kernel32.dll", "EnumSystemLocalesEx", CallConv.Stdcall, 4, c =>
            {
                var name = heap.Alloc(32);
                WriteText(name, "en-US", true);
                CallGuest(c.Arg(0), name, 1, c.Arg(2));
                heap.Free(name);
                return 1;
            });
            i.Register("kernel32.dll", "GetLogicalProcessorInformationEx", CallConv.Stdcall, 3, c =>
                LogicalProcessorInformationEx(c.Arg(0), c.Arg(1), c.Arg(2)));
        }

        /// <summary>Only this process is reachable; other handles are refused.</summary>
        private bool IsThisProcess(uint handle) => handle == PseudoCurrentProcess || handle == 0 || handle == 0xFFFFFFFF;

        private uint EnumModules(uint array, uint size, uint neededOut)
        {
            var bases = new List<uint>();
            if (process.MainImage != null) bases.Add(process.MainImage.BaseAddress);
            foreach (var image in process.Images) if (image != process.MainImage) bases.Add(image.BaseAddress);
            if (neededOut != 0) memory.Write32(neededOut, (uint)bases.Count * 4);
            for (var n = 0; n < bases.Count && (uint)(n + 1) * 4 <= size && array != 0; n++)
                memory.Write32(array + (uint)n * 4, bases[n]);
            return 1;
        }

        private uint ModuleBaseName(uint module, uint buffer, uint size, bool wide)
        {
            var image = ImageAt(module);
            string name = image != null ? (image == process.MainImage ? ExePath.Substring(ExePath.LastIndexOf('\\') + 1) : image.Name)
                : fakeHandles.TryGetValue(module, out var system) ? system : null;
            if (name == null) { process.LastError = ErrorInvalidHandle; return 0; }
            if (buffer == 0 || size == 0) return 0;
            var text = name.Length < size ? name : name.Substring(0, (int)size - 1);
            if (wide) memory.WriteUnicode(buffer, text); else memory.WriteAnsi(buffer, text);
            return (uint)text.Length;
        }

        // PROCESS_MEMORY_COUNTERS(_EX) (x86): cb, PageFaultCount, then SIZE_T
        // PeakWorkingSetSize, WorkingSetSize, four pool counters, PagefileUsage,
        // PeakPagefileUsage (40 bytes); _EX adds PrivateUsage (44).
        private long peakMapped;

        private uint MemoryCounters(uint counters, uint size)
        {
            if (counters == 0 || size < 40) { process.LastError = ErrorInsufficientBuffer; return 0; }
            var bytes = memory.MappedPages * GuestMemory.PageSize;
            if (bytes > peakMapped) peakMapped = bytes;
            uint now = (uint)Math.Min(bytes, uint.MaxValue), peak = (uint)Math.Min(peakMapped, uint.MaxValue);
            memory.WriteBytes(counters, new byte[Math.Min(size, 44u)]);
            memory.Write32(counters + 0, Math.Min(size, 44u));
            memory.Write32(counters + 8, peak);
            memory.Write32(counters + 12, now);
            memory.Write32(counters + 32, now);
            memory.Write32(counters + 36, peak);
            if (size >= 44) memory.Write32(counters + 40, now);
            return 1;
        }

        // MODULEINFO: lpBaseOfDll +0, SizeOfImage +4, EntryPoint +8.
        private uint ModuleInformation(uint module, uint info, uint size)
        {
            var image = ImageAt(module);
            if (image == null) { process.LastError = ErrorInvalidHandle; return 0; }
            if (info == 0 || size < 12) { process.LastError = ErrorInsufficientBuffer; return 0; }
            memory.Write32(info + 0, image.BaseAddress);
            memory.Write32(info + 4, image.ImageSize);
            memory.Write32(info + 8, image.EntryPoint);
            return 1;
        }

        private static uint DosError(uint status)
        {
            switch (status)
            {
                case 0x00000000: return 0;
                case 0x00000103: return 997;   // STATUS_PENDING -> ERROR_IO_PENDING
                case 0x80000005: return 234;   // BUFFER_OVERFLOW -> ERROR_MORE_DATA
                case 0xC0000001: return 31;    // UNSUCCESSFUL -> ERROR_GEN_FAILURE
                case 0xC0000002: return 1;     // NOT_IMPLEMENTED -> ERROR_INVALID_FUNCTION
                case 0xC0000005: return 998;   // ACCESS_VIOLATION -> ERROR_NOACCESS
                case 0xC0000008: return 6;     // INVALID_HANDLE
                case 0xC000000D: return 87;    // INVALID_PARAMETER
                case 0xC0000011: return 38;    // END_OF_FILE -> ERROR_HANDLE_EOF
                case 0xC0000017: return 8;     // NO_MEMORY -> ERROR_NOT_ENOUGH_MEMORY
                case 0xC0000022: return 5;     // ACCESS_DENIED
                case 0xC0000023: return 122;   // BUFFER_TOO_SMALL -> ERROR_INSUFFICIENT_BUFFER
                case 0xC0000034: return 2;     // OBJECT_NAME_NOT_FOUND -> ERROR_FILE_NOT_FOUND
                case 0xC0000035: return 183;   // OBJECT_NAME_COLLISION -> ERROR_ALREADY_EXISTS
                case 0xC000003A: return 3;     // OBJECT_PATH_NOT_FOUND -> ERROR_PATH_NOT_FOUND
                case 0xC00000BB: return 50;    // NOT_SUPPORTED
                case 0xC0000120: return 995;   // CANCELLED -> ERROR_OPERATION_ABORTED
                case 0xC0000135: return 126;   // DLL_NOT_FOUND -> ERROR_MOD_NOT_FOUND
                case 0xC0000139: return 127;   // ENTRYPOINT_NOT_FOUND -> ERROR_PROC_NOT_FOUND
                default: return 317;           // ERROR_MR_MID_NOT_FOUND, as Windows answers for the unmapped
            }
        }

        // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX (x86): Relationship +0, Size +4,
        // then the relationship's body. GROUP_AFFINITY is Mask (4), Group (2),
        // Reserved[3] (6) = 12 bytes on 32-bit.
        private uint LogicalProcessorInformationEx(uint relationship, uint buffer, uint lengthPointer)
        {
            const uint Core = 0, Numa = 1, Cache = 2, Package = 3, Group = 4, All = 0xFFFF;
            var records = new List<byte[]>();
            var all = (1u << (int)ProcessorCount) - 1;

            byte[] Record(uint kind, int bodySize, Action<byte[]> fill)
            {
                var r = new byte[8 + bodySize];
                Put(r, 0, kind);
                Put(r, 4, (uint)r.Length);
                fill(r);
                return r;
            }
            // PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass, Reserved[20], GroupCount, GroupMask[1].
            byte[] Processor(uint kind, uint mask) => Record(kind, 36, r => { Put16(r, 8 + 22, 1); Put(r, 8 + 24, mask); });

            if (relationship == Core || relationship == All)
                for (uint n = 0; n < ProcessorCount; n++) records.Add(Processor(Core, 1u << (int)n));
            if (relationship == Package || relationship == All) records.Add(Processor(Package, all));
            if (relationship == Numa || relationship == All)
                // NUMA_NODE_RELATIONSHIP: NodeNumber, Reserved[18], GroupCount, GroupMask.
                records.Add(Record(Numa, 36, r => { Put16(r, 8 + 22, 1); Put(r, 8 + 24, all); }));
            if (relationship == Cache || relationship == All)
            {
                // CACHE_RELATIONSHIP: Level, Associativity, LineSize, CacheSize, Type, Reserved[18], GroupCount, GroupMask.
                void AddCache(uint level, uint ways, uint size, uint type, uint mask) =>
                    records.Add(Record(Cache, 44, r =>
                    {
                        r[8] = (byte)level; r[9] = (byte)ways; Put16(r, 10, 64);
                        Put(r, 12, size); Put(r, 16, type); Put16(r, 8 + 30, 1); Put(r, 8 + 32, mask);
                    }));
                for (uint n = 0; n < ProcessorCount; n++)
                {
                    AddCache(1, 8, 32 * 1024, 2, 1u << (int)n);
                    AddCache(1, 8, 32 * 1024, 1, 1u << (int)n);
                    AddCache(2, 8, 512 * 1024, 0, 1u << (int)n);
                }
                AddCache(3, 16, 8 * 1024 * 1024, 0, all);
            }
            if (relationship == Group || relationship == All)
                // GROUP_RELATIONSHIP: MaximumGroupCount, ActiveGroupCount, Reserved[20], then one
                // PROCESSOR_GROUP_INFO: MaximumProcessorCount, ActiveProcessorCount, Reserved[38], ActiveProcessorMask.
                records.Add(Record(Group, 68, r =>
                {
                    Put16(r, 8, 1); Put16(r, 10, 1);
                    r[8 + 24] = (byte)ProcessorCount; r[8 + 25] = (byte)ProcessorCount; Put(r, 8 + 64, all);
                }));
            if (records.Count == 0) { process.LastError = ErrorInvalidParameter; return 0; }

            if (lengthPointer == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            uint needed = 0;
            foreach (var r in records) needed += (uint)r.Length;
            var given = memory.Read32(lengthPointer);
            memory.Write32(lengthPointer, needed);
            if (buffer == 0 || given < needed) { process.LastError = ErrorInsufficientBuffer; return 0; }
            var at = buffer;
            foreach (var r in records) { memory.WriteBytes(at, r); at += (uint)r.Length; }
            return 1;
        }

        private static void Put(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        private static void Put16(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8);
        }
    }
}
