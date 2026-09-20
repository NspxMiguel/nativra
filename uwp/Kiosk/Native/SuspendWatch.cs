using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Keeps the game's garbage collector from freezing the application.
    ///
    /// The managed runtime inside a Unity game stops the world to collect: it
    /// walks its list of threads, suspends each one, reads its registers for
    /// anything that looks like a reference, and resumes it. That is ordinary,
    /// and on a normal Windows process it only ever touches threads the game
    /// itself made.
    ///
    /// Here the game is not a process. It is mapped inside ours, and it shares
    /// every thread the application already had — the one that draws the
    /// screen, the one that writes the report. A collector that suspends one
    /// of those has suspended a thread holding a lock belonging to a runtime
    /// it has never heard of, and nothing in the process will ever run again.
    /// That is the shape of what was measured: suspend, read, resume as the
    /// last three calls before everything turned into a spin.
    ///
    /// So the application's own threads are declared off limits. Asking to
    /// suspend one succeeds, reports the honest previous count, and does
    /// nothing — which is exactly what a collector expects from a thread that
    /// is not running managed code of its own.
    /// </summary>
    internal static class SuspendWatch
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint SuspendDelegate(IntPtr thread);

        [DllImport("kernel32.dll")]
        private static extern uint GetThreadId(IntPtr thread);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static SuspendDelegate suspend;
        private static SuspendDelegate resume;
        private static SuspendDelegate realSuspend;
        private static SuspendDelegate realResume;

        // Written once, on the way in, and only read afterwards.
        private static readonly HashSet<uint> Ours = new HashSet<uint>();

        // How deep each thread is suspended, by thread id. A thread the game
        // suspended and never resumed is the whole question.
        private static readonly Dictionary<uint, int> Depth = new Dictionary<uint, int>();

        /// <summary>Times the game was told no, because the thread was ours.</summary>
        public static long Refused;

        /// <summary>
        /// Marks the calling thread as the application's, so the game's
        /// collector will never stop it. Called from each thread that belongs
        /// to the application, before the game is allowed to start.
        /// </summary>
        public static void ThisThreadIsOurs()
        {
            lock (Ours) Ours.Add(GetCurrentThreadId());
        }

        /// <summary>Threads left suspended, which should be none.</summary>
        public static List<string> Held()
        {
            var held = new List<string>();
            lock (Depth)
            {
                foreach (var pair in Depth)
                {
                    if (pair.Value > 0)
                    {
                        held.Add("thread " + pair.Key + " held down " + pair.Value + "x");
                    }
                }
            }
            if (Refused > 0) held.Add("refused to stop our own threads " + Refused + "x");
            return held;
        }

        private static void Count(uint thread, int by)
        {
            lock (Depth)
            {
                Depth.TryGetValue(thread, out var deep);
                var now = deep + by;
                if (now < 0) now = 0;
                Depth[thread] = now;
            }
        }

        public static void Install(SystemImports imports)
        {
            var suspendAt = imports.SystemAddress("kernel32.dll", "SuspendThread");
            var resumeAt = imports.SystemAddress("kernel32.dll", "ResumeThread");
            if (suspendAt == IntPtr.Zero || resumeAt == IntPtr.Zero) return;

            realSuspend = Marshal.GetDelegateForFunctionPointer<SuspendDelegate>(suspendAt);
            realResume = Marshal.GetDelegateForFunctionPointer<SuspendDelegate>(resumeAt);

            suspend = thread =>
            {
                var id = GetThreadId(thread);
                bool mine;
                lock (Ours) mine = Ours.Contains(id);
                if (mine)
                {
                    // Zero is the truth here: this thread was not suspended
                    // before, and it is not suspended now.
                    Refused++;
                    return 0;
                }
                var before = realSuspend(thread);
                if (before != uint.MaxValue) Count(id, 1);
                return before;
            };

            resume = thread =>
            {
                var id = GetThreadId(thread);
                bool mine;
                lock (Ours) mine = Ours.Contains(id);
                if (mine) return 1;

                var before = realResume(thread);
                if (before != uint.MaxValue) Count(id, -1);
                return before;
            };

            foreach (var module in new[]
                     {
                         "KERNEL32.dll", "kernel32.dll", "KERNELBASE.dll",
                         "api-ms-win-core-processthreads-l1-1-0.dll",
                         "api-ms-win-core-processthreads-l1-1-1.dll",
                         "api-ms-win-core-processthreads-l1-1-2.dll",
                     })
            {
                imports.Overrides[module + "!SuspendThread"] =
                    Marshal.GetFunctionPointerForDelegate(suspend);
                imports.Overrides[module + "!ResumeThread"] =
                    Marshal.GetFunctionPointerForDelegate(resume);
            }
        }
    }
}
