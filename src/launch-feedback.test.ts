import { expect, test } from "bun:test";

const page = await Bun.file("uwp/Kiosk/MainPage.xaml.cs").text();
const xaml = await Bun.file("uwp/Kiosk/MainPage.xaml").text();
const mirror = await Bun.file("uwp/Kiosk/Native/FrameMirror.cs").text();

test("game launch acknowledges input before waiting and suppresses duplicate launches", () => {
  const launch = page.slice(page.indexOf("private async Task StartGameAsync"));
  expect(launch).toContain(
    "if (gameLaunchPending || Native.NativeProbe.GameRunning) return;",
  );
  expect(
    launch.indexOf("GameLoading.Visibility = Visibility.Visible"),
  ).toBeLessThan(launch.indexOf("await Native.NativeProbe.RunAsync"));
  expect(launch).toContain("gameLaunchPending = false;");
  expect(xaml).toContain('x:Name="GameLoadingRing"');
});

test("the virtual pointer is visible at its actual input coordinates", () => {
  expect(page).toContain("GamePointerTransform.X = Native.PointerBridge.X;");
  expect(page).toContain("GamePointerTransform.Y = Native.PointerBridge.Y;");
  expect(xaml).toContain('x:Name="GamePointerTransform"');
  expect(xaml).toContain('x:Name="GameInputHint"');
  expect(mirror).toContain("Presented?.Invoke();");
});
