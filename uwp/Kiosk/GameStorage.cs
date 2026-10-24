using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;

namespace Kiosk
{
    /// <summary>One place a game can be downloaded to.</summary>
    public sealed class GamePlace
    {
        /// <summary>"local", "dev", or "usb:E:\" for a removable drive.</summary>
        public string Id;
        public string Name;
        public ulong? Free;
    }

    /// <summary>
    /// Where games live. The app's own storage fills first on a console, so
    /// a game can also go to a USB drive, measured writable through the
    /// removable-storage capability. Every lookup of a downloaded game goes
    /// through here, so a game is found wherever it was put.
    /// </summary>
    public static class GameStorage
    {
        public const string UsbPrefix = "usb:";

        /// <summary>The folder a USB drive keeps its games in.</summary>
        private const string UsbFolder = "Nativra";

        /// <summary>The removable drives, as folders the app can write.</summary>
        public static async Task<List<StorageFolder>> RemovableAsync()
        {
            var drives = new List<StorageFolder>();
            try
            {
                foreach (var device in await KnownFolders.RemovableDevices.GetFoldersAsync())
                    if (!string.IsNullOrEmpty(device.Path)) drives.Add(device);
            }
            catch
            {
                // No capability or no drive: the console's storage is enough.
            }
            return drives;
        }

        /// <summary>Everywhere a download can go, with the room left in each.</summary>
        public static async Task<List<GamePlace>> PlacesAsync()
        {
            var places = new List<GamePlace>
            {
                new GamePlace
                {
                    Id = "local",
                    Name = Texts.Get("game.place.console"),
                    Free = await Steam.SteamDownload.FreeBytesAsync(ApplicationData.Current.LocalFolder),
                },
            };
            foreach (var drive in await RemovableAsync())
            {
                places.Add(new GamePlace
                {
                    Id = UsbPrefix + drive.Path,
                    Name = Texts.Get("game.place.usb", drive.Path.TrimEnd('\\')),
                    Free = await Steam.SteamDownload.FreeBytesAsync(drive),
                });
            }
            return places;
        }

        /// <summary>The place with the most room, which is where a game should go by default.</summary>
        public static GamePlace Roomiest(List<GamePlace> places)
        {
            GamePlace best = null;
            foreach (var place in places)
                if (best == null || (place.Free ?? 0) > (best.Free ?? 0)) best = place;
            return best;
        }

        /// <summary>The "games" folder a place keeps, created when asked to.</summary>
        public static async Task<StorageFolder> GamesFolderAsync(string place, bool create)
        {
            StorageFolder parent;
            if (place != null && place.StartsWith(UsbPrefix, StringComparison.Ordinal))
            {
                // The drive as the removable-devices capability hands it over:
                // a folder reached by path works too, but every file made
                // through it goes to the broker (about 250 ms each, measured)
                // where this one costs about 9.
                var path = place.Substring(UsbPrefix.Length);
                StorageFolder drive = null;
                foreach (var device in await RemovableAsync())
                    if (string.Equals(device.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        drive = device;
                if (drive == null) drive = await StorageFolder.GetFolderFromPathAsync(path);
                parent = create
                    ? await drive.CreateFolderAsync(UsbFolder, CreationCollisionOption.OpenIfExists)
                    : await drive.TryGetItemAsync(UsbFolder) as StorageFolder;
                if (parent == null) return null;
            }
            else if (place == "dev")
            {
                parent = await StorageFolder.GetFolderFromPathAsync(@"D:\DevelopmentFiles");
            }
            else
            {
                parent = ApplicationData.Current.LocalFolder;
            }
            return create
                ? await parent.CreateFolderAsync(Steam.SteamDownload.DefaultFolder, CreationCollisionOption.OpenIfExists)
                : await parent.TryGetItemAsync(Steam.SteamDownload.DefaultFolder) as StorageFolder;
        }

        /// <summary>Every existing "games" folder, each with the place it belongs to.</summary>
        public static async Task<List<KeyValuePair<string, StorageFolder>>> GamesFoldersAsync()
        {
            var found = new List<KeyValuePair<string, StorageFolder>>();
            var ids = new List<string> { "local", "dev" };
            foreach (var drive in await RemovableAsync()) ids.Add(UsbPrefix + drive.Path);
            foreach (var id in ids)
            {
                try
                {
                    var games = await GamesFolderAsync(id, false);
                    if (games != null) found.Add(new KeyValuePair<string, StorageFolder>(id, games));
                }
                catch
                {
                    // A drive pulled out or a share not reachable: skip it.
                }
            }
            return found;
        }

        /// <summary>A game's folder, wherever it was downloaded to, or null.</summary>
        public static async Task<StorageFolder> FindAsync(uint appId)
        {
            foreach (var pair in await GamesFoldersAsync())
                if (await pair.Value.TryGetItemAsync(appId.ToString()) is StorageFolder folder)
                    return folder;
            return null;
        }

        /// <summary>A game whose download finished, wherever it is.</summary>
        public static async Task<bool> IsDownloadedAsync(uint appId)
        {
            foreach (var pair in await GamesFoldersAsync())
                if (await pair.Value.TryGetItemAsync(appId.ToString()) is StorageFolder folder && await IsReadyAsync(folder))
                    return true;
            return false;
        }

        /// <summary>
        /// A verified install with no pending transaction. Legacy markers and
        /// executable presence alone do not establish payload completeness.
        /// </summary>
        public static async Task<bool> IsReadyAsync(StorageFolder folder)
        {
            if (await folder.TryGetItemAsync(".downloading") != null) return false;
            var marker = await folder.TryGetItemAsync(".downloaded") as StorageFile;
            return marker != null && await FileIO.ReadTextAsync(marker) == "verified-v1";
        }

        /// <summary>
        /// LocalState\forget.txt lists app ids, one per line, whose downloaded
        /// files are deleted from every place they may be, then the list itself:
        /// space handed back without the Device Portal, which cannot reach a USB
        /// drive. Steam's copy is untouched; the game downloads again on request.
        /// Returns the ids it removed.
        /// </summary>
        public static async Task<List<uint>> ForgetListedAsync()
        {
            var removed = new List<uint>();
            var list = await ApplicationData.Current.LocalFolder.TryGetItemAsync("forget.txt") as StorageFile;
            if (list == null) return removed;
            foreach (var line in (await FileIO.ReadTextAsync(list)).Split('\n'))
            {
                if (!uint.TryParse(line.Trim(), out var appId)) continue;
                foreach (var place in await PlacesAsync())
                {
                    try
                    {
                        var games = await GamesFolderAsync(place.Id, false);
                        if (games == null) continue;
                        if (await games.TryGetItemAsync(appId.ToString()) is StorageFolder game)
                        {
                            await game.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            if (!removed.Contains(appId)) removed.Add(appId);
                        }
                    }
                    catch
                    {
                        // A place that cannot be reached keeps its copy; the rest still go.
                    }
                }
            }
            await list.DeleteAsync(StorageDeleteOption.PermanentDelete);
            return removed;
        }

        /// <summary>
        /// LocalState\export.txt lists files of installed games, one per line as
        /// "appid/relative/path", copied into LocalState\export\ (flat, with the slashes turned into
        /// underscores) so the Device Portal can pull them off a USB drive it cannot reach. The list
        /// is deleted afterwards; a missing file is skipped.
        /// </summary>
        public static async Task ExportListedAsync()
        {
            var list = await ApplicationData.Current.LocalFolder.TryGetItemAsync("export.txt") as StorageFile;
            if (list == null) return;
            var target = await ApplicationData.Current.LocalFolder.CreateFolderAsync("export", CreationCollisionOption.OpenIfExists);
            foreach (var line in (await FileIO.ReadTextAsync(list)).Split('\n'))
            {
                var path = line.Trim().Replace('\\', '/');
                if (path.Length == 0) continue;
                try
                {
                    var parts = path.Split('/');
                    foreach (var place in await PlacesAsync())
                    {
                        var games = await GamesFolderAsync(place.Id, false);
                        if (games == null) continue;
                        IStorageItem item = games;
                        foreach (var part in parts)
                        {
                            item = (item as StorageFolder) == null ? null : await ((StorageFolder)item).TryGetItemAsync(part);
                            if (item == null) break;
                        }
                        if (item is StorageFile file)
                        {
                            await file.CopyAsync(target, path.Replace('/', '_'), NameCollisionOption.ReplaceExisting);
                            break;
                        }
                    }
                }
                catch
                {
                    // One unreadable file does not stop the rest of the list.
                }
            }
            await list.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }

        /// <summary>Copy into hidden staging, verify, publish, then remove the intact source.</summary>
        public static async Task<StorageFolder> MoveAsync(
            StorageFolder source, string place, Action<ulong> onMoved)
        {
            var games = await GamesFolderAsync(place, true);
            if (await games.TryGetItemAsync(source.Name) != null)
                throw new Steam.DownloadFailure("move-conflict", true, "destination already contains this app; source retained");
            var name = source.Name;
            if (await games.TryGetItemAsync(".move-" + name) is StorageFolder abandoned)
                await abandoned.DeleteAsync(StorageDeleteOption.PermanentDelete);
            var bytes = await TreeBytesAsync(source);
            Steam.DownloadCheckpoint.RequireCapacity(await Steam.SteamDownload.FreeBytesAsync(games), 0, bytes);
            var staging = await games.CreateFolderAsync(".move-" + source.Name, CreationCollisionOption.OpenIfExists);
            ulong moved = 0;
            try
            {
                await CopyTreeAsync(source, staging, count =>
                {
                    moved += count;
                    onMoved?.Invoke(moved);
                });
                // Rename on the same volume is the commit; failures leave source intact.
                await staging.RenameAsync(source.Name, NameCollisionOption.FailIfExists);
            }
            catch (Exception error)
            {
                // Only our hidden staging tree is rolled back. Never delete the source.
                try { await staging.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                if (Steam.DownloadCheckpoint.IsDiskFull(error))
                    throw new Steam.DiskFullException($"move required={bytes} available={(await Steam.SteamDownload.FreeBytesAsync(games))?.ToString() ?? "unknown"} source retained");
                throw;
            }
            await source.DeleteAsync(StorageDeleteOption.PermanentDelete);
            return await games.GetFolderAsync(name);
        }

        private static async Task<ulong> TreeBytesAsync(StorageFolder folder)
        {
            ulong total = 0;
            foreach (var file in await folder.GetFilesAsync())
                total = checked(total + (await file.GetBasicPropertiesAsync()).Size);
            foreach (var child in await folder.GetFoldersAsync()) total = checked(total + await TreeBytesAsync(child));
            return total;
        }

        private static async Task CopyTreeAsync(StorageFolder source, StorageFolder target, Action<ulong> onFile)
        {
            foreach (var file in await source.GetFilesAsync())
            {
                var copy = await target.CreateFileAsync(file.Name, CreationCollisionOption.ReplaceExisting);
                using (var input = await file.OpenStreamForReadAsync())
                using (var output = await copy.OpenStreamForWriteAsync())
                    await Steam.DownloadCheckpoint.CopyVerifiedAsync(input, output);
                onFile((await file.GetBasicPropertiesAsync()).Size);
            }
            foreach (var child in await source.GetFoldersAsync())
            {
                var into = await target.CreateFolderAsync(child.Name, CreationCollisionOption.OpenIfExists);
                await CopyTreeAsync(child, into, onFile);
            }
        }

        private static readonly Regex OtherRenderer = new Regex(
            @"vk$|vulkan|opengl|_gl$", RegexOptions.IgnoreCase);

        private static readonly Regex NotTheGame = new Regex(
            @"^unity|crash|unins|setup|redist|dxsetup|vc_?redist|bugreport|cefprocess|launcher|helper",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// The game's own x64 program, and the folder it lives in. Engines put
        /// it in different places: the root (Unity, TT Games), x64 (Hades),
        /// Binaries\Win64 behind a small launcher (Unreal). A 32-bit build
        /// beside a 64-bit one is skipped, and crash reporters and installers
        /// are never the game.
        /// </summary>
        public static async Task<Tuple<StorageFolder, StorageFile>> ExecutableAsync(StorageFolder root)
        {
            var level = new List<StorageFolder> { root };
            // A game with no x64 program still names its 32-bit one, so the
            // player is told the architecture rather than "nothing found".
            Tuple<StorageFolder, StorageFile> other = null;
            for (var depth = 0; depth <= 3 && level.Count > 0; depth++)
            {
                Tuple<StorageFolder, StorageFile> best = null;
                ulong bestSize = 0;
                var next = new List<StorageFolder>();
                foreach (var folder in level)
                {
                    IReadOnlyList<StorageFile> files;
                    try
                    {
                        files = await folder.GetFilesAsync();
                        foreach (var sub in await folder.GetFoldersAsync())
                            if (!sub.Name.StartsWith(".", StringComparison.Ordinal) &&
                                !sub.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) next.Add(sub);
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (var file in files)
                    {
                        if (!file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (NotTheGame.IsMatch(file.Name)) continue;
                        if (!await IsX64Async(file))
                        {
                            if (other == null) other = Tuple.Create(folder, file);
                            continue;
                        }
                        // Unreal's real program; the root one only starts it.
                        if (file.Name.IndexOf("-Shipping", StringComparison.OrdinalIgnoreCase) >= 0)
                            return Tuple.Create(folder, file);
                        // The Vulkan or OpenGL build beside the Direct3D one
                        // is only a fallback: the console speaks Direct3D.
                        var size = OtherRenderer.IsMatch(folder.Name)
                            ? 1UL
                            : (await file.GetBasicPropertiesAsync()).Size;
                        if (best == null || size > bestSize)
                        {
                            best = Tuple.Create(folder, file);
                            bestSize = size;
                        }
                    }
                }
                // An Unreal launcher at the root is small and x64 too; a
                // Shipping build deeper down wins over it.
                if (best != null && !(depth == 0 && await HasShippingAsync(next))) return best;
                level = next;
            }
            return other;
        }

        private static async Task<bool> HasShippingAsync(List<StorageFolder> folders)
        {
            foreach (var folder in folders)
            {
                try
                {
                    var binaries = folder.Name == "Binaries" ? folder : await folder.TryGetItemAsync("Binaries") as StorageFolder;
                    if (binaries == null) continue;
                    if (await binaries.TryGetItemAsync("Win64") is StorageFolder win64)
                        foreach (var file in await win64.GetFilesAsync())
                            if (file.Name.IndexOf("-Shipping", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                catch
                {
                }
            }
            return false;
        }

        /// <summary>Reads the PE header: machine 0x8664 is x64.</summary>
        public static async Task<bool> IsX64Async(StorageFile file)
        {
            try
            {
                using (var stream = await file.OpenStreamForReadAsync())
                {
                    var header = new byte[1024];
                    var read = await stream.ReadAsync(header, 0, header.Length);
                    if (read < 64 || header[0] != 'M' || header[1] != 'Z') return false;
                    var pe = BitConverter.ToInt32(header, 0x3C);
                    if (pe < 0 || pe + 6 > read) return false;
                    return BitConverter.ToUInt16(header, pe + 4) == 0x8664;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
