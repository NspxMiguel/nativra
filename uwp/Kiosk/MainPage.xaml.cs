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
        public SolidColorBrush Accent { get; set; }
        public double Dimmed => string.IsNullOrEmpty(Protocol) ? 0.45 : 1.0;
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
            Tiles.Clear();

            var apps = await ReadListAsync();
            if (apps == null)
            {
                NameText.Text = Texts.Get("empty.title");
                SubText.Text = Texts.Get("empty.next");
                CountText.Text = string.Empty;
                StatusText.Text = string.Empty;
                return;
            }

            var index = 0;
            foreach (var app in apps)
            {
                Tiles.Add(new Tile
                {
                    Title = app.Item1,
                    Subtitle = app.Item2,
                    Protocol = app.Item3,
                    Initial = app.Item1.Substring(0, 1).ToUpperInvariant(),
                    Accent = new SolidColorBrush(Accents[index++ % Accents.Length]),
                });
            }

            CountText.Text = Texts.Get("status.count", Tiles.Count);
            StatusText.Text = string.Empty;

            if (Tiles.Count > 0)
            {
                AppRail.UpdateLayout();
                AppRail.SelectedIndex = 0;
                (AppRail.ContainerFromIndex(0) as Control)?.Focus(FocusState.Programmatic);
            }
        }

        /// <summary>Reads apps.json, which xbdev sync drops into LocalState.</summary>
        private static async Task<List<Tuple<string, string, string>>> ReadListAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync("apps.json")
                    as StorageFile;
                if (file == null) return null;

                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return null;

                var list = new List<Tuple<string, string, string>>();
                foreach (var value in root.GetNamedArray("apps"))
                {
                    var item = value.GetObject();
                    var title = item.GetNamedString("title", string.Empty);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var subtitle = item.GetNamedString("subtitle", string.Empty);
                    string protocol = null;
                    if (item.ContainsKey("protocol") &&
                        item["protocol"].ValueType == JsonValueType.String)
                    {
                        protocol = item.GetNamedString("protocol");
                    }
                    list.Add(Tuple.Create(title, subtitle, protocol));
                }
                return list;
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
            StatusText.Text = string.IsNullOrEmpty(tile.Protocol)
                ? Texts.Get("status.noprotocol")
                : string.Empty;
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
            if (string.IsNullOrEmpty(tile.Protocol))
            {
                StatusText.Text = Texts.Get("status.noprotocol");
                return;
            }

            StatusText.Text = Texts.Get("status.opening", tile.Title);
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
