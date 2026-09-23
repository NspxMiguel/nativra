import { expect, test } from "bun:test";

const view = await Bun.file("uwp/Kiosk/MainPage.xaml").text();
const page = await Bun.file("uwp/Kiosk/MainPage.xaml.cs").text();

test("home removes redundant branding without removing Steam sign-in", () => {
  expect(view).not.toContain('Text="Nativra"');
  expect(view).not.toContain('x:Name="AvatarButton"');
  expect(view).toContain('Click="OnSignInClicked"');
});

test("dock navigation tracks its destination independently of focus color", () => {
  expect(page).toContain('private string activeDestination = "library";');
  expect(page).toContain('(icon.Tag as string) == activeDestination');
  expect(page).not.toContain('icon.Background == Application.Current.Resources["Accent"]');
  expect(page).not.toContain('icon.Background = active ? lit : dim;');
  const library = view.match(/<Button x:Name="DockLibrary"[^>]+>/)?.[0];
  expect(library).toBeDefined();
  expect(library).not.toContain('Background="{StaticResource Accent}"');
});

test("portrait covers fill a two-to-three tile without colored side bands", () => {
  expect(page).toContain("public double TileWidth => 220;");
  expect(page).toContain("public double TileHeight => 330;");
  expect(view).toContain('ImageSource="{x:Bind Icon}" Stretch="UniformToFill"');
});
