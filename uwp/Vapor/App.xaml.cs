using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Vapor
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // On a console there is no mouse: leaving pointer mode on draws a
            // cursor over everything and makes the app read as a browser window.
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            if (!(Window.Current.Content is Frame frame))
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null) frame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
        }
    }
}
