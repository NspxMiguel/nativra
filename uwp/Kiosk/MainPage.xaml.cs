using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Kiosk
{
    /// <summary>
    /// One installed app. The console refuses to let a sideloaded app enumerate
    /// other packages, so this list is written into our own folder from the Mac
    /// (xbdev sync) and read from there.
    /// </summary>
    public sealed class Tile
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Initial { get; set; }
        public string Protocol { get; set; }

        /// <summary>A screen inside this app rather than another package.</summary>
        public string Route { get; set; }

        /// <summary>Identity for launching through the console's own portal.</summary>
        public string PackageFullName { get; set; }
        public string AppId { get; set; }

        public ImageSource Icon { get; set; }
        public Visibility IconShown =>
            Icon == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility LetterShown =>
            Icon == null ? Visibility.Visible : Visibility.Collapsed;

        public SolidColorBrush Accent { get; set; }
        public double Dimmed => 1.0;
    }

    public sealed partial class MainPage : Page
    {
        public ObservableCollection<Tile> Tiles { get; } = new ObservableCollection<Tile>();

        // One colour per tile is identity here, not decoration: without the
        // package query there is no app artwork to show.
        private static readonly Color[] Accents =
        {
            Color.FromArgb(255, 59, 224, 129),
            Color.FromArgb(255, 86, 168, 245),
            Color.FromArgb(255, 242, 201, 76),
            Color.FromArgb(255, 235, 110, 125),
            Color.FromArgb(255, 155, 124, 245),
            Color.FromArgb(255, 88, 211, 199),
        };

        private DispatcherTimer clock;
        private ConsolePortal portal;
        private bool portalReady;

        public MainPage()
        {
            InitializeComponent();
            ApplyStaticText();
            StartClock();
            Loaded += async (s, e) => await LoadAppsAsync();
        }

        private void ApplyStaticText()
        {
            HintOpenText.Text = Texts.Get("hint.open");
            HintRefreshText.Text = Texts.Get("hint.refresh");
            try
            {
                MachineText.Text = new EasClientDeviceInformation().FriendlyName;
            }
            catch
            {
                MachineText.Text = Texts.Get("app.eyebrow");
            }
        }

        private void StartClock()
        {
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            clock.Tick += (s, e) => ClockText.Text = DateTime.Now.ToString("HH:mm");
            ClockText.Text = DateTime.Now.ToString("HH:mm");
            clock.Start();
        }

        private async Task LoadAppsAsync()
        {
            StatusText.Text = Texts.Get("status.reading");
            await Settings.LoadAsync();
            Tiles.Clear();

            // Steam is part of this app, not a package on the console, so it is
            // always on the rail — signed in or not.
            Tiles.Add(new Tile
            {
                Title = Texts.Get("tile.steam"),
                Subtitle = Texts.Get("tile.steam.sub"),
                Route = "steam",
                Initial = "S",
                Icon = new BitmapImage(new Uri("ms-appx:///Assets/Steam.png")),
                Accent = new SolidColorBrush(Accents[0]),
            });

            // Games pulled from Steam sit beside the emulators: he asked for one
            // home screen, not two places to look.
            foreach (var game in await ReadGamesAsync())
            {
                Tiles.Add(game);
            }

            var apps = await ReadListAsync();
            var index = 1;
            foreach (var app in apps ?? new List<Tile>())
            {
                app.Accent = new SolidColorBrush(Accents[index++ % Accents.Length]);
                Tiles.Add(app);
            }

            // Asking the console's own portal is what reaches the apps that
            // register no protocol; without it they can only open from Dev Home.
            CountText.Text = Texts.Get("status.count", Tiles.Count);
            StatusText.Text = string.Empty;

            if (Tiles.Count > 0)
            {
                AppRail.UpdateLayout();
                AppRail.SelectedIndex = 0;
                (AppRail.ContainerFromIndex(0) as Control)?.Focus(FocusState.Programmatic);
            }

            // A test harness: a file naming an app id makes the console fetch
            // that game itself. It is how a build gets something to load
            // without a hundred megabytes crossing the network by hand.
            await AutoDownloadAsync();

            // The measurements run after the screen is usable, never before.
            portal = await ConsolePortal.LoadAsync();
            portalReady = portal != null && await portal.ProbeAsync() != null;
            await RecordProbeAsync();
            // Taken here because it cannot be taken anywhere else: a CoreWindow
            // belongs to the thread that owns it, and the game runs on another.
            // It is the surface a PC game's frames will end up on.
            try
            {
                Native.GraphicsBridge.ConsoleWindow =
                    System.Runtime.InteropServices.Marshal.GetIUnknownForObject(
                        Windows.UI.Core.CoreWindow.GetForCurrentThread());
            }
            catch
            {
                // Without it the bridge says so rather than guessing.
            }

            await Native.NativeProbe.RunAsync();
        }

        /// <summary>
        /// Whether this app can reach the Device Portal decides whether it can
        /// launch and install packages by itself, so the answer is written down
        /// rather than left on screen: xbdev pull reads it back.
        /// </summary>
        private async Task RecordProbeAsync()
        {
            var lines = new List<string>
            {
                "at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "portal.json=" + (portal != null),
                "reachable=" + portalReady,
            };
            if (portal != null)
            {
                lines.Add("name=" + (await portal.ProbeAsync() ?? "-"));
                lines.Add("lastError=" + (portal.LastError ?? "-"));
                if (portalReady)
                {
                    lines.Add("packages=" + (await portal.PackagesAsync()).Count);
                }
            }
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "portal-probe.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(file, lines);
            }
            catch
            {
                // The probe is diagnostics; failing to write it changes nothing.
            }
        }

        /// <summary>Reads apps.json, which xbdev sync drops into LocalState.</summary>
        private static async Task<List<Tile>> ReadListAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync("apps.json")
                    as StorageFile;
                if (file == null) return null;

                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return null;

                var list = new List<Tile>();
                foreach (var value in root.GetNamedArray("apps"))
                {
                    var item = value.GetObject();
                    var title = item.GetNamedString("title", string.Empty);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    list.Add(new Tile
                    {
                        Title = title,
                        Subtitle = item.GetNamedString("subtitle", string.Empty),
                        Protocol = Text(item, "protocol"),
                        PackageFullName = Text(item, "packageFullName"),
                        AppId = Text(item, "appId"),
                        Icon = Artwork(Text(item, "icon")),
                        Initial = title.Substring(0, 1).ToUpperInvariant(),
                    });
                }
                return list;
            }
            catch
            {
                return null;
            }
        }

        private async Task AutoDownloadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync("autodownload.txt") as StorageFile;
                if (file == null) return;

                var text = (await FileIO.ReadTextAsync(file)).Trim();
                if (!uint.TryParse(text, out var appId)) return;

                var games = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync("games") as StorageFolder;
                if (games != null && await games.TryGetItemAsync(appId.ToString()) != null)
                {
                    return;
                }

                var session = await SteamSession.LoadAsync();
                if (!session.IsSignedIn) return;

                StatusText.Text = Texts.Get("steam.starting", text);
                await Steam.SteamDownload.RunAsync(session, appId, "local", progress =>
                {
                    var _ = Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Low,
                        () => StatusText.Text = Texts.Get(
                            "steam.downloading", text, progress.Percent, progress.File));
                });
                StatusText.Text = Texts.Get("steam.downloaded", text);
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("steam.downloadfailed", "auto", error.Message);
            }
        }

        /// <summary>Reads games.json: what has been downloaded from Steam.</summary>
        private static async Task<List<Tile>> ReadGamesAsync()
        {
            var list = new List<Tile>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync("games.json") as StorageFile;
                if (file == null) return list;

                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return list;

                foreach (var value in root.GetNamedArray("games"))
                {
                    var item = value.GetObject();
                    var title = item.GetNamedString("name", string.Empty);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var appId = (uint)item.GetNamedNumber("appid", 0);
                    list.Add(new Tile
                    {
                        Title = title,
                        Subtitle = Texts.Get("tile.game.sub"),
                        Route = "game:" + appId,
                        Initial = title.Substring(0, 1).ToUpperInvariant(),
                        Accent = new SolidColorBrush(Accents[(int)(appId % 6)]),
                        Icon = Artwork(
                            "https://cdn.cloudflare.steamstatic.com/steam/apps/"
                            + appId + "/header.jpg"),
                    });
                }
            }
            catch
            {
                // No list means nothing downloaded yet.
            }
            return list;
        }

        /// <summary>A JSON string field, or null when it is absent or null.</summary>
        private static string Text(JsonObject item, string key)
        {
            if (!item.ContainsKey(key)) return null;
            return item[key].ValueType == JsonValueType.String
                ? item.GetNamedString(key)
                : null;
        }

        /// <summary>
        /// An icon that rides with the app list, or a game's own artwork on the
        /// web. Prefixing an absolute address with the local scheme is what
        /// left the downloaded games as empty tiles.
        /// </summary>
        private static ImageSource Artwork(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                var uri = name.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(name)
                    : new Uri("ms-appdata:///local/" + name);
                return new BitmapImage(uri);
            }
            catch
            {
                return null;
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(AppRail.SelectedItem is Tile tile)) return;
            NameText.Text = tile.Title;
            SubText.Text = tile.Subtitle;
            var reachable = portalReady || !string.IsNullOrEmpty(tile.Protocol)
                || !string.IsNullOrEmpty(tile.Route);
            StatusText.Text = reachable ? string.Empty : Texts.Get("status.noprotocol");
        }

        private async void OnTileInvoked(object sender, ItemClickEventArgs e)
        {
            await LaunchAsync(e.ClickedItem as Tile);
        }

        /// <summary>
        /// Launching by URI is the one route the console leaves open to a
        /// sideloaded app; packages that register no protocol have to be opened
        /// from Dev Home.
        /// </summary>
        private async Task LaunchAsync(Tile tile)
        {
            if (tile == null) return;
            if (tile.Route == "steam")
            {
                Frame.Navigate(typeof(SteamPage));
                return;
            }
            if (tile.Route != null && tile.Route.StartsWith("game:", StringComparison.Ordinal))
            {
                StatusText.Text = Texts.Get("status.gamenotyet", tile.Title);
                return;
            }
            StatusText.Text = Texts.Get("status.opening", tile.Title);

            if (portalReady && !string.IsNullOrEmpty(tile.PackageFullName))
            {
                if (await portal.LaunchAsync(tile.PackageFullName, tile.AppId)) return;
            }

            if (string.IsNullOrEmpty(tile.Protocol))
            {
                StatusText.Text = Texts.Get("status.noprotocol");
                return;
            }

            try
            {
                var opened = await Launcher.LaunchUriAsync(new Uri(tile.Protocol + ":"));
                if (!opened) StatusText.Text = Texts.Get("status.refused", tile.Title);
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("status.failed", tile.Title, error.Message);
            }
        }

        private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.GamepadA:
                case Windows.System.VirtualKey.Enter:
                    await LaunchAsync(AppRail.SelectedItem as Tile);
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.GamepadY:
                case Windows.System.VirtualKey.F5:
                    await LoadAppsAsync();
                    e.Handled = true;
                    break;
            }
        }
    }
}
