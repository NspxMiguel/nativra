using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Kiosk
{
    public static class CompatibilityCatalog
    {
        private static readonly Dictionary<uint, string> Ratings = new Dictionary<uint, string>();

        public static string Rating(uint appId) => Ratings.TryGetValue(appId, out var rating) ? rating : "untested";

        public static async Task LoadAsync()
        {
            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/compatibility.json"));
                var json = JsonObject.Parse(await FileIO.ReadTextAsync(file));
                Ratings.Clear();
                foreach (var item in json)
                    if (uint.TryParse(item.Key, out var id))
                    {
                        var rating = item.Value.GetString();
                        Ratings[id] = rating == "verified" || rating == "playable" || rating == "not-working" ? rating : "untested";
                    }
            }
            catch
            {
                Ratings.Clear();
            }
        }
    }
}
