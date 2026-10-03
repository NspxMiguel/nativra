using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Nativra.X86.Jit
{
    /// <summary>
    /// Turns an access violation raised by translated guest code into a guest
    /// fault instead of a host crash.
    ///
    /// Compiled blocks touch guest memory directly (host base + guest
    /// address), so a guest read of an unmapped page is a real access
    /// violation in the host process, which .NET cannot catch. A vectored
    /// exception handler sees it first: when the faulting instruction lies in
    /// a published block, it records the fault and resumes the thread at that
    /// block's fault exit, which stores the guest registers and returns to the
    /// dispatcher like any other block exit. The dispatcher maps the host
    /// address back to the guest instruction. Windows only; elsewhere guest
    /// faults under the JIT stay fatal (the interpreter always reports them).
    /// </summary>
    internal static class JitFaults
    {
        private const uint StatusAccessViolation = 0xC0000005;
        private const int ContextRip = 0xF8;           // CONTEXT.Rip (x64)
        private const int RecordInformation = 0x20;    // EXCEPTION_RECORD.ExceptionInformation[0]
        private const int ExceptionContinueExecution = -1;
        private const int ExceptionContinueSearch = 0;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int VectoredHandler(IntPtr pointers);

        [DllImport("api-ms-win-core-errorhandling-l1-1-0.dll")]
        private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

        private static readonly object gate = new object();
        private static readonly List<Range> ranges = new List<Range>();
        private static VectoredHandler handler;   // kept alive for the process's lifetime
        private static bool attempted;

        private struct Range
        {
            public ulong Start, End, Stub;
            public int[] HostOffsets;
            public uint[] GuestEips;
        }

        private static string reportPath;
        private static string probePath;
        [ThreadStatic] private static ulong guestBase;
        [ThreadStatic] private static uint runningEip;

        public static void ConfigureReport(string path, string probe)
        {
            reportPath = path;
            probePath = probe;
        }

        public static void SetExecution(IntPtr memoryBase, uint eip)
        {
            guestBase = (ulong)memoryBase.ToInt64();
            runningEip = eip;
        }

        public static void ClearExecution()
        {
            guestBase = 0;
            runningEip = 0;
        }

        [ThreadStatic] public static bool Pending;
        [ThreadStatic] public static ulong Rip;
        [ThreadStatic] public static ulong Address;
        [ThreadStatic] public static uint Access;   // 0 read, 1 write, 8 execute

        /// <summary>True once the handler is in place (Windows x64 only).</summary>
        public static bool Installed { get; private set; }

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
                catch (Exception)
                {
                    Installed = false;   // no handler: faults stay fatal, as before
                }
            }
        }

        /// <summary>Makes a published block's code eligible, with the address of its fault exit.</summary>
        public static void Register(IntPtr start, int length, IntPtr faultStub, int[] hostOffsets, uint[] guestEips)
        {
            if (!Installed) return;
            lock (gate)
                ranges.Add(new Range
                {
                    Start = (ulong)start.ToInt64(),
                    End = (ulong)start.ToInt64() + (ulong)length,
                    Stub = (ulong)faultStub.ToInt64(),
                    HostOffsets = hostOffsets,
                    GuestEips = guestEips,
                });
        }

        public static void Unregister(IntPtr start)
        {
            if (!Installed) return;
            var at = (ulong)start.ToInt64();
            lock (gate) ranges.RemoveAll(r => r.Start == at);
        }

        private static int OnException(IntPtr pointers)
        {
            var record = Marshal.ReadIntPtr(pointers);
            var context = Marshal.ReadIntPtr(pointers, IntPtr.Size);
            var status = (uint)Marshal.ReadInt32(record);

            var rip = (ulong)Marshal.ReadInt64(context, ContextRip);
            ulong stub = 0;
            ulong start = 0;
            ulong codeAddress = 0;
            var eip = runningEip;
            var address = (ulong)Marshal.ReadInt64(record, RecordInformation + 8);
            var addressInCode = false;
            lock (gate)
            {
                foreach (var r in ranges)
                {
                    if (address >= r.Start && address < r.End)
                    {
                        addressInCode = true;
                        if (codeAddress == 0) { start = r.Start; codeAddress = address; }
                    }
                    if (rip >= r.Start && rip < r.End)
                    {
                        stub = r.Stub;
                        start = r.Start;
                        codeAddress = rip;
                        for (var i = 0; i < r.HostOffsets.Length && (ulong)r.HostOffsets[i] <= rip - r.Start; i++)
                            eip = r.GuestEips[i];
                    }
                }
            }
            var addressInGuest = guestBase != 0 && address >= guestBase && address - guestBase < 0x100000000UL;
            if ((stub != 0 && (status != StatusAccessViolation || !addressInGuest || addressInCode)) || addressInCode)
                WriteNativeFault(status, rip, address, start, codeAddress, eip);
            if (stub == 0 || status != StatusAccessViolation || !addressInGuest || addressInCode)
                return ExceptionContinueSearch;

            Pending = true;
            Rip = rip;
            Access = (uint)Marshal.ReadInt64(record, RecordInformation);
            Address = address;
            Marshal.WriteInt64(context, ContextRip, (long)stub);
            return ExceptionContinueExecution;
        }

        private static void WriteNativeFault(uint status, ulong rip, ulong address, ulong block, ulong codeAddress, uint eip)
        {
            if (reportPath == null) return;
            try
            {
                var line = "status=0x" + status.ToString("X8") + " host=0x" + rip.ToString("X16") +
                    " address=0x" + address.ToString("X16") + " code-offset=" +
                    (block == 0 ? "outside" : "0x" + (codeAddress - block).ToString("X")) +
                    " guest-eip=0x" + eip.ToString("X8") + Environment.NewLine;
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
