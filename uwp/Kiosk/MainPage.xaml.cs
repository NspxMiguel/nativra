using System;
using System.Linq;
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
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace Kiosk
{
    /// <summary>
    /// One installed app. The console refuses to let a sideloaded app enumerate
    /// other packages, so this list is written into our own folder from the Mac
    /// (xbdev sync) and read from there.
    /// </summary>
    public sealed class Tile : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private int progress = -1;

        /// <summary>Download progress in percent while the game downloads; -1 when it does not.</summary>
        public int Progress
        {
            get => progress;
            set
            {
                if (progress == value) return;
                progress = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Progress)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProgressShown)));
            }
        }

        public Visibility ProgressShown => progress >= 0 ? Visibility.Visible : Visibility.Collapsed;

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

        /// <summary>The game on Steam, when it came from there.</summary>
        public uint SteamAppId { get; set; }

        /// <summary>Whether he has asked for this on the shelf.</summary>
        public bool OnShelf { get; set; }
        public string ShelfAction =>
            OnShelf ? Texts.Get("emu.remove") : Texts.Get("emu.add");
        public Visibility IconShown =>
            Icon == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility LetterShown =>
            Icon == null ? Visibility.Visible : Visibility.Collapsed;

        public SolidColorBrush Accent { get; set; }

        // ---------------------------------------------------------- the shelf

        /// <summary>
        /// Box art. Real art comes from the store; until it has arrived this is
        /// one of six gradients, chosen by the title so a given game always
        /// gets the same one. A shelf of identical empty rectangles reads as a
        /// fault, and a shelf of arbitrary colours reads as noise.
        /// </summary>
        public Brush Art { get; set; }

        /// <summary>
        /// The first tile on the shelf is the one played most recently and is
        /// drawn larger. Everything after it is the same size as everything
        /// else, so the eye has exactly one place to start.
        /// </summary>
        public bool Hero { get; set; }

        // One size for every tile. The one being looked at is grown by the
        // template when it takes focus, so the largest thing on the shelf is
        // always the thing the controller is pointing at — which is the whole
        // job of the large tile, and position cannot do it.
        public double TileWidth => 220;
        public double TileHeight => 330;

        public bool Installed { get; set; } = true;
        public bool Favourite { get; set; }

        /// <summary>Not installed: dimmed, with a cloud in the corner.</summary>
        public double Dimmed => Installed ? 1.0 : 0.6;
        public Visibility CloudShown =>
            Installed ? Visibility.Collapsed : Visibility.Visible;
        public Visibility StarShown =>
            Favourite ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// The tile that ends the shelf and opens everything at once. It is a
        /// tile rather than a button because it lives in the same row and the
        /// same focus order as the games.
        /// </summary>
        public bool IsAllGames { get; set; }
        public Visibility ArtShown =>
            IsAllGames ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AllGamesShown =>
            IsAllGames ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// The six gradients of the handoff, cycled by name so the choice is
        /// stable across restarts rather than by list position, which is not.
        /// </summary>
        public static Brush ArtFor(string title, ResourceDictionary from)
        {
            var sum = 0;
            foreach (var letter in title ?? string.Empty) sum += letter;
            return from["Art" + (sum % 6)] as Brush;
        }
    }

    /// <summary>A download as the Downloads screen shows it: a name, a one-line
    /// state, and a bar with the percent while it is still running.</summary>
    public sealed class DownloadRow
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public double Percent { get; set; }
        public Visibility BarShown { get; set; }
    }

    public sealed partial class MainPage : Page
    {
        public ObservableCollection<Tile> Tiles { get; } = new ObservableCollection<Tile>();

        /// <summary>
        /// The emulators themselves, which are not games.
        ///
        /// They used to sit on the shelf beside Baldur's Gate, and that is the
        /// wrong shape: an emulator is a thing you set up once, not a thing
        /// you play. They live on their own screen now, and a game running
        /// under one reaches the shelf only when he puts it there.
        /// </summary>
        public ObservableCollection<Tile> Emulators { get; } =
            new ObservableCollection<Tile>();

        /// <summary>Catalogue entries not installed yet, offered on the emulator screen.</summary>
        public ObservableCollection<CatalogItem> ShopItems { get; } =
            new ObservableCollection<CatalogItem>();

        /// <summary>Every game on the shelf, laid out as a grid by the All games screen.</summary>
        public ObservableCollection<Tile> AllGames { get; } =
            new ObservableCollection<Tile>();

        /// <summary>The Downloads screen's rows, refreshed while it is open.</summary>
        public ObservableCollection<DownloadRow> Downloads { get; } =
            new ObservableCollection<DownloadRow>();

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
        private uint requestedGame;
        private bool gameLaunchPending;
        private int shownControllerMode = -1;
        private int hintUntil;

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            requestedGame = e.Parameter is uint appId ? appId : 0;
        }

        public MainPage()
        {
            InitializeComponent();
            Native.PadBridge.Watch();
            DownloadManager.Changed += OnDownloadsChanged;
            if (DownloadManager.Active() == null) DownloadManager.ClearStaleMarker();
            InitializeGameHost();
            InitializeDiagnostics();
            ApplyStaticText();
            StartClock();
            Loaded += async (s, e) => await LoadAppsAsync();
            AddHandler(KeyDownEvent, new KeyEventHandler(OnGameKeyDown), true);
            AddHandler(KeyUpEvent, new KeyEventHandler(OnGameKeyUp), true);
            Window.Current.Activated += (s, e) =>
            {
                if (e.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated)
                    Native.PointerBridge.ReleaseHostKeys();
            };
        }

        private void OnGameKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (gameLaunchPending && !Native.NativeProbe.GameRunning)
            {
                e.Handled = true;
                return;
            }
            if (!Native.NativeProbe.GameRunning
                && AllGamesScreen.Visibility == Visibility.Visible
                && (e.OriginalKey == Windows.System.VirtualKey.GamepadB || e.OriginalKey == Windows.System.VirtualKey.Escape))
            {
                OnDockClicked(DockLibrary, null);
                FocusShelf();
                e.Handled = true;
                return;
            }
            if (!Native.NativeProbe.GameRunning) return;
            // XAML maps GamepadA to Space and GamepadB to Escape for UI controls.
            // Hosted games need the original button so A remains a mouse click.
            Native.PointerBridge.HostKey((int)e.OriginalKey, true);
            e.Handled = true;
        }

        private void OnGameKeyUp(object sender, KeyRoutedEventArgs e)
        {
            Native.PointerBridge.HostKey((int)e.OriginalKey, false);
            if (Native.NativeProbe.GameRunning) e.Handled = true;
        }

        private void ApplyStaticText()
        {
            // The console says what it is running, once, in the quietest text
            // on the screen. Useful to know; never the thing being read.
            try
            {
                MachineText.Text = Texts.Get("app.eyebrow");
                GameCreditText.Text = MachineText.Text;
                GameLoadingText.Text = Texts.Get("game.loading");
                GameLoadingHint.Text = Texts.Get("game.loading.hint");
                GameInputHint.Text = Texts.Get("game.input.hint");
                // A gear, the way consoles mark settings; the name stays for
                // narrators and for the tooltip.
                SetupButton.Content = "\uE713";
                SetupButton.FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets");
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(SetupButton, Texts.Get("setup.title"));
                ToolTipService.SetToolTip(SetupButton, Texts.Get("setup.title"));
            }
            catch
            {
                MachineText.Text = Texts.Get("app.eyebrow");
            }
        }

        private void StartClock()
        {
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            clock.Tick += (s, e) =>
            {
                ClockText.Text = DateTime.Now.ToString("HH:mm");

                // Controllers come and go while the app is open, and a charge
                // read once at startup is a charge that is wrong by the
                // evening. Read again on every tick, which is cheap.
                ShowPads();
            };
            ClockText.Text = DateTime.Now.ToString("HH:mm");
            clock.Start();
        }

        private async Task LoadAppsAsync()
        {
            StatusText.Text = Texts.Get("status.reading");
            await Settings.LoadAsync();
            // The invitation to sign in is only for someone who has not.
            try
            {
                SignInLink.Visibility = (await SteamSession.LoadAsync()).IsSignedIn
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            catch
            {
                SignInLink.Visibility = Visibility.Visible;
            }
            Native.ControllerMode.Desktop = Settings.DesktopInput;
            Tiles.Clear();

            // The store is not a game. It has its own place in the menu,
            // and putting it on the shelf as well made the first thing on a
            // screen of games be the one thing there that is not one.

            // Games pulled from Steam sit beside the emulators: he asked for one
            // home screen, not two places to look.
            // On the shelf exactly when it is downloaded: a name in games.json
            // for a game that is not on any drive would open into nothing.
            var shelved = new HashSet<uint>();
            foreach (var game in await ReadGamesAsync())
            {
                if (!await GameStorage.IsDownloadedAsync(game.SteamAppId)) continue;
                Tiles.Add(game);
                shelved.Add(game.SteamAppId);
            }
            // And every game the app downloaded itself, wherever it went (the
            // console, the share, a USB drive): games.json is written from the
            // Mac, so Hades and LEGO, downloaded here, never reached the shelf.
            foreach (var game in await DownloadedTilesAsync(shelved))
            {
                Tiles.Add(game);
            }

            // Installed packages are emulators and tools, not games. They go
            // to their own screen; the shelf keeps what is actually played.
            Emulators.Clear();
            var apps = await ReadListAsync();
            var index = 1;
            var onShelf = await ReadShortcutsAsync();
            foreach (var app in apps ?? new List<Tile>())
            {
                app.Accent = new SolidColorBrush(Accents[index++ % Accents.Length]);
                app.OnShelf = onShelf.Contains(app.Title);
                Emulators.Add(app);
                if (app.OnShelf) Tiles.Add(app);
            }

            // The shelf reads as a shelf, not as a list: the first tile is
            // the one played most recently and is drawn larger, every tile
            // gets its stand-in art, and the row ends with the one tile that
            // opens everything at once.
            var first = true;
            foreach (var tile in Tiles)
            {
                first = false;
                if (tile.Art == null) tile.Art = Tile.ArtFor(tile.Title, Application.Current.Resources);
            }
            Tiles.Add(new Tile
            {
                Title = Texts.Get("tile.allgames", Tiles.Count),
                IsAllGames = true,
                Route = "allgames",
            });

            // How many controllers are in the room. Shown beside the pad in
            // the dock, and only when there is more than one to tell apart.
            ShowPads();
            StatusText.Text = string.Empty;

            // Focus starts on the shelf rather than the dock, because the
            // first thing a person wants is the game they last played.
            AppRail.UpdateLayout();
            FocusShelf();

            // A test harness: a file naming an app id makes the console fetch
            // that game itself. It is how a build gets something to load
            // without a hundred megabytes crossing the network by hand.
            await AutoDownloadAsync();
            await ResumeInterruptedAsync();
            await DriveProbe.RunAsync();
            await MemoryProbe.RunAsync();

            // A newer build on GitHub installs itself as an update, which
            // keeps the games and the Steam sign-in. Only here, on the home
            // screen: never while a game is running.
            var ___ = Updater.CheckAsync(text =>
            {
                var ____ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => StatusText.Text = text);
            });

            // The measurements run after the screen is usable, never before.
            portal = await ConsolePortal.LoadAsync();
            portalReady = portal != null && await portal.ProbeAsync() != null;
            await RecordProbeAsync();
            if (requestedGame != 0)
                await StartGameAsync(requestedGame);
            else if (await ApplicationData.Current.LocalFolder.TryGetItemAsync("autoplay.txt") is StorageFile autoplay)
            {
                // A test harness names the game to start; pressing A on the
                // shelf only ever starts whichever game is first there.
                if (uint.TryParse((await FileIO.ReadTextAsync(autoplay)).Trim(), out var appId) && appId != 0)
                    await StartGameAsync(appId);
                else
                    await Native.NativeProbe.RunAsync();
            }
        }

        private void InitializeGameHost()
        {
            // Bind the host before the shelf accepts input, never after network
            // probes: a fast launch must not fall into the native DXGI fallback.
            // Taken here because it cannot be taken anywhere else: a CoreWindow
            // belongs to the thread that owns it, and the game runs on another.
            // It is the surface a PC game's frames will end up on.
            // Each of these stands on its own. They used to share one try,
            // and a failure in the first — asking the framework for the
            // console's window, which not every host allows — silently skipped
            // the two after it, including the surface the game's frames are
            // shown on. A screen that never appears because of an unrelated
            // failure is the worst kind to look for.
            Native.GraphicsBridge.Mirror = GameImage;
            Native.FrameMirror.Credit = GameCredit;
            Native.FrameMirror.Presented = () =>
            {
                GameLoading.Visibility = Visibility.Collapsed;
                GameLoadingRing.IsActive = false;
                GamePointerTransform.X = Native.PointerBridge.X;
                GamePointerTransform.Y = Native.PointerBridge.Y;
                GamePointer.Visibility = Native.ControllerMode.Desktop ? Visibility.Visible : Visibility.Collapsed;
                RecordingBadge.Visibility = Native.Recorder.Active ? Visibility.Visible : Visibility.Collapsed;
                // The notice shows like a notification: the first 15 seconds of a
                // game, and 5 seconds after each switch, unless turned off.
                var now = Environment.TickCount;
                if (hintUntil == 0) hintUntil = now + 15000;
                GameInputHintCard.Visibility = Settings.ShowInputHint && now - hintUntil < 0
                    ? Visibility.Visible : Visibility.Collapsed;
                if (shownControllerMode != Native.ControllerMode.Changes)
                {
                    if (shownControllerMode >= 0) hintUntil = Math.Max(hintUntil, now + 5000);
                    shownControllerMode = Native.ControllerMode.Changes;
                    GameInputHint.Text = Texts.Get(Native.ControllerMode.Desktop ? "game.mode.pc" : "game.mode.controller");
                    if (Native.ControllerMode.Desktop)
                        GameInputHint.Text += "\n" + Texts.Get("game.input.hint");
                }
            };
            Native.GraphicsBridge.OnUi = Dispatcher;
            Native.ThreadRank.RaiseThisThread();
            Native.Recorder.Watch();
            EmulatorShop.Attach(Dispatcher);

            try
            {
                // No arrow on a TV: the shelf is driven by the pad, and a game
                // that wants a pointer gets the app-drawn GamePointer instead.
                // Hiding the system cursor stops the stray mouse arrow the user
                // saw on top of everything in controller mode.
                var window = Windows.UI.Core.CoreWindow.GetForCurrentThread();
                if (window != null) window.PointerCursor = null;
            }
            catch
            {
                // Not every host lets the cursor go; the pad still drives the shelf.
            }

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

        }

        private async Task StartGameAsync(uint appId)
        {
            if (gameLaunchPending || Native.NativeProbe.GameRunning) return;
            if (setupOpen) return;
            Native.ControllerMode.Desktop = Settings.DesktopInput;
            Native.ControllerMode.Changes++;
            gameLaunchPending = true;
            // The pad's A reaches this page too, and XAML answered it with the
            // console's navigation click over the game's own sound. The game
            // owns the pad from here; the app's sounds stay off while it runs.
            ElementSoundPlayer.State = ElementSoundPlayerState.Off;
            GameLoading.Visibility = Visibility.Visible;
            GameLoadingRing.IsActive = true;
            var started = false;
            try
            {
                await Native.NativeProbe.RunAsync(appId);
                started = true;
            }
            catch (PlatformNotSupportedException)
            {
                StatusText.Text = Texts.Get("game.architecture");
            }
            catch (System.IO.FileNotFoundException)
            {
                StatusText.Text = Texts.Get("game.missing");
            }
            catch (InvalidOperationException)
            {
                StatusText.Text = Texts.Get("game.restart");
            }
            catch (Exception)
            {
                StatusText.Text = Texts.Get("game.launchfailed");
            }
            finally
            {
                gameLaunchPending = false;
                GameLoading.Visibility = Visibility.Collapsed;
                GameLoadingRing.IsActive = false;
                // A game that did not start gives the home screen its sounds back.
                if (!started) ElementSoundPlayer.State = ElementSoundPlayerState.Auto;
            }
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

                // Only a finished game is skipped; a folder left half-way is
                // exactly what the download picks up again.
                if (await GameStorage.IsDownloadedAsync(appId)) return;

                var session = await SteamSession.LoadAsync();
                if (!session.IsSignedIn) return;

                var name = await GameNameAsync(session, appId) ?? text;
                StatusText.Text = Texts.Get("steam.starting", name);
                var place = GameStorage.Roomiest(await GameStorage.PlacesAsync());
                DownloadManager.Start(session, appId, name, place?.Id ?? "local");
            }
            catch (Exception error) when (SteamAuth.MeansSignedOut(error))
            {
                // A session Steam no longer accepts is not a download error:
                // drop it so the app asks for the phone instead of retrying.
                try { await (await SteamSession.LoadAsync()).ClearAsync(); } catch { }
                StatusText.Text = Texts.Get("steam.signinagain");
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("steam.downloadfailed", "auto", error.Message);
            }
        }

        private Dictionary<uint, string> libraryNames;

        /// <summary>
        /// A game's name from his library or his family's, for the downloads
        /// screen; null when the library cannot be reached.
        /// </summary>
        private async Task<string> GameNameAsync(SteamSession session, uint appId)
        {
            if (libraryNames == null)
            {
                var names = new Dictionary<uint, string>();
                try
                {
                    foreach (var game in await SteamLibrary.OwnedAsync(session)) names[game.AppId] = game.Name;
                    foreach (var game in await SteamLibrary.FamilyAsync(session)) names[game.AppId] = game.Name;
                    libraryNames = names;
                }
                catch
                {
                    return null;
                }
            }
            return libraryNames.TryGetValue(appId, out var name) ? name : null;
        }

        /// <summary>
        /// Picks up every download the last session left half-way: a game
        /// folder with ".downloading" and no ".downloaded". The console can end
        /// the app at any time (a system update, a crash, the power), and a
        /// large game should not need someone to find it and press download
        /// again. Chunks already on disk are kept, so this costs only the rest.
        /// </summary>
        private async Task ResumeInterruptedAsync()
        {
            try
            {
                // Every place a game can be: the console, the share, a USB drive.
                var pending = new List<KeyValuePair<uint, string>>();
                foreach (var place in await GameStorage.GamesFoldersAsync())
                {
                    foreach (var folder in await place.Value.GetFoldersAsync())
                    {
                        if (!uint.TryParse(folder.Name, out var appId)) continue;
                        if (await folder.TryGetItemAsync(".downloading") == null) continue;
                        if (await folder.TryGetItemAsync(".downloaded") != null) continue;
                        if (DownloadManager.Find(appId)?.Running == true) continue;
                        pending.Add(new KeyValuePair<uint, string>(appId, place.Key));
                    }
                }
                if (pending.Count == 0) return;

                var session = await SteamSession.LoadAsync();
                if (!session.IsSignedIn) return;

                foreach (var item in pending)
                    DownloadManager.Start(session, item.Key,
                        await GameNameAsync(session, item.Key) ?? item.Key.ToString(), item.Value);
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("steam.downloadfailed", "resume", error.Message);
            }
        }

        private static Tile GameTile(uint appId, string title) => new Tile
        {
            Title = title,
            Subtitle = Texts.Get("tile.game.sub"),
            Route = "game:" + appId,
            Initial = title.Substring(0, 1).ToUpperInvariant(),
            Accent = new SolidColorBrush(Accents[(int)(appId % 6)]),
            SteamAppId = appId,
            Icon = Artwork(
                "https://cdn.cloudflare.steamstatic.com/steam/apps/"
                + appId + "/library_600x900.jpg"),
        };

        /// <summary>Finished downloads in every game folder, as shelf tiles.</summary>
        private async Task<List<Tile>> DownloadedTilesAsync(HashSet<uint> skip)
        {
            var list = new List<Tile>();
            try
            {
                var found = new List<uint>();
                foreach (var place in await GameStorage.GamesFoldersAsync())
                    foreach (var folder in await place.Value.GetFoldersAsync())
                        if (uint.TryParse(folder.Name, out var appId) && !skip.Contains(appId) && !found.Contains(appId)
                            && (await GameStorage.IsReadyAsync(folder) || DownloadManager.Find(appId)?.Running == true))
                            found.Add(appId);
                // A download started this session has a tile from its first
                // moment, with its progress on it.
                foreach (var job in DownloadManager.Snapshot())
                    if (job.Running && !skip.Contains(job.AppId) && !found.Contains(job.AppId)) found.Add(job.AppId);
                if (found.Count == 0) return list;
                var session = await SteamSession.LoadAsync();
                foreach (var appId in found)
                {
                    var name = session.IsSignedIn ? await GameNameAsync(session, appId) : null;
                    var tile = GameTile(appId, name ?? DownloadManager.Find(appId)?.Name ?? appId.ToString());
                    var job = DownloadManager.Find(appId);
                    if (job != null && job.Running) tile.Progress = job.Percent;
                    list.Add(tile);
                }
            }
            catch
            {
                // A drive that went away takes its games off the shelf, nothing more.
            }
            return list;
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
                    list.Add(GameTile(appId, title));
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

        /// <summary>
        /// The dock names whatever the shelf has focus on, in the same row it
        /// names its own icons in — one place for "what am I pointing at".
        /// </summary>
        private void OnTileFocused(object sender, RoutedEventArgs e)
        {
            ScaleTile(sender as Button, true);
            var tile = (sender as FrameworkElement)?.Tag as Tile;
            if (tile != null) NameText.Text = tile.Title;
        }

        private void OnTileUnfocused(object sender, RoutedEventArgs e)
        {
            ScaleTile(sender as Button, false);
        }

        private static void ScaleTile(Button button, bool focused)
        {
            var content = button?.Content as FrameworkElement;
            var outline = content?.FindName("SelectionOutline") as Border;
            if (outline != null)
                outline.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
            if (!(button?.RenderTransform is ScaleTransform scale)) return;
            var target = focused ? (double)Application.Current.Resources["TileFocusScale"] : 1;
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                scale.ScaleX = scale.ScaleY = target;
                return;
            }
            var storyboard = new Storyboard();
            foreach (var property in new[] { "ScaleX", "ScaleY" })
            {
                var animation = new DoubleAnimation
                {
                    To = target,
                    Duration = TimeSpan.FromMilliseconds(
                        (double)Application.Current.Resources["TileFocusDuration"]),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, scale);
                Storyboard.SetTargetProperty(animation, property);
                storyboard.Children.Add(animation);
            }
            storyboard.Begin();
        }

        /// <summary>
        /// A tile was chosen. The tile is the button now rather than a row in a
        /// list, so the game comes from the sender instead of from a click
        /// event — which also means a controller's A button and a pointer end
        /// up in exactly the same place.
        /// </summary>
        private async void OnTileClicked(object sender, RoutedEventArgs e)
        {
            await LaunchAsync((sender as FrameworkElement)?.Tag as Tile);
        }

        /// <summary>
        /// The dock. Six places, always in the same order, always reachable:
        /// on a television there is no menu bar to fall back on, so the way
        /// between screens has to be permanently on screen.
        /// </summary>
        /// <summary>Every download this session, running ones first.</summary>
        private void ShowDownloads()
        {
            DownloadsTitle.Text = Texts.Get("downloads.title");
            var jobs = DownloadManager.Snapshot();
            jobs.Sort((a, b) => b.Running.CompareTo(a.Running));
            Downloads.Clear();
            foreach (var job in jobs)
            {
                var status = job.Moving ? job.File
                    : job.Running ? Texts.Get("downloads.running", job.Percent)
                    : job.Finished ? Texts.Get("downloads.done")
                    : Texts.Get("downloads.failed", job.Error ?? string.Empty);
                Downloads.Add(new DownloadRow
                {
                    Name = job.Name,
                    Status = status,
                    Percent = job.Percent,
                    BarShown = job.Running ? Visibility.Visible : Visibility.Collapsed,
                });
            }
            DownloadsEmpty.Text = Texts.Get("downloads.empty");
            DownloadsEmpty.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// A running download shows on the home screen's status line, and the
        /// Downloads screen follows it while it is open.
        /// </summary>
        private void OnDownloadsChanged()
        {
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                if (Native.NativeProbe.GameRunning) return;
                // Name and percent only: the file being written belongs on
                // the Downloads screen, and a long path ran into the dock.
                var active = DownloadManager.Active();
                if (active != null)
                    StatusText.Text = active.Moving
                        ? active.Name + " — " + active.File
                        : Texts.Get("steam.downloading.short", active.Name, active.Percent);
                else
                {
                    var jobs = DownloadManager.Snapshot();
                    var last = jobs.Count > 0 ? jobs[jobs.Count - 1] : null;
                    if (last?.Finished == true) StatusText.Text = Texts.Get("steam.downloaded", last.Name);
                    else if (last?.Error != null) StatusText.Text = Texts.Get("steam.downloadfailed", last.Name, last.Error);
                }
                if (DownloadsScreen.Visibility == Visibility.Visible) ShowDownloads();

                // The shelf follows the downloads: a tile's bar moves, a new
                // download gets a tile, and a finished one drops its bar.
                var shelfChanged = false;
                foreach (var job in DownloadManager.Snapshot())
                {
                    Tile shown = null;
                    foreach (var tile in Tiles) if (tile.SteamAppId == job.AppId) shown = tile;
                    if (shown == null) { if (job.Running) shelfChanged = true; continue; }
                    shown.Progress = job.Running ? job.Percent : -1;
                }
                if (shelfChanged && !refreshingShelf)
                {
                    refreshingShelf = true;
                    var ____ = RefreshShelfAsync();
                }
            });
        }

        private bool refreshingShelf;

        private async Task RefreshShelfAsync()
        {
            try
            {
                var shelved = new HashSet<uint>();
                foreach (var tile in Tiles) if (tile.SteamAppId != 0) shelved.Add(tile.SteamAppId);
                foreach (var tile in await DownloadedTilesAsync(shelved)) Tiles.Add(tile);
            }
            catch
            {
            }
            finally
            {
                refreshingShelf = false;
            }
        }

        private void OnDockClicked(object sender, RoutedEventArgs e)
        {
            if (gameLaunchPending || Native.NativeProbe.GameRunning) return;
            var where = (sender as FrameworkElement)?.Tag as string;
            Light(where);

            // The shop is the store's own screen: signing in, the library that
            // comes back, and installing from it. It already exists, so the
            // dock opens it rather than growing a second one beside it.
            if (where == "shop")
            {
                Light("library");
                Frame.Navigate(typeof(SteamPage));
                return;
            }

            // The rest are named, reachable, and honest about not being built,
            // which is better than an icon that swallows the press.
            LibraryScreen.Visibility =
                where == "library" ? Visibility.Visible : Visibility.Collapsed;
            EmulatorScreen.Visibility =
                where == "emulators" ? Visibility.Visible : Visibility.Collapsed;
            DownloadsScreen.Visibility =
                where == "downloads" ? Visibility.Visible : Visibility.Collapsed;
            AllGamesScreen.Visibility = Visibility.Collapsed;
            if (where == "downloads") ShowDownloads();
            if (where == "emulators") { var shop = ShowShopAsync(); }

            var built = where == "library" || where == "emulators" || where == "downloads";
            StatusText.Text = built ? string.Empty : Texts.Get("status.notyet", where);
        }

        /// <summary>
        /// Lights one dock icon and puts the rest out.
        ///
        /// The colour goes on the button rather than on the glyph inside it:
        /// the circle and the icon have to change together, and when they did
        /// not, a focused icon was grey on near-white — the least readable
        /// thing on the screen, which is the opposite of what focus is for.
        /// </summary>
        /// <summary>
        /// Moves one place along the dock and opens it, wrapping at both ends
        /// so a shoulder held down keeps going rather than stopping dead.
        /// </summary>
        private void Step(int by)
        {
            var icons = new[]
            {
                DockLibrary, DockShop, DockEmulators,
                DockFriends, DockMods, DockDownloads,
            };
            var at = Array.FindIndex(
                icons, icon => (icon.Tag as string) == activeDestination);
            if (at < 0) at = 0;
            var next = icons[((at + by) % icons.Length + icons.Length) % icons.Length];
            next.Focus(FocusState.Programmatic);
            OnDockClicked(next, null);
        }

        /// <summary>
        /// Which emulators he asked to see on the shelf. Kept beside the app's
        /// own data so it survives an update, and read as a plain list of
        /// names because that is what it is.
        /// </summary>
        private static async Task<HashSet<string>> ReadShortcutsAsync()
        {
            var chosen = new HashSet<string>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync("shortcuts.json") as StorageFile;
                if (file == null) return chosen;
                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return chosen;
                foreach (var value in root.GetNamedArray("shelf"))
                {
                    chosen.Add(value.GetString());
                }
            }
            catch
            {
                // Nothing chosen yet is the ordinary case, not a failure.
            }
            return chosen;
        }

        private static async Task WriteShortcutsAsync(IEnumerable<string> chosen)
        {
            var shelf = new JsonArray();
            foreach (var name in chosen) shelf.Add(JsonValue.CreateStringValue(name));
            var root = new JsonObject { { "shelf", shelf } };
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                "shortcuts.json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, root.Stringify());
        }

        /// <summary>
        /// Puts an emulator on the shelf, or takes it off. His choice, either
        /// way: the shelf is his, and nothing arrives on it uninvited.
        /// </summary>
        private async void OnShelfToggled(object sender, RoutedEventArgs e)
        {
            var tile = (sender as FrameworkElement)?.Tag as Tile;
            if (tile == null) return;

            tile.OnShelf = !tile.OnShelf;
            if (tile.OnShelf)
            {
                Tiles.Insert(Math.Max(0, Tiles.Count - 1), tile);
            }
            else
            {
                Tiles.Remove(tile);
            }

            var chosen = new List<string>();
            foreach (var one in Emulators)
            {
                if (one.OnShelf) chosen.Add(one.Title);
            }
            await WriteShortcutsAsync(chosen);

            // The list is rebuilt rather than nudged: the cards read their
            // label from the tile, and a collection that was changed in place
            // does not tell them to look again.
            var all = new List<Tile>(Emulators);
            Emulators.Clear();
            foreach (var one in all) Emulators.Add(one);
        }

        /// <summary>
        /// The pads in the room. More than one and the count is what matters;
        /// exactly one and the count says nothing anybody needed, so the
        /// charge takes its place.
        /// </summary>
        private void ShowPads()
        {
            var pads = Native.PadBridge.Pads;
            CountText.Text = pads.Count > 1 ? "\u00D7" + pads.Count : string.Empty;

            if (pads.Count != 1)
            {
                BatteryBox.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                var report = pads[0].TryGetBatteryReport();
                var full = report?.FullChargeCapacityInMilliwattHours;
                var left = report?.RemainingCapacityInMilliwattHours;
                if (full == null || left == null || full == 0)
                {
                    // A wired pad reports no cell at all, which is not a
                    // fault and should not be drawn as an empty battery.
                    BatteryBox.Visibility = Visibility.Collapsed;
                    return;
                }

                var part = Math.Max(0, Math.Min(1, (double)left / full.Value));
                BatteryFill.Width = 52 * part;
                BatteryFill.Background = (Brush)Application.Current.Resources[
                    part > 0.25 ? "Accent" : "Warning"];
                BatteryText.Text = (int)Math.Round(part * 100) + "%";
                BatteryBox.Visibility = Visibility.Visible;
            }
            catch
            {
                BatteryBox.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Puts focus on the first tile of whichever shelf is showing.
        ///
        /// Walking the tree for it rather than keeping a reference: the tiles
        /// are made by a template, so there is nothing to hold on to until
        /// the layout has actually produced them.
        /// </summary>
        /// <summary>
        /// The whole library as a grid. The shelf is one row that scrolls;
        /// past a handful of games, finding one means walking the row, so the
        /// last tile on it opens this instead.
        /// </summary>
        private void ShowAllGames()
        {
            AllGames.Clear();
            foreach (var tile in Tiles)
                if (!tile.IsAllGames) AllGames.Add(tile);
            AllGamesTitle.Text = Texts.Get("tile.allgames", AllGames.Count);
            Light("library");
            LibraryScreen.Visibility = Visibility.Collapsed;
            EmulatorScreen.Visibility = Visibility.Collapsed;
            DownloadsScreen.Visibility = Visibility.Collapsed;
            AllGamesScreen.Visibility = Visibility.Visible;
            StatusText.Text = string.Empty;
            AllGamesGrid.UpdateLayout();
            var first = FirstButton(AllGamesGrid);
            if (first != null) first.Focus(FocusState.Programmatic);
        }

        private async Task ShowShopAsync()
        {
            var items = await EmulatorShop.AvailableAsync();
            // An install in flight keeps its card: rebuilding would drop its progress.
            foreach (var busy in ShopItems.Where(i => i.Busy).ToList())
                items.RemoveAll(i => i.Slug == busy.Slug);
            foreach (var idle in ShopItems.Where(i => !i.Busy).ToList()) ShopItems.Remove(idle);
            foreach (var item in items) ShopItems.Add(item);
            ShopTitle.Text = Texts.Get("shop.title");
            ShopTitle.Visibility = ShopItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnInstallClicked(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.Tag as CatalogItem;
            if (item == null || item.Busy) return;
            var failed = await EmulatorShop.InstallAsync(item);
            item.Status = failed == null
                ? Texts.Get("shop.installed")
                : Texts.Get("shop.failed", failed);
            StatusText.Text = failed == null ? Texts.Get("shop.installed.long", item.Name) : string.Empty;
        }

        private void FocusShelf()
        {
            var first = FirstButton(AppRail);
            if (first != null) first.Focus(FocusState.Programmatic);
        }

        private static Button FirstButton(DependencyObject from)
        {
            if (from == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(from);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(from, i);
                if (child is Button button) return button;
                var deeper = FirstButton(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private string activeDestination = "library";

        private void Light(string where)
        {
            activeDestination = where;
            var dim = (Brush)Application.Current.Resources["Surface2"];
            var onLit = (Brush)Application.Current.Resources["TextPrimary"];
            var onDim = (Brush)Application.Current.Resources["TextTertiary"];

            foreach (var icon in new[]
                     {
                         DockLibrary, DockShop, DockEmulators,
                         DockFriends, DockMods, DockDownloads,
                     })
            {
                var active = (icon.Tag as string) == where;
                // Selection is not a held press. Only focus/hover lights the circle.
                icon.Background = dim;
                icon.Foreground = active ? onLit : onDim;
            }
        }

        /// <summary>
        /// The dock names whatever has focus, in a row of fixed height above
        /// the icons — so the icons never move when the name appears.
        /// </summary>
        private void OnDockHover(object sender, RoutedEventArgs e)
        {
            var where = (sender as FrameworkElement)?.Tag as string;
            if (where != null) NameText.Text = Texts.Get("dock." + where);
        }

        private void OnDockHover(object sender, PointerRoutedEventArgs e)
        {
            OnDockHover(sender, (RoutedEventArgs)null);
        }

        private void OnDockLeave(object sender, RoutedEventArgs e)
        {
            NameText.Text = string.Empty;
        }

        private void OnDockLeave(object sender, PointerRoutedEventArgs e)
        {
            NameText.Text = string.Empty;
        }

        private async void OnSignInClicked(object sender, RoutedEventArgs e)
        {
            await LaunchAsync(new Tile { Route = "steam", Title = Texts.Get("tile.steam") });
        }

        private async void OnRetryClicked(object sender, RoutedEventArgs e)
        {
            ErrorScreen.Visibility = Visibility.Collapsed;
            await LoadAppsAsync();
        }

        /// <summary>
        /// Every failure fills the content area rather than tucking a line
        /// under something else. Three metres from a screen, an inline warning
        /// is a warning nobody reads.
        /// </summary>
        private void ShowError(string title, string body)
        {
            ErrorTitle.Text = title;
            ErrorBody.Text = body;
            LibraryScreen.Visibility = Visibility.Collapsed;
            EmptyScreen.Visibility = Visibility.Collapsed;
            ErrorScreen.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Launching by URI is the one route the console leaves open to a
        /// sideloaded app; packages that register no protocol have to be opened
        /// from Dev Home.
        /// </summary>
        private async Task LaunchAsync(Tile tile)
        {
            if (gameLaunchPending || Native.NativeProbe.GameRunning) return;
            if (tile == null) return;
            if (tile.Route == "steam")
            {
                Frame.Navigate(typeof(SteamPage));
                return;
            }
            if (tile.Route == "allgames")
            {
                ShowAllGames();
                return;
            }
            if (tile.Route != null && tile.Route.StartsWith("game:", StringComparison.Ordinal))
            {
                if (!uint.TryParse(tile.Route.Substring(5), out var appId)) return;
                // Still downloading: its progress, not an attempt to run half a game.
                if (DownloadManager.Find(appId)?.Running == true)
                {
                    OnDockClicked(DockDownloads, null);
                    return;
                }
                await StartGameAsync(appId);
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
            if (setupOpen) return;
            // A game on screen has the controller. Ours is still behind it and
            // still focused, and without this the player would be walking
            // through a menu they cannot see while they play.
            if (Native.NativeProbe.GameRunning)
            {
                e.Handled = true;
                return;
            }

            // One press, one move. Left to itself the focus walks into the
            // scroller first and out of it second, so crossing between the
            // shelf and the menu cost two presses where it should cost one.
            if (e.Key == Windows.System.VirtualKey.GamepadDPadDown ||
                e.Key == Windows.System.VirtualKey.Down)
            {
                if (FocusManager.GetFocusedElement() == SetupButton) FocusShelf();
                else DockLibrary.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.GamepadDPadUp ||
                e.Key == Windows.System.VirtualKey.Up)
            {
                if ((FocusManager.GetFocusedElement() as FrameworkElement)?.Tag is Tile)
                    SetupButton.Focus(FocusState.Programmatic);
                else FocusShelf();
                e.Handled = true;
                return;
            }

            // The shoulders walk the dock, which is how a console moves
            // between places: the sticks belong to whatever is on screen, and
            // the way out of it should not be one of them.
            if (e.Key == Windows.System.VirtualKey.GamepadLeftShoulder ||
                e.Key == Windows.System.VirtualKey.GamepadRightShoulder)
            {
                Step(e.Key == Windows.System.VirtualKey.GamepadRightShoulder ? 1 : -1);
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Windows.System.VirtualKey.GamepadA:
                case Windows.System.VirtualKey.Enter:
                    await LaunchAsync(FocusManager.GetFocusedElement() is FrameworkElement on
                        ? on.Tag as Tile : null);
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
