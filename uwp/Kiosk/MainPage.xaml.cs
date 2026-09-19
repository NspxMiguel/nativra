using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace Kiosk
{
    /// <summary>
    /// One entry per launchable app installed on the console. Sideloaded
    /// packages only — these are the emulators and PC ports this system exists
    /// to open.
    /// </summary>
    public sealed class Tile
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Initial { get; set; }
        public BitmapImage Logo { get; set; }
        public Visibility LogoVisible => Logo == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility InitialVisible => Logo == null ? Visibility.Visible : Visibility.Collapsed;
        public AppListEntry Entry { get; set; }
    }

    public sealed partial class MainPage : Page
    {
        public ObservableCollection<Tile> Tiles { get; } = new ObservableCollection<Tile>();

        // What each catalogue package actually runs. Anything else that is
        // sideloaded still appears, just without a subtitle.
        private static readonly Dictionary<string, string> KnownApps =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "retroarch", "200+ sistemas" },
                { "xbsx2", "PlayStation 2" },
                { "xenia", "Xbox 360" },
                { "dolphin", "GameCube / Wii" },
                { "ppsspp", "PSP" },
                { "flycast", "Dreamcast" },
                { "gzdoom", "Doom / Heretic / Hexen" },
                { "zdoom", "Doom / Heretic / Hexen" },
                { "raze", "Duke Nukem / Blood" },
                { "scummvm", "ScummVM" },
                { "dosbox", "MS-DOS" },
                { "openbor", "Beat 'em ups" },
                { "ikemen", "Luta" },
                { "supermodel", "Sega Model 3" },
                { "ruffle", "Flash" },
            };

        private DispatcherTimer clock;

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
            var tiles = new List<Tile>();

            // Counters, surfaced on screen: on a console there is no debugger, so
            // the app has to be able to say why a list came back short.
            var seen = 0;
            var skippedFramework = 0;
            var skippedSystem = 0;
            var skippedMicrosoft = 0;
            var skippedSelf = 0;
            var skippedNoEntries = 0;
            string machineError = null;

            try
            {
                var manager = new PackageManager();
                var packages = manager.FindPackagesForUser(string.Empty).ToList();
                var perUserCount = packages.Count;

                // Try the machine-wide query too and keep whichever sees more:
                // on this console the per-user call returns only a handful.
                var machineCount = -1;
                try
                {
                    var all = manager.FindPackages().ToList();
                    machineCount = all.Count;
                    if (all.Count > packages.Count) packages = all;
                }
                catch (Exception queryError)
                {
                    machineError = queryError.HResult.ToString("X8");
                }

                foreach (var package in packages)
                {
                    seen++;
                    if (package.IsFramework || package.IsResourcePackage) { skippedFramework++; continue; }
                    // Retail and system packages are not ours to launch from here.
                    if (package.SignatureKind == PackageSignatureKind.System) { skippedSystem++; continue; }
                    if (package.Id.FamilyName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) { skippedMicrosoft++; continue; }
                    if (package.Id.FamilyName.StartsWith("NSPX.Kiosk", StringComparison.OrdinalIgnoreCase)) { skippedSelf++; continue; }

                    IReadOnlyList<AppListEntry> entries;
                    try
                    {
                        entries = await package.GetAppListEntriesAsync();
                    }
                    catch
                    {
                        skippedNoEntries++;
                        continue;
                    }
                    if (entries.Count == 0) skippedNoEntries++;

                    foreach (var entry in entries)
                    {
                        var title = entry.DisplayInfo?.DisplayName;
                        if (string.IsNullOrWhiteSpace(title)) title = package.DisplayName;
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        tiles.Add(new Tile
                        {
                            Title = title,
                            Subtitle = DescribeApp(package.Id.Name, title),
                            Initial = title.Substring(0, 1).ToUpperInvariant(),
                            Logo = await LoadLogoAsync(entry),
                            Entry = entry,
                        });
                    }
                }
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("status.unreadable", error.Message);
                return;
            }

            Tiles.Clear();
            foreach (var tile in tiles.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
            {
                Tiles.Add(tile);
            }

            CountText.Text = Texts.Get("status.count", Tiles.Count);
            StatusText.Text =
                $"user {perUserCount} · machine {machineCount}{(machineError == null ? "" : " err " + machineError)} · " +
                $"fw {skippedFramework} · sys {skippedSystem} · ms {skippedMicrosoft} · self {skippedSelf} · noapp {skippedNoEntries}";

            if (Tiles.Count == 0)
            {
                NameText.Text = Texts.Get("empty.title");
                SubText.Text = Texts.Get("empty.next");
                return;
            }

            AppRail.UpdateLayout();
            AppRail.SelectedIndex = 0;
            (AppRail.ContainerFromIndex(0) as Control)?.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// The app's own tile art, which is what makes this read like a console
        /// dashboard instead of a list. Falls back to the coloured initial when
        /// a package has no usable logo.
        /// </summary>
        private static async Task<BitmapImage> LoadLogoAsync(AppListEntry entry)
        {
            try
            {
                var reference = entry.DisplayInfo?.GetLogo(new Size(256, 256));
                if (reference == null) return null;
                using (var stream = await reference.OpenReadAsync())
                {
                    if (stream == null || stream.Size == 0) return null;
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeApp(string packageName, string title)
        {
            foreach (var known in KnownApps)
            {
                if (packageName.IndexOf(known.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf(known.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return known.Value;
                }
            }
            return string.Empty;
        }

        /// <summary>The name above the rail belongs to whatever is focused.</summary>
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(AppRail.SelectedItem is Tile tile)) return;
            NameText.Text = tile.Title;
            SubText.Text = tile.Subtitle;
        }

        private async void OnTileInvoked(object sender, ItemClickEventArgs e)
        {
            await LaunchAsync(e.ClickedItem as Tile);
        }

        private async Task LaunchAsync(Tile tile)
        {
            if (tile?.Entry == null) return;
            StatusText.Text = Texts.Get("status.opening", tile.Title);
            try
            {
                var launched = await tile.Entry.LaunchAsync();
                if (!launched) StatusText.Text = Texts.Get("status.refused", tile.Title);
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
                // A on the gamepad, Enter on a keyboard.
                case Windows.System.VirtualKey.GamepadA:
                case Windows.System.VirtualKey.Enter:
                    await LaunchAsync(AppRail.SelectedItem as Tile);
                    e.Handled = true;
                    break;

                // Y re-reads the console, for right after installing something.
                case Windows.System.VirtualKey.GamepadY:
                case Windows.System.VirtualKey.F5:
                    await LoadAppsAsync();
                    e.Handled = true;
                    break;
            }
        }
    }
}
