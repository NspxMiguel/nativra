using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Kiosk
{
    /// <summary>
    /// The handful of choices that outlive a session. Steam lets you pick where
    /// a game lands, and so does this: "local" is the app's own storage, "dev"
    /// is the developer share, which is where a real drive would show up.
    /// </summary>
    public static class Settings
    {
        private const string FileName = "settings.json";

        public static string DownloadRoot { get; private set; } = "local";

        public static async Task LoadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync(FileName) as StorageFile;
                if (file == null) return;
                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return;
                DownloadRoot = root.GetNamedString("downloadRoot", "local");
            }
            catch
            {
                // The defaults are the honest fallback.
            }
        }

        public static async Task SetDownloadRootAsync(string value)
        {
            DownloadRoot = value;
            try
            {
                var root = new JsonObject
                {
                    { "downloadRoot", JsonValue.CreateStringValue(value) },
                };
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, root.Stringify());
            }
            catch
            {
                // Not persisting the choice still leaves it applied this run.
            }
        }
    }
}
