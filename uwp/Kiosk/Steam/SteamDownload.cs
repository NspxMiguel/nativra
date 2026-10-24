using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Kiosk.Steam
{
    public sealed class DownloadProgress
    {
        public string File { get; set; }
        public string Identity { get; set; }
        public long ChunkOffset { get; set; }
        public string Stage { get; set; } = "install";
        public long Done { get; set; }
        public long Total { get; set; }
        public int Percent => Total > 0 ? (int)(Done * 100 / Total) : 0;
    }

    /// <summary>
    /// Pulls a game down onto the console. Chunks are written straight into
    /// place at their offset, so a half-finished file is a real file and
    /// starting again costs only what is missing.
    /// </summary>
    public static class SteamDownload
    {
        /// <summary>Where games go unless he picks somewhere else.</summary>
        public const string DefaultFolder = "games";

        public static async Task<StorageFolder> TargetAsync(string root, uint appId)
        {
            var games = await GameStorage.GamesFolderAsync(root, true);
            return await games.CreateFolderAsync(
                appId.ToString(), CreationCollisionOption.OpenIfExists);
        }

        /// <summary>Free bytes where the folder lives, or null if it will not say.</summary>
        public static async Task<ulong?> FreeBytesAsync(StorageFolder folder)
        {
            try
            {
                var properties = await folder.Properties.RetrievePropertiesAsync(
                    new[] { "System.FreeSpace" });
                if (properties.TryGetValue("System.FreeSpace", out var value) && value != null)
                    return Convert.ToUInt64(value);
            }
            catch
            {
                // A failed probe remains explicitly unknown.
            }
            return null;
        }

        public static string Human(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return size.ToString(size >= 10 ? "0" : "0.0") + " " + units[unit];
        }

        private sealed class DepotPlan
        {
            public Depot Depot;
            public Manifest Manifest;
            public byte[] Key;
        }

        public static async Task RunAsync(
            SteamSession session,
            uint appId,
            string root,
            Action<DownloadProgress> onProgress,
            int parallel = 6,
            CancellationToken cancellation = default(CancellationToken))
        {
            using (var cm = new SteamCm())
            {
                var endpoints = await SteamCm.EndpointsAsync();
                Exception last = null;
                var connected = false;
                var renewedOnce = false;
                foreach (var endpoint in endpoints.GetRange(0, Math.Min(5, endpoints.Count)))
                {
                    try
                    {
                        await cm.ConnectAsync(endpoint);
                        try
                        {
                            await cm.LogOnAsync(session.SteamId, await session.RefreshTokenForLogOnAsync());
                        }
                        catch (SteamLogOnException refused) when (refused.Result == 5 && !renewedOnce)
                        {
                            // The refresh token may have been rotated by a renewal that
                            // ran while this started; renew (or learn the sign-in is
                            // really gone) and log on once more with the current one.
                            renewedOnce = true;
                            if (await session.ForgetIfRefusedAsync()) throw;
                            await cm.ConnectAsync(endpoint);   // Steam closes the socket after a refused log-on
                            await cm.LogOnAsync(session.SteamId, await session.RefreshTokenForLogOnAsync());
                        }
                        connected = true;
                        break;
                    }
                    catch (SteamLogOnException error) when
                        (error.Result == 5 || error.Result == 15 || error.Result == 84)
                    {
                        // Credentials and rate limits do not improve on another CM.
                        throw;
                    }
                    catch (Exception error)
                    {
                        last = error;
                    }
                }
                if (!connected) throw last ?? new Exception("no connection manager answered");

                var servers = await SteamDepot.ServersAsync(cm);
                if (servers.Count == 0) throw new Exception("Steam listed no content servers");

                var info = KeyValue.Parse(await cm.AppInfoAsync(appId, await cm.AppTokenAsync(appId)));
                var depots = Depots.ForWindows(info);
                if (depots.Count == 0) throw new Exception("this app has no Windows depot");

                var folder = await TargetAsync(root, appId);

                // Resolve the licensed manifest set before capacity planning. A job timeout
                // is not a license refusal and must never silently omit a depot.
                var plans = new List<DepotPlan>();
                foreach (var depot in depots)
                {
                    cancellation.ThrowIfCancellationRequested();
                    byte[] key;
                    try { key = await cm.DepotKeyAsync(appId, depot.Id); }
                    catch (DepotLicenseException) when (depots.Count > 1) { continue; }
                    var code = await SteamDepot.ManifestCodeAsync(cm, appId, depot.Id, depot.ManifestId);
                    var manifest = SteamDepot.ParseManifest(
                        await SteamDepot.FetchManifestAsync(servers, depot.Id, depot.ManifestId, code));
                    if (manifest.DepotId != depot.Id)
                        throw new DownloadFailure("manifest", false, "depot identity mismatch");
                    plans.Add(new DepotPlan { Depot = depot, Manifest = manifest, Key = key });
                }
                if (plans.Count == 0) throw new DownloadFailure("license", false, "no licensed Windows depot");

                // Invalidate a stale completion marker before inspecting any payload.
                var pending = await folder.CreateFileAsync(".downloading", CreationCollisionOption.ReplaceExisting);
                var completed = await folder.TryGetItemAsync(".downloaded");
                if (completed != null) await completed.DeleteAsync();
                ulong remaining = 0;
                foreach (var plan in plans)
                    foreach (var file in plan.Manifest.Files)
                    {
                        var name = DownloadCheckpoint.SafePath(plan.Manifest.NamesEncrypted
                            ? SteamDepot.DecryptName(file.Name, plan.Key) : file.Name);
                        if (file.IsDirectory) continue;
                        DownloadCheckpoint.ValidateLayout(file.Size, Chunks(file));
                        var target = await CreateAsync(folder, name);
                        var size = (await target.GetBasicPropertiesAsync()).Size;
                        remaining = checked(remaining + (ulong)Math.Max(0, file.Size - (long)size));
                    }
                DownloadCheckpoint.RequireCapacity(await FreeBytesAsync(folder), remaining);

                foreach (var plan in plans)
                {
                    long done = 0;
                    foreach (var file in plan.Manifest.Files)
                    {
                        var name = DownloadCheckpoint.SafePath(plan.Manifest.NamesEncrypted
                            ? SteamDepot.DecryptName(file.Name, plan.Key) : file.Name);
                        if (file.IsDirectory)
                        {
                            await CreateFolderAsync(folder, name);
                            continue;
                        }
                        var identity = DownloadCheckpoint.Identity(appId, plan.Depot.Id, plan.Depot.ManifestId, name);
                        var progress = new DownloadProgress
                        {
                            File = name, Identity = identity, Done = done, Total = plan.Manifest.TotalBytes,
                        };
                        onProgress?.Invoke(progress);
                        try
                        {
                            var target = await CreateAsync(folder, name);
                            // One chunk at a time bounds buffers independently of depot size.
                            // Rehashing the current file binds every reused range to the
                            // app/depot/manifest/file context, including legacy checkpoints.
                            using (var stream = await target.OpenStreamForWriteAsync())
                            {
                                await RepairAsync(stream, file, servers, plan.Depot.Id, plan.Key,
                                    async growth => DownloadCheckpoint.RequireCapacity(await FreeBytesAsync(folder), growth),
                                    offset => progress.ChunkOffset = offset,
                                    bytes =>
                                    {
                                        done += bytes;
                                        progress.Done = done;
                                        onProgress?.Invoke(progress);
                                    }, cancellation);
                            }
                        }
                        catch (Exception error)
                        {
                            if (DownloadCheckpoint.IsDiskFull(error))
                            {
                                var available = await FreeBytesAsync(folder);
                                throw new DiskFullException($"identity={identity} chunk={progress.ChunkOffset} done={done} total={progress.Total} available={available?.ToString() ?? "unknown"}");
                            }
                            var category = DownloadCheckpoint.Category(error);
                            throw new DownloadFailure(category,
                                error is DownloadFailure failure ? failure.Recoverable : true,
                                $"identity={identity} chunk={progress.ChunkOffset} done={done} total={progress.Total} " +
                                (error is DownloadFailure ? error.Message : ""));
                        }
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                // Publish once, only after every selected manifest range passed readback.
                var marker = await folder.CreateFileAsync(".downloaded", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(marker, "verified-v1");
                await pending.DeleteAsync();
            }
        }

        private static List<DownloadChunk> Chunks(ManifestFile file)
        {
            var chunks = new List<DownloadChunk>();
            foreach (var chunk in file.Chunks)
                chunks.Add(new DownloadChunk { Offset = chunk.Offset, Length = chunk.Original, Hash = chunk.Sha });
            chunks.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return chunks;
        }

        private static Task RepairAsync(Stream stream, ManifestFile file, List<ContentServer> servers,
            uint depotId, byte[] key, Func<ulong, Task> capacity, Action<long> onChunk,
            Action<long> onVerified, CancellationToken cancellation)
        {
            return DownloadCheckpoint.RestoreAsync(stream, file.Size, Chunks(file), chunk =>
            {
                onChunk(chunk.Offset);
                return FetchOneAsync(servers, depotId, chunk, key);
            }, capacity, onVerified, cancellation);
        }

        private static async Task<byte[]> FetchOneAsync(
            List<ContentServer> servers, uint depotId, DownloadChunk chunk, byte[] key)
        {
            var raw = await SteamDepot.FetchChunkAsync(servers, depotId, chunk.Hash);
            var decrypted = SteamDepot.Decrypt(raw, key);
            try { return SteamDepot.Decompress(decrypted); }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                throw new DownloadFailure("decompression", true, "invalid compressed chunk");
            }
        }

        /// <summary>Creates a folder and every folder on the way to it.</summary>
        private static async Task CreateFolderAsync(StorageFolder root, string path)
        {
            var folder = root;
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0) continue;
                folder = await folder.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists);
            }
        }

        /// <summary>Creates a file and every folder on the way to it.</summary>
        private static async Task<StorageFile> CreateAsync(StorageFolder root, string path)
        {
            var parts = path.Split('/');
            var folder = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Length == 0) continue;
                folder = await folder.CreateFolderAsync(
                    parts[i], CreationCollisionOption.OpenIfExists);
            }
            return await folder.CreateFileAsync(
                parts[parts.Length - 1], CreationCollisionOption.OpenIfExists);
        }
    }
}
