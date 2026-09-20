using System;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Who gets the processor first.
    ///
    /// A PC game assumes it owns the machine, and on a machine it owns that is
    /// true. Here it shares the process with the code that draws the screen —
    /// so the game's threads are started a step below normal, and the thread
    /// that draws asks for a step above. Neither costs the game anything it can
    /// feel; both are the difference between a game that runs and a game that
    /// can be seen.
    /// </summary>
    public static class ThreadRank
    {
        private const int AboveNormal = 1;

        [DllImport("api-ms-win-core-processthreads-l1-1-0.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("api-ms-win-core-processthreads-l1-1-0.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        /// <summary>What happened, for the report to carry.</summary>
        public static string Note = "not asked";

        /// <summary>Raises the calling thread. Call it from the thread that draws.</summary>
        public static void RaiseThisThread()
        {
            try
            {
                Note = SetThreadPriority(GetCurrentThread(), AboveNormal)
                    ? "screen thread raised"
                    : "refused: " + Marshal.GetLastWin32Error();
            }
            catch (Exception error)
            {
                Note = error.GetType().Name;
            }
        }
    }
}
