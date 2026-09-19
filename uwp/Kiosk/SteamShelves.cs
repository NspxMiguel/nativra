using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Kiosk
{
    /// <summary>
    /// One row of the sidebar: his own Steam collections, plus the two the app
    /// adds — everything, and what the family shares.
    /// </summary>
    public sealed class Shelf
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public HashSet<uint> Ids { get; set; } = new HashSet<uint>();

        /// <summary>Everything the account can see, minus what he hid.</summary>
        public bool IsAll { get; set; }
        public bool IsFamily { get; set; }
        public bool IsHidden { get; set; }

        public int Count { get; set; }
        public string Label => Count > 0 ? $"{Name}  {Count}" : Name;
    }

    /// <summary>
    /// Reads shelf.json, which carries the collections straight out of his Steam
    /// account. They live in the account's cloud config store, which only the
    /// client connection serves, so the Mac fetches them and drops them here.
    /// </summary>
    public static class SteamShelves
    {
        public static async Task<List<Shelf>> LoadAsync()
        {
            var shelves = new List<Shelf>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync("shelf.json") as StorageFile;
                if (file == null) return shelves;

                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return shelves;

                foreach (var value in root.GetNamedArray("collections"))
                {
                    var item = value.GetObject();
                    var id = item.GetNamedString("id", string.Empty);
                    if (string.IsNullOrEmpty(id)) continue;

                    var shelf = new Shelf
                    {
                        Id = id,
                        Name = item.GetNamedString("name", id),
                        IsHidden = id == "hidden",
                    };
                    if (item.ContainsKey("added"))
                    {
                        foreach (var appid in item.GetNamedArray("added"))
                        {
                            shelf.Ids.Add((uint)appid.GetNumber());
                        }
                    }
                    shelves.Add(shelf);
                }
            }
            catch
            {
                // No file means no collections, which is a plain library.
            }
            return shelves;
        }
    }
}
