import { expect, test } from "bun:test";

test("the rejected generated splash and cover are not shipped by the app", async () => {
  const manifest = await Bun.file("uwp/Kiosk/Package.appxmanifest").text();
  const project = await Bun.file("uwp/Kiosk/Kiosk.csproj").text();
  expect(manifest).not.toContain("uap:SplashScreen");
  expect(project).not.toContain('Content Include="Assets\\SplashScreen.png"');
  expect(project).not.toContain('Content Include="Assets\\NativraCover.png"');
  expect(manifest).toContain('DisplayName="Nativra"');
});
