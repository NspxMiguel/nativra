using System.Collections.Generic;

namespace Nativra.X86.Loader
{
    // What the processor looks like, and the process's own threads as a
    // snapshot: the queries engines make at start-up to size their job
    // systems and to calibrate timers. The answers agree with GetSystemInfo
    // (four processors, mask 0xF): one logical processor per core, one
    // package, one NUMA node.
    public sealed partial class GuestKernel
    {
        private const uint CurrentProcessId = GuestProcess.ProcessId;
        private const uint ProcessorCount = 4;
        private const uint ProcessorMhz = 3800;   // the Series X CPU's clock
        private const uint ThreadSnapshotFlag = 0x4;   // TH32CS_SNAPTHREAD

        private readonly Dictionary<uint, List<uint>> threadSnapshots = new Dictionary<uint, List<uint>>();
        private readonly Dictionary<uint, int> snapshotCursor = new Dictionary<uint, int>();

        private void InstallSystemInfo(GuestImports i)
        {
            const string k = "kernel32.dll";
            i.Register(k, "GetLogicalProcessorInformation", CallConv.Stdcall, 2, c =>
                LogicalProcessorInformation(c.Arg(0), c.Arg(1)));
            i.Register("powrprof.dll", "CallNtPowerInformation", CallConv.Stdcall, 5, c =>
                PowerInformation(c.Arg(0), c.Arg(3), c.Arg(4)));

            i.Register(k, "CreateToolhelp32Snapshot", CallConv.Stdcall, 2, c => CreateSnapshot(c.Arg(0)));
            i.Register(k, "Thread32First", CallConv.Stdcall, 2, c => NextThreadEntry(c.Arg(0), c.Arg(1), true));
            i.Register(k, "Thread32Next", CallConv.Stdcall, 2, c => NextThreadEntry(c.Arg(0), c.Arg(1), false));
            i.Register(k, "OpenThread", CallConv.Stdcall, 3, c => OpenThread(c.Arg(2)));

            // No child processes here, so no pipe to one either.
            i.Register(k, "CreatePipe", CallConv.Stdcall, 4, c => { process.LastError = ErrorNotSupported; return 0; });
        }

        // SYSTEM_LOGICAL_PROCESSOR_INFORMATION (x86): ProcessorMask +0,
        // Relationship +4, a 16-byte union at +8; 24 bytes each.
        private uint LogicalProcessorInformation(uint buffer, uint lengthPointer)
        {
            const uint EntrySize = 24;
            const uint Core = 0, NumaNode = 1, Cache = 2, Package = 3;
            const uint Unified = 0, Instruction = 1, Data = 2;
            var entries = new List<uint[]>();   // mask, relationship, then union words
            for (uint n = 0; n < ProcessorCount; n++)
            {
                var mask = 1u << (int)n;
                entries.Add(new uint[] { mask, Core, 0u });
                entries.Add(new uint[] { mask, Cache, CacheWord(1, 8, 64), 32 * 1024, Data });
                entries.Add(new uint[] { mask, Cache, CacheWord(1, 8, 64), 32 * 1024, Instruction });
                entries.Add(new uint[] { mask, Cache, CacheWord(2, 8, 64), 512 * 1024, Unified });
            }
            var all = (1u << (int)ProcessorCount) - 1;
            entries.Add(new uint[] { all, Cache, CacheWord(3, 16, 64), 8 * 1024 * 1024, Unified });
            entries.Add(new uint[] { all, Package, 0u });
            entries.Add(new uint[] { all, NumaNode, 0u });

            if (lengthPointer == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            var needed = (uint)entries.Count * EntrySize;
            var given = memory.Read32(lengthPointer);
            memory.Write32(lengthPointer, needed);
            if (buffer == 0 || given < needed) { process.LastError = ErrorInsufficientBuffer; return 0; }

            for (var e = 0; e < entries.Count; e++)
            {
                var at = buffer + (uint)e * EntrySize;
                memory.WriteBytes(at, new byte[EntrySize]);
                for (var w = 0; w < entries[e].Length; w++) memory.Write32(at + (uint)w * 4, entries[e][w]);
            }
            return 1;
        }

        /// <summary>CACHE_DESCRIPTOR's first word: Level, Associativity (bytes), LineSize (word).</summary>
        private static uint CacheWord(uint level, uint associativity, uint lineSize) =>
            level | (associativity << 8) | (lineSize << 16);

        // CallNtPowerInformation(ProcessorInformation): one
        // PROCESSOR_POWER_INFORMATION per processor, six ULONGs each.
        private uint PowerInformation(uint level, uint output, uint outputLength)
        {
            const uint ProcessorInformation = 11;
            const uint StatusBufferTooSmall = 0xC0000023, StatusNotImplemented = 0xC0000002;
            if (level != ProcessorInformation) return StatusNotImplemented;
            if (output == 0 || outputLength < ProcessorCount * 24) return StatusBufferTooSmall;
            for (uint n = 0; n < ProcessorCount; n++)
            {
                var at = output + n * 24;
                memory.Write32(at + 0, n);
                memory.Write32(at + 4, ProcessorMhz);    // MaxMhz
                memory.Write32(at + 8, ProcessorMhz);    // CurrentMhz
                memory.Write32(at + 12, ProcessorMhz);   // MhzLimit
                memory.Write32(at + 16, 0);              // MaxIdleState
                memory.Write32(at + 20, 0);              // CurrentIdleState
            }
            return 0;
        }

        private uint CreateSnapshot(uint flags)
        {
            const uint InvalidHandleValue = 0xFFFFFFFF;
            // Modules and processes are not listed; asked for only those, say so.
            if ((flags & ThreadSnapshotFlag) == 0) { process.LastError = ErrorNotSupported; return InvalidHandleValue; }
            var handle = NewHandle();
            var ids = new List<uint>();
            foreach (var t in process.Threads) if (!t.IsDone) ids.Add(t.Id);
            threadSnapshots[handle] = ids;
            snapshotCursor[handle] = 0;
            return handle;
        }

        // THREADENTRY32: dwSize +0, cntUsage +4, th32ThreadID +8,
        // th32OwnerProcessID +0xC, tpBasePri +0x10, tpDeltaPri +0x14, dwFlags +0x18.
        private uint NextThreadEntry(uint snapshot, uint entry, bool first)
        {
            if (!threadSnapshots.TryGetValue(snapshot, out var ids)) { process.LastError = ErrorInvalidHandle; return 0; }
            if (entry == 0 || memory.Read32(entry) < 28) { process.LastError = ErrorInvalidParameter; return 0; }
            var at = first ? 0 : snapshotCursor[snapshot];
            if (at >= ids.Count) { process.LastError = ErrorNoMoreFiles; return 0; }
            memory.Write32(entry + 0x04, 0);
            memory.Write32(entry + 0x08, ids[at]);
            memory.Write32(entry + 0x0C, CurrentProcessId);
            memory.Write32(entry + 0x10, 8);   // THREAD_PRIORITY_NORMAL's base
            memory.Write32(entry + 0x14, 0);
            memory.Write32(entry + 0x18, 0);
            snapshotCursor[snapshot] = at + 1;
            return 1;
        }

        private uint OpenThread(uint threadId)
        {
            foreach (var t in process.Threads)
            {
                if (t.Id != threadId || t.IsDone) continue;
                var handle = NewHandle();
                waitables[handle] = new ThreadObject { Thread = t };
                return handle;
            }
            process.LastError = ErrorInvalidParameter;
            return 0;
        }
    }
}
