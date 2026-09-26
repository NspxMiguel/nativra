using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // The rest of kernel32 a 32-bit game reaches for, family by family, with
    // Wine's kernel32 as the reference for return values and error codes:
    // the Interlocked calls (real atomics on the guest address), the Rtl
    // memory helpers kernel32 forwards, pointer probes, resources, private
    // profile (.ini) files, file mappings, atoms, the old _l* file calls,
    // system folders and volume information, date and time pictures,
    // FormatMessage, process and power queries, waitable timers, and the
    // console calls a GUI program touches in passing.
    public sealed partial class GuestKernel
    {
        private const uint ErrorNotSupported = 50, ErrorNotEnoughMemory = 8;
        private const uint ErrorResourceDataNotFound = 1812, ErrorResourceTypeNotFound = 1813;
        private const uint ErrorResourceNameNotFound = 1814;
        private const string WindowsFolder = "C:\\Windows";
        private const string SystemFolder = "C:\\Windows\\system32";

        private readonly Dictionary<uint, FileMapping> mappings = new Dictionary<uint, FileMapping>();
        private readonly Dictionary<uint, MappedView> views = new Dictionary<uint, MappedView>();
        private readonly Dictionary<string, ushort> atomsByName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ushort, string> atomNames = new Dictionary<ushort, string>();
        private ushort nextAtom = 0xC000;

        private sealed class FileMapping
        {
            public string Name;
            public uint File;          // 0 for a mapping backed by the page file
            public long Size;
            public bool Writable;
            public uint Shared;        // guest memory of a page-file mapping, shared by every view
        }

        private sealed class MappedView
        {
            public FileMapping Mapping;
            public long Offset;
            public uint Size;
            public bool Writable;
        }

        private sealed class GuestTimer : Waitable
        {
            public Func<long> Now;
            public bool ManualReset;
            public long Due;       // 0: not set
            public long Period;
            public bool Signaled;
            public override bool Ready(uint thread)
            {
                if (!Signaled && Due != 0 && Now() >= Due)
                {
                    Signaled = true;
                    Due = Period > 0 ? Due + Period : 0;
                }
                return Signaled;
            }
            public override void Consume(uint thread) { if (!ManualReset) Signaled = false; }
        }

        private void InstallKernel32(GuestImports i)
        {
            const string k = "kernel32.dll";

            // --- Interlocked ------------------------------------------------
            i.Register(k, "InterlockedIncrement", CallConv.Stdcall, 1, c => memory.ExchangeAdd32(c.Arg(0), 1) + 1);
            i.Register(k, "InterlockedDecrement", CallConv.Stdcall, 1, c => memory.ExchangeAdd32(c.Arg(0), 0xFFFFFFFF) - 1);
            i.Register(k, "InterlockedExchange", CallConv.Stdcall, 2, c => memory.Exchange32(c.Arg(0), c.Arg(1)));
            i.Register(k, "InterlockedExchangeAdd", CallConv.Stdcall, 2, c => memory.ExchangeAdd32(c.Arg(0), c.Arg(1)));
            i.Register(k, "InterlockedCompareExchange", CallConv.Stdcall, 3, c =>
                memory.CompareExchange32(c.Arg(0), c.Arg(1), c.Arg(2)));
            i.Register(k, "InterlockedCompareExchange64", CallConv.Stdcall, 5, c =>
                memory.CompareExchange64(c.Arg(0), c.Arg64(1), c.Arg64(3)));

            // --- Rtl memory helpers (exported by kernel32 on x86) -------------
            foreach (var m in new[] { k, "ntdll.dll" })
            {
                i.Register(m, "RtlMoveMemory", CallConv.Stdcall, 3, c => { MoveMemory(c.Arg(0), c.Arg(1), c.Arg(2)); return 0; });
                i.Register(m, "RtlCopyMemory", CallConv.Stdcall, 3, c => { MoveMemory(c.Arg(0), c.Arg(1), c.Arg(2)); return 0; });
                i.Register(m, "RtlZeroMemory", CallConv.Stdcall, 2, c => { FillMemory(c.Arg(0), c.Arg(1), 0); return 0; });
                i.Register(m, "RtlFillMemory", CallConv.Stdcall, 3, c => { FillMemory(c.Arg(0), c.Arg(1), (byte)c.Arg(2)); return 0; });
            }
            i.Register(k, "MulDiv", CallConv.Stdcall, 3, c => (uint)MulDiv((int)c.Arg(0), (int)c.Arg(1), (int)c.Arg(2)));

            // --- pointer probes ------------------------------------------------
            i.Register(k, "IsBadReadPtr", CallConv.Stdcall, 2, c => Readable(c.Arg(0), c.Arg(1)) ? 0u : 1u);
            i.Register(k, "IsBadWritePtr", CallConv.Stdcall, 2, c => Readable(c.Arg(0), c.Arg(1)) ? 0u : 1u);
            i.Register(k, "IsBadHugeReadPtr", CallConv.Stdcall, 2, c => Readable(c.Arg(0), c.Arg(1)) ? 0u : 1u);
            i.Register(k, "IsBadHugeWritePtr", CallConv.Stdcall, 2, c => Readable(c.Arg(0), c.Arg(1)) ? 0u : 1u);
            i.Register(k, "IsBadCodePtr", CallConv.Stdcall, 1, c => Readable(c.Arg(0), 1) ? 0u : 1u);
            i.Register(k, "IsBadStringPtrA", CallConv.Stdcall, 2, c => StringReadable(c.Arg(0), c.Arg(1), 1) ? 0u : 1u);
            i.Register(k, "IsBadStringPtrW", CallConv.Stdcall, 2, c => StringReadable(c.Arg(0), c.Arg(1), 2) ? 0u : 1u);

            // --- resources ------------------------------------------------------
            i.Register(k, "FindResourceA", CallConv.Stdcall, 3, c => FindResource(c.Arg(0), c.Arg(2), c.Arg(1), false));
            i.Register(k, "FindResourceW", CallConv.Stdcall, 3, c => FindResource(c.Arg(0), c.Arg(2), c.Arg(1), true));
            i.Register(k, "FindResourceExA", CallConv.Stdcall, 4, c => FindResource(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(k, "FindResourceExW", CallConv.Stdcall, 4, c => FindResource(c.Arg(0), c.Arg(1), c.Arg(2), true));
            i.Register(k, "LoadResource", CallConv.Stdcall, 2, c =>
                c.Arg(1) == 0 ? 0 : ModuleOrMain(c.Arg(0)) + memory.Read32(c.Arg(1)));
            i.Register(k, "LockResource", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(k, "SizeofResource", CallConv.Stdcall, 2, c => c.Arg(1) == 0 ? 0 : memory.Read32(c.Arg(1) + 4));
            i.Register(k, "FreeResource", CallConv.Stdcall, 1, c => 0);

            // --- private profile (.ini) files ------------------------------------
            i.Register(k, "GetPrivateProfileStringA", CallConv.Stdcall, 6, c => ProfileString(c, false));
            i.Register(k, "GetPrivateProfileStringW", CallConv.Stdcall, 6, c => ProfileString(c, true));
            i.Register(k, "GetPrivateProfileIntA", CallConv.Stdcall, 4, c => ProfileInt(c, false));
            i.Register(k, "GetPrivateProfileIntW", CallConv.Stdcall, 4, c => ProfileInt(c, true));
            i.Register(k, "WritePrivateProfileStringA", CallConv.Stdcall, 4, c => WriteProfileString(c, false));
            i.Register(k, "WritePrivateProfileStringW", CallConv.Stdcall, 4, c => WriteProfileString(c, true));
            i.Register(k, "GetPrivateProfileSectionA", CallConv.Stdcall, 4, c => ProfileSection(c, false));
            i.Register(k, "GetPrivateProfileSectionW", CallConv.Stdcall, 4, c => ProfileSection(c, true));
            i.Register(k, "GetPrivateProfileSectionNamesA", CallConv.Stdcall, 3, c => ProfileSectionNames(c, false));
            i.Register(k, "GetPrivateProfileSectionNamesW", CallConv.Stdcall, 3, c => ProfileSectionNames(c, true));
            // win.ini: nothing is there, so the caller's default comes back.
            i.Register(k, "GetProfileStringA", CallConv.Stdcall, 5, c => CopyTruncated(ReadText(c.Arg(2), false), c.Arg(3), c.Arg(4), false));
            i.Register(k, "GetProfileStringW", CallConv.Stdcall, 5, c => CopyTruncated(ReadText(c.Arg(2), true), c.Arg(3), c.Arg(4), true));
            i.Register(k, "GetProfileIntA", CallConv.Stdcall, 3, c => c.Arg(2));
            i.Register(k, "GetProfileIntW", CallConv.Stdcall, 3, c => c.Arg(2));
            i.Register(k, "WriteProfileStringA", CallConv.Stdcall, 3, c => 1);
            i.Register(k, "WriteProfileStringW", CallConv.Stdcall, 3, c => 1);

            // --- file mappings ----------------------------------------------------
            i.Register(k, "CreateFileMappingA", CallConv.Stdcall, 6, c => CreateFileMapping(c, false));
            i.Register(k, "CreateFileMappingW", CallConv.Stdcall, 6, c => CreateFileMapping(c, true));
            i.Register(k, "OpenFileMappingA", CallConv.Stdcall, 3, c => OpenFileMapping(ReadText(c.Arg(2), false)));
            i.Register(k, "OpenFileMappingW", CallConv.Stdcall, 3, c => OpenFileMapping(ReadText(c.Arg(2), true)));
            i.Register(k, "MapViewOfFile", CallConv.Stdcall, 5, c => MapView(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), 0));
            i.Register(k, "MapViewOfFileEx", CallConv.Stdcall, 6, c => MapView(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
            i.Register(k, "FlushViewOfFile", CallConv.Stdcall, 2, c => FlushView(c.Arg(0)) ? 1u : 0u);
            i.Register(k, "UnmapViewOfFile", CallConv.Stdcall, 1, c => UnmapView(c.Arg(0)) ? 1u : 0u);

            // --- atoms --------------------------------------------------------------
            foreach (var prefix in new[] { "", "Global" })
            {
                i.Register(k, prefix + "AddAtomA", CallConv.Stdcall, 1, c => AddAtom(c.Arg(0), false));
                i.Register(k, prefix + "AddAtomW", CallConv.Stdcall, 1, c => AddAtom(c.Arg(0), true));
                i.Register(k, prefix + "FindAtomA", CallConv.Stdcall, 1, c => FindAtom(c.Arg(0), false));
                i.Register(k, prefix + "FindAtomW", CallConv.Stdcall, 1, c => FindAtom(c.Arg(0), true));
                i.Register(k, prefix + "GetAtomNameA", CallConv.Stdcall, 3, c => AtomName(c.Arg(0), c.Arg(1), c.Arg(2), false));
                i.Register(k, prefix + "GetAtomNameW", CallConv.Stdcall, 3, c => AtomName(c.Arg(0), c.Arg(1), c.Arg(2), true));
                i.Register(k, prefix + "DeleteAtom", CallConv.Stdcall, 1, c => DeleteAtom(c.Arg(0)));
            }
            i.Register(k, "InitAtomTable", CallConv.Stdcall, 1, c => 1);

            // --- the 16-bit-era file calls --------------------------------------------
            i.Register(k, "_lopen", CallConv.Stdcall, 2, c => LegacyOpen(ReadText(c.Arg(0), false), c.Arg(1) & 3, false));
            i.Register(k, "_lcreat", CallConv.Stdcall, 2, c => LegacyOpen(ReadText(c.Arg(0), false), 2, true));
            i.Register(k, "_lread", CallConv.Stdcall, 3, c => LegacyTransfer(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(k, "_hread", CallConv.Stdcall, 3, c => LegacyTransfer(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(k, "_lwrite", CallConv.Stdcall, 3, c => LegacyTransfer(c.Arg(0), c.Arg(1), c.Arg(2), true));
            i.Register(k, "_hwrite", CallConv.Stdcall, 3, c => LegacyTransfer(c.Arg(0), c.Arg(1), c.Arg(2), true));
            i.Register(k, "_llseek", CallConv.Stdcall, 3, c => SetFilePointer(c.Arg(0), c.Arg(1), 0, c.Arg(2)));
            i.Register(k, "_lclose", CallConv.Stdcall, 1, c => CloseHandle(c.Arg(0)) != 0 ? 0 : 0xFFFFFFFF);
            i.Register(k, "OpenFile", CallConv.Stdcall, 3, c => OpenFileLegacy(c.Arg(0), c.Arg(1), c.Arg(2)));

            // --- more file calls ----------------------------------------------------
            i.Register(k, "CopyFileA", CallConv.Stdcall, 3, c => CopyFile(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false), c.Arg(2) != 0, false));
            i.Register(k, "CopyFileW", CallConv.Stdcall, 3, c => CopyFile(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true), c.Arg(2) != 0, false));
            i.Register(k, "MoveFileA", CallConv.Stdcall, 2, c => CopyFile(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false), true, true));
            i.Register(k, "MoveFileW", CallConv.Stdcall, 2, c => CopyFile(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true), true, true));
            i.Register(k, "MoveFileExA", CallConv.Stdcall, 3, c => CopyFile(ReadText(c.Arg(0), false), ReadText(c.Arg(1), false), (c.Arg(2) & 1) == 0, true));
            i.Register(k, "MoveFileExW", CallConv.Stdcall, 3, c => CopyFile(ReadText(c.Arg(0), true), ReadText(c.Arg(1), true), (c.Arg(2) & 1) == 0, true));
            i.Register(k, "RemoveDirectoryA", CallConv.Stdcall, 1, c => DeletePath(ReadText(c.Arg(0), false)));
            i.Register(k, "RemoveDirectoryW", CallConv.Stdcall, 1, c => DeletePath(ReadText(c.Arg(0), true)));
            i.Register(k, "GetFileTime", CallConv.Stdcall, 4, c => GetFileTime(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(k, "SetFileTime", CallConv.Stdcall, 4, c => 1);
            i.Register(k, "GetCompressedFileSizeA", CallConv.Stdcall, 2, c => CompressedSize(ReadText(c.Arg(0), false), c.Arg(1)));
            i.Register(k, "GetCompressedFileSizeW", CallConv.Stdcall, 2, c => CompressedSize(ReadText(c.Arg(0), true), c.Arg(1)));
            i.Register(k, "GetTempFileNameA", CallConv.Stdcall, 4, c => TempFileName(c, false));
            i.Register(k, "GetTempFileNameW", CallConv.Stdcall, 4, c => TempFileName(c, true));
            i.Register(k, "GetShortPathNameA", CallConv.Stdcall, 3, c => CopyPathResult(ReadText(c.Arg(0), false), c.Arg(1), c.Arg(2), false));
            i.Register(k, "GetShortPathNameW", CallConv.Stdcall, 3, c => CopyPathResult(ReadText(c.Arg(0), true), c.Arg(1), c.Arg(2), true));
            i.Register(k, "GetLongPathNameA", CallConv.Stdcall, 3, c => CopyPathResult(ReadText(c.Arg(0), false), c.Arg(1), c.Arg(2), false));
            i.Register(k, "GetLongPathNameW", CallConv.Stdcall, 3, c => CopyPathResult(ReadText(c.Arg(0), true), c.Arg(1), c.Arg(2), true));
            i.Register(k, "LockFile", CallConv.Stdcall, 5, c => 1);
            i.Register(k, "UnlockFile", CallConv.Stdcall, 5, c => 1);
            i.Register(k, "LockFileEx", CallConv.Stdcall, 6, c => 1);
            i.Register(k, "UnlockFileEx", CallConv.Stdcall, 5, c => 1);
            i.Register(k, "CancelIo", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "DeviceIoControl", CallConv.Stdcall, 8, c => { process.LastError = ErrorNotSupported; return 0; });
            i.Register(k, "GetBinaryTypeA", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "GetBinaryTypeW", CallConv.Stdcall, 2, c => 0);

            // --- folders and volumes ----------------------------------------------------
            i.Register(k, "GetWindowsDirectoryA", CallConv.Stdcall, 2, c => CopyPathResult(WindowsFolder, c.Arg(0), c.Arg(1), false));
            i.Register(k, "GetWindowsDirectoryW", CallConv.Stdcall, 2, c => CopyPathResult(WindowsFolder, c.Arg(0), c.Arg(1), true));
            i.Register(k, "GetSystemWindowsDirectoryA", CallConv.Stdcall, 2, c => CopyPathResult(WindowsFolder, c.Arg(0), c.Arg(1), false));
            i.Register(k, "GetSystemWindowsDirectoryW", CallConv.Stdcall, 2, c => CopyPathResult(WindowsFolder, c.Arg(0), c.Arg(1), true));
            i.Register(k, "GetSystemDirectoryA", CallConv.Stdcall, 2, c => CopyPathResult(SystemFolder, c.Arg(0), c.Arg(1), false));
            i.Register(k, "GetSystemDirectoryW", CallConv.Stdcall, 2, c => CopyPathResult(SystemFolder, c.Arg(0), c.Arg(1), true));
            i.Register(k, "GetComputerNameA", CallConv.Stdcall, 2, c => NameInto("XBOX", c.Arg(0), c.Arg(1), false));
            i.Register(k, "GetComputerNameW", CallConv.Stdcall, 2, c => NameInto("XBOX", c.Arg(0), c.Arg(1), true));
            i.Register(k, "GetComputerNameExA", CallConv.Stdcall, 3, c => NameInto("XBOX", c.Arg(1), c.Arg(2), false));
            i.Register(k, "GetComputerNameExW", CallConv.Stdcall, 3, c => NameInto("XBOX", c.Arg(1), c.Arg(2), true));
            i.Register(k, "ExpandEnvironmentStringsA", CallConv.Stdcall, 3, c => ExpandEnvironment(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(k, "ExpandEnvironmentStringsW", CallConv.Stdcall, 3, c => ExpandEnvironment(c.Arg(0), c.Arg(1), c.Arg(2), true));
            i.Register(k, "GetLogicalDrives", CallConv.Stdcall, 0, c => 1u << 2);   // C:
            i.Register(k, "GetLogicalDriveStringsA", CallConv.Stdcall, 2, c => DriveStrings(c.Arg(0), c.Arg(1), false));
            i.Register(k, "GetLogicalDriveStringsW", CallConv.Stdcall, 2, c => DriveStrings(c.Arg(0), c.Arg(1), true));
            i.Register(k, "GetDiskFreeSpaceA", CallConv.Stdcall, 5, c => DiskFreeSpace(c));
            i.Register(k, "GetDiskFreeSpaceW", CallConv.Stdcall, 5, c => DiskFreeSpace(c));
            i.Register(k, "GetDiskFreeSpaceExA", CallConv.Stdcall, 4, c => DiskFreeSpaceEx(c));
            i.Register(k, "GetDiskFreeSpaceExW", CallConv.Stdcall, 4, c => DiskFreeSpaceEx(c));
            i.Register(k, "GetVolumeInformationA", CallConv.Stdcall, 8, c => VolumeInformation(c, false));
            i.Register(k, "GetVolumeInformationW", CallConv.Stdcall, 8, c => VolumeInformation(c, true));

            // --- dates and times ------------------------------------------------------------
            i.Register(k, "CompareFileTime", CallConv.Stdcall, 2, c => Sign(memory.Read64(c.Arg(0)).CompareTo(memory.Read64(c.Arg(1)))));
            i.Register(k, "LocalFileTimeToFileTime", CallConv.Stdcall, 2, c => { memory.Write64(c.Arg(1), memory.Read64(c.Arg(0))); return 1; });
            i.Register(k, "FileTimeToDosDateTime", CallConv.Stdcall, 3, c => FileTimeToDos(c.Arg(0), c.Arg(1), c.Arg(2)));
            i.Register(k, "DosDateTimeToFileTime", CallConv.Stdcall, 3, c => DosToFileTime((ushort)c.Arg(0), (ushort)c.Arg(1), c.Arg(2)));
            i.Register(k, "SystemTimeToTzSpecificLocalTime", CallConv.Stdcall, 3, c => { MoveMemory(c.Arg(2), c.Arg(1), 16); return 1; });
            i.Register(k, "TzSpecificLocalTimeToSystemTime", CallConv.Stdcall, 3, c => { MoveMemory(c.Arg(2), c.Arg(1), 16); return 1; });
            i.Register(k, "GetDateFormatA", CallConv.Stdcall, 6, c => FormatDateTime(c, true, false));
            i.Register(k, "GetDateFormatW", CallConv.Stdcall, 6, c => FormatDateTime(c, true, true));
            i.Register(k, "GetDateFormatEx", CallConv.Stdcall, 7, c => FormatDateTime(c, true, true));
            i.Register(k, "GetTimeFormatA", CallConv.Stdcall, 6, c => FormatDateTime(c, false, false));
            i.Register(k, "GetTimeFormatW", CallConv.Stdcall, 6, c => FormatDateTime(c, false, true));
            i.Register(k, "GetTimeFormatEx", CallConv.Stdcall, 6, c => FormatDateTime(c, false, true));

            // --- messages -----------------------------------------------------------------
            i.Register(k, "FormatMessageA", CallConv.Stdcall, 7, c => FormatMessage(c, false));
            i.Register(k, "FormatMessageW", CallConv.Stdcall, 7, c => FormatMessage(c, true));

            // --- character types ----------------------------------------------------------
            i.Register(k, "GetStringTypeA", CallConv.Stdcall, 5, c => StringTypeA(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(k, "GetStringTypeExA", CallConv.Stdcall, 5, c => StringTypeA(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(k, "IsDBCSLeadByte", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "IsDBCSLeadByteEx", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "EnumSystemLocalesA", CallConv.Stdcall, 2, c => { EnumLocale(c.Arg(0), false); return 1; });
            i.Register(k, "EnumSystemLocalesW", CallConv.Stdcall, 2, c => { EnumLocale(c.Arg(0), true); return 1; });

            // --- processes, power, the machine ----------------------------------------------
            i.Register(k, "GetProcessTimes", CallConv.Stdcall, 5, c => ProcessTimes(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(k, "GetThreadTimes", CallConv.Stdcall, 5, c => ProcessTimes(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4)));
            i.Register(k, "GetSystemTimes", CallConv.Stdcall, 3, c =>
            {
                var busy = (ulong)Milliseconds * 10_000;
                if (c.Arg(0) != 0) memory.Write64(c.Arg(0), busy * 3);
                if (c.Arg(1) != 0) memory.Write64(c.Arg(1), busy * 3 + busy / 2);
                if (c.Arg(2) != 0) memory.Write64(c.Arg(2), busy / 2);
                return 1;
            });
            i.Register(k, "GetPriorityClass", CallConv.Stdcall, 1, c => 0x20);   // NORMAL_PRIORITY_CLASS
            i.Register(k, "SetPriorityClass", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetProcessAffinityMask", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetProcessPriorityBoost", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetProcessWorkingSetSize", CallConv.Stdcall, 3, c => 1);
            i.Register(k, "GetProcessWorkingSetSize", CallConv.Stdcall, 3, c =>
            {
                if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 200 * 1024);
                if (c.Arg(2) != 0) memory.Write32(c.Arg(2), 1380 * 1024);
                return 1;
            });
            i.Register(k, "GetProcessVersion", CallConv.Stdcall, 1, c => 0x00060000);
            i.Register(k, "OpenProcess", CallConv.Stdcall, 3, c =>
            {
                if (c.Arg(2) == GuestProcess.ProcessId) return PseudoCurrentProcess;
                process.LastError = ErrorAccessDenied;
                return 0;
            });
            i.Register(k, "GetExitCodeProcess", CallConv.Stdcall, 2, c => { memory.Write32(c.Arg(1), 259); return 1; });   // STILL_ACTIVE
            i.Register(k, "GetProcessId", CallConv.Stdcall, 1, c => GuestProcess.ProcessId);
            i.Register(k, "CreateProcessA", CallConv.Stdcall, 10, c => { process.LastError = ErrorNotSupported; return 0; });
            i.Register(k, "CreateProcessW", CallConv.Stdcall, 10, c => { process.LastError = ErrorNotSupported; return 0; });
            i.Register(k, "WinExec", CallConv.Stdcall, 2, c => 2);   // ERROR_FILE_NOT_FOUND
            i.Register(k, "GetThreadContext", CallConv.Stdcall, 2, c => { process.LastError = ErrorNotSupported; return 0; });
            i.Register(k, "SetThreadContext", CallConv.Stdcall, 2, c => { process.LastError = ErrorNotSupported; return 0; });
            i.Register(k, "SetThreadExecutionState", CallConv.Stdcall, 1, c => 0x80000000);   // ES_CONTINUOUS before
            i.Register(k, "GetSystemPowerStatus", CallConv.Stdcall, 1, c =>
            {
                memory.Write8(c.Arg(0), 1);            // on AC power
                memory.Write8(c.Arg(0) + 1, 128);      // no system battery
                memory.Write8(c.Arg(0) + 2, 255);      // unknown percentage
                memory.Write8(c.Arg(0) + 3, 0);
                memory.Write32(c.Arg(0) + 4, 0xFFFFFFFF);
                memory.Write32(c.Arg(0) + 8, 0xFFFFFFFF);
                return 1;
            });
            i.Register(k, "Beep", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "GetLargePageMinimum", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "VirtualLock", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "VirtualUnlock", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "GetHandleInformation", CallConv.Stdcall, 2, c => { if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0); return 1; });
            i.Register(k, "SetHandleInformation", CallConv.Stdcall, 3, c => 1);
            i.Register(k, "GetSystemDefaultLocaleName", CallConv.Stdcall, 2, c => CopyPathResult("en-US", c.Arg(0), c.Arg(1), true) + 1);

            // --- named objects (one process: nothing to open by name) ---------------------------
            foreach (var name in new[] { "OpenEventA", "OpenEventW", "OpenMutexA", "OpenMutexW", "OpenSemaphoreA", "OpenSemaphoreW",
                                         "OpenWaitableTimerA", "OpenWaitableTimerW" })
                i.Register(k, name, CallConv.Stdcall, 3, c => { process.LastError = ErrorFileNotFound; return 0; });

            // --- waitable timers -------------------------------------------------------------------
            i.Register(k, "CreateWaitableTimerA", CallConv.Stdcall, 3, c => CreateTimer(c.Arg(1) != 0));
            i.Register(k, "CreateWaitableTimerW", CallConv.Stdcall, 3, c => CreateTimer(c.Arg(1) != 0));
            i.Register(k, "CreateWaitableTimerExA", CallConv.Stdcall, 4, c => CreateTimer((c.Arg(2) & 1) != 0));
            i.Register(k, "CreateWaitableTimerExW", CallConv.Stdcall, 4, c => CreateTimer((c.Arg(2) & 1) != 0));
            i.Register(k, "SetWaitableTimer", CallConv.Stdcall, 6, c => SetTimer(c.Arg(0), c.Arg(1), (int)c.Arg(2)));
            i.Register(k, "SetWaitableTimerEx", CallConv.Stdcall, 7, c => SetTimer(c.Arg(0), c.Arg(1), (int)c.Arg(2)));
            i.Register(k, "CancelWaitableTimer", CallConv.Stdcall, 1, c =>
            {
                if (waitables.TryGetValue(c.Arg(0), out var w) && w is GuestTimer t) { t.Due = 0; return 1; }
                process.LastError = ErrorInvalidHandle;
                return 0;
            });

            // --- console calls a GUI program makes in passing -----------------------------------------
            i.Register(k, "AllocConsole", CallConv.Stdcall, 0, c => 1);
            i.Register(k, "FreeConsole", CallConv.Stdcall, 0, c => 1);
            i.Register(k, "AttachConsole", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "GetConsoleWindow", CallConv.Stdcall, 0, c => 0);
            i.Register(k, "SetConsoleTitleA", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "SetConsoleTitleW", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "GetConsoleTitleA", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "GetConsoleTitleW", CallConv.Stdcall, 2, c => 0);
            i.Register(k, "SetConsoleMode", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetConsoleTextAttribute", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetConsoleCursorPosition", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "SetConsoleCP", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "SetConsoleOutputCP", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "GetConsoleScreenBufferInfo", CallConv.Stdcall, 2, c => { process.LastError = ErrorInvalidHandle; return 0; });
            i.Register(k, "GetNumberOfConsoleInputEvents", CallConv.Stdcall, 2, c => { if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 0); return 1; });
            i.Register(k, "ReadConsoleA", CallConv.Stdcall, 5, c => { if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0); return 1; });
            i.Register(k, "ReadConsoleW", CallConv.Stdcall, 5, c => { if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0); return 1; });
            i.Register(k, "FlushConsoleInputBuffer", CallConv.Stdcall, 1, c => 1);
        }

        // --- memory ---------------------------------------------------------

        internal void MoveMemory(uint destination, uint source, uint count)
        {
            // Read before writing: overlapping ranges behave as memmove.
            const int Chunk = 1 << 20;
            if (destination <= source || destination >= source + count)
            {
                for (uint done = 0; done < count; done += Chunk)
                {
                    var n = (int)Math.Min(Chunk, count - done);
                    memory.WriteBytes(destination + done, memory.ReadBytes(source + done, n));
                }
                return;
            }
            // Overlap with the destination above the source: copy from the end.
            for (var left = count; left > 0;)
            {
                var n = (int)Math.Min(Chunk, left);
                left -= (uint)n;
                memory.WriteBytes(destination + left, memory.ReadBytes(source + left, n));
            }
        }

        internal void FillMemory(uint destination, uint count, byte value)
        {
            const int Chunk = 1 << 16;
            var block = new byte[Math.Min(Chunk, (int)Math.Min(count, int.MaxValue))];
            if (value != 0) for (var n = 0; n < block.Length; n++) block[n] = value;
            for (uint done = 0; done < count; done += Chunk)
                memory.WriteBytes(destination + done, block, 0, (int)Math.Min(Chunk, count - done));
        }

        private static int MulDiv(int a, int b, int c)
        {
            if (c == 0) return -1;
            var product = (long)a * b;
            // Rounded half away from zero, as Windows does.
            var q = Math.Abs(product) + Math.Abs((long)c) / 2;
            q /= Math.Abs((long)c);
            if ((product < 0) != (c < 0)) q = -q;
            return q > int.MaxValue || q < int.MinValue ? -1 : (int)q;
        }

        private bool Readable(uint address, uint size)
        {
            if (size == 0) return true;
            if (address == 0) return false;
            var end = address + size - 1;
            if (end < address) return false;
            for (var page = address & ~(uint)(GuestMemory.PageSize - 1); ; page += GuestMemory.PageSize)
            {
                if (!memory.IsMapped(page)) return false;
                if (page >= (end & ~(uint)(GuestMemory.PageSize - 1))) return true;
            }
        }

        private bool StringReadable(uint address, uint max, uint unit)
        {
            if (address == 0) return false;
            for (uint n = 0; n < max; n++)
            {
                var at = address + n * unit;
                if (!memory.IsMapped(at) || !memory.IsMapped(at + unit - 1)) return false;
                if ((unit == 1 ? memory.Read8(at) : memory.Read16(at)) == 0) return true;
            }
            return true;
        }

        // --- resources ------------------------------------------------------

        private uint ModuleOrMain(uint module) => module == 0 ? MainBase : module;

        private Pe32Image ImageAt(uint module)
        {
            module = ModuleOrMain(module);
            foreach (var image in process.Images) if (image.BaseAddress == module) return image;
            return null;
        }

        /// <summary>
        /// FindResource: walks the image's resource tree type → name → language
        /// and returns the data entry's address (the HRSRC).
        /// </summary>
        private uint FindResource(uint module, uint type, uint name, bool wide)
        {
            var image = ImageAt(module);
            var root = image?.Directory(Pe32Image.DirResource, out _) ?? 0;
            if (root == 0) { process.LastError = ErrorResourceDataNotFound; return 0; }

            var typeDir = ResourceChild(root, root, type, wide);
            if (typeDir == 0) { process.LastError = ErrorResourceTypeNotFound; return 0; }
            var nameDir = ResourceChild(root, typeDir, name, wide);
            if (nameDir == 0) { process.LastError = ErrorResourceNameNotFound; return 0; }
            // Any language: the first entry (neutral or en-US in practice).
            var entry = FirstResourceEntry(root, nameDir);
            if (entry == 0) { process.LastError = ErrorResourceNameNotFound; return 0; }
            return entry;
        }

        /// <summary>The subdirectory (or data entry) under <paramref name="dir"/> that matches an id or a name.</summary>
        private uint ResourceChild(uint root, uint dir, uint key, bool wide)
        {
            string name = null;
            uint id = key;
            if (key >= 0x10000)
            {
                name = ReadText(key, wide);
                if (name.StartsWith("#", StringComparison.Ordinal) && uint.TryParse(name.Substring(1), out var parsed))
                {
                    id = parsed;
                    name = null;
                }
            }
            var named = memory.Read16(dir + 12);
            var ids = memory.Read16(dir + 14);
            for (var n = 0; n < named + ids; n++)
            {
                var entry = dir + 16 + (uint)n * 8;
                var nameField = memory.Read32(entry);
                var target = memory.Read32(entry + 4);
                bool match;
                if ((nameField & 0x80000000) != 0)
                {
                    if (name == null) continue;
                    var s = root + (nameField & 0x7FFFFFFF);
                    var length = memory.Read16(s);
                    var chars = new char[length];
                    for (var ch = 0; ch < length; ch++) chars[ch] = (char)memory.Read16(s + 2 + (uint)ch * 2);
                    match = string.Equals(new string(chars), name, StringComparison.OrdinalIgnoreCase);
                }
                else match = name == null && nameField == id;
                if (!match) continue;
                return (target & 0x80000000) != 0 ? root + (target & 0x7FFFFFFF) : root + target;
            }
            return 0;
        }

        private uint FirstResourceEntry(uint root, uint dir)
        {
            var count = memory.Read16(dir + 12) + memory.Read16(dir + 14);
            if (count == 0) return 0;
            var target = memory.Read32(dir + 16 + 4);
            return (target & 0x80000000) != 0 ? FirstResourceEntry(root, root + (target & 0x7FFFFFFF)) : root + target;
        }

        // --- private profile files ----------------------------------------------

        /// <summary>
        /// An .ini path as Windows resolves it: a bare file name lives in the
        /// Windows folder, which for a game is its own folder here (the guest
        /// has no Windows folder, and writes must land somewhere the game can
        /// read them back).
        /// </summary>
        private string ProfilePath(uint file, bool wide)
        {
            var name = file == 0 ? "win.ini" : ReadText(file, wide);
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) < 0) name = Folder(ExePath) + name;
            return FullPath(name);
        }

        private List<KeyValuePair<string, List<KeyValuePair<string, string>>>> ReadProfile(string path)
        {
            var sections = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>();
            string text;
            try
            {
                using (var stream = Files.Open(path, FileMode.Open, FileAccess.Read))
                {
                    var bytes = new byte[stream.Length];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                        ? Encoding.Unicode.GetString(bytes, 2, read - 2)
                        : Ansi.Decode(bytes);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return sections; }

            List<KeyValuePair<string, string>> current = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim(' ', '\t', '\r');
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[')
                {
                    var close = line.IndexOf(']');
                    var section = close > 0 ? line.Substring(1, close - 1).Trim() : line.Substring(1).Trim();
                    current = new List<KeyValuePair<string, string>>();
                    sections.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(section, current));
                    continue;
                }
                if (current == null) continue;
                var eq = line.IndexOf('=');
                var key = (eq >= 0 ? line.Substring(0, eq) : line).Trim();
                var value = eq >= 0 ? line.Substring(eq + 1).Trim() : "";
                // One level of matching quotes around a value is stripped.
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                    value = value.Substring(1, value.Length - 2);
                current.Add(new KeyValuePair<string, string>(key, value));
            }
            return sections;
        }

        private static List<KeyValuePair<string, string>> Section(
            List<KeyValuePair<string, List<KeyValuePair<string, string>>>> profile, string name)
        {
            foreach (var s in profile)
                if (string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase)) return s.Value;
            return null;
        }

        private static string Lookup(List<KeyValuePair<string, string>> section, string key)
        {
            if (section == null) return null;
            foreach (var pair in section)
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }

        private uint ProfileString(GuestCall c, bool wide)
        {
            uint section = c.Arg(0), key = c.Arg(1), buffer = c.Arg(3), size = c.Arg(4);
            var profile = ReadProfile(ProfilePath(c.Arg(5), wide));
            if (section == 0)
            {
                var names = new List<string>();
                foreach (var s in profile) names.Add(s.Key);
                return CopyList(names, buffer, size, wide);
            }
            var entries = Section(profile, ReadText(section, wide));
            if (key == 0)
            {
                var keys = new List<string>();
                if (entries != null) foreach (var e in entries) keys.Add(e.Key);
                return CopyList(keys, buffer, size, wide);
            }
            var value = Lookup(entries, ReadText(key, wide));
            if (value == null)
            {
                value = ReadText(c.Arg(2), wide).TrimEnd(' ');
                process.LastError = ErrorFileNotFound;
            }
            return CopyTruncated(value, buffer, size, wide);
        }

        private uint ProfileInt(GuestCall c, bool wide)
        {
            var value = Lookup(Section(ReadProfile(ProfilePath(c.Arg(3), wide)), ReadText(c.Arg(0), wide)), ReadText(c.Arg(1), wide));
            if (value == null) return c.Arg(2);
            // Leading digits only, as Windows reads them; a 0x prefix is hex.
            var text = value.Trim();
            var negative = text.StartsWith("-", StringComparison.Ordinal);
            if (negative) text = text.Substring(1);
            long result = 0;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var ch in text.Substring(2))
                {
                    var d = HexDigit(ch);
                    if (d < 0) break;
                    result = (result << 4) | (uint)d;
                }
            }
            else foreach (var ch in text) { if (ch < '0' || ch > '9') break; result = result * 10 + (ch - '0'); }
            return (uint)(negative ? -result : result);
        }

        private static int HexDigit(char ch) =>
            ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1;

        private uint ProfileSection(GuestCall c, bool wide)
        {
            var entries = Section(ReadProfile(ProfilePath(c.Arg(3), wide)), ReadText(c.Arg(0), wide));
            var lines = new List<string>();
            if (entries != null) foreach (var e in entries) lines.Add(e.Key + "=" + e.Value);
            return CopyList(lines, c.Arg(1), c.Arg(2), wide);
        }

        private uint ProfileSectionNames(GuestCall c, bool wide)
        {
            var names = new List<string>();
            foreach (var s in ReadProfile(ProfilePath(c.Arg(2), wide))) names.Add(s.Key);
            return CopyList(names, c.Arg(0), c.Arg(1), wide);
        }

        private uint WriteProfileString(GuestCall c, bool wide)
        {
            if (c.Arg(0) == 0) return 1;   // flush the cache: nothing cached
            var path = ProfilePath(c.Arg(3), wide);
            var profile = ReadProfile(path);
            var sectionName = ReadText(c.Arg(0), wide);
            var entries = Section(profile, sectionName);

            if (c.Arg(1) == 0)
            {
                // A NULL key deletes the whole section.
                profile.RemoveAll(s => string.Equals(s.Key, sectionName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var key = ReadText(c.Arg(1), wide);
                if (entries == null)
                {
                    if (c.Arg(2) == 0) return 1;
                    entries = new List<KeyValuePair<string, string>>();
                    profile.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(sectionName, entries));
                }
                var at = entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
                if (c.Arg(2) == 0) { if (at >= 0) entries.RemoveAt(at); }
                else
                {
                    var pair = new KeyValuePair<string, string>(key, ReadText(c.Arg(2), wide));
                    if (at >= 0) entries[at] = pair; else entries.Add(pair);
                }
            }

            var text = new StringBuilder();
            foreach (var s in profile)
            {
                text.Append('[').Append(s.Key).Append("]\r\n");
                foreach (var e in s.Value) text.Append(e.Key).Append('=').Append(e.Value).Append("\r\n");
            }
            try
            {
                using (var stream = Files.Open(path, FileMode.Create, FileAccess.Write))
                {
                    var bytes = Ansi.Encode(text.ToString());
                    stream.Write(bytes, 0, bytes.Length);
                }
                return 1;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                process.LastError = ErrorAccessDenied;
                return 0;
            }
        }

        /// <summary>
        /// A string into a fixed buffer, truncated to fit with its NUL: returns
        /// the characters copied, not counting the NUL (the profile and
        /// GetProfileString convention).
        /// </summary>
        private uint CopyTruncated(string text, uint buffer, uint size, bool wide)
        {
            if (buffer == 0 || size == 0) return 0;
            if (text.Length > size - 1) text = text.Substring(0, (int)size - 1);
            WriteText(buffer, text, wide);
            return (uint)text.Length;
        }

        /// <summary>
        /// A list of strings, each NUL-terminated, ending in an extra NUL. When
        /// the list does not fit, it is cut and size - 2 is returned, as Windows does.
        /// </summary>
        private uint CopyList(List<string> items, uint buffer, uint size, bool wide)
        {
            if (buffer == 0 || size < 2) return 0;
            var unit = wide ? 2u : 1u;
            uint at = 0;
            foreach (var item in items)
            {
                var text = item;
                if (at + text.Length + 2 > size)
                {
                    text = text.Substring(0, (int)Math.Max(0, size - 2 - at));
                    WriteText(buffer + at * unit, text, wide);
                    WriteText(buffer + (size - 1) * unit, "", wide);
                    return size - 2;
                }
                WriteText(buffer + at * unit, text, wide);
                at += (uint)text.Length + 1;
            }
            WriteText(buffer + at * unit, "", wide);
            return at;
        }

        // --- file mappings -------------------------------------------------------

        private uint CreateFileMapping(GuestCall c, bool wide)
        {
            uint file = c.Arg(0), protect = c.Arg(2);
            var name = c.Arg(5) != 0 ? ReadText(c.Arg(5), wide) : null;
            if (name != null)
                foreach (var pair in mappings)
                    if (string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        process.LastError = ErrorAlreadyExists;
                        return pair.Key;
                    }

            var size = ((long)c.Arg(3) << 32) | c.Arg(4);
            var mapping = new FileMapping
            {
                Name = name,
                Writable = (protect & 0x04) != 0 || (protect & 0x40) != 0 || (protect & 0x08) != 0,   // READWRITE, EXECUTE_READWRITE, WRITECOPY
            };
            if (file != InvalidHandleValue && file != 0)
            {
                if (!TryFile(file, out var f)) return 0;
                mapping.File = file;
                if (size == 0) size = f.Stream.Length;
                else if (size > f.Stream.Length && mapping.Writable) f.Stream.SetLength(size);
            }
            else if (size == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            mapping.Size = size;
            var handle = NewHandle();
            mappings[handle] = mapping;
            process.LastError = 0;
            return handle;
        }

        private uint OpenFileMapping(string name)
        {
            foreach (var pair in mappings)
                if (string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            process.LastError = ErrorFileNotFound;
            return 0;
        }

        private uint MapView(uint handle, uint access, uint offsetHigh, uint offsetLow, uint bytes, uint wanted)
        {
            if (!mappings.TryGetValue(handle, out var mapping)) { process.LastError = ErrorInvalidHandle; return 0; }
            var offset = ((long)offsetHigh << 32) | offsetLow;
            var size = bytes != 0 ? bytes : (uint)Math.Max(0, mapping.Size - offset);
            if (offset + size > mapping.Size || size == 0) { process.LastError = ErrorAccessDenied; return 0; }

            uint at;
            if (mapping.File == 0)
            {
                // Page-file memory: one block every view of the mapping shares.
                if (mapping.Shared == 0) mapping.Shared = VirtualAlloc(0, (uint)mapping.Size);
                at = mapping.Shared + (uint)offset;
            }
            else
            {
                at = VirtualAlloc(wanted, size);
                if (at == 0) { process.LastError = ErrorNotEnoughMemory; return 0; }
                if (!TryFile(mapping.File, out var f)) return 0;
                var saved = f.Stream.Position;
                f.Stream.Position = offset;
                var chunk = new byte[Math.Min(size, 1u << 20)];
                for (uint done = 0; done < size;)
                {
                    var n = f.Stream.Read(chunk, 0, (int)Math.Min((uint)chunk.Length, size - done));
                    if (n <= 0) break;
                    memory.WriteBytes(at + done, chunk, 0, n);
                    done += (uint)n;
                }
                f.Stream.Position = saved;
            }
            const uint FileMapWrite = 0x2, FileMapAllAccess = 0xF001F;
            views[at] = new MappedView
            {
                Mapping = mapping,
                Offset = offset,
                Size = size,
                Writable = mapping.Writable && ((access & FileMapWrite) != 0 || access == FileMapAllAccess),
            };
            return at;
        }

        private bool FlushView(uint address)
        {
            foreach (var pair in views)
            {
                if (address < pair.Key || address >= pair.Key + pair.Value.Size) continue;
                WriteBack(pair.Key, pair.Value);
                return true;
            }
            return false;
        }

        private void WriteBack(uint at, MappedView view)
        {
            if (!view.Writable || view.Mapping.File == 0) return;
            if (!files.TryGetValue(view.Mapping.File, out var f) || f.Stream == null) return;
            var saved = f.Stream.Position;
            f.Stream.Position = view.Offset;
            for (uint done = 0; done < view.Size;)
            {
                var n = (int)Math.Min(1u << 20, view.Size - done);
                f.Stream.Write(memory.ReadBytes(at + done, n), 0, n);
                done += (uint)n;
            }
            f.Stream.Flush();
            f.Stream.Position = saved;
        }

        private bool UnmapView(uint address)
        {
            if (!views.TryGetValue(address, out var view)) { process.LastError = ErrorInvalidParameter; return false; }
            WriteBack(address, view);
            views.Remove(address);
            if (view.Mapping.File != 0) memory.Unmap(address, view.Size);
            return true;
        }

        private bool CloseMapping(uint handle) => mappings.Remove(handle);

        // --- atoms -------------------------------------------------------------------

        private uint AddAtom(uint name, bool wide)
        {
            if (name < 0x10000) return name;   // an integer atom is itself
            var text = ReadText(name, wide);
            if (IntegerAtom(text, out var integer)) return integer;
            if (atomsByName.TryGetValue(text, out var atom)) return atom;
            atom = nextAtom++;
            atomsByName[text] = atom;
            atomNames[atom] = text;
            return atom;
        }

        private uint FindAtom(uint name, bool wide)
        {
            if (name < 0x10000) return name;
            var text = ReadText(name, wide);
            if (IntegerAtom(text, out var integer)) return integer;
            if (atomsByName.TryGetValue(text, out var atom)) return atom;
            process.LastError = ErrorFileNotFound;
            return 0;
        }

        private uint AtomName(uint atom, uint buffer, uint size, bool wide)
        {
            string text;
            if (atom < 0xC000) text = "#" + atom;
            else if (!atomNames.TryGetValue((ushort)atom, out text)) { process.LastError = ErrorInvalidHandle; return 0; }
            return CopyTruncated(text, buffer, size, wide);
        }

        private uint DeleteAtom(uint atom)
        {
            if (atomNames.TryGetValue((ushort)atom, out var text))
            {
                atomNames.Remove((ushort)atom);
                atomsByName.Remove(text);
            }
            return 0;
        }

        private static bool IntegerAtom(string text, out uint atom)
        {
            atom = 0;
            return text.StartsWith("#", StringComparison.Ordinal) && uint.TryParse(text.Substring(1), out atom) && atom > 0 && atom < 0xC000;
        }

        // --- 16-bit-era files -----------------------------------------------------------

        private uint LegacyOpen(string path, uint mode, bool create)
        {
            path = FullPath(path);
            var access = mode == 0 ? FileAccess.Read : mode == 1 ? FileAccess.Write : FileAccess.ReadWrite;
            try
            {
                var stream = Files.Open(path, create ? FileMode.Create : FileMode.Open, create ? FileAccess.ReadWrite : access);
                var handle = NewHandle();
                files[handle] = new OpenFile { Path = path, Stream = stream };
                return handle;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                if (e is FileNotFoundException || e is DirectoryNotFoundException) FilesNotFound.Add(path);
                process.LastError = e is UnauthorizedAccessException ? ErrorAccessDenied : ErrorFileNotFound;
                return 0xFFFFFFFF;   // HFILE_ERROR
            }
        }

        private uint LegacyTransfer(uint handle, uint buffer, uint count, bool write)
        {
            var done = heap.Alloc(4);
            var ok = write ? WriteFile(handle, buffer, count, done) : ReadFile(handle, buffer, count, done);
            var n = memory.Read32(done);
            heap.Free(done);
            return ok != 0 ? n : 0xFFFFFFFF;
        }

        private uint OpenFileLegacy(uint name, uint ofstruct, uint style)
        {
            const uint OfExist = 0x4000, OfCreate = 0x1000, OfDelete = 0x200, OfParse = 0x100;
            var path = FullPath(ReadText(name, false));
            if (ofstruct != 0)
            {
                memory.Write8(ofstruct, 136);   // cBytes
                memory.Write8(ofstruct + 1, 1); // fFixedDisk
                WriteText(ofstruct + 8, path.Length > 127 ? path.Substring(0, 127) : path, false);
            }
            if ((style & OfParse) != 0) return 0;
            if ((style & OfDelete) != 0) return Files.Delete(path) ? 1u : 0xFFFFFFFF;
            if ((style & OfExist) != 0)
            {
                if (Files.Stat(path) != null) return 1;
                process.LastError = ErrorFileNotFound;
                return 0xFFFFFFFF;
            }
            return LegacyOpen(path, style & 3, (style & OfCreate) != 0);
        }

        private uint CopyFile(string from, string to, bool failIfExists, bool move)
        {
            from = FullPath(from);
            to = FullPath(to);
            if (Files.Stat(from) == null) { FilesNotFound.Add(from); process.LastError = ErrorFileNotFound; return 0; }
            if (failIfExists && Files.Stat(to) != null) { process.LastError = move ? ErrorAlreadyExists : ErrorFileExists; return 0; }
            try
            {
                using (var source = Files.Open(from, FileMode.Open, FileAccess.Read))
                using (var target = Files.Open(to, FileMode.Create, FileAccess.Write))
                    source.CopyTo(target);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                process.LastError = ErrorAccessDenied;
                return 0;
            }
            if (move) Files.Delete(from);
            return 1;
        }

        private uint DeletePath(string path)
        {
            if (Files.Delete(FullPath(path))) return 1;
            process.LastError = ErrorFileNotFound;
            return 0;
        }

        private uint GetFileTime(uint handle, uint creation, uint access, uint write)
        {
            if (!files.TryGetValue(handle, out var f)) { process.LastError = ErrorInvalidHandle; return 0; }
            var entry = Files.Stat(f.Path);
            var time = (ulong)(entry?.WriteTimeUtc ?? UtcNow).ToFileTimeUtc();
            if (creation != 0) memory.Write64(creation, time);
            if (access != 0) memory.Write64(access, time);
            if (write != 0) memory.Write64(write, time);
            return 1;
        }

        private uint CompressedSize(string path, uint highOut)
        {
            var entry = Files.Stat(FullPath(path));
            if (entry == null) { process.LastError = ErrorFileNotFound; return 0xFFFFFFFF; }
            if (highOut != 0) memory.Write32(highOut, (uint)(entry.Size >> 32));
            return (uint)entry.Size;
        }

        private uint TempFileName(GuestCall c, bool wide)
        {
            var folder = ReadText(c.Arg(0), wide).TrimEnd('\\');
            var prefix = ReadText(c.Arg(1), wide);
            if (prefix.Length > 3) prefix = prefix.Substring(0, 3);
            var unique = c.Arg(2) & 0xFFFF;
            var create = unique == 0;
            if (create) unique = (uint)(Milliseconds & 0xFFFF);
            string path;
            for (var tries = 0; ; tries++)
            {
                if (unique == 0) unique = 1;
                path = $"{folder}\\{prefix}{unique:X}.tmp";
                if (!create || Files.Stat(FullPath(path)) == null || tries > 0xFFFF) break;
                unique = (unique + 1) & 0xFFFF;
            }
            if (create)
            {
                try { Files.Open(FullPath(path), FileMode.CreateNew, FileAccess.Write).Dispose(); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { process.LastError = ErrorAccessDenied; return 0; }
            }
            WriteText(c.Arg(3), path, wide);
            return unique;
        }

        /// <summary>
        /// The GetWindowsDirectory convention: the length without the NUL when
        /// it fits, else the size needed including the NUL.
        /// </summary>
        private uint CopyPathResult(string text, uint buffer, uint size, bool wide)
        {
            var needed = CopyOut(text, buffer, size, wide);
            return buffer != 0 && size >= needed ? needed - 1 : needed;
        }

        /// <summary>GetComputerName: size is in/out and the call fails when the buffer is short.</summary>
        private uint NameInto(string name, uint buffer, uint sizePtr, bool wide)
        {
            var size = sizePtr != 0 ? memory.Read32(sizePtr) : 0;
            if (size < name.Length + 1)
            {
                if (sizePtr != 0) memory.Write32(sizePtr, (uint)name.Length + 1);
                process.LastError = 111;   // ERROR_BUFFER_OVERFLOW
                return 0;
            }
            WriteText(buffer, name, wide);
            memory.Write32(sizePtr, (uint)name.Length);
            return 1;
        }

        private uint ExpandEnvironment(uint source, uint buffer, uint size, bool wide)
        {
            var text = ReadText(source, wide);
            var result = new StringBuilder();
            for (var n = 0; n < text.Length; n++)
            {
                var close = text[n] == '%' ? text.IndexOf('%', n + 1) : -1;
                if (close > n && environment.TryGetValue(text.Substring(n + 1, close - n - 1), out var value))
                {
                    result.Append(value);
                    n = close;
                }
                else result.Append(text[n]);
            }
            var expanded = result.ToString();
            var needed = (uint)(wide ? expanded.Length : Ansi.Encode(expanded).Length) + 1;
            if (buffer != 0 && size >= needed) WriteText(buffer, expanded, wide);
            return needed;
        }

        private uint DriveStrings(uint size, uint buffer, bool wide)
        {
            const string Drives = "C:\\";
            if (size < Drives.Length + 2) return (uint)Drives.Length + 2;
            WriteText(buffer, Drives, wide);
            WriteText(buffer + (uint)(Drives.Length + 1) * (wide ? 2u : 1u), "", wide);
            return (uint)Drives.Length + 1;
        }

        private const ulong DiskTotal = 1UL << 40, DiskFree = 256UL << 30;   // 1 TiB, 256 GiB free

        private uint DiskFreeSpace(GuestCall c)
        {
            // 4 KiB clusters; the counts saturate as Windows does past 2 GB-era limits.
            if (c.Arg(1) != 0) memory.Write32(c.Arg(1), 8);
            if (c.Arg(2) != 0) memory.Write32(c.Arg(2), 512);
            if (c.Arg(3) != 0) memory.Write32(c.Arg(3), (uint)Math.Min(DiskFree / 4096, uint.MaxValue));
            if (c.Arg(4) != 0) memory.Write32(c.Arg(4), (uint)Math.Min(DiskTotal / 4096, uint.MaxValue));
            return 1;
        }

        private uint DiskFreeSpaceEx(GuestCall c)
        {
            if (c.Arg(1) != 0) memory.Write64(c.Arg(1), DiskFree);
            if (c.Arg(2) != 0) memory.Write64(c.Arg(2), DiskTotal);
            if (c.Arg(3) != 0) memory.Write64(c.Arg(3), DiskFree);
            return 1;
        }

        private uint VolumeInformation(GuestCall c, bool wide)
        {
            if (c.Arg(1) != 0 && c.Arg(2) != 0) CopyTruncated("Games", c.Arg(1), c.Arg(2), wide);
            if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0x4E415456);   // "NATV"
            if (c.Arg(4) != 0) memory.Write32(c.Arg(4), 255);
            if (c.Arg(5) != 0) memory.Write32(c.Arg(5), 0x000700FF);   // case-preserving, Unicode, ACLs...
            if (c.Arg(6) != 0 && c.Arg(7) != 0) CopyTruncated("NTFS", c.Arg(6), c.Arg(7), wide);
            return 1;
        }

        // --- dates and times ---------------------------------------------------------------

        private uint FileTimeToDos(uint fileTime, uint dateOut, uint timeOut)
        {
            var t = DateTime.FromFileTimeUtc((long)memory.Read64(fileTime));
            if (t.Year < 1980 || t.Year > 2107) { process.LastError = ErrorInvalidParameter; return 0; }
            memory.Write16(dateOut, (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
            memory.Write16(timeOut, (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)));
            return 1;
        }

        private uint DosToFileTime(ushort date, ushort time, uint fileTime)
        {
            try
            {
                var t = new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 15), Math.Max(1, date & 31),
                    time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Utc);
                memory.Write64(fileTime, (ulong)t.ToFileTimeUtc());
                return 1;
            }
            catch (ArgumentOutOfRangeException) { process.LastError = ErrorInvalidParameter; return 0; }
        }

        private DateTime SystemTimeOrNow(uint p)
        {
            if (p == 0) return UtcNow;
            try
            {
                return new DateTime(memory.Read16(p), memory.Read16(p + 2), memory.Read16(p + 6),
                    memory.Read16(p + 8), memory.Read16(p + 10), memory.Read16(p + 12), memory.Read16(p + 14));
            }
            catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
        }

        /// <summary>
        /// GetDateFormat / GetTimeFormat and their Ex forms, in en-US. A
        /// picture ("dd MMM yyyy", "hh':'mm tt") is Windows' own syntax, which
        /// .NET's custom format strings share for these letters; every other
        /// character is passed through as a literal.
        /// </summary>
        private uint FormatDateTime(GuestCall c, bool date, bool wide)
        {
            // A: lcid, flags, time, format, buffer, size. Ex date: name, flags, time, format, buffer, size, calendar.
            uint flags = c.Arg(1), when = c.Arg(2), picture = c.Arg(3), buffer = c.Arg(4), size = c.Arg(5);
            var t = SystemTimeOrNow(when);
            if (t == DateTime.MinValue) { process.LastError = ErrorInvalidParameter; return 0; }

            string format;
            if (picture != 0) format = ToDotNetPicture(ReadText(picture, wide));
            else if (date) format = (flags & 2) != 0 ? "dddd, MMMM d, yyyy" : "M/d/yyyy";   // DATE_LONGDATE
            else
            {
                const uint NoMinutes = 1, NoSeconds = 2, NoMarker = 4, Force24 = 8;
                format = (flags & Force24) != 0 ? "H" : "h";
                if ((flags & NoMinutes) == 0) format += ":mm";
                if ((flags & (NoMinutes | NoSeconds)) == 0) format += ":ss";
                if ((flags & NoMarker) == 0 && (flags & Force24) == 0) format += " tt";
            }
            var text = t.ToString(format, CultureInfo.InvariantCulture);
            var needed = (uint)text.Length + 1;
            if (size == 0) return needed;
            if (size < needed) { process.LastError = ErrorInsufficientBuffer; return 0; }
            WriteText(buffer, text, wide);
            return needed;
        }

        private static string ToDotNetPicture(string picture)
        {
            var result = new StringBuilder();
            for (var n = 0; n < picture.Length; n++)
            {
                var ch = picture[n];
                if (ch == '\'')
                {
                    var close = picture.IndexOf('\'', n + 1);
                    if (close < 0) close = picture.Length;
                    var literal = picture.Substring(n + 1, close - n - 1);
                    result.Append(literal.Length == 0 ? "\\'" : "'" + literal + "'");
                    n = close;
                }
                else if ("dMyghHmst".IndexOf(ch) >= 0) result.Append(ch);
                else result.Append('\\').Append(ch);
            }
            return result.ToString();
        }

        // --- FormatMessage ---------------------------------------------------------------------

        private static readonly Dictionary<uint, string> SystemMessages = new Dictionary<uint, string>
        {
            [0] = "The operation completed successfully.",
            [1] = "Incorrect function.",
            [2] = "The system cannot find the file specified.",
            [3] = "The system cannot find the path specified.",
            [5] = "Access is denied.",
            [6] = "The handle is invalid.",
            [8] = "Not enough memory resources are available to process this command.",
            [18] = "There are no more files.",
            [32] = "The process cannot access the file because it is being used by another process.",
            [50] = "The request is not supported.",
            [80] = "The file exists.",
            [87] = "The parameter is incorrect.",
            [122] = "The data area passed to a system call is too small.",
            [126] = "The specified module could not be found.",
            [127] = "The specified procedure could not be found.",
            [183] = "Cannot create a file when that file already exists.",
            [1400] = "Invalid window handle.",
        };

        private uint FormatMessage(GuestCall c, bool wide)
        {
            const uint AllocateBuffer = 0x100, IgnoreInserts = 0x200, FromString = 0x400;
            const uint FromHModule = 0x800, FromSystem = 0x1000, ArgumentArray = 0x2000;
            uint flags = c.Arg(0), source = c.Arg(1), id = c.Arg(2), buffer = c.Arg(4), size = c.Arg(5), args = c.Arg(6);

            string template;
            if ((flags & FromString) != 0) template = ReadText(source, wide);
            else if ((flags & FromSystem) != 0 || (flags & FromHModule) != 0)
            {
                if (!SystemMessages.TryGetValue(id, out template))
                {
                    process.LastError = 317;   // ERROR_MR_MID_NOT_FOUND
                    return 0;
                }
                template += "\r\n";
            }
            else { process.LastError = ErrorInvalidParameter; return 0; }

            var text = (flags & IgnoreInserts) != 0 ? template : ExpandInserts(template, args, (flags & ArgumentArray) != 0, wide);
            var maxWidth = flags & 0xFF;
            if (maxWidth == 0xFF) text = text.Replace("\r\n", " ");

            if ((flags & AllocateBuffer) != 0)
            {
                var bytes = (uint)(text.Length + 1) * (wide ? 2u : 1u);
                var block = heap.Alloc(Math.Max(bytes, size * (wide ? 2u : 1u)));
                WriteText(block, text, wide);
                memory.Write32(buffer, block);
                return (uint)text.Length;
            }
            if (size < text.Length + 1) { process.LastError = ErrorInsufficientBuffer; return 0; }
            WriteText(buffer, text, wide);
            return (uint)text.Length;
        }

        /// <summary>%1..%99 (with an optional !printf-spec!), %n, %%, %0, %space, %., %!.</summary>
        private string ExpandInserts(string template, uint args, bool array, bool wide)
        {
            var result = new StringBuilder();
            for (var n = 0; n < template.Length; n++)
            {
                var ch = template[n];
                if (ch != '%' || n + 1 >= template.Length) { result.Append(ch); continue; }
                var next = template[++n];
                if (next == 'n') { result.Append("\r\n"); continue; }
                if (next == '0') break;
                if (next < '1' || next > '9') { result.Append(next); continue; }

                var index = next - '0';
                if (n + 1 < template.Length && char.IsDigit(template[n + 1])) index = index * 10 + (template[++n] - '0');
                var spec = "s";
                if (n + 1 < template.Length && template[n + 1] == '!')
                {
                    var close = template.IndexOf('!', n + 2);
                    if (close > 0) { spec = template.Substring(n + 2, close - n - 2); n = close; }
                }
                // Both forms read the arguments as an array of DWORDs: a
                // va_list on x86 is a pointer to the first one.
                var at = array ? args : memory.Read32(args);
                if (args == 0) { result.Append('%').Append(index); continue; }
                var value = memory.Read32(at + (uint)(index - 1) * 4);
                switch (spec.TrimStart('-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.').ToLowerInvariant())
                {
                    case "d": case "i": case "ld": case "li": result.Append(((int)value).ToString(CultureInfo.InvariantCulture)); break;
                    case "u": case "lu": result.Append(value.ToString(CultureInfo.InvariantCulture)); break;
                    case "x": case "lx": result.Append(value.ToString("x", CultureInfo.InvariantCulture)); break;
                    case "c": result.Append((char)value); break;
                    case "ws": case "ls": case "s" when wide: result.Append(ReadText(value, true)); break;
                    default: result.Append(ReadText(value, spec == "S" ? !wide : wide)); break;
                }
            }
            return result.ToString();
        }

        // --- character types and locales ----------------------------------------------------------

        /// <summary>CT_CTYPE1 bits for a character, as GetStringType and the C runtime's tables report them.</summary>
        internal static ushort CharType1(char ch)
        {
            ushort t = 0x200;   // C1_DEFINED
            if (char.IsUpper(ch)) t |= 0x001;
            if (char.IsLower(ch)) t |= 0x002;
            if (ch >= '0' && ch <= '9') t |= 0x004;
            if (ch == ' ' || (ch >= '\t' && ch <= '\r') || ch == '\u00A0' || ch == '\u3000') t |= 0x008;
            if (char.IsPunctuation(ch) || char.IsSymbol(ch)) t |= 0x010;
            if (char.IsControl(ch)) t |= 0x020;
            if (ch == ' ' || ch == '\t' || ch == '\u00A0') t |= 0x040;
            if (HexDigit(ch) >= 0 && ch < 0x80) t |= 0x080;
            if (char.IsLetter(ch)) t |= 0x100;
            return t;
        }

        private uint StringTypeA(uint type, uint source, uint length, uint output)
        {
            if (type != 1) { process.LastError = ErrorInvalidParameter; return 0; }   // CT_CTYPE1 only
            var count = (int)length == -1 ? (uint)ReadAnsiBytes(source).Length + 1 : length;
            var text = Ansi.Decode(memory.ReadBytes(source, (int)count));
            for (var n = 0; n < text.Length; n++) memory.Write16(output + (uint)n * 2, CharType1(text[n]));
            return 1;
        }

        private void EnumLocale(uint callback, bool wide)
        {
            var text = heap.Alloc(32);
            WriteText(text, "00000409", wide);
            CallGuest(callback, text);
            heap.Free(text);
        }

        // --- processes and timers ----------------------------------------------------------------

        private uint ProcessTimes(uint creation, uint exit, uint kernel, uint user)
        {
            var now = (ulong)UtcNow.ToFileTimeUtc();
            var busy = (ulong)Milliseconds * 10_000;
            if (creation != 0) memory.Write64(creation, now - busy);
            if (exit != 0) memory.Write64(exit, 0);
            if (kernel != 0) memory.Write64(kernel, busy / 10);
            if (user != 0) memory.Write64(user, busy / 2);
            return 1;
        }

        private uint CreateTimer(bool manualReset)
        {
            var handle = NewHandle();
            waitables[handle] = new GuestTimer { Now = () => Milliseconds, ManualReset = manualReset };
            return handle;
        }

        /// <summary>SetWaitableTimer: a negative due time is relative (100 ns units), a positive one an absolute FILETIME.</summary>
        private uint SetTimer(uint handle, uint dueTime, int periodMs)
        {
            if (!waitables.TryGetValue(handle, out var w) || !(w is GuestTimer timer)) { process.LastError = ErrorInvalidHandle; return 0; }
            var due = (long)memory.Read64(dueTime);
            long delayMs = due < 0 ? -due / 10_000 : Math.Max(0, (due - UtcNow.ToFileTimeUtc()) / 10_000);
            timer.Signaled = false;
            timer.Period = Math.Max(0, periodMs);
            timer.Due = Milliseconds + Math.Max(1, delayMs);
            return 1;
        }
    }
}
