using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Nativra.X86.Jit
{
    /// <summary>Turns host faults in translated guest code into guest access violations.</summary>
    internal static class JitFaults
    {
        private const uint StatusAccessViolation = 0xC0000005;
        private const int ContextRip = 0xF8;
        private const int RecordInformation = 0x20;
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;
        private const ulong GuestSize = 0x100000000UL;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int VectoredHandler(IntPtr pointers);

        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private sealed class Execution
        {
            public ulong GuestBase, Start, End, Stub;
            public uint RunningEip;
            public int[] HostOffsets;
            public uint[] GuestEips;
            public bool Pending;
            public ulong Rip, Address;
            public uint Access, FaultEip;
        }

        private struct Page
        {
            public ulong Start, End;
        }

        private static readonly object gate = new object();
        private static readonly JitFaultRanges ranges = new JitFaultRanges();
        private static readonly List<Page> pages = new List<Page>();
        private static readonly Dictionary<ulong, int> guestBases = new Dictionary<ulong, int>();
        private static readonly Dictionary<uint, Execution> executions = new Dictionary<uint, Execution>();
        private static VectoredHandler handler;
        private static bool attempted;
        private static string reportPath;
        private static string probePath;

        public static bool Installed { get; private set; }

        public static void ConfigureReport(string path, string probe)
        {
            reportPath = path;
            probePath = probe;
        }

        public static void Install()
        {
            lock (gate)
            {
                if (attempted) return;
                attempted = true;
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                    RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
                try
                {
                    handler = OnException;
                    Installed = AddVectoredExceptionHandler(1, Marshal.GetFunctionPointerForDelegate(handler)) != IntPtr.Zero;
                }
                catch (Exception) { Installed = false; }
            }
        }

        public static void RegisterGuestBase(IntPtr memoryBase)
        {
            if (!Installed) return;
            var address = (ulong)memoryBase.ToInt64();
            lock (gate)
            {
                guestBases.TryGetValue(address, out var count);
                guestBases[address] = count + 1;
            }
        }

        public static void UnregisterGuestBase(IntPtr memoryBase)
        {
            if (!Installed) return;
            var address = (ulong)memoryBase.ToInt64();
            lock (gate)
            {
                if (!guestBases.TryGetValue(address, out var count)) return;
                if (count == 1) guestBases.Remove(address);
                else guestBases[address] = count - 1;
            }
        }

        public static void RegisterPage(IntPtr start, ulong size)
        {
            if (!Installed) return;
            var address = (ulong)start.ToInt64();
            lock (gate) pages.Add(new Page { Start = address, End = address + size });
        }

        public static void UnregisterPage(IntPtr start)
        {
            if (!Installed) return;
            var address = (ulong)start.ToInt64();
            lock (gate) pages.RemoveAll(p => p.Start == address);
        }

        public static void Register(IntPtr start, int length, IntPtr faultStub, int[] hostOffsets, uint[] guestEips)
        {
            if (!Installed) return;
            lock (gate) ranges.Register((ulong)start.ToInt64(), length, (ulong)faultStub.ToInt64(), hostOffsets, guestEips);
        }

        public static void Unregister(IntPtr start)
        {
            if (!Installed) return;
            lock (gate) ranges.Unregister((ulong)start.ToInt64());
        }

        public static void SetExecution(IntPtr memoryBase, uint eip, IntPtr block, int length,
            IntPtr faultStub, int[] hostOffsets, uint[] guestEips)
        {
            if (!Installed) return;
            var start = (ulong)block.ToInt64();
            lock (gate)
            {
                var thread = GetCurrentThreadId();
                if (!executions.TryGetValue(thread, out var execution))
                    executions[thread] = execution = new Execution();
                execution.GuestBase = (ulong)memoryBase.ToInt64();
                execution.RunningEip = eip;
                execution.Start = start;
                execution.End = start + (ulong)length;
                execution.Stub = (ulong)faultStub.ToInt64();
                execution.HostOffsets = hostOffsets;
                execution.GuestEips = guestEips;
                execution.Pending = false;
            }
        }

        public static void ClearExecution()
        {
            if (!Installed) return;
            lock (gate)
            {
                var thread = GetCurrentThreadId();
                if (executions.TryGetValue(thread, out var execution))
                {
                    // Keep the fault until the dispatcher has consumed it.
                    execution.Start = execution.End = execution.Stub = 0;
                    if (!execution.Pending) execution.GuestBase = 0;
                }
            }
        }

        public static bool TakeFault(out ulong rip, out ulong address, out uint access, out uint eip)
        {
            rip = address = 0;
            access = eip = 0;
            if (!Installed) return false;
            lock (gate)
            {
                var thread = GetCurrentThreadId();
                if (!executions.TryGetValue(thread, out var execution) || !execution.Pending) return false;
                rip = execution.Rip;
                address = execution.Address;
                access = execution.Access;
                eip = execution.FaultEip;
                execution.Pending = false;
                execution.GuestBase = 0;
                return true;
            }
        }

        private static uint GuestEip(int[] offsets, uint[] eips, ulong start, ulong rip, uint fallback)
        {
            if (rip < start) return fallback;
            var offset = rip - start;
            for (var i = 0; i < offsets.Length && (ulong)offsets[i] <= offset; i++) fallback = eips[i];
            return fallback;
        }

        private static int OnException(IntPtr pointers)
        {
            var record = Marshal.ReadIntPtr(pointers);
            var context = Marshal.ReadIntPtr(pointers, IntPtr.Size);
            var status = (uint)Marshal.ReadInt32(record);
            var rip = (ulong)Marshal.ReadInt64(context, ContextRip);
            var address = (ulong)Marshal.ReadInt64(record, RecordInformation + 8);
            ulong stub = 0, start = 0, page = 0;
            uint eip = 0;
            bool registered, addressInCode = false, addressInGuest = false, guestBaseSet = false, owned = false;
            Execution execution;
            lock (gate)
            {
                executions.TryGetValue(GetCurrentThreadId(), out execution);
                guestBaseSet = execution != null && execution.GuestBase != 0 && execution.Stub != 0;
                eip = execution != null ? execution.RunningEip : 0;
                var range = ranges.Find(rip);
                registered = range.HasValue;
                if (range.HasValue)
                {
                    var match = range.Value;
                    stub = match.Stub;
                    start = match.Start;
                    eip = GuestEip(match.HostOffsets, match.GuestEips, start, rip, eip);
                }
                addressInCode = ranges.Find(address).HasValue;
                foreach (var ownedPage in pages)
                {
                    if (rip >= ownedPage.Start && rip < ownedPage.End) { owned = true; page = ownedPage.Start; }
                    if (address >= ownedPage.Start && address < ownedPage.End) addressInCode = true;
                }
                if (execution != null && execution.GuestBase != 0 &&
                    address >= execution.GuestBase && address - execution.GuestBase < GuestSize)
                    addressInGuest = true;
                if (!addressInGuest)
                    foreach (var baseAddress in guestBases.Keys)
                        if (address >= baseAddress && address - baseAddress < GuestSize) { addressInGuest = true; break; }

                // The active block retains its fault exit even if its range entry is missing.
                // Its epilogue is the only safe way to restore the pinned guest registers.
                if (stub == 0 && owned && execution != null && execution.Stub != 0)
                {
                    stub = execution.Stub;
                    start = execution.Start;
                    if (rip >= execution.Start && rip < execution.End)
                        eip = GuestEip(execution.HostOffsets, execution.GuestEips, start, rip, eip);
                }
                if (stub != 0 && status == StatusAccessViolation && addressInGuest && !addressInCode && execution != null)
                {
                    execution.Pending = true;
                    execution.Rip = rip;
                    execution.Address = address;
                    execution.Access = (uint)Marshal.ReadInt64(record, RecordInformation);
                    execution.FaultEip = eip;
                    Marshal.WriteInt64(context, ContextRip, (long)stub);
                    return ExceptionContinueExecution;
                }
            }
            if (owned || addressInGuest)
                WriteNativeFault(status, rip, address, registered, guestBaseSet, page, start, eip);
            return ExceptionContinueSearch;
        }

        private static void WriteNativeFault(uint status, ulong rip, ulong address, bool registered,
            bool guestBaseSet, ulong page, ulong block, uint eip)
        {
            if (reportPath == null) return;
            try
            {
                var line = "status=0x" + status.ToString("X8") + " rip=0x" + rip.ToString("X16") +
                    " address=0x" + address.ToString("X16") + " registered=" + registered +
                    " guest-base-set=" + guestBaseSet + " page=0x" + page.ToString("X16") +
                    " block=0x" + block.ToString("X16") + " guest-eip=0x" + eip.ToString("X8") + Environment.NewLine;
                WriteLine(reportPath, line);
                if (probePath != null) WriteLine(probePath, "x86.log=native-fault " + line);
            }
            catch (Exception) { }
        }

        private static void WriteLine(string path, string line)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(line);
                writer.Flush();
                stream.Flush(true);
            }
        }
    }
}
