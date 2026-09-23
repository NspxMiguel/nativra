import { expect, test } from "bun:test";

const audio = await Bun.file("uwp/Kiosk/Native/AudioBridge.cs").text();

test("endpoint properties expose the device description before client activation", () => {
  expect(audio).toContain(
    "Is(key, FriendlyNameGroup, 14) || Is(key, FriendlyNameGroup, 2)",
  );
  expect(audio).toContain(
    'Is(key, FriendlyNameGroup, 2) ? "Speakers" : "Xbox"',
  );
  expect(audio).toContain(
    "Marshal.WriteInt32(result, 16, 2); // PKEY_Device_DeviceDesc",
  );
  expect(audio).toContain("Marshal.WriteInt32(result, 3);");
  expect(audio).toContain("Marshal.WriteInt16(value, 0, 31);");
});
