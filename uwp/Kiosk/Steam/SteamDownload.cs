using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace Kiosk.Steam
{
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
            var baseFolder = ApplicationData.Current.LocalFolder;
            if (root == "dev")
            {
                // The developer share is the roomy one; LocalState is the app's
                // own corner and fills up first.
                try
                {
                    baseFolder = await StorageFolder.GetFolderFromPathAsync(@"D:\DevelopmentFiles");
                }
                catch
                {
                    baseFolder = ApplicationData.Current.LocalFolder;
                }
            }
            var games = await baseFolder.CreateFolderAsync(
                DefaultFolder, CreationCollisionOption.OpenIfExists);
            return await games.CreateFolderAsync(
                appId.ToString(), CreationCollisionOption.OpenIfExists);
        }

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

                foreach (var depot in depots)
                {
                    var key = await cm.DepotKeyAsync(appId, depot.Id);
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
                                File = name, Done = done, Total = manifest.TotalBytes,
                            });
                            continue;
                        }

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
                                    File = name, Done = done, Total = manifest.TotalBytes,
                                });
                            }
                        }
                    }
                }
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
