using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;

namespace Kiosk
{
    /// <summary>
    /// Updates the app from its own GitHub releases, on the console.
    ///
    /// Every build is published as kiosk-build-N with the package version
    /// 1.0.N.0, so a higher N is a newer app. The bundle is installed through
    /// the console's Device Portal as an update of the package already there,
    /// and an update keeps the app's storage: the downloaded games and the
    /// Steam sign-in survive it. Checked when the app opens, never mid-game.
    /// </summary>
    public static class Updater
    {
        private const string Releases =
            "https://api.github.com/repos/NspxMiguel/nativra/releases?per_page=10";
        private const string Asset = "nativra-x64.msixbundle";
        private const string TagPrefix = "kiosk-build-";

        private static bool checkedOnce;

        /// <summary>What happened on the last check, for diagnostics.</summary>
        public static string Note = "not checked";

        public static int Installed => Package.Current.Id.Version.Build;

        /// <summary>
        /// Looks for a newer build and installs it. The app closes when the
        /// console replaces it; status receives what to show meanwhile.
        /// </summary>
        public static async Task CheckAsync(Action<string> status)
        {
            if (checkedOnce) return;
            checkedOnce = true;
            try
            {
                // Opt-out for development: builds installed by the cycle must
                // not be replaced behind the measurement.
                if (await ApplicationData.Current.LocalFolder.TryGetItemAsync("noupdate.txt") != null)
                {
                    Note = "disabled by noupdate.txt";
                    return;
                }

                var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Nativra-updater");
                var text = await http.GetStringAsync(new Uri(Releases));
                if (!JsonArray.TryParse(text, out var releases))
                {
                    Note = "release list unreadable";
                    return;
                }

                var newest = Installed;
                string url = null;
                foreach (var value in releases)
                {
                    var release = value.GetObject();
                    if (release.GetNamedBoolean("draft", false)) continue;
                    var tag = release.GetNamedString("tag_name", string.Empty);
                    if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
                    if (!int.TryParse(tag.Substring(TagPrefix.Length), out var build) || build <= newest) continue;
                    foreach (var assetValue in release.GetNamedArray("assets", new JsonArray()))
                    {
                        var asset = assetValue.GetObject();
                        if (asset.GetNamedString("name", string.Empty) != Asset) continue;
                        newest = build;
                        url = asset.GetNamedString("browser_download_url", string.Empty);
                    }
                }
                if (url == null)
                {
                    Note = "up to date at build " + Installed;
                    return;
                }

                var portal = await ConsolePortal.LoadAsync();
                if (portal == null)
                {
                    Note = "build " + newest + " available; no Device Portal access";
                    return;
                }

                status?.Invoke(Texts.Get("update.downloading", newest));
                var bytes = await http.GetBufferAsync(new Uri(url));
                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    Asset, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBufferAsync(file, bytes);

                // Installing restarts the app, which would cut a game download
                // or a game in play; the update waits for both to be over.
                while (DownloadManager.Active() != null || Native.NativeProbe.GameRunning)
                {
                    Note = "build " + newest + " waits for downloads and games to finish";
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }

                status?.Invoke(Texts.Get("update.installing", newest));
                Note = "installing build " + newest;
                if (!await portal.InstallAsync(file))
                {
                    Note = "install of build " + newest + " failed: " + portal.LastError;
                    status?.Invoke(Texts.Get("update.failed", newest));
                }
            }
            catch (Exception error)
            {
                Note = "check failed: " + error.GetType().Name;
            }
        }
    }
}
