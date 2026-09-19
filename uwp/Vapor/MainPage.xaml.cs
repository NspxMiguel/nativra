using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.QrCode;

namespace Vapor
{
    public sealed partial class MainPage : Page
    {
        private QrSession session;
        private string shownUrl;

        public MainPage()
        {
            InitializeComponent();
            Title.Text = Texts.Get("signin.title");
            Subtitle.Text = Texts.Get("signin.how");
            Loaded += async (s, e) => await StartAsync();
        }

        private async Task StartAsync()
        {
            Status.Text = Texts.Get("signin.asking");
            try
            {
                session = await SteamAuth.BeginAsync("Xbox Series X");
            }
            catch (Exception error)
            {
                Status.Text = Texts.Get("signin.failed", error.Message);
                return;
            }

            ShowQr(session.ChallengeUrl);
            Status.Text = Texts.Get("signin.waiting");
            await PollLoopAsync();
        }

        /// <summary>
        /// Steam rotates the challenge while nobody has approved, so the code on
        /// screen has to follow it or the scan stops working.
        /// </summary>
        private async Task PollLoopAsync()
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, session.Interval)));

                LoginResult login;
                try
                {
                    login = await SteamAuth.PollAsync(session);
                }
                catch (Exception error)
                {
                    Status.Text = Texts.Get("signin.retry", error.Message);
                    continue;
                }

                if (login != null)
                {
                    await SignedInAsync(login);
                    return;
                }

                if (session.ChallengeUrl != shownUrl) ShowQr(session.ChallengeUrl);
            }

            Status.Text = Texts.Get("signin.expired");
        }

        private async Task SignedInAsync(LoginResult login)
        {
            QrFrame.Visibility = Visibility.Collapsed;
            Title.Text = Texts.Get("signed.title", login.AccountName ?? "");
            Subtitle.Text = Texts.Get("signed.next");
            Status.Text = string.Empty;

            // The refresh token is what keeps the console signed in; it stays in
            // the app's own storage and never leaves the console.
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(
                    "session.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file,
                    (login.AccountName ?? "") + "\n" + (login.RefreshToken ?? ""));
            }
            catch
            {
                // Storage failing must not undo a successful sign-in.
            }
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
                Status.Text = Texts.Get("signin.qrfailed", error.Message);
            }
        }
    }
}
