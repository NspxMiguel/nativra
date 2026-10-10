using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Kiosk
{
    public sealed partial class MainPage
    {
        private DispatcherTimer quickAccessTimer;
        private int quickAccessRequests;
        private bool quickAccessOpen;
        private bool? quickStats;

        private void InitializeQuickAccess()
        {
            QuickAccessButton.Content = Texts.Get("quick.title");
            quickAccessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            quickAccessTimer.Tick += (sender, args) =>
            {
                var requests = System.Threading.Volatile.Read(ref Native.ControllerMode.QuickAccessRequests);
                if (requests == quickAccessRequests) return;
                quickAccessRequests = requests;
                if (Native.NativeProbe.GameRunning) OnQuickAccessClicked(QuickAccessButton, null);
            };
            Loaded += (sender, args) => quickAccessTimer.Start();
            Unloaded += (sender, args) => quickAccessTimer.Stop();
        }

        private async void OnQuickAccessClicked(object sender, RoutedEventArgs e)
        {
            if (quickAccessOpen || setupOpen || (gameLaunchPending && !Native.NativeProbe.GameRunning)) return;
            quickAccessOpen = true;
            var focus = Windows.UI.Xaml.Input.FocusManager.GetFocusedElement() as Control;
            Native.ControllerMode.ShellOpen = true;
            Native.PointerBridge.ReleaseHostKeys();
            try
            {
                var cappedRate = Native.GraphicsBridge.Ceiling > 0 ? Native.GraphicsBridge.Ceiling
                    : GameProfileStore.Current.FrameLimit == 30 ? 30 : 60;
                var limit = new ToggleSwitch
                {
                    Header = Texts.Get("quick.limit"),
                    IsOn = Native.GraphicsBridge.Ceiling != 0,
                    OnContent = Texts.Get("setup.on"),
                    OffContent = Texts.Get("setup.off"),
                };
                limit.Toggled += (s, args) => Native.GraphicsBridge.Ceiling = limit.IsOn ? cappedRate : 0;
                var stats = new ToggleSwitch
                {
                    Header = Texts.Get("quick.stats"),
                    IsOn = quickStats ?? Settings.ShowDiagnostics,
                    OnContent = Texts.Get("setup.on"),
                    OffContent = Texts.Get("setup.off"),
                };
                stats.Toggled += (s, args) => { quickStats = stats.IsOn; UpdateDiagnostics(); };
                var volume = new Slider
                {
                    Header = Texts.Get("quick.volume"),
                    Minimum = 0,
                    Maximum = 100,
                    StepFrequency = 5,
                    Value = Native.AudioBridge.HostVolume * 100,
                    IsEnabled = Native.AudioBridge.TrySetHostVolume(Native.AudioBridge.HostVolume),
                };
                var notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
                if (!volume.IsEnabled) notice.Text = Texts.Get("quick.volume.unavailable");
                volume.ValueChanged += (s, args) =>
                {
                    if (!Native.AudioBridge.TrySetHostVolume((float)(volume.Value / 100)))
                        notice.Text = Texts.Get("quick.volume.unavailable");
                };
                var screenshot = new Button
                {
                    Content = Texts.Get("quick.screenshot"),
                    Style = Application.Current.Resources["ChipStyle"] as Style,
                    IsEnabled = Native.FrameMirror.Running,
                };
                screenshot.Click += async (s, args) =>
                {
                    screenshot.IsEnabled = false;
                    try { notice.Text = Texts.Get("quick.captured", await Native.FrameMirror.SaveScreenshotAsync()); }
                    catch { notice.Text = Texts.Get("quick.capture.failed"); }
                    finally { screenshot.IsEnabled = Native.FrameMirror.Running; }
                };
                var stop = new Button
                {
                    Content = Texts.Get("quick.stop"),
                    IsEnabled = Native.NativeProbe.GameRunning,
                    Style = Application.Current.Resources["ChipStyle"] as Style,
                    Foreground = Application.Current.Resources["Danger"] as Windows.UI.Xaml.Media.Brush,
                };
                stop.Click += (s, args) => Windows.ApplicationModel.Core.CoreApplication.Exit();
                var content = new StackPanel { Spacing = 20 };
                content.Children.Add(limit);
                content.Children.Add(stats);
                content.Children.Add(volume);
                content.Children.Add(screenshot);
                content.Children.Add(notice);
                content.Children.Add(new TextBlock { Text = Texts.Get("quick.stop.hint"), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(stop);
                await new ContentDialog
                {
                    Title = Texts.Get("quick.title"),
                    Content = content,
                    CloseButtonText = Texts.Get("quick.close"),
                }.ShowAsync();
            }
            catch { StatusText.Text = Texts.Get("quick.failed"); }
            finally
            {
                Native.PointerBridge.ReleaseHostKeys();
                Native.ControllerMode.ShellOpen = false;
                quickAccessOpen = false;
                focus?.Focus(FocusState.Programmatic);
            }
        }

        private void OnTileContextRequested(object sender, RightTappedRoutedEventArgs e)
        {
            ShowGameContext(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowGameContext(FrameworkElement anchor)
        {
            if (setupOpen || quickAccessOpen || gameLaunchPending || Native.NativeProbe.GameRunning ||
                !(anchor?.Tag is Tile tile) || tile.SteamAppId == 0) return;
            var menu = new MenuFlyout();
            var profile = new MenuFlyoutItem { Text = Texts.Get("profile.title") };
            profile.Click += (s, args) => EditGameProfile(tile.SteamAppId, anchor as Control);
            menu.Items.Add(profile);
            menu.ShowAt(anchor);
        }

        private async void EditGameProfile(uint appId, Control focus)
        {
            if (setupOpen || quickAccessOpen || gameLaunchPending || Native.NativeProbe.GameRunning) return;
            setupOpen = true;
            try
            {
                var profile = await GameProfileStore.LoadAsync(appId);
                var limit = new ComboBox { Header = Texts.Get("profile.limit"), HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var value in new[] { Texts.Get("profile.unlimited"), "30", "60" }) limit.Items.Add(value);
                limit.SelectedIndex = profile.FrameLimit == 0 ? 0 : profile.FrameLimit == 30 ? 1 : 2;
                var scale = new ComboBox { Header = Texts.Get("profile.scale"), HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var value in new[] { "50%", "75%", "100%" }) scale.Items.Add(value);
                scale.SelectedIndex = profile.ResolutionScale == 0.5 ? 0 : profile.ResolutionScale == 0.75 ? 1 : 2;
                var interpreter = new ToggleSwitch
                {
                    Header = Texts.Get("profile.interpreter"),
                    IsOn = profile.ForceInterpreter,
                    OnContent = Texts.Get("setup.on"),
                    OffContent = Texts.Get("setup.off"),
                };
                var layout = new ComboBox { Header = Texts.Get("profile.layout"), HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var value in new[] { Texts.Get("profile.default"), Texts.Get("setup.pc"), Texts.Get("setup.controller") }) layout.Items.Add(value);
                layout.SelectedIndex = profile.ControllerLayout == "desktop" ? 1 : profile.ControllerLayout == "native" ? 2 : 0;
                var content = new StackPanel { Spacing = 20 };
                content.Children.Add(limit);
                content.Children.Add(scale);
                content.Children.Add(new TextBlock { Text = Texts.Get("profile.scale.hint"), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(interpreter);
                content.Children.Add(layout);
                var dialog = new ContentDialog
                {
                    Title = Texts.Get("profile.title"),
                    Content = content,
                    PrimaryButtonText = Texts.Get("setup.save"),
                    CloseButtonText = Texts.Get("setup.cancel"),
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    profile.FrameLimit = new[] { 0, 30, 60 }[limit.SelectedIndex];
                    profile.ResolutionScale = new[] { 0.5, 0.75, 1 }[scale.SelectedIndex];
                    profile.ForceInterpreter = interpreter.IsOn;
                    profile.ControllerLayout = new[] { "default", "desktop", "native" }[layout.SelectedIndex];
                    await GameProfileStore.SaveAsync(appId, profile);
                    StatusText.Text = Texts.Get("profile.saved");
                }
            }
            catch { StatusText.Text = Texts.Get("profile.failed"); }
            finally { setupOpen = false; focus?.Focus(FocusState.Programmatic); }
        }
    }
}
