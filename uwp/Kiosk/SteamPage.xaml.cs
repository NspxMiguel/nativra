using System;
using System.Collections.ObjectModel;
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
            HintSignOutText.Text = Texts.Get("hint.signout");
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
            SignOutHint.Visibility = Visibility.Collapsed;
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
            SignOutHint.Visibility = Visibility.Visible;
            AccountText.Text = Texts.Get("signed.title", session.AccountName ?? "");
            StatusText.Text = Texts.Get("steam.loading");

            try
            {
                var games = await SteamLibrary.OwnedAsync(session);
                Games.Clear();
                foreach (var game in games) Games.Add(game);

                StatusText.Text = Games.Count == 0
                    ? Texts.Get("steam.empty")
                    : Texts.Get("steam.count", Games.Count);

                if (Games.Count > 0)
                {
                    GameGrid.UpdateLayout();
                    GameGrid.SelectedIndex = 0;
                    (GameGrid.ContainerFromIndex(0) as Control)?.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception error)
            {
                StatusText.Text = Texts.Get("steam.failed", error.Message);
            }
        }

        private void OnGameSelected(object sender, SelectionChangedEventArgs e)
        {
            if (!(GameGrid.SelectedItem is OwnedGame game)) return;
            HeaderText.Text = game.Name;
            StatusText.Text = game.Played;
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
                        await session.ClearAsync();
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
