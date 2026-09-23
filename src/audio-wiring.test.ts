import { expect, test } from "bun:test";

const audio = await Bun.file("uwp/Kiosk/Native/AudioBridge.cs").text();
const proxy = await Bun.file("uwp/Kiosk/Native/ComProxy.cs").text();

test("asynchronous audio activation keeps an agile callback alive per request", () => {
  expect(audio).toContain("CompletedDelegate completed = (self, operation)");
  expect(audio).toContain("Proxy.Keep(completed);");
  expect(audio).toContain("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
  expect(audio).not.toContain("private static CompletedDelegate completed;");
});

test("audio endpoint data flow uses its own vtable and a shared COM identity", () => {
  expect(audio).toContain(
    'EndpointInterface = "1be09788-6894-4089-8586-9a2a6c265ac5"',
  );
  expect(audio).toContain(
    "Proxy.LinkInterfacePair(device, DeviceInterface, endpointView, EndpointInterface)",
  );
  expect(audio).toContain("Marshal.WriteInt32(result, 0); // eRender");
  expect(proxy).toContain('["00000000-0000-0000-c000-000000000046"] = first');
  expect(proxy).toContain("Marshal.WriteIntPtr(result, target);");
  expect(proxy).toContain("Marshal.WriteIntPtr(result, IntPtr.Zero);");
});

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
  expect(audio).toContain("Marshal.WriteInt32(result, 4);");
  expect(audio).toContain("Is(key, InterfaceNameGroup, 2)");
  expect(audio).toContain("PKEY_DeviceInterface_FriendlyName");
  expect(audio).toContain("Marshal.WriteInt16(value, 0, 31);");
});
