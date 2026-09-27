using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Names the lock a game is spinning on, when it is.
    ///
    /// LEGO Jurassic World's last calls before it goes unresponsive are
    /// hundreds of EnterCriticalSection/LeaveCriticalSection pairs ending in
    /// Sleep — a normal engine spin-wait, on a lock nobody is releasing. The
    /// trace already named the function; it never named which
    /// CRITICAL_SECTION, so a real contention on one lock and ordinary
    /// unrelated locking traffic looked identical. This wraps the real
    /// function (behaviour unchanged) and remembers the address of a lock
    /// entered many times in a row without another one in between, which is
    /// what a spin looks like from the outside.
    /// </summary>
    internal static class LockWatch
    {
        [DllImport("api-ms-win-core-synch-l1-2-0.dll", EntryPoint = "EnterCriticalSection")]
        private static extern void RealEnter(IntPtr section);

        [DllImport("api-ms-win-core-synch-l1-2-0.dll", EntryPoint = "LeaveCriticalSection")]
        private static extern void RealLeave(IntPtr section);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void SectionDelegate(IntPtr section);

        private static SectionDelegate enter, leave;

        private static IntPtr lastSection;
        private static int streak;
        public static IntPtr SpinningOn;
        public static int SpinCount;

        /// <summary>
        /// Tracking is opt-in (stacks.txt), so a game that already works well
        /// pays only the one indirection this wrap always costs, not the
        /// bookkeeping on top of it.
        /// </summary>
        public static bool Active;

        private static readonly SectionDelegate enterImpl = section =>
        {
            RealEnter(section);
            if (!Active) return;
            if (section == lastSection)
            {
                streak++;
                if (streak > SpinCount)
                {
                    SpinCount = streak;
                    SpinningOn = section;
                }
            }
            else
            {
                lastSection = section;
                streak = 1;
            }
        };

        private static readonly SectionDelegate leaveImpl = section => RealLeave(section);

        public static void Install(SystemImports imports)
        {
            enter = enterImpl;
            leave = leaveImpl;
            var enterPtr = Marshal.GetFunctionPointerForDelegate(enter);
            var leavePtr = Marshal.GetFunctionPointerForDelegate(leave);
            foreach (var module in new[]
            {
                "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll",
                "api-ms-win-core-synch-l1-2-0.dll", "api-ms-win-core-synch-l1-1-0.dll",
            })
            {
                imports.Overrides[module + "!EnterCriticalSection"] = enterPtr;
                imports.Overrides[module + "!LeaveCriticalSection"] = leavePtr;
            }
        }
    }
}
