using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace Kiosk
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // On a console there is no mouse: leaving pointer mode on draws a
            // cursor over everything and makes the app read as a browser window.
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;

            // A console app is laid out inside the title-safe area by default,
            // which is the border he sees. Drawing into the whole core window
            // fills the screen; the safe margin is then ours to keep.
            Windows.UI.ViewManagement.ApplicationView.GetForCurrentView()
                .SetDesiredBoundsMode(
                    Windows.UI.ViewManagement.ApplicationViewBoundsMode.UseCoreWindow);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Draw into the whole screen: on a TV the system chrome only steals space.
            ApplicationView.PreferredLaunchWindowingMode = ApplicationViewWindowingMode.FullScreen;

            if (!(Window.Current.Content is Frame frame))
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            if (frame.Content == null)
            {
                frame.Navigate(typeof(MainPage), e.Arguments);
            }

            Window.Current.Activate();
        }
    }
}
