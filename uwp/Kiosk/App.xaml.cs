using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Core;
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

            // The app has vanished mid-session more than once with nothing to
            // show for it: no console crash dump (those only exist for a
            // native access violation; a .NET exception that reaches here
            // ends the process a different way), no log line, nothing. This
            // is the one place that sees it before the process is gone.
            UnhandledException += (sender, args) =>
            {
                WriteCrashLog("UnhandledException", args.Exception, args.Message);
                // Leaving args.Handled false: swallowing it would run the app
                // on in a state nobody chose, which is worse than the crash.
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                WriteCrashLog("UnobservedTaskException", args.Exception, null);
                args.SetObserved();
            };
        }

        /// <summary>
        /// Best-effort, synchronous: by the time this runs the process may be
        /// on its way out, so no awaited I/O — a plain Win32 file write, done
        /// before OnLaunched even has a chance to run again.
        /// </summary>
        private static void WriteCrashLog(string source, Exception error, string extra)
        {
            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + source + ": "
                    + (error?.GetType().Name ?? "?") + " 0x" + (error?.HResult ?? 0).ToString("X8")
                    + " " + (error?.Message ?? extra ?? string.Empty)
                    + "\r\n" + (error?.StackTrace ?? string.Empty) + "\r\n---\r\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(local, "crash-log.txt"), line);
            }
            catch
            {
                // If even this fails, there was never going to be a report.
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Draw into the whole screen: on a TV the system chrome only steals space.
            ApplicationView.PreferredLaunchWindowingMode = ApplicationViewWindowingMode.FullScreen;

            // A console app is laid out inside the title-safe area by default,
            // which is the border around everything. Drawing into the whole
            // core window fills the screen; the safe margin is then ours to
            // keep. It belongs here and not in the constructor: there is no
            // view yet when the application object is built, and asking for one
            // there throws before the first screen is ever drawn.
            try
            {
                ApplicationView.GetForCurrentView()
                    .SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);
            }
            catch
            {
                // Not every host allows it; the title-safe layout still works.
            }

            var isNewFrame = !(Window.Current.Content is Frame);
            if (!(Window.Current.Content is Frame frame))
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            if (frame.Content == null)
            {
                frame.Navigate(typeof(MainPage), e.Arguments);
            }

            // The controller's B press reaches the app as this event, not as
            // a key press a page could catch on its own — nothing hooked it,
            // so Xbox's own default ran instead: leaving the whole app for
            // Home, from any screen, exactly like pressing it on the Home
            // screen itself does. Send it to the frame's own back stack
            // first; only the root screen (nothing to go back to) still
            // falls through to that default, which is the right one there.
            if (isNewFrame)
            {
                SystemNavigationManager.GetForCurrentView().BackRequested += (sender, args) =>
                {
                    if (frame.CanGoBack)
                    {
                        frame.GoBack();
                        args.Handled = true;
                    }
                };
            }

            Window.Current.Activate();
        }
    }
}
