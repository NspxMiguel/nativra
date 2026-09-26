using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace Kiosk
{
    /// <summary>
    /// How much memory this app can really commit, versus what the API reports.
    ///
    /// MemoryManager.AppMemoryUsageLimit reads 5120 MiB on the Series X in game
    /// mode, but that is a reported figure; the real ceiling is where a commit
    /// is refused. This allocates 128 MiB blocks and touches every page (a
    /// reservation that is never written costs nothing, so it must be dirtied
    /// to count), until VirtualAllocFromApp returns zero or a write faults,
    /// then frees everything and writes the ceiling to memory-ceiling.txt.
    /// Runs only when memtest.txt is present, and never while a game is loaded.
    /// It moves nothing of the system: it asks for memory the ordinary way and
    /// gives it all back.
    /// </summary>
    internal static class MemoryProbe
    {
        [DllImport("api-ms-win-core-memory-l1-1-0.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocFromApp(
            IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("api-ms-win-core-memory-l1-1-0.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        private const long Block = 128L * 1024 * 1024;
        private const int PageStep = 4096;

        public static async Task RunAsync()
        {
            var local = ApplicationData.Current.LocalFolder;
            if (await local.TryGetItemAsync("memtest.txt") == null) return;
            if (Native.NativeProbe.GameRunning) return;

            var report = new StringBuilder();
            report.AppendLine(DateTime.UtcNow.ToString("o"));
            try
            {
                report.AppendLine("limit " + MemoryManager.AppMemoryUsageLimit
                    + " used " + MemoryManager.AppMemoryUsage
                    + " level " + MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception error)
            {
                report.AppendLine("memorymanager " + error.GetType().Name);
            }

            var blocks = new System.Collections.Generic.List<IntPtr>();
            long committed = 0;
            var stopped = "no more blocks tried";
            try
            {
                for (var i = 0; i < 512; i++)
                {
                    var at = VirtualAllocFromApp(
                        IntPtr.Zero, (UIntPtr)(ulong)Block, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    if (at == IntPtr.Zero)
                    {
                        stopped = "commit refused (" + Marshal.GetLastWin32Error() + ")";
                        break;
                    }
                    // A committed page is not backed until it is written; dirty
                    // every page so the block counts against the real ceiling.
                    for (long off = 0; off < Block; off += PageStep) Marshal.WriteByte(at, (int)off, 1);
                    blocks.Add(at);
                    committed += Block;
                    if (committed >= 12L * 1024 * 1024 * 1024) { stopped = "reached 12 GiB, stopping"; break; }
                }
            }
            catch (Exception error)
            {
                stopped = "faulted after " + committed + " (" + error.GetType().Name + ")";
            }
            finally
            {
                foreach (var block in blocks) VirtualFree(block, UIntPtr.Zero, MEM_RELEASE);
            }

            report.AppendLine("committed " + committed + " bytes (" + (committed / (1024 * 1024)) + " MiB) before: " + stopped);
            try
            {
                var file = await local.CreateFileAsync("memory-ceiling.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, report.ToString());
            }
            catch
            {
            }
        }
    }
}
