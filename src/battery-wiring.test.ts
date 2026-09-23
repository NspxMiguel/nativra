import { expect, test } from "bun:test";

const view = await Bun.file("uwp/Kiosk/MainPage.xaml").text();
const tokens = await Bun.file("uwp/Kiosk/Tokens.xaml").text();

test("battery icon is smaller than the controller without shrinking its percentage", () => {
  expect(tokens).toContain('<x:Double x:Key="BatteryIconWidth">36</x:Double>');
  expect(tokens).toContain('<x:Double x:Key="BatteryIconHeight">18</x:Double>');
  expect(view).toContain('<Viewbox Width="{StaticResource BatteryIconWidth}"');
  expect(view).toContain(
    'x:Name="BatteryText" FontSize="{StaticResource BodySize}"',
  );
});
