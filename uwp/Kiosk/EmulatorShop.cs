using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.Web.Http;

namespace Kiosk
{
    /// <summary>One package of the catalogue, as the emulator screen offers it.</summary>
    public sealed class CatalogItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Slug { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Note { get; set; }
        public string Url { get; set; }

        private string status = string.Empty;
        private double percent;
        private bool busy;

        /// <summary>What is happening to it, in a line; empty when nothing is.</summary>
        public string Status
        {
            get => status;
            set { status = value; Changed(nameof(Status)); }
        }

        public double Percent
        {
            get => percent;
            set { percent = value; Changed(nameof(Percent)); }
        }

        public bool Busy
        {
            get => busy;
            set
            {
                busy = value;
                Changed(nameof(Busy));
                Changed(nameof(BarShown));
                Changed(nameof(CanInstall));
            }
        }

        public string InstallLabel => Texts.Get("shop.install");
        public Visibility BarShown => busy ? Visibility.Visible : Visibility.Collapsed;
        public bool CanInstall => !busy;

        private void Changed(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Installs emulators and apps from the catalogue on the console itself.
    ///
    /// The catalogue (catalog-console.json, pushed by xbdev sync) lists signed
    /// packages on public release pages. The app downloads one straight to a
    /// file, unpacks it when it comes as a zip with its dependencies, and hands
    /// it to the package manager — the same route the app's own updates take,
    /// since the Device Portal is out of an app's reach on its own console.
    /// </summary>
    public static class EmulatorShop
    {
        private const string CatalogFile = "catalog-console.json";

        /// <summary>The catalogue's entries that are not installed yet.</summary>
        public static async Task<List<CatalogItem>> AvailableAsync()
        {
            var items = new List<CatalogItem>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(CatalogFile) as StorageFile;
                if (file == null) return items;
                if (!JsonObject.TryParse(await FileIO.ReadTextAsync(file), out var root)) return items;
                foreach (var value in root.GetNamedArray("packages", new JsonArray()))
                {
                    var entry = value.GetObject();
                    if (entry.GetNamedBoolean("installed", false)) continue;
                    var url = entry.GetNamedString("url", string.Empty);
                    if (string.IsNullOrEmpty(url)) continue;
                    items.Add(new CatalogItem
                    {
                        Slug = entry.GetNamedString("slug", string.Empty),
                        Name = entry.GetNamedString("name", string.Empty),
                        Kind = entry.GetNamedString("kind", string.Empty),
                        Note = entry.GetNamedString("note", string.Empty),
                        Url = url,
                    });
                }
            }
            catch
            {
                // No catalogue, or one this version cannot read: nothing to offer.
            }
            return items;
        }

        /// <summary>Downloads and installs one entry; null when it worked, otherwise why not.</summary>
        public static async Task<string> InstallAsync(CatalogItem item)
        {
            item.Busy = true;
            var work = Path.Combine(ApplicationData.Current.TemporaryFolder.Path, "install-" + item.Slug);
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(work);

                var name = Path.GetFileName(new Uri(item.Url).AbsolutePath);
                var download = Path.Combine(work, name);
                item.Status = Texts.Get("shop.downloading");
                await DownloadAsync(new Uri(item.Url), download, item);

                string main;
                var dependencies = new List<Uri>();
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    item.Status = Texts.Get("shop.unpacking");
                    item.Percent = 0;
                    var unpacked = Path.Combine(work, "unpacked");
                    await Task.Run(() => ZipFile.ExtractToDirectory(download, unpacked));
                    File.Delete(download);
                    main = Pick(unpacked, dependencies);
                    if (main == null) return Texts.Get("shop.nopackage");
                }
                else
                {
                    main = download;
                }

                // Staged first, then registered: on the console a single
                // AddPackageAsync stopped at about 95% and never returned, and
                // left a registration the system would not launch. Two steps,
                // each logged, show which half is the one that stops.
                item.Status = Texts.Get("shop.installing");
                var manager = new PackageManager();
                Log(item.Slug + ": " + Path.GetFileName(main) + " with " + dependencies.Count + " dependencies"
                    + (dependencies.Count > 0 ? ": " + string.Join(", ", dependencies.Select(d => Path.GetFileName(d.LocalPath))) : string.Empty));
                var failed = await StepAsync(item.Slug + " stage",
                    manager.StagePackageAsync(new Uri(main), dependencies), item, 0, 60);
                if (failed != null) return failed;
                failed = await StepAsync(item.Slug + " register",
                    manager.AddPackageAsync(new Uri(main), dependencies, DeploymentOptions.None), item, 60, 40);
                if (failed != null) return failed;
                await MarkInstalledAsync(item.Slug);
                return null;
            }
            catch (Exception error)
            {
                Log(item.Slug + " failed: " + error.GetType().Name + " 0x" + error.HResult.ToString("X8") + " " + error.Message);
                return error.GetType().Name + " 0x" + error.HResult.ToString("X8") + " " + error.Message;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(work)) Directory.Delete(work, true);
                }
                catch
                {
                    // Temporary storage: the system clears what is left.
                }
                item.Busy = false;
            }
        }

        /// <summary>
        /// Awaits one deployment step: every state and percent goes to
        /// shop-log.txt, and two minutes without progress ends the wait with
        /// the last one seen instead of leaving the card spinning.
        /// </summary>
        private static async Task<string> StepAsync(string what,
            Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation,
            CatalogItem item, double from, double span)
        {
            var lastChange = DateTime.UtcNow;
            var lastSeen = "queued";
            operation.Progress = (info, progress) =>
            {
                var seen = progress.state + " " + progress.percentage + "%";
                if (seen != lastSeen)
                {
                    lastSeen = seen;
                    lastChange = DateTime.UtcNow;
                    Log(what + " " + seen);
                }
                Ui(() => item.Percent = from + span * progress.percentage / 100.0);
            };
            var done = operation.AsTask();
            while (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(10))) != done)
            {
                if (DateTime.UtcNow - lastChange < TimeSpan.FromMinutes(2)) continue;
                Log(what + " stuck at " + lastSeen + " for 2 minutes, operation " + operation.Status);
                return Texts.Get("shop.stuck", lastSeen);
            }
            DeploymentResult result;
            try
            {
                result = await done;
            }
            catch (Exception error)
            {
                // A refused deployment arrives as an exception, not a result.
                Log(what + " failed: " + error.GetType().Name + " 0x" + error.HResult.ToString("X8") + " " + error.Message);
                return "0x" + error.HResult.ToString("X8") + " " + error.Message;
            }
            var code = result.ExtendedErrorCode != null ? result.ExtendedErrorCode.HResult : 0;
            Log(what + " done: registered=" + result.IsRegistered + " 0x" + code.ToString("X8") + " " + result.ErrorText);
            return code == 0 ? null : "0x" + code.ToString("X8") + " " + result.ErrorText;
        }

        private static readonly object logGate = new object();
        private static Task logTail = Task.CompletedTask;

        /// <summary>Appends a timestamped line to LocalState\shop-log.txt, readable from the Mac.</summary>
        private static void Log(string line)
        {
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + "\r\n";
            lock (logGate)
            {
                logTail = logTail.ContinueWith(async _ =>
                {
                    try
                    {
                        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                            "shop-log.txt", CreationCollisionOption.OpenIfExists);
                        await FileIO.AppendTextAsync(file, text);
                    }
                    catch
                    {
                        // Diagnostics only.
                    }
                }).Unwrap();
            }
        }

        private static Windows.UI.Core.CoreDispatcher dispatcher;

        /// <summary>The page's dispatcher, for progress raised on worker threads.</summary>
        public static void Attach(Windows.UI.Core.CoreDispatcher ui) => dispatcher = ui;

        private static void Ui(Action action)
        {
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                action();
                return;
            }
            var _ = dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => action());
        }

        /// <summary>Streams the file to disk, so a large package never sits in memory.</summary>
        private static async Task DownloadAsync(Uri from, string to, CatalogItem item)
        {
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Nativra-shop");
                using (var response = await http.GetAsync(from, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 0;
                    using (var input = (await response.Content.ReadAsInputStreamAsync()).AsStreamForRead())
                    using (var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        var buffer = new byte[1 << 20];
                        ulong done = 0;
                        var shown = -1;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read);
                            done += (ulong)read;
                            if (total == 0) continue;
                            var now = (int)(done * 100 / total);
                            if (now == shown) continue;
                            shown = now;
                            Ui(() => item.Percent = now);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The package to install out of an unpacked zip, filling in the
        /// dependencies beside it. Release zips carry the app plus a
        /// Dependencies folder with one subfolder per architecture; only the
        /// x64 ones apply to a Series console.
        /// </summary>
        private static string Pick(string folder, List<Uri> dependencies)
        {
            var packages = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(IsPackage)
                .ToList();
            string main = null;
            long mainSize = -1;
            foreach (var path in packages)
            {
                var file = Path.GetFileName(path);
                var inDependencies = path.IndexOf(Path.DirectorySeparatorChar + "Dependencies" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) >= 0;
                if (inDependencies || IsFramework(file))
                {
                    if (IsOtherArchitecture(path)) continue;
                    dependencies.Add(new Uri(path));
                    continue;
                }
                var size = new FileInfo(path).Length;
                if (size > mainSize)
                {
                    main = path;
                    mainSize = size;
                }
            }
            return main;
        }

        private static bool IsPackage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".msixbundle" || ext == ".appxbundle" || ext == ".msix" || ext == ".appx";
        }

        private static bool IsFramework(string file) =>
            file.StartsWith("Microsoft.VCLibs", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("Microsoft.NET.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase);

        private static bool IsOtherArchitecture(string path)
        {
            var lower = path.ToLowerInvariant();
            foreach (var arch in new[] { "x86", "arm64", "arm" })
            {
                if (lower.Contains(Path.DirectorySeparatorChar + arch + Path.DirectorySeparatorChar)) return true;
                if (lower.Contains("." + arch + ".") || lower.Contains("_" + arch + "_") || lower.Contains("_" + arch + ".")) return true;
            }
            return false;
        }

        /// <summary>Records the install in the catalogue, so the entry leaves the list.</summary>
        private static async Task MarkInstalledAsync(string slug)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(CatalogFile) as StorageFile;
                if (file == null) return;
                if (!JsonObject.TryParse(await FileIO.ReadTextAsync(file), out var root)) return;
                foreach (var value in root.GetNamedArray("packages", new JsonArray()))
                {
                    var entry = value.GetObject();
                    if (entry.GetNamedString("slug", string.Empty) == slug)
                        entry.SetNamedValue("installed", JsonValue.CreateBooleanValue(true));
                }
                await FileIO.WriteTextAsync(file, root.Stringify());
            }
            catch
            {
                // The package is installed either way; the list catches up at the next sync.
            }
        }
    }
}
