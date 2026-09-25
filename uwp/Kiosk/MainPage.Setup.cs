using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Kiosk
{
    public sealed partial class MainPage
    {
        private bool setupOpen;

        private async void OnSetupClicked(object sender, RoutedEventArgs e)
        {
            if (setupOpen || gameLaunchPending || Native.NativeProbe.GameRunning) return;
            setupOpen = true;
            try
            {
                var mode = new ComboBox { Header = Texts.Get("setup.input"), HorizontalAlignment = HorizontalAlignment.Stretch };
                mode.Items.Add(Texts.Get("setup.pc"));
                mode.Items.Add(Texts.Get("setup.controller"));
                mode.SelectedIndex = Settings.DesktopInput ? 0 : 1;
                var sensitivity = new Slider
                {
                    Header = Texts.Get("setup.sensitivity"),
                    Minimum = 0.25,
                    Maximum = 2,
                    StepFrequency = 0.25,
                    Value = Settings.PointerSensitivity,
                };
                var diagnostics = new ToggleSwitch
                {
                    Header = Texts.Get("setup.diagnostics"),
                    IsOn = Settings.ShowDiagnostics,
                    OnContent = Texts.Get("setup.on"),
                    OffContent = Texts.Get("setup.off"),
                };
                var inputHint = new ToggleSwitch
                {
                    Header = Texts.Get("setup.inputhint"),
                    IsOn = Settings.ShowInputHint,
                    OnContent = Texts.Get("setup.on"),
                    OffContent = Texts.Get("setup.off"),
                };
                var content = new StackPanel { Spacing = 20 };
                content.Children.Add(new TextBlock { Text = Texts.Get("setup.hint"), TextWrapping = TextWrapping.Wrap });
                content.Children.Add(mode);
                content.Children.Add(sensitivity);
                content.Children.Add(diagnostics);
                content.Children.Add(inputHint);
                var dialog = new ContentDialog
                {
                    Title = Texts.Get("setup.title"),
                    Content = content,
                    PrimaryButtonText = Texts.Get("setup.save"),
                    CloseButtonText = Texts.Get("setup.cancel"),
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await Settings.SetInputAsync(mode.SelectedIndex == 0, sensitivity.Value, diagnostics.IsOn, inputHint.IsOn);
                    Native.ControllerMode.Desktop = Settings.DesktopInput;
                    Native.ControllerMode.Changes++;
                    StatusText.Text = Texts.Get("setup.applied");
                }
            }
            catch (System.Exception)
            {
                StatusText.Text = Texts.Get("setup.failed");
            }
            finally
            {
                setupOpen = false;
                SetupButton.Focus(FocusState.Programmatic);
            }
        }
    }
}
