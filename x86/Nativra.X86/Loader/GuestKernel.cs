using System;
using System.Collections.Generic;
using System.Text;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    /// <summary>
    /// A baseline, platform-independent Win32/CRT surface for a guest process:
    /// the kernel32 calls a program reaches for before it does anything of its
    /// own — heap and virtual memory, thread-local storage, the last-error slot,
    /// module handles, the command line, timing, and the critical-section calls
    /// that a single-threaded guest can safely treat as no-ops.
    ///
    /// Everything here works purely against <see cref="GuestMemory"/>, so it is
    /// unit-tested on the runner with no operating system behind it. The app's
    /// UWP layer adds the calls that must reach the real system (file and device
    /// I/O, real module loading, graphics) on top of this same import table.
    /// </summary>
    public sealed partial class GuestKernel
    {
        private const uint ProcessHeapHandle = 0x00A00000;
        private const uint HeapZeroMemory = 0x00000008;
        private const uint TlsOutOfIndexes = 0xFFFFFFFF;
        private const uint PseudoCurrentProcess = 0xFFFFFFFF;
        private const uint PseudoCurrentThread = 0xFFFFFFFE;
        private const uint ErrorModNotFound = 126;
        private const uint ErrorProcNotFound = 127;

        // Stand-in HMODULEs for system DLLs that are served by host handlers
        // rather than mapped into the guest. Unmapped on purpose: they are
        // handles, not something the guest should read through.
        private const uint FakeModuleBase = 0x7E800000;
        private const uint FakeModuleStride = 0x00010000;

        // Loaded in every Windows process whether or not the program imports them.
        private static readonly string[] AlwaysLoaded = { "kernel32.dll", "kernelbase.dll", "ntdll.dll" };

        private readonly GuestProcess process;
        private readonly GuestMemory memory;
        private readonly GuestHeap heap;

        private readonly Dictionary<string, uint> modules =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, string> fakeHandles = new Dictionary<uint, string>();
        private readonly Dictionary<string, uint> fakeByName =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> probedAbsent = new List<string>();
        private readonly bool[] tlsUsed = new bool[64];

        private uint commandLineAnsi;
        private uint commandLineWide;
        private uint virtualCursor = 0x20000000;   // where anonymous VirtualAlloc lands
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private long Milliseconds => DeterministicTime ? 60_000 : clock.ElapsedMilliseconds + 60_000;   // never 0, as on a PC that has been up a while
        private long Ticks => DeterministicTime ? 600_000 : clock.ElapsedTicks;
        private DateTime UtcNow => DeterministicTime ? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) : DateTime.UtcNow;

        /// <summary>
        /// Every clock the guest reads stands still (tests that run one
        /// program twice and compare, such as JIT against interpreter).
        /// </summary>
        public bool DeterministicTime { get; set; }

        public GuestHeap Heap => heap;

        /// <summary>The program's full path, as GetModuleFileName reports it.</summary>
        public string ExePath { get; set; } = "C:\\game\\game.exe";

        /// <summary>
        /// Functions the guest looked up with GetProcAddress that have no host
        /// implementation; it was told they do not exist, and these are the
        /// first candidates when a game takes a fallback path it should not.
        /// </summary>
        public IReadOnlyList<string> ProbedAbsent => probedAbsent;

        public GuestKernel(GuestProcess process, uint heapBase = 0x30000000, uint heapSize = 0x10000000)
        {
            this.process = process;
            memory = process.Memory;
            heap = new GuestHeap(memory, heapBase, heapSize);
        }

        /// <summary>Records a module base so GetModuleHandle can answer for it by name.</summary>
        public void RegisterModule(string name, uint baseAddress)
        {
            if (!string.IsNullOrEmpty(name)) modules[Trim(name)] = baseAddress;
        }

        /// <summary>Sets the command line both programs' GetCommandLine variants return.</summary>
        public void SetCommandLine(string commandLine)
        {
            commandLine = commandLine ?? "";
            commandLineAnsi = heap.Alloc((uint)commandLine.Length + 1);
            memory.WriteAnsi(commandLineAnsi, commandLine);
            commandLineWide = heap.Alloc((uint)(commandLine.Length + 1) * 2);
            memory.WriteUnicode(commandLineWide, commandLine);

            // The same strings in the process parameters, as UNICODE_STRINGs.
            var image = heap.Alloc((uint)(ExePath.Length + 1) * 2);
            memory.WriteUnicode(image, ExePath);
            WriteUnicodeString(process.ProcessParameters + 0x38, image, ExePath.Length);          // ImagePathName
            WriteUnicodeString(process.ProcessParameters + 0x40, commandLineWide, commandLine.Length); // CommandLine
        }

        private void WriteUnicodeString(uint at, uint buffer, int chars)
        {
            memory.Write16(at, (ushort)(chars * 2));
            memory.Write16(at + 2, (ushort)(chars * 2 + 2));
            memory.Write32(at + 4, buffer);
        }

        /// <summary>Registers every handler on the process's import table.</summary>
        public void Install()
        {
            var i = process.Imports;
            const string k = "kernel32.dll";

            i.Register(k, "GetLastError", CallConv.Stdcall, 0, c => process.LastError);
            i.Register(k, "SetLastError", CallConv.Stdcall, 1, c => { process.LastError = c.Arg(0); return 0; });

            i.Register(k, "ExitProcess", CallConv.Stdcall, 1, c => throw new GuestExitException(c.Arg(0)));
            i.Register(k, "TerminateProcess", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(0) == PseudoCurrentProcess) throw new GuestExitException(c.Arg(1));
                return 1;
            });

            i.Register(k, "LoadLibraryA", CallConv.Stdcall, 1, c => LoadLibrary(c.Arg(0), false));
            i.Register(k, "LoadLibraryW", CallConv.Stdcall, 1, c => LoadLibrary(c.Arg(0), true));
            i.Register(k, "LoadLibraryExA", CallConv.Stdcall, 3, c => LoadLibrary(c.Arg(0), false));
            i.Register(k, "LoadLibraryExW", CallConv.Stdcall, 3, c => LoadLibrary(c.Arg(0), true));
            i.Register(k, "FreeLibrary", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "GetProcAddress", CallConv.Stdcall, 2, c => GetProcAddress(c.Arg(0), c.Arg(1)));
            i.Register(k, "GetModuleFileNameA", CallConv.Stdcall, 3, c =>
                ModuleFileName(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(k, "GetModuleFileNameW", CallConv.Stdcall, 3, c =>
                ModuleFileName(c.Arg(0), c.Arg(1), c.Arg(2), true));
            i.Register(k, "GetCurrentProcessId", CallConv.Stdcall, 0, c => 0x1234);
            i.Register(k, "GetCurrentProcess", CallConv.Stdcall, 0, c => PseudoCurrentProcess);
            i.Register(k, "GetCurrentThread", CallConv.Stdcall, 0, c => PseudoCurrentThread);

            i.Register(k, "GetProcessHeap", CallConv.Stdcall, 0, c => ProcessHeapHandle);
            i.Register(k, "HeapAlloc", CallConv.Stdcall, 3, c =>
                heap.Alloc(c.Arg(2), (c.Arg(1) & HeapZeroMemory) != 0));
            i.Register(k, "HeapFree", CallConv.Stdcall, 3, c => heap.Free(c.Arg(2)) ? 1u : 0u);
            i.Register(k, "HeapReAlloc", CallConv.Stdcall, 4, c => heap.ReAlloc(c.Arg(2), c.Arg(3)));
            i.Register(k, "HeapSize", CallConv.Stdcall, 3, c => heap.SizeOf(c.Arg(2)));
            i.Register(k, "HeapDestroy", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "HeapCreate", CallConv.Stdcall, 3, c => ProcessHeapHandle);

            i.Register(k, "VirtualAlloc", CallConv.Stdcall, 4, c => VirtualAlloc(c.Arg(0), c.Arg(1)));
            i.Register(k, "VirtualFree", CallConv.Stdcall, 3, c => 1);
            i.Register(k, "VirtualProtect", CallConv.Stdcall, 4, c =>
            {
                if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0x04 /* PAGE_READWRITE */);
                return 1;
            });
            i.Register(k, "VirtualQuery", CallConv.Stdcall, 3, c => 0);

            i.Register(k, "TlsAlloc", CallConv.Stdcall, 0, c => TlsAlloc());
            i.Register(k, "TlsFree", CallConv.Stdcall, 1, c => TlsFree(c.Arg(0)));
            i.Register(k, "TlsGetValue", CallConv.Stdcall, 1, c => process.TlsGetValue(c.Arg(0)));
            i.Register(k, "TlsSetValue", CallConv.Stdcall, 2, c =>
            {
                process.TlsSetValue(c.Arg(0), c.Arg(1));
                return 1;
            });

            i.Register(k, "GetModuleHandleA", CallConv.Stdcall, 1, c => ModuleHandle(c.Arg(0), false));
            i.Register(k, "GetModuleHandleW", CallConv.Stdcall, 1, c => ModuleHandle(c.Arg(0), true));
            i.Register(k, "GetCommandLineA", CallConv.Stdcall, 0, c => commandLineAnsi);
            i.Register(k, "GetCommandLineW", CallConv.Stdcall, 0, c => commandLineWide);

            // Real time: games pace frames and time out waits on these.
            i.Register(k, "GetTickCount", CallConv.Stdcall, 0, c => (uint)Milliseconds);
            i.Register(k, "GetTickCount64", CallConv.Stdcall, 0, c => (ulong)Milliseconds);
            i.Register(k, "QueryPerformanceCounter", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), (ulong)Ticks);
                return 1;
            });
            i.Register(k, "QueryPerformanceFrequency", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), (ulong)System.Diagnostics.Stopwatch.Frequency);
                return 1;
            });
            i.Register(k, "GetVersion", CallConv.Stdcall, 0, c => (26100u << 16) | 0x000A);
            i.Register(k, "IsProcessorFeaturePresent", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "GetSystemInfo", CallConv.Stdcall, 1, c => { FillSystemInfo(c.Arg(0)); return 0; });
            i.Register(k, "GetSystemTimeAsFileTime", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), (ulong)UtcNow.ToFileTimeUtc());
                return 0;
            });


            InstallRuntime(i);
            InstallFiles(i);
            InstallSeh(i);
            InstallThreads(i);
            InstallUser32(i);
        }

        // --- handler bodies -----------------------------------------------

        private uint VirtualAlloc(uint address, uint size)
        {
            if (size == 0) return 0;
            if (address == 0)
            {
                var at = memory.FindFree(size, virtualCursor);
                if (at == 0) return 0;
                memory.Map(at, size);
                virtualCursor = at + RoundPage(size);
                return at;
            }
            memory.Map(address, size);
            return address;
        }

        private uint TlsAlloc()
        {
            for (uint idx = 0; idx < tlsUsed.Length; idx++)
                if (!tlsUsed[idx]) { tlsUsed[idx] = true; process.TlsSetValue(idx, 0); return idx; }
            return TlsOutOfIndexes;
        }

        private uint TlsFree(uint index)
        {
            if (index >= tlsUsed.Length || !tlsUsed[index]) return 0;
            tlsUsed[index] = false;
            return 1;
        }

        private uint MainBase =>
            process.MainImage != null ? process.MainImage.BaseAddress
            : process.Images.Count > 0 ? process.Images[0].BaseAddress : 0;

        /// <summary>
        /// GetModuleHandle: a guest image answers with its base; a system DLL the
        /// program links against (or that every process has) answers with a
        /// stand-in handle; anything else is not loaded.
        /// </summary>
        private uint ModuleHandle(uint namePtr, bool wide)
        {
            if (namePtr == 0) return MainBase;
            var name = Trim(wide ? memory.ReadUnicode(namePtr) : memory.ReadAnsi(namePtr));

            var image = process.FindModule(name);
            if (image != null) return image.BaseAddress;
            if (modules.TryGetValue(name, out var registered)) return registered;
            if (IsAlwaysLoaded(name) || process.Imports.KnowsModule(name)) return FakeHandle(name);

            process.LastError = ErrorModNotFound;
            return 0;
        }

        /// <summary>
        /// LoadLibrary: maps a DLL the game carries into the guest (running its
        /// DllMain), or hands back a stand-in handle for a system DLL, whose
        /// functions then resolve to host handlers through GetProcAddress.
        /// </summary>
        private uint LoadLibrary(uint namePtr, bool wide)
        {
            if (namePtr == 0) { process.LastError = ErrorModNotFound; return 0; }
            var name = Trim(wide ? memory.ReadUnicode(namePtr) : memory.ReadAnsi(namePtr));

            var alreadyMapped = process.FindModule(name) != null;
            var image = process.LoadModule(name);
            if (image != null)
            {
                if (!alreadyMapped)
                {
                    // A nested run on the same stack, as the real loader does:
                    // LoadLibrary returns only after DllMain(PROCESS_ATTACH).
                    var saved = SaveRegisters();
                    var result = process.AttachModule(image);
                    RestoreRegisters(saved);
                    if (!result.Ok) return 0;
                }
                return image.BaseAddress;
            }
            if (modules.TryGetValue(name, out var registered)) return registered;
            return FakeHandle(name);
        }

        /// <summary>GetProcAddress by name or ordinal (a "name" below 0x10000 is an ordinal).</summary>
        private uint GetProcAddress(uint module, uint nameOrOrdinal)
        {
            var byOrdinal = nameOrOrdinal < 0x10000;
            var function = byOrdinal ? null : memory.ReadAnsi(nameOrOrdinal);
            var ordinal = byOrdinal ? (int)nameOrOrdinal : -1;

            foreach (var image in process.Images)
            {
                if (image.BaseAddress != module) continue;
                var va = byOrdinal ? image.ExportByOrdinal(ordinal) : image.Export(function);
                if (va == 0) process.LastError = ErrorProcNotFound;
                return va;
            }

            if (fakeHandles.TryGetValue(module, out var moduleName))
            {
                // Only hand out functions we can actually serve: a program that
                // probes for an optional API takes its fallback when told the
                // function does not exist, which beats trapping on it later.
                if (!byOrdinal && process.Imports.HasHandler(moduleName, function))
                    return process.Imports.Bind(moduleName, function, -1);
                probedAbsent.Add(byOrdinal ? $"{moduleName}#{ordinal}" : $"{moduleName}!{function}");
            }
            process.LastError = ErrorProcNotFound;
            return 0;
        }

        /// <summary>GetModuleFileName: the program's path, or its folder plus a DLL's name.</summary>
        private uint ModuleFileName(uint module, uint buffer, uint size, bool wide)
        {
            string path;
            if (module == 0 || module == MainBase) path = ExePath;
            else
            {
                path = null;
                foreach (var image in process.Images)
                    if (image.BaseAddress == module) { path = Folder(ExePath) + image.Name; break; }
                if (path == null && fakeHandles.TryGetValue(module, out var system))
                    path = "C:\\Windows\\System32\\" + system;
                if (path == null) { process.LastError = ErrorModNotFound; return 0; }
            }
            if (buffer == 0 || size == 0) return 0;

            // Truncate to the buffer, always NUL-terminated, as Windows does.
            var length = (uint)Math.Min(path.Length, (int)size - 1);
            var text = path.Substring(0, (int)length);
            if (wide) memory.WriteUnicode(buffer, text); else memory.WriteAnsi(buffer, text);
            return length;
        }

        private uint FakeHandle(string name)
        {
            if (fakeByName.TryGetValue(name, out var existing)) return existing;
            var handle = FakeModuleBase + (uint)fakeByName.Count * FakeModuleStride;
            fakeByName[name] = handle;
            fakeHandles[handle] = name;
            return handle;
        }

        private static bool IsAlwaysLoaded(string name)
        {
            foreach (var n in AlwaysLoaded) if (n == name) return true;
            return false;
        }

        private static string Folder(string path)
        {
            var slash = path.LastIndexOf('\\');
            return slash >= 0 ? path.Substring(0, slash + 1) : "";
        }

        private uint[] SaveRegisters()
        {
            var saved = new uint[9];
            Array.Copy(process.Cpu.R, saved, 8);
            saved[8] = process.Cpu.Eip;
            return saved;
        }

        private void RestoreRegisters(uint[] saved)
        {
            Array.Copy(saved, process.Cpu.R, 8);
            process.Cpu.Eip = saved[8];
        }

        private void FillSystemInfo(uint p)
        {
            if (p == 0) return;
            memory.Write16(p + 0, 0);          // PROCESSOR_ARCHITECTURE_INTEL
            memory.Write32(p + 4, GuestMemory.PageSize);
            memory.Write32(p + 8, 0x00010000); // min application address
            memory.Write32(p + 12, 0x7FFEFFFF); // max application address
            memory.Write32(p + 16, 0xF);       // active processor mask
            memory.Write32(p + 20, 4);         // number of processors
            memory.Write32(p + 24, 586);       // processor type
            memory.Write32(p + 28, 0x00010000); // allocation granularity
            memory.Write16(p + 32, 6);         // processor level
            memory.Write16(p + 34, 0);         // processor revision
        }

        private static uint RoundPage(uint value) =>
            (value + GuestMemory.PageSize - 1) / GuestMemory.PageSize * GuestMemory.PageSize;

        /// <summary>A module name as Windows matches it: file name only, lower case, ".dll" implied.</summary>
        private static string Trim(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name.Substring(slash + 1);
            name = name.ToLowerInvariant();
            return GuestImports.Canonical(name.IndexOf('.') < 0 ? name + ".dll" : name);
        }
    }
}
