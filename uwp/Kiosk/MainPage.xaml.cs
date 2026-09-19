using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Kiosk
{
    /// <summary>
    /// One tile per launchable app installed on the console. Sideloaded packages
    /// come first — those are the emulators and PC ports this system is for.
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

        // Packages the catalogue installs, with what they actually run. Anything
        // else that is sideloaded still shows up, just without a subtitle.
        private static readonly Dictionary<string, string> KnownApps =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "retroarch", "200+ systems" },
                { "xbsx2", "PlayStation 2" },
                { "xenia", "Xbox 360" },
                { "dolphin", "GameCube / Wii" },
                { "ppsspp", "PSP" },
                { "flycast", "Dreamcast" },
                { "gzdoom", "Doom / Heretic / Hexen" },
                { "zdoom", "Doom / Heretic / Hexen" },
                { "raze", "Duke Nukem / Blood" },
                { "scummvm", "Adventure games" },
                { "dosbox", "MS-DOS" },
                { "openbor", "Beat 'em ups" },
                { "ikemen", "Fighting games" },
                { "supermodel", "Sega Model 3" },
                { "ruffle", "Flash" },
            };

        public MainPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadAppsAsync();
        }

        private async Task LoadAppsAsync()
        {
            EyebrowText.Text = Texts.Get("app.eyebrow");
            TitleText.Text = Texts.Get("app.title");
            StatusText.Text = Texts.Get("status.reading");
            var tiles = new List<Tile>();

            try
            {
                var manager = new PackageManager();
                var packages = manager.FindPackagesForUser(string.Empty);

                foreach (var package in packages)
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    // Retail/system packages are not ours to launch from here.
                    if (package.SignatureKind == PackageSignatureKind.System) continue;
                    if (package.Id.FamilyName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (package.Id.FamilyName.StartsWith("Kiosk", StringComparison.OrdinalIgnoreCase)) continue;

                    IReadOnlyList<AppListEntry> entries;
                    try
                    {
                        entries = await package.GetAppListEntriesAsync();
                    }
                    catch
                    {
                        continue;
                    }

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

            foreach (var tile in tiles.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
            {
                Tiles.Add(tile);
            }

            StatusText.Text = Tiles.Count == 0
                ? Texts.Get("status.empty")
                : Texts.Get("status.count", Tiles.Count);

            if (Tiles.Count > 0)
            {
                AppGrid.UpdateLayout();
                AppGrid.SelectedIndex = 0;
                (AppGrid.ContainerFromIndex(0) as Control)?.Focus(FocusState.Programmatic);
            }
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
                var reference = entry.DisplayInfo?.GetLogo(new Size(150, 150));
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
            // Gamepad A arrives as GamepadA; Enter covers a hardware keyboard.
            if (e.Key == Windows.System.VirtualKey.GamepadA ||
                e.Key == Windows.System.VirtualKey.Enter)
            {
                await LaunchAsync(AppGrid.SelectedItem as Tile);
                e.Handled = true;
            }
        }
    }
}
