using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Vapor
{
    sealed partial class App : Application
    {
        public App() { InitializeComponent(); }

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
