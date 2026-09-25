import { expect, test } from "bun:test";

test("setup persists real input choices without replacing download preferences", async () => {
  const settings = await Bun.file("uwp/Kiosk/Settings.cs").text();
  const setup = await Bun.file("uwp/Kiosk/MainPage.Setup.cs").text();
  const pointer = await Bun.file("uwp/Kiosk/Native/PointerBridge.cs").text();
  for (const key of [
    "downloadRoot",
    "desktopInput",
    "pointerSensitivity",
    "showDiagnostics",
  ])
    expect(settings.split(`\"${key}\"`).length).toBe(3);
  expect(setup).toContain("ContentDialogResult.Primary");
  expect(setup).toContain("Settings.SetInputAsync");
  expect(pointer).toContain(
    "var distance = Speed * Settings.PointerSensitivity * seconds;",
  );
  expect(pointer).toContain(
    "position.Move(dx * distance, -dy * distance, Width, Height);",
  );
});

test("hiding diagnostics preserves the input overlay and launch defaults", async () => {
  const diagnostics = await Bun.file(
    "uwp/Kiosk/MainPage.Diagnostics.cs",
  ).text();
  const page = await Bun.file("uwp/Kiosk/MainPage.xaml.cs").text();
  expect(diagnostics).toContain(
    "GameDiagnosticsPanel.Visibility = Settings.ShowDiagnostics",
  );
  const launch = page.slice(page.indexOf("private async Task StartGameAsync"));
  expect(
    launch.indexOf("ControllerMode.Desktop = Settings.DesktopInput"),
  ).toBeLessThan(launch.indexOf("await Native.NativeProbe.RunAsync"));
  // A gear icon, still named for screen readers in the chosen language.
  expect(page).toContain('SetupButton.Content = "\\uE713"');
  expect(page).toContain(
    'AutomationProperties.SetName(SetupButton, Texts.Get("setup.title"))',
  );
  expect(page).toContain("SetupButton.Focus(FocusState.Programmatic)");
  expect(page).toContain(
    "if (FocusManager.GetFocusedElement() == SetupButton) FocusShelf()",
  );
});
