using System;
using System.Diagnostics;
using System.Threading;
using Windows.ApplicationModel;
using Windows.Gaming.Input;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Xaml;

namespace Kiosk
{
    public sealed partial class MainPage
    {
        private DispatcherTimer diagnosticsTimer;
        private long diagnosticTimestamp;
        private long diagnosticPresents;
        private long diagnosticShown;
        private string diagnosticSystem;

        private void InitializeDiagnostics()
        {
            // No per-frame callbacks, disk reads, network requests or native
            // import tracing. Sampling happens once a second on the UI thread.
            diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            diagnosticsTimer.Tick += (sender, args) => UpdateDiagnostics();
            Loaded += (sender, args) => diagnosticsTimer.Start();
            Unloaded += (sender, args) => diagnosticsTimer.Stop();
        }

        private void UpdateDiagnostics()
        {
            var now = Stopwatch.GetTimestamp();
            var presents = Interlocked.Read(ref Native.GraphicsBridge.Frames);
            var shown = Interlocked.Read(ref Native.FrameMirror.Shown);
            var elapsed = (now - diagnosticTimestamp) / (double)Stopwatch.Frequency;
            var validSample = diagnosticTimestamp != 0 && elapsed > 0;
            var presentRate = validSample ? Math.Max(0, presents - diagnosticPresents) / elapsed : 0;
            var shownRate = validSample ? Math.Max(0, shown - diagnosticShown) / elapsed : 0;
            diagnosticTimestamp = now;
            diagnosticPresents = presents;
            diagnosticShown = shown;

            GameDiagnostics.Visibility = Native.NativeProbe.GameRunning &&
                GameImage.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (GameDiagnostics.Visibility != Visibility.Visible) return;

            try
            {
                if (diagnosticSystem == null)
                {
                    var info = AnalyticsInfo.VersionInfo;
                    var packed = ulong.Parse(info.DeviceFamilyVersion);
                    var version = string.Format("{0}.{1}.{2}.{3}", packed >> 48,
                        (packed >> 32) & 65535, (packed >> 16) & 65535, packed & 65535);
                    // Product model is public hardware information. Never show
                    // device names, account identifiers, IPs or authentication.
                    var model = new EasClientDeviceInformation().SystemProductName;
                    diagnosticSystem = Texts.Get("debug.system", model, info.DeviceFamily, version);
                }
                var package = Package.Current.Id;
                var v = package.Version;
                var audio = Native.AudioBridge.ClientAcquired
                    ? Texts.Get("debug.audio.client")
                    : Texts.Get("debug.audio.pending", Native.AudioBridge.LastActivationResult.ToString("X8"));
                GameDiagnosticsText.Text = string.Join("\n", new[]
                {
                    Texts.Get("debug.title", $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", package.Architecture),
                    diagnosticSystem,
                    Texts.Get("debug.gpu", Native.GraphicsBridge.ReportedAdapter ?? Texts.Get("debug.unknown"),
                        Native.GraphicsBridge.ReportedVendor.ToString("X4"), Native.GraphicsBridge.ReportedDevice.ToString("X4")),
                    Texts.Get("debug.vram", (Native.GraphicsBridge.ReportedVideoMemory / 1048576.0).ToString("F0"),
                        Native.GraphicsBridge.ReportedFeatureLevel.ToString("X")),
                    Texts.Get("debug.render", Native.FrameMirror.PixelWidth, Native.FrameMirror.PixelHeight),
                    Texts.Get("debug.frames", presentRate.ToString("F1"), shownRate.ToString("F1")),
                    Native.FrameMirror.Timing,
                    Texts.Get("debug.memory", (MemoryManager.AppMemoryUsage / 1048576.0).ToString("F0"),
                        (MemoryManager.AppMemoryUsageLimit / 1048576.0).ToString("F0")),
                    Texts.Get("debug.input", Gamepad.Gamepads.Count, Interlocked.Read(ref Native.PadBridge.Reads),
                        Native.PointerBridge.Keys),
                    audio,
                    Texts.Get("debug.sample", DateTime.Now.ToString("HH:mm:ss")),
                });
            }
            catch
            {
                GameDiagnosticsText.Text = Texts.Get("debug.unavailable");
            }
        }
    }
}
