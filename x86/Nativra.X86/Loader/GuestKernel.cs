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
    public sealed class GuestKernel
    {
        private const uint ProcessHeapHandle = 0x00A00000;
        private const uint HeapZeroMemory = 0x00000008;
        private const uint TlsOutOfIndexes = 0xFFFFFFFF;
        private const uint PseudoCurrentProcess = 0xFFFFFFFF;
        private const uint PseudoCurrentThread = 0xFFFFFFFE;

        private readonly GuestProcess process;
        private readonly GuestMemory memory;
        private readonly GuestHeap heap;

        private readonly Dictionary<string, uint> modules =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly bool[] tlsUsed = new bool[64];

        private uint commandLineAnsi;
        private uint commandLineWide;
        private uint lastError;
        private uint virtualCursor = 0x20000000;   // where anonymous VirtualAlloc lands
        private long ticks = 1;

        public GuestHeap Heap => heap;

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
        }

        /// <summary>Registers every handler on the process's import table.</summary>
        public void Install()
        {
            var i = process.Imports;
            const string k = "kernel32.dll";

            i.Register(k, "GetLastError", CallConv.Stdcall, 0, c => lastError);
            i.Register(k, "SetLastError", CallConv.Stdcall, 1, c => { lastError = c.Arg(0); return 0; });
            i.Register(k, "GetCurrentThreadId", CallConv.Stdcall, 0, c => 0x1000);
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

            i.Register(k, "GetTickCount", CallConv.Stdcall, 0, c => (uint)(ticks++ & 0xFFFFFFFF));
            i.Register(k, "GetTickCount64", CallConv.Stdcall, 0, c => (ulong)ticks++);
            i.Register(k, "QueryPerformanceCounter", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), (ulong)(ticks++));
                return 1;
            });
            i.Register(k, "QueryPerformanceFrequency", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), 10_000_000);
                return 1;
            });
            i.Register(k, "Sleep", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "GetVersion", CallConv.Stdcall, 0, c => (26100u << 16) | 0x000A);
            i.Register(k, "IsProcessorFeaturePresent", CallConv.Stdcall, 1, c => 1);
            i.Register(k, "GetSystemInfo", CallConv.Stdcall, 1, c => { FillSystemInfo(c.Arg(0)); return 0; });
            i.Register(k, "GetSystemTimeAsFileTime", CallConv.Stdcall, 1, c =>
            {
                memory.Write64(c.Arg(0), (ulong)(ticks++) * 10_000);
                return 0;
            });

            // Single-threaded guest: the lock calls are structurally no-ops.
            i.Register(k, "InitializeCriticalSection", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "InitializeCriticalSectionAndSpinCount", CallConv.Stdcall, 2, c => 1);
            i.Register(k, "InitializeCriticalSectionEx", CallConv.Stdcall, 3, c => 1);
            i.Register(k, "EnterCriticalSection", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "LeaveCriticalSection", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "DeleteCriticalSection", CallConv.Stdcall, 1, c => 0);

            i.Register(k, "OutputDebugStringA", CallConv.Stdcall, 1, c => 0);
            i.Register(k, "OutputDebugStringW", CallConv.Stdcall, 1, c => 0);
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

        private uint ModuleHandle(uint namePtr, bool wide)
        {
            if (namePtr == 0)
                return process.Images.Count > 0 ? process.Images[0].BaseAddress : 0;
            var name = wide ? memory.ReadUnicode(namePtr) : memory.ReadAnsi(namePtr);
            return modules.TryGetValue(Trim(name), out var baseAddress) ? baseAddress : 0;
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

        private static string Trim(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name.Substring(slash + 1);
            return name.ToLowerInvariant();
        }
    }
}
