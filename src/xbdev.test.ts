import { test, expect } from "bun:test";
import {
  human,
  isPackageFile,
  installOrder,
  isForThisConsole,
  parseIdentity,
  PACKAGE_EXTENSIONS,
} from "./util";
import { t } from "./i18n";
import catalog from "../catalog.json";

test("human formats sizes the way a person reads them", () => {
  expect(human(0)).toBe("0B");
  expect(human(896581)).toBe("876KB");
  expect(human(621834240)).toBe("593MB");
  expect(human(2 * 1024 * 1024 * 1024)).toBe("2.0GB");
});

test("package files are recognised regardless of case", () => {
  expect(isPackageFile("RetroArch.appx")).toBe(true);
  expect(isPackageFile("Kiosk_1.0.0.0_x64.MSIXBUNDLE")).toBe(true);
  expect(isPackageFile("xenia.appxbundle")).toBe(true);
  expect(isPackageFile("cert.cer")).toBe(true);
  expect(isPackageFile("readme.txt")).toBe(false);
  expect(isPackageFile("game.zip")).toBe(false);
});

test("install order puts the bundle first and the certificate last", () => {
  const files = [
    "Microsoft.VCLibs.x64.14.00.appx",
    "Kiosk.cer",
    "Kiosk_1.0.0.0_x64.msixbundle",
    "Microsoft.UI.Xaml.2.7.appx",
  ];
  const sorted = [...files].sort(installOrder);
  expect(sorted[0]).toBe("Kiosk_1.0.0.0_x64.msixbundle");
  expect(sorted[sorted.length - 1]).toBe("Kiosk.cer");
  // Frameworks sit between the app and the certificate.
  expect(sorted.slice(1, 3).every((f) => f.includes("Microsoft."))).toBe(true);
});

test("a lone appx still installs first when there is no bundle", () => {
  const sorted = ["Microsoft.VCLibs.x64.14.00.appx", "flycast-2.7.appx"].sort(
    installOrder,
  );
  expect(sorted[0]).toBe("flycast-2.7.appx");
});

test("translations interpolate and fall back instead of throwing", () => {
  expect(t("find.found", { host: "10.0.0.50" })).toContain("10.0.0.50");
  expect(t("kit.step", { n: 2, total: 14, name: "Dolphin" })).toContain("2");
  // An unknown key returns the key itself rather than blowing up mid-command.
  expect(t("nope.not.here")).toBe("nope.not.here");
});

test("every catalogue entry is installable and uniquely named", () => {
  const entries = [...catalog.packages, ...catalog.dependencies];
  const slugs = entries.map((entry) => entry.slug);
  expect(new Set(slugs).size).toBe(slugs.length);

  for (const entry of entries) {
    expect(entry.url.startsWith("https://")).toBe(true);
    const fileName = entry.url.split("/").pop()!;
    const installable =
      isPackageFile(fileName) || fileName.toLowerCase().endsWith(".zip");
    expect(installable).toBe(true);
    expect(entry.name.length).toBeGreaterThan(0);
  }
});

test("the catalogue actually carries native PC games, not just emulators", () => {
  const pcGames = catalog.packages.filter((entry) => entry.kind === "pc-game");
  expect(pcGames.length).toBeGreaterThanOrEqual(5);
  const names = pcGames.map((entry) => entry.name);
  expect(names).toContain("GZDoom");
  expect(names).toContain("DOSBox Pure");
});

test("PACKAGE_EXTENSIONS covers what the Device Portal accepts", () => {
  expect(PACKAGE_EXTENSIONS).toContain(".msixbundle");
  expect(PACKAGE_EXTENSIONS).toContain(".appx");
});

test("packages for other architectures are left behind", () => {
  expect(isForThisConsole("out/Dependencies/x64/Microsoft.VCLibs.x64.14.00.appx")).toBe(true);
  expect(isForThisConsole("out/Dependencies/arm64/Microsoft.VCLibs.ARM64.14.00.appx")).toBe(false);
  expect(isForThisConsole("out/Dependencies/x86/Microsoft.NET.Native.Runtime.2.2.appx")).toBe(false);
  expect(isForThisConsole("out/Kiosk_1.0.0.0_x64.msixbundle")).toBe(true);
  expect(isForThisConsole("pacotes/retroarch/RetroArch-SeriesConsoles-AllCores.appx")).toBe(true);
});

test("a manifest tells us the package identity and whether the console takes it", () => {
  const x64 = parseIdentity(
    "gzdoom.msixbundle",
    '<Identity Name="d19ff9e2" Version="1.0.7.0" ProcessorArchitecture="x64" />',
  );
  expect(x64.ok).toBe(true);
  expect(x64.name).toBe("d19ff9e2");
  expect(x64.version).toBe("1.0.7.0");

  // A bundle lists one architecture per contained package.
  const bundle = parseIdentity(
    "raze.msixbundle",
    '<Identity Name="raze" Version="1.0.22.0" /><Package ProcessorArchitecture="x64" /><Package ProcessorArchitecture="arm64" />',
  );
  expect(bundle.ok).toBe(true);
  expect(bundle.architectures).toContain("arm64");

  // ARM-only would install on nothing this console runs.
  const armOnly = parseIdentity(
    "wrong.appx",
    '<Identity Name="wrong" Version="1.0.0.0" ProcessorArchitecture="arm64" />',
  );
  expect(armOnly.ok).toBe(false);
  expect(armOnly.problem).toContain("no x64");

  // A truncated or wrong file has no Identity at all.
  const junk = parseIdentity("junk.appx", "<html>404</html>");
  expect(junk.ok).toBe(false);
});
