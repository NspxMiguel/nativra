using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace Kiosk.Steam
{
    /// <summary>The place a game was going to has no room left for it.</summary>
    public sealed class DiskFullException : Exception
    {
        public DiskFullException(string message) : base(message) { }
    }

    public sealed class DownloadProgress
    {
        public string File { get; set; }
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
            StorageFolder games;
            try
            {
                games = await GameStorage.GamesFolderAsync(root, true);
            }
            catch when (root != "local")
            {
                // A drive pulled out or a share not reachable: the app's own
                // storage still takes the game.
                games = await GameStorage.GamesFolderAsync("local", true);
            }
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
                // Free space is information, not a requirement.
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

        private const int DiskFull = unchecked((int)0x80070070);

        public static async Task RunAsync(
            SteamSession session,
            uint appId,
            string root,
            Action<DownloadProgress> onProgress,
            int parallel = 6)
        {
            using (var cm = new SteamCm())
            {
                var endpoints = await SteamCm.EndpointsAsync();
                Exception last = null;
                var connected = false;
                foreach (var endpoint in endpoints.GetRange(0, Math.Min(5, endpoints.Count)))
                {
                    try
                    {
                        await cm.ConnectAsync(endpoint);
                        await cm.LogOnAsync(session.SteamId, session.RefreshToken);
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

                // A fresh download that cannot fit is refused before the first
                // byte, rather than failing gigabytes in. A resumed one has
                // part of its size on disk already, so only the real write can
                // tell, and a full disk is then reported in the same words.
                long needed = 0;
                foreach (var depot in depots) needed += depot.Size;
                var resuming = await folder.TryGetItemAsync(".downloading") != null;
                var free = await FreeBytesAsync(folder);
                if (!resuming && free.HasValue && needed > 0 && (ulong)needed > free.Value)
                {
                    throw new DiskFullException(Texts.Get("steam.nospace",
                        Human((ulong)needed - free.Value), Human(free.Value)));
                }

                var pending = await folder.CreateFileAsync(".downloading", CreationCollisionOption.ReplaceExisting);

                var licensed = 0;
                try
                {
                    foreach (var depot in depots)
                    {
                        // A depot the account has no license for (a DLC or a
                        // soundtrack it does not own) is refused a key; that is
                        // not the game, so it is left out rather than failing.
                        byte[] key;
                        try
                        {
                            key = await cm.DepotKeyAsync(appId, depot.Id);
                        }
                        catch (Exception) when (depots.Count > 1)
                        {
                            continue;
                        }
                        licensed++;
                        var code = await SteamDepot.ManifestCodeAsync(cm, appId, depot.Id, depot.ManifestId);
                        var manifest = SteamDepot.ParseManifest(
                            await SteamDepot.FetchManifestAsync(servers, depot.Id, depot.ManifestId, code));

                        long done = 0;
                        foreach (var file in manifest.Files)
                        {
                            if (file.IsDirectory) continue;

                            var name = manifest.NamesEncrypted
                                ? SteamDepot.DecryptName(file.Name, key)
                                : file.Name;
                            var target = await CreateAsync(folder, name.Replace('\\', '/'));

                            var existing = await target.GetBasicPropertiesAsync();
                            if ((long)existing.Size == file.Size)
                            {
                                done += file.Size;
                                onProgress?.Invoke(new DownloadProgress
                                {
                                    File = name,
                                    Done = done,
                                    Total = manifest.TotalBytes,
                                });
                                continue;
                            }

                            // Named before its first chunk, so a failure points at
                            // this file rather than at the last one that finished.
                            onProgress?.Invoke(new DownloadProgress
                            {
                                File = name,
                                Done = done,
                                Total = manifest.TotalBytes,
                            });
                            using (var stream = await target.OpenStreamForWriteAsync())
                            {
                                for (var i = 0; i < file.Chunks.Count; i += parallel)
                                {
                                    var batch = new List<Task<KeyValuePair<Chunk, byte[]>>>();
                                    for (var j = i; j < Math.Min(i + parallel, file.Chunks.Count); j++)
                                    {
                                        var chunk = file.Chunks[j];
                                        batch.Add(FetchOneAsync(servers, depot.Id, chunk, key));
                                    }
                                    foreach (var task in batch)
                                    {
                                        var result = await task;
                                        stream.Seek(result.Key.Offset, SeekOrigin.Begin);
                                        await stream.WriteAsync(result.Value, 0, result.Value.Length);
                                        done += result.Value.Length;
                                    }
                                    onProgress?.Invoke(new DownloadProgress
                                    {
                                        File = name,
                                        Done = done,
                                        Total = manifest.TotalBytes,
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception error) when (error.HResult == DiskFull)
                {
                    var left = await FreeBytesAsync(folder);
                    throw new DiskFullException(Texts.Get("steam.diskfull",
                        left.HasValue ? Human(left.Value) : "0 B"));
                }
                if (licensed == 0) throw new Exception("no depot of this app is licensed to this account");
                await folder.CreateFileAsync(".downloaded", CreationCollisionOption.ReplaceExisting);
                await pending.DeleteAsync();
            }
        }

        private static async Task<KeyValuePair<Chunk, byte[]>> FetchOneAsync(
            List<ContentServer> servers, uint depotId, Chunk chunk, byte[] key)
        {
            var raw = await SteamDepot.FetchChunkAsync(servers, depotId, chunk.Sha);
            var plain = SteamDepot.Decompress(SteamDepot.Decrypt(raw, key));
            return new KeyValuePair<Chunk, byte[]>(chunk, plain);
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
