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

        private static string note = "not checked";
        private static readonly object logGate = new object();
        private static Task logTail = Task.CompletedTask;

        /// <summary>
        /// What happened on the last check, for diagnostics. Every change is
        /// also appended to update-log.txt: all the screen shows of a failed
        /// install is that it failed, and the reason — the portal's answer —
        /// used to stay in memory where nobody could read it.
        /// </summary>
        public static string Note
        {
            get => note;
            set
            {
                note = value;
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " build " + Installed + ": " + value + "\r\n";
                lock (logGate) logTail = logTail.ContinueWith(_ => AppendAsync(line)).Unwrap();
            }
        }

        private static async Task AppendAsync(string line)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "update-log.txt", CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file, line);
            }
            catch
            {
                // Diagnostics only: a log that cannot be written changes nothing.
            }
        }

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
                Note = "installing build " + newest + " through the package manager";
                var refused = await InstallAsync(file);
                if (refused == null)
                {
                    Note = "build " + newest + " registered by the package manager";
                    return;
                }

                // The portal is the old route and is kept only as a fallback:
                // from inside the app it has never connected (see InstallAsync).
                var portal = await ConsolePortal.LoadAsync();
                if (portal != null && await portal.InstallAsync(file)) return;
                Note = "install of build " + newest + " failed: package manager " + refused
                    + (portal != null ? "; portal " + portal.LastError : "; no portal.json");
                status?.Invoke(Texts.Get("update.failed", newest));
            }
            catch (Exception error)
            {
                Note = "check failed: " + error.GetType().Name + " 0x" + error.HResult.ToString("X8") + " " + error.Message;
            }
        }

        /// <summary>
        /// Replaces this package with the downloaded bundle; null when it went
        /// through, otherwise why not. The Device Portal cannot do this from
        /// here: the portal of the console an app runs on is at the console's
        /// own address, which UWP network isolation treats as loopback and
        /// blocks, so every install through it failed with "a connection with
        /// the server could not be established". The package manager is the
        /// platform's own route (packageManagement capability, allowed for a
        /// Dev Mode app); ForceApplicationShutdown lets it replace the running
        /// app, which usually ends this process before the call returns.
        /// </summary>
        private static async Task<string> InstallAsync(StorageFile bundle)
        {
            try
            {
                var manager = new Windows.Management.Deployment.PackageManager();
                var result = await manager.AddPackageAsync(
                    new Uri(bundle.Path), null,
                    Windows.Management.Deployment.DeploymentOptions.ForceApplicationShutdown);
                if (result.IsRegistered) return null;
                var code = result.ExtendedErrorCode != null ? result.ExtendedErrorCode.HResult : 0;
                return "0x" + code.ToString("X8") + " " + result.ErrorText;
            }
            catch (Exception error)
            {
                return error.GetType().Name + " 0x" + error.HResult.ToString("X8") + " " + error.Message;
            }
        }
    }
}
