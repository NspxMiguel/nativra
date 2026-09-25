using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Kiosk
{
    /// <summary>Somewhere a game can be installed, and how much room is left there.</summary>
    public sealed class InstallPlace
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Free { get; set; }
    }

    /// <summary>
    /// One game, the way its store page reads: its own art, how long it has
    /// been played, and a button that says what will happen. Downloading asks
    /// where it should land before it starts, because that is his disk.
    /// </summary>
    public sealed partial class GamePage : Page
    {
        public ObservableCollection<InstallPlace> Places { get; } =
            new ObservableCollection<InstallPlace>();

        private OwnedGame game;
        private SteamSession session;
        private bool installed;
        private bool busy;

        public GamePage()
        {
            InitializeComponent();
            HintBackText.Text = Texts.Get("hint.back");
            SheetTitle.Text = Texts.Get("game.install");
            SheetOk.Content = Texts.Get("game.install");
            SheetCancel.Content = Texts.Get("game.cancel");
            SheetWhereLabel.Text = Texts.Get("game.where");
            PlayedLabel.Text = Texts.Get("game.playedlabel");
            StateLabel.Text = Texts.Get("game.statelabel");
            PlaceList.ItemsSource = Places;
            AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var argument = e.Parameter as GameArgument;
            if (argument == null) return;

            game = argument.Game;
            session = argument.Session;
            DownloadManager.Changed += OnDownloadsChanged;
            var running = DownloadManager.Find(game.AppId);
            if (running != null && running.Running)
            {
                busy = true;
                ShowJob(running);
            }

            TitleText.Text = game.Name;
            PlayedText.Text = game.Played;
            try
            {
                HeroImage.Source = new BitmapImage(new Uri(
                    "https://cdn.cloudflare.steamstatic.com/steam/apps/"
                    + game.AppId + "/library_hero.jpg"));
            }
            catch
            {
                // No hero art is a plain background, not a failure.
            }

            await RefreshStateAsync();
        }

        private async Task RefreshStateAsync()
        {
            installed = await IsInstalledAsync();
            StateText.Text = installed
                ? Texts.Get("game.state.installed")
                : Texts.Get("game.state.notinstalled");
            InstallButton.Content = installed
                ? Texts.Get("game.play")
                : Texts.Get("game.install");
        }

        private async Task<bool> IsInstalledAsync()
        {
            try
            {
                // Wherever it went: the console, the developer share or a USB drive.
                var folder = await GameStorage.FindAsync(game.AppId);
                return folder != null && await folder.TryGetItemAsync(".downloading") == null;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------- install

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (installed)
            {
                Frame.Navigate(typeof(MainPage), game.AppId);
                return;
            }

            // The console, then any USB drive; the roomiest is picked for
            // him, since the console's own storage is the one that fills.
            Places.Clear();
            var places = await GameStorage.PlacesAsync();
            try
            {
                var dev = await StorageFolder.GetFolderFromPathAsync(@"D:\DevelopmentFiles");
                places.Add(new GamePlace
                {
                    Id = "dev",
                    Name = Texts.Get("game.place.dev"),
                    Free = await Steam.SteamDownload.FreeBytesAsync(dev),
                });
            }
            catch
            {
                // The developer share is not always reachable from in here.
            }
            var roomiest = GameStorage.Roomiest(places);
            var chosen = 0;
            foreach (var place in places)
            {
                if (place == roomiest) chosen = Places.Count;
                Places.Add(new InstallPlace
                {
                    Id = place.Id,
                    Name = place.Name,
                    Free = place.Free.HasValue ? Steam.SteamDownload.Human(place.Free.Value) : "",
                });
            }

            PlaceList.SelectedIndex = chosen;
            SheetName.Text = game.Name;
            SheetSize.Text = Texts.Get("game.sizeunknown");
            InstallSheet.Visibility = Visibility.Visible;
            SheetOk.Focus(FocusState.Programmatic);
        }

        private void OnCancelInstall(object sender, RoutedEventArgs e)
        {
            InstallSheet.Visibility = Visibility.Collapsed;
        }

        private async void OnConfirmInstall(object sender, RoutedEventArgs e)
        {
            InstallSheet.Visibility = Visibility.Collapsed;
            if (busy) return;

            var place = PlaceList.SelectedItem as InstallPlace;
            var root = place?.Id ?? "local";
            await Settings.SetDownloadRootAsync(root);

            busy = true;
            StatusText.Text = Texts.Get("steam.starting", game.Name);
            // The download belongs to the app, not to this page: leaving the
            // page no longer stops it, and Downloads on the home dock shows it.
            ShowJob(DownloadManager.Start(session, game.AppId, game.Name, root));
        }

        private void ShowJob(DownloadJob job)
        {
            StatusText.Text = Texts.Get("steam.downloading", job.Name, job.Percent, job.File);
        }

        private void OnDownloadsChanged()
        {
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, async () =>
            {
                var job = game == null ? null : DownloadManager.Find(game.AppId);
                if (job == null) return;
                if (job.Running)
                {
                    ShowJob(job);
                    return;
                }
                if (!busy) return;
                busy = false;
                if (job.Finished)
                {
                    StatusText.Text = Texts.Get("steam.downloaded", game.Name);
                    await RefreshStateAsync();
                }
                else if (job.Error != null)
                {
                    ShowError(Texts.Get("game.failed.title"), job.Error, Texts.Get("game.failed.hint"));
                }
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            DownloadManager.Changed -= OnDownloadsChanged;
            base.OnNavigatedFrom(e);
        }

        private void ShowError(string title, string body, string hint)
        {
            ErrorTitle.Text = title;
            ErrorBody.Text = body;
            ErrorHint.Text = hint;
            ErrorScreen.Visibility = Visibility.Visible;
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.GamepadB &&
                e.Key != Windows.System.VirtualKey.Escape)
            {
                return;
            }

            if (ErrorScreen.Visibility == Visibility.Visible)
            {
                ErrorScreen.Visibility = Visibility.Collapsed;
            }
            else if (InstallSheet.Visibility == Visibility.Visible)
            {
                InstallSheet.Visibility = Visibility.Collapsed;
            }
            else if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            e.Handled = true;
        }
    }

    /// <summary>What the library hands the detail page.</summary>
    public sealed class GameArgument
    {
        public OwnedGame Game { get; set; }
        public SteamSession Session { get; set; }
    }
}
