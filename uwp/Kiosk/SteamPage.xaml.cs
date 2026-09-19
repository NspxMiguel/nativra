using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Data.Json;
using Windows.Storage;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using ZXing;
using ZXing.QrCode;

namespace Kiosk
{
    /// <summary>
    /// The Steam half of the app: sign in by QR once, then the account's own
    /// library. Both live here so the console has a single front end.
    /// </summary>
    public sealed partial class SteamPage : Page
    {
        public ObservableCollection<OwnedGame> Games { get; } =
            new ObservableCollection<OwnedGame>();

        /// <summary>Every game the account owns; Games is what the filters leave.</summary>
        private readonly List<OwnedGame> allGames = new List<OwnedGame>();
        public ObservableCollection<Shelf> Shelves { get; } =
            new ObservableCollection<Shelf>();

        private readonly HashSet<uint> tested = new HashSet<uint>();
        private readonly HashSet<uint> hidden = new HashSet<uint>();
        private int filter;
        private string query = string.Empty;
        private bool showHidden;

        private static readonly string[] FilterKeys =
        {
            "steam.filter.all", "steam.filter.played", "steam.filter.never", "steam.filter.tested",
        };

        private SteamSession session = new SteamSession();
        private QrSession challenge;
        private string shownUrl;
        private bool polling;

        public SteamPage()
        {
            InitializeComponent();
            HeaderText.Text = Texts.Get("steam.header");
            HintBackText.Text = Texts.Get("hint.back");
            HintRefreshText.Text = Texts.Get("hint.refresh");
            HintSearchText.Text = Texts.Get("hint.search");
            HintFilterText.Text = Texts.Get("hint.filter");
            HintOptionsText.Text = Texts.Get("hint.options");

            // The grid claims the shoulder buttons for its own paging, so the
            // page has to listen after it rather than before.
            AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            session = await SteamSession.LoadAsync();
            if (session.IsSignedIn)
            {
                await ShowLibraryAsync();
            }
            else
            {
                await StartSignInAsync();
            }
        }

        // ------------------------------------------------------------- sign in

        private async Task StartSignInAsync()
        {
            SignInPanel.Visibility = Visibility.Visible;
            LibraryPanel.Visibility = Visibility.Collapsed;
            SearchHint.Visibility = Visibility.Collapsed;
            FilterHint.Visibility = Visibility.Collapsed;
            FilterChip.Visibility = Visibility.Collapsed;
            OptionsHint.Visibility = Visibility.Collapsed;
            AccountText.Text = string.Empty;
            SignInTitle.Text = Texts.Get("signin.title");
            SignInHow.Text = Texts.Get("signin.how");
            StatusText.Text = Texts.Get("signin.asking");

            try
            {
                challenge = await SteamAuth.BeginAsync("Xbox Series X");
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("signin.failed", error.Message);
                return;
            }

            ShowQr(challenge.ChallengeUrl);
            StatusText.Text = Texts.Get("signin.waiting");
            await PollLoopAsync();
        }

        /// <summary>
        /// Steam rotates the challenge while nobody has approved, so the code on
        /// screen has to follow it or the scan stops working.
        /// </summary>
        private async Task PollLoopAsync()
        {
            if (polling) return;
            polling = true;
            try
            {
                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, challenge.Interval)));

                    LoginResult login;
                    try
                    {
                        login = await SteamAuth.PollAsync(challenge);
                    }
                    catch (Exception error)
                    {
                        StatusText.Text = Texts.Get("signin.retry", error.Message);
                        continue;
                    }

                    if (login != null)
                    {
                        await SignedInAsync(login);
                        return;
                    }

                    if (challenge.ChallengeUrl != shownUrl) ShowQr(challenge.ChallengeUrl);
                }
                StatusText.Text = Texts.Get("signin.expired");
            }
            finally
            {
                polling = false;
            }
        }

        private async Task SignedInAsync(LoginResult login)
        {
            session = new SteamSession
            {
                AccountName = login.AccountName,
                RefreshToken = login.RefreshToken,
                AccessToken = login.AccessToken,
                SteamId = SteamSession.SteamIdFromToken(
                    login.AccessToken ?? login.RefreshToken),
            };
            await session.SaveAsync();
            await ShowLibraryAsync();
        }

        private void ShowQr(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                var writer = new BarcodeWriterPixelData
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new QrCodeEncodingOptions
                    {
                        Width = 512,
                        Height = 512,
                        Margin = 1,
                        ErrorCorrection = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
                    },
                };
                var pixelData = writer.Write(url);

                var bitmap = new WriteableBitmap(pixelData.Width, pixelData.Height);
                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(pixelData.Pixels, 0, pixelData.Pixels.Length);
                }
                bitmap.Invalidate();
                QrImage.Source = bitmap;
                shownUrl = url;
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("signin.qrfailed", error.Message);
            }
        }

        // ------------------------------------------------------------- library

        private async Task ShowLibraryAsync()
        {
            SignInPanel.Visibility = Visibility.Collapsed;
            LibraryPanel.Visibility = Visibility.Visible;
            SearchHint.Visibility = Visibility.Visible;
            FilterHint.Visibility = Visibility.Visible;
            FilterChip.Visibility = Visibility.Visible;
            OptionsHint.Visibility = Visibility.Visible;
            AccountText.Text = Texts.Get("signed.title", session.AccountName ?? "");
            StatusText.Text = Texts.Get("steam.loading");

            try
            {
                await LoadTestedAsync();

                // His own games plus what the family shares: the client shows
                // both, so a list with only the owned ones reads as missing.
                var games = await SteamLibrary.OwnedAsync(session);
                var seen = new HashSet<uint>();
                allGames.Clear();
                foreach (var game in games)
                {
                    game.Tested = tested.Contains(game.AppId);
                    seen.Add(game.AppId);
                    allGames.Add(game);
                }
                foreach (var game in await SteamLibrary.FamilyAsync(session))
                {
                    if (!seen.Add(game.AppId)) continue;
                    game.Tested = tested.Contains(game.AppId);
                    allGames.Add(game);
                }

                await BuildShelvesAsync();
                ApplyFilter();
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("steam.failed", error.Message);
            }
        }

        /// <summary>
        /// Which games have been seen running on a console. The list is supplied
        /// from outside for now; the shared one is the next step.
        /// </summary>
        private async Task LoadTestedAsync()
        {
            tested.Clear();
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .TryGetItemAsync("tested.json") as StorageFile;
                if (file == null) return;
                var text = await FileIO.ReadTextAsync(file);
                if (!JsonObject.TryParse(text, out var root)) return;
                foreach (var value in root.GetNamedArray("appids"))
                {
                    tested.Add((uint)value.GetNumber());
                }
            }
            catch
            {
                // No list means nothing is marked, which is the honest default.
            }
        }

        /// <summary>
        /// The sidebar: everything, then his own collections in his own order,
        /// then the family. Counts come from what the account actually has.
        /// </summary>
        private async Task BuildShelvesAsync()
        {
            var owned = new HashSet<uint>(allGames.Select(g => g.AppId));
            hidden.Clear();
            Shelves.Clear();

            // The count has to be set before the shelf is added: the template
            // binds once, so a number filled in afterwards never reaches it.
            var loaded = await SteamShelves.LoadAsync();
            foreach (var shelf in loaded)
            {
                if (!shelf.IsHidden) continue;
                foreach (var id in shelf.Ids) hidden.Add(id);
            }

            Shelves.Add(new Shelf
            {
                Id = "all",
                Name = Texts.Get("steam.shelf.all"),
                IsAll = true,
                Count = allGames.Count(g => !hidden.Contains(g.AppId)),
            });

            foreach (var shelf in loaded)
            {
                shelf.Count = shelf.Ids.Count(id => owned.Contains(id));
                if (shelf.Count == 0 && !shelf.IsHidden) continue;
                Shelves.Add(shelf);
            }

            var shared = allGames.Count(g => g.Shared);
            if (shared > 0)
            {
                Shelves.Add(new Shelf
                {
                    Id = "family",
                    Name = Texts.Get("steam.shelf.family"),
                    IsFamily = true,
                    Count = shared,
                });
            }

            ShelfList.SelectedIndex = 0;
        }

        private Shelf Current =>
            ShelfList.SelectedItem as Shelf ?? (Shelves.Count > 0 ? Shelves[0] : null);

        private void OnShelfChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            FilterText.Text = Texts.Get(FilterKeys[filter]);
            var shelf = Current;

            var shown = allGames.Where(game =>
            {
                if (shelf != null)
                {
                    if (shelf.IsFamily && !game.Shared) return false;
                    else if (shelf.IsAll)
                    {
                        if (!showHidden && hidden.Contains(game.AppId)) return false;
                    }
                    else if (!shelf.IsFamily && !shelf.Ids.Contains(game.AppId)) return false;
                }
                if (query.Length > 0 &&
                    game.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
                switch (filter)
                {
                    case 1: return game.MinutesPlayed > 0;
                    case 2: return game.MinutesPlayed == 0;
                    case 3: return game.Tested;
                    default: return true;
                }
            }).ToList();

            Games.Clear();
            foreach (var game in shown) Games.Add(game);

            HeaderText.Text = Current?.Name ?? Texts.Get("steam.header");
            StatusText.Text = allGames.Count == 0
                ? Texts.Get("steam.empty")
                : Texts.Get("steam.showing", Games.Count, allGames.Count);

            if (Games.Count > 0)
            {
                GameGrid.UpdateLayout();
                GameGrid.SelectedIndex = 0;
            }
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            query = SearchBox.Text ?? string.Empty;
            ApplyFilter();
        }

        private void OnGameSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!(GameGrid.SelectedItem is OwnedGame game)) return;
            StatusText.Text = game.Name + "  ·  " + game.Played;
        }

        /// <summary>
        /// Installing a PC game is the next block of work; until the runtime
        /// exists, saying so plainly beats a button that does nothing.
        /// </summary>
        private void OnGameInvoked(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is OwnedGame game)) return;
            StatusText.Text = Texts.Get("steam.notyet", game.Name);
        }

        // ------------------------------------------------------------- input

        private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.GamepadB:
                case Windows.System.VirtualKey.Escape:
                    if (Frame.CanGoBack) Frame.GoBack();
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.GamepadY:
                case Windows.System.VirtualKey.F5:
                    if (session.IsSignedIn) await ShowLibraryAsync();
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.GamepadX:
                    if (session.IsSignedIn)
                    {
                        SearchBox.Visibility = Visibility.Visible;
                        SearchBox.Focus(FocusState.Programmatic);
                    }
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.GamepadLeftShoulder:
                    filter = (filter + FilterKeys.Length - 1) % FilterKeys.Length;
                    ApplyFilter();
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.GamepadRightShoulder:
                    filter = (filter + 1) % FilterKeys.Length;
                    ApplyFilter();
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.GamepadMenu:
                    // Two switches, cycled: titles off, then the hidden ones on.
                    if (OwnedGame.ShowTitles)
                    {
                        OwnedGame.ShowTitles = false;
                    }
                    else if (!showHidden)
                    {
                        showHidden = true;
                        OwnedGame.ShowTitles = true;
                    }
                    else
                    {
                        showHidden = false;
                    }
                    ApplyFilter();
                    e.Handled = true;
                    break;

                case Windows.System.VirtualKey.GamepadView:
                    if (session.IsSignedIn)
                    {
                        await session.ClearAsync();
                        allGames.Clear();
                        Games.Clear();
                        HeaderText.Text = Texts.Get("steam.header");
                        await StartSignInAsync();
                    }
                    e.Handled = true;
                    break;
            }
        }
    }
}
