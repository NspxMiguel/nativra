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
                var games = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync(Steam.SteamDownload.DefaultFolder) as StorageFolder;
                if (games == null) return false;
                return await games.TryGetItemAsync(game.AppId.ToString()) != null;
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
                ShowError(
                    Texts.Get("game.cannotrun.title"),
                    Texts.Get("game.cannotrun.body", game.Name),
                    Texts.Get("game.cannotrun.hint"));
                return;
            }

            Places.Clear();
            Places.Add(new InstallPlace
            {
                Id = "local",
                Name = Texts.Get("game.place.console"),
                Free = await FreeAsync(ApplicationData.Current.LocalFolder),
            });
            try
            {
                var dev = await StorageFolder.GetFolderFromPathAsync(@"D:\DevelopmentFiles");
                Places.Add(new InstallPlace
                {
                    Id = "dev",
                    Name = Texts.Get("game.place.dev"),
                    Free = await FreeAsync(dev),
                });
            }
            catch
            {
                // The developer share is not always reachable from in here.
            }

            PlaceList.SelectedIndex = 0;
            SheetName.Text = game.Name;
            SheetSize.Text = Texts.Get("game.sizeunknown");
            InstallSheet.Visibility = Visibility.Visible;
            SheetOk.Focus(FocusState.Programmatic);
        }

        private static async Task<string> FreeAsync(StorageFolder folder)
        {
            try
            {
                var properties = await folder.Properties.RetrievePropertiesAsync(
                    new[] { "System.FreeSpace" });
                if (properties.TryGetValue("System.FreeSpace", out var value) && value != null)
                {
                    return Human(Convert.ToUInt64(value));
                }
            }
            catch
            {
                // Free space is information, not a requirement.
            }
            return "";
        }

        private static string Human(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return size.ToString(size >= 10 ? "0" : "0.0") + " " + units[unit];
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
            try
            {
                await Steam.SteamDownload.RunAsync(session, game.AppId, root, progress =>
                {
                    var _ = Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Low,
                        () => StatusText.Text = Texts.Get(
                            "steam.downloading", game.Name, progress.Percent, progress.File));
                });
                StatusText.Text = Texts.Get("steam.downloaded", game.Name);
                await RefreshStateAsync();
            }
            catch (Exception error)
            {
                ShowError(
                    Texts.Get("game.failed.title"),
                    error.Message,
                    Texts.Get("game.failed.hint"));
            }
            finally
            {
                busy = false;
            }
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
