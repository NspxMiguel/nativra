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
        /// <summary>Moving what is already down to a roomier place.</summary>
        public bool Moving;
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

        /// <summary>
        /// A marker left by a session that ended mid-download says nothing
        /// about this one: nothing downloads until a page starts it.
        /// </summary>
        public static void ClearStaleMarker()
        {
            marked = true;
            var _ = MarkAsync(false);
        }

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

        /// <summary>Another place with more room than this one, or null.</summary>
        private static async Task<GamePlace> RoomierAsync(string place)
        {
            var places = await GameStorage.PlacesAsync();
            ulong here = 0;
            foreach (var candidate in places)
                if (candidate.Id == place) here = candidate.Free ?? 0;
            places.RemoveAll(candidate => candidate.Id == place || (candidate.Free ?? 0) <= here);
            return GameStorage.Roomiest(places);
        }

        /// <summary>
        /// A move cut short leaves part of a game in the old place. Before
        /// downloading, whatever is elsewhere is brought to where the
        /// download goes, so nothing is fetched twice or stranded.
        /// </summary>
        private static async Task GatherAsync(DownloadJob job, uint appId, string place)
        {
            try
            {
                var target = new GamePlace { Id = place, Name = place };
                foreach (var candidate in await GameStorage.PlacesAsync())
                    if (candidate.Id == place) target = candidate;
                foreach (var pair in await GameStorage.GamesFoldersAsync())
                {
                    if (pair.Key == place) continue;
                    if (!(await pair.Value.TryGetItemAsync(appId.ToString()) is Windows.Storage.StorageFolder stray)) continue;
                    if (await stray.TryGetItemAsync(".downloaded") != null) continue;
                    await MoveAsync(job, appId, pair.Key, target);
                }
            }
            catch
            {
                // Gathering is tidying; the download itself still runs.
            }
        }

        private static async Task MoveAsync(DownloadJob job, uint appId, string from, GamePlace to)
        {
            var games = await GameStorage.GamesFolderAsync(from, false);
            var folder = games == null
                ? null
                : await games.TryGetItemAsync(appId.ToString()) as Windows.Storage.StorageFolder;
            if (folder == null) return;
            var lastRaised = 0;
            job.Moving = true;
            try
            {
                await GameStorage.MoveAsync(folder, to.Id, moved =>
                {
                    job.File = Texts.Get("downloads.moving", to.Name, Steam.SteamDownload.Human(moved));
                    var now = Environment.TickCount;
                    if (now - lastRaised < 500) return;
                    lastRaised = now;
                    Raise();
                });
            }
            finally
            {
                job.Moving = false;
            }
        }

        /// <summary>
        /// download-error.txt: the whole exception, with the file it was on.
        /// The screen has room for one line; finding why a download stops
        /// at the same file every time needs the stack.
        /// </summary>
        private static async Task RecordAsync(DownloadJob job, Exception error)
        {
            try
            {
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "download-error.txt", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteTextAsync(file,
                    $"{DateTime.UtcNow:o} app {job.AppId} at {job.Percent}% {job.File}\r\n{error}\r\n");
            }
            catch
            {
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
                    var place = root;
                    await GatherAsync(job, appId, place);
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await Steam.SteamDownload.RunAsync(session, appId, place, progress =>
                            {
                                job.Percent = progress.Percent;
                                job.File = progress.File ?? string.Empty;
                                // Enough to move a bar, not a flood of redraws.
                                var now = Environment.TickCount;
                                if (now - lastRaised < 500) return;
                                lastRaised = now;
                                Raise();
                            });
                            break;
                        }
                        catch (Steam.DiskFullException) when (attempt == 0)
                        {
                            // The console's storage filled: what is already
                            // down moves to the roomiest other place, a USB
                            // drive, and the download carries on there.
                            var other = await RoomierAsync(place);
                            if (other == null) throw;
                            await MoveAsync(job, appId, place, other);
                            place = other.Id;
                        }
                    }
                    job.Percent = 100;
                    job.Finished = true;
                }
                catch (Exception error)
                {
                    job.Error = error.Message;
                    await RecordAsync(job, error);
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
