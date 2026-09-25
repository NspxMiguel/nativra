import { expect, test } from "bun:test";

// Guards for fixes measured on the console. Each test names the failure it
// prevents, so a change that breaks one says what it will break.
const imports = await Bun.file("uwp/Kiosk/Native/SystemImports.cs").text();
const loader = await Bun.file("uwp/Kiosk/Native/LoaderStubs.cs").text();
const hid = await Bun.file("uwp/Kiosk/Native/HidBridge.cs").text();
const pad = await Bun.file("uwp/Kiosk/Native/PadBridge.cs").text();
const steam = await Bun.file("uwp/Kiosk/Native/SteamBridge.cs").text();
const fileWatch = await Bun.file("uwp/Kiosk/Native/FileWatch.cs").text();
const download = await Bun.file("uwp/Kiosk/Steam/SteamDownload.cs").text();
const cm = await Bun.file("uwp/Kiosk/Steam/SteamCm.cs").text();

test("console shell libraries never resolve to the system (Starting the game hang)", () => {
  for (const name of ["USER32.dll", "SETUPAPI.dll", "HID.DLL"]) {
    expect(imports).toContain(`"${name}"`);
  }
  expect(imports).toContain("if (NeverFromSystem.Contains(name))");
  expect(loader).toContain("if (SystemImports.IsShell(name)) return Invent(name);");
});

test("desktop C++ runtime falls back to the framework's _app build", () => {
  expect(imports).toContain('return stem + "_app.dll";');
  expect(imports).toContain('stem.StartsWith("msvcp140")');
});

test("the pad is listed through SetupAPI so Unity reads XInput", () => {
  expect(hid).toContain("ig_00");
  expect(hid).toContain('["SetupDiGetClassDevsA"]');
  expect(hid).toContain('["SetupDiGetDeviceInterfaceDetailW"]');
  // Chained through a hook: converting FileWatch's pointer back into another
  // delegate type throws InvalidCastException under .NET Native.
  expect(hid).toContain("FileWatch.Intercept =");
  expect(fileWatch).toContain("public static Func<IntPtr, IntPtr> Intercept;");
});

test("slot 0 stays present and pads are followed through GamepadAdded", () => {
  expect(pad).toContain("return index == 0 || index < (uint)pads.Count;");
  expect(pad).toContain("Gamepad.GamepadAdded +=");
  expect(pad).toContain("pressed |= KeyButtons(PointerBridge.HostKeys)");
});

test("the Steam bridge answers only Steam's own exports", () => {
  expect(steam).toContain('export.StartsWith("SteamAPI_", StringComparison.Ordinal)');
  expect(steam).toContain('export != "SteamClient"');
});

test("downloads skip unlicensed depots and Steam connects are bounded", () => {
  expect(download).toContain("catch (Exception) when (depots.Count > 1)");
  expect(download).toContain('if (licensed == 0) throw');
  expect(cm).toContain("Task.Delay(10000)");
  expect(cm).toContain("public static async Task<SteamCm> ConnectAnyAsync()");
});
