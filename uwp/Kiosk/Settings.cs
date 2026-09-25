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
        public static bool DesktopInput { get; private set; } = true;
        public static double PointerSensitivity { get; private set; } = 1;
        public static bool ShowDiagnostics { get; private set; } = true;

        /// <summary>Shows the controller-mode hint for a few seconds when a game starts.</summary>
        public static bool ShowInputHint { get; private set; } = true;

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
                DesktopInput = root.GetNamedBoolean("desktopInput", true);
                var sensitivity = root.GetNamedNumber("pointerSensitivity", 1);
                PointerSensitivity = double.IsNaN(sensitivity) || double.IsInfinity(sensitivity)
                    ? 1 : Math.Max(0.25, Math.Min(2, sensitivity));
                ShowDiagnostics = root.GetNamedBoolean("showDiagnostics", true);
                ShowInputHint = root.GetNamedBoolean("showInputHint", true);
            }
            catch
            {
                // The defaults are the honest fallback.
            }
        }

        public static async Task SetDownloadRootAsync(string value)
        {
            DownloadRoot = value;
            await SaveAsync();
        }

        public static async Task SetInputAsync(bool desktop, double sensitivity, bool diagnostics, bool inputHint)
        {
            DesktopInput = desktop;
            PointerSensitivity = Math.Max(0.25, Math.Min(2, sensitivity));
            ShowDiagnostics = diagnostics;
            ShowInputHint = inputHint;
            await SaveAsync();
        }

        private static async Task SaveAsync()
        {
            try
            {
                var root = new JsonObject
                {
                    { "downloadRoot", JsonValue.CreateStringValue(DownloadRoot) },
                    { "desktopInput", JsonValue.CreateBooleanValue(DesktopInput) },
                    { "pointerSensitivity", JsonValue.CreateNumberValue(PointerSensitivity) },
                    { "showDiagnostics", JsonValue.CreateBooleanValue(ShowDiagnostics) },
                    { "showInputHint", JsonValue.CreateBooleanValue(ShowInputHint) },
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
