using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Kiosk
{
    /// <summary>
    /// A game's name, once known, kept — so the shelf shows "Brawlhalla"
    /// forever after the first time it learns that, rather than falling back
    /// to the bare Steam app id whenever the live library fetch that names
    /// come from does not happen to succeed at that exact moment (a session
    /// still renewing, a network hiccup, Steam rate-limiting). The shelf used
    /// to re-ask Steam for names on every refresh with nothing remembered in
    /// between; a single bad refresh showed numbers for games it had named
    /// correctly moments before.
    /// </summary>
    internal static class GameNameCache
    {
        private const string FileName = "game-names.json";
        private static Dictionary<uint, string> names;
        private static bool dirty;

        private static async Task EnsureLoadedAsync()
        {
            if (names != null) return;
            names = new Dictionary<uint, string>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) as StorageFile;
                if (file == null) return;
                if (!JsonObject.TryParse(await FileIO.ReadTextAsync(file), out var root)) return;
                foreach (var pair in root)
                    if (uint.TryParse(pair.Key, out var appId))
                        names[appId] = pair.Value.GetString();
            }
            catch
            {
                // Starts empty; every name gets learned again as it comes up.
            }
        }

        /// <summary>The remembered name, or null when this app id has never been seen.</summary>
        public static async Task<string> GetAsync(uint appId)
        {
            await EnsureLoadedAsync();
            return names.TryGetValue(appId, out var name) ? name : null;
        }

        /// <summary>Remembers a name learned just now; saved the next time <see cref="FlushAsync"/> runs.</summary>
        public static async Task RememberAsync(uint appId, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            await EnsureLoadedAsync();
            if (names.TryGetValue(appId, out var already) && already == name) return;
            names[appId] = name;
            dirty = true;
        }

        /// <summary>Writes the cache to disk when something changed; a no-op otherwise.</summary>
        public static async Task FlushAsync()
        {
            if (!dirty || names == null) return;
            try
            {
                var root = new JsonObject();
                foreach (var pair in names) root[pair.Key.ToString()] = JsonValue.CreateStringValue(pair.Value);
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, root.Stringify());
                dirty = false;
            }
            catch
            {
                // Tried again on the next successful name lookup.
            }
        }
    }
}
