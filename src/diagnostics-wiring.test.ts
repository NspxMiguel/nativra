import { expect, test } from "bun:test";

const page = await Bun.file("uwp/Kiosk/MainPage.Diagnostics.cs").text();
const graphics = await Bun.file("uwp/Kiosk/Native/GraphicsBridge.cs").text();
const xaml = await Bun.file("uwp/Kiosk/MainPage.xaml").text();
const texts = await Bun.file("uwp/Kiosk/Texts.cs").text();

test("the game overlay samples real counters without per-frame tracing", () => {
  expect(page).toContain("Interval = TimeSpan.FromSeconds(1)");
  expect(page).toContain("Interlocked.Read(ref Native.GraphicsBridge.Frames)");
  expect(page).toContain("Interlocked.Read(ref Native.FrameMirror.Shown)");
  expect(page).toContain("MemoryManager.AppMemoryUsageLimit");
  expect(page).toContain("AnalyticsInfo.VersionInfo");
  expect(page).toContain("diagnosticsTimer.Stop()");
  expect(page).not.toContain("File.Read");
  expect(page).not.toContain("Shim.Watched");
  expect(xaml.indexOf('x:Name="GameDiagnostics"')).toBeGreaterThan(
    xaml.indexOf('x:Name="GameImage"'),
  );
  expect(texts).toContain("not TV FPS");
  expect(texts).toContain("não FPS da TV");
  expect(texts).toContain("output unverified");
});

test("GPU diagnostics capture the original adapter before any naming override", () => {
  const capture = graphics.indexOf("ReportedAdapter = Marshal.PtrToStringUni");
  const override = graphics.indexOf("if (code != S_OK || !NameTheCard)");
  expect(capture).toBeGreaterThan(0);
  expect(capture).toBeLessThan(override);
  expect(graphics).toContain("Marshal.ReadInt64(desc, 272)");
  expect(texts).toContain("not a budget");
});
