using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Kiosk
{
    /// <summary>One game being downloaded, as the screens show it.</summary>
    public sealed class DownloadJob
    {
        public uint AppId;
        public string Name;
        public int Percent;
        public string File = string.Empty;
        public bool Running;
        public bool Finished;
        public string Error;
    }

    /// <summary>
    /// Downloads that outlive the screen that started them.
    ///
    /// A download used to run inside the game page, so leaving the page ended
    /// it and the progress was only visible there. The work now belongs to the
    /// app: pages start a job, and any screen can show where it is.
    /// </summary>
    public static class DownloadManager
    {
        private static readonly List<DownloadJob> jobs = new List<DownloadJob>();

        /// <summary>Raised on a background thread whenever a job moves.</summary>
        public static event Action Changed;

        public static List<DownloadJob> Snapshot()
        {
            lock (jobs) return new List<DownloadJob>(jobs);
        }

        public static DownloadJob Find(uint appId)
        {
            lock (jobs)
            {
                for (var i = jobs.Count - 1; i >= 0; i--)
                    if (jobs[i].AppId == appId) return jobs[i];
            }
            return null;
        }

        /// <summary>The first job still running, for a one-line status.</summary>
        public static DownloadJob Active()
        {
            lock (jobs)
                foreach (var job in jobs)
                    if (job.Running) return job;
            return null;
        }

        private static bool marked;

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch { }
            var _ = MarkAsync(Active() != null);
        }

        /// <summary>
        /// downloading.txt in the app's storage while anything downloads, so
        /// tools that replace or close the app can see it is busy.
        /// </summary>
        private static async Task MarkAsync(bool busy)
        {
            if (busy == marked) return;
            marked = busy;
            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalFolder;
                if (busy)
                    await local.CreateFileAsync("downloading.txt", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                else if (await local.TryGetItemAsync("downloading.txt") is Windows.Storage.IStorageItem item)
                    await item.DeleteAsync();
            }
            catch
            {
                // Only a courtesy to outside tools.
            }
        }

        /// <summary>
        /// Starts a download, or returns the one already running for the game.
        /// The task completes when the download does; nothing waits on it.
        /// </summary>
        public static DownloadJob Start(SteamSession session, uint appId, string name, string root)
        {
            DownloadJob job;
            lock (jobs)
            {
                var existing = Find(appId);
                if (existing != null && existing.Running) return existing;
                job = new DownloadJob { AppId = appId, Name = name, Running = true };
                jobs.Add(job);
            }
            Raise();
            var _ = Task.Run(async () =>
            {
                var lastRaised = 0;
                try
                {
                    await Steam.SteamDownload.RunAsync(session, appId, root, progress =>
                    {
                        job.Percent = progress.Percent;
                        job.File = progress.File ?? string.Empty;
                        // Enough to move a bar, not a flood of redraws.
                        var now = Environment.TickCount;
                        if (now - lastRaised < 500) return;
                        lastRaised = now;
                        Raise();
                    });
                    job.Percent = 100;
                    job.Finished = true;
                }
                catch (Exception error)
                {
                    job.Error = error.Message;
                }
                finally
                {
                    job.Running = false;
                    Raise();
                }
            });
            return job;
        }
    }
}
