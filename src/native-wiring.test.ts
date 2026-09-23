import { expect, test } from "bun:test";

const probe = await Bun.file("uwp/Kiosk/Native/NativeProbe.cs").text();
const tls = await Bun.file("uwp/Kiosk/Native/ThreadTls.cs").text();
const loader = await Bun.file("uwp/Kiosk/Native/LoaderStubs.cs").text();
const pe = await Bun.file("uwp/Kiosk/Native/PeImage.cs").text();

test("only the selected game executable is mapped alongside its libraries", () => {
  expect(probe).toContain(
    "!file.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase)",
  );
});

test("game TLS owns a private vector instead of writing past the system table", () => {
  expect(pe).not.toContain("extern uint TlsAlloc");
  expect(pe).not.toContain("Marshal.WriteIntPtr(existing, slot");
  expect(tls).toContain("state.Copies.ContainsKey(one.Slot)");
  expect(tls).toContain("Marshal.WriteIntPtr(state.Table, one.Slot");
  expect(tls).toContain("Marshal.WriteIntPtr(teb + 0x58, state.Original)");
  expect(tls).toContain('imports.Overrides[module + "!ExitThread"]');
  expect(tls).toContain(
    'imports.Overrides[module + "!FreeLibraryAndExitThread"]',
  );
});

test("thread TLS uses the proven PE reader and reports adoption failures", () => {
  expect(tls).toContain("PeImage.CurrentTeb()");
  expect(tls).not.toContain("VirtualAllocFromApp");
  expect(probe).toContain('"exe.tls.failures="');
});

test("thread hooks share the managed function-pointer delegate type", () => {
  expect(loader).toContain("ThreadTls.CreateThreadDelegate makeThread");
  expect(tls).toContain("internal delegate IntPtr CreateThreadDelegate");
});

test("TLS wraps the loader thread hook after installation", () => {
  const loader = probe.indexOf("LoaderStubs.Install(imports);");
  const wrapper = probe.indexOf("ThreadTls.Install(imports);");
  expect(loader).toBeGreaterThan(-1);
  expect(wrapper).toBeGreaterThan(loader);
  expect(tls).toContain(
    'imports.Overrides.TryGetValue("kernel32.dll!CreateThread"',
  );
});

test("managed entry threads adopt TLS before entering game code", () => {
  expect(probe).toMatch(
    /ThreadTls\.Adopt\(\);\s+string result;\s+try\s*\{\s*result = StartModule\(image\);/,
  );
  const runner = probe.slice(
    probe.indexOf("var runner = new System.Threading.Thread"),
  );
  expect(runner.indexOf("ThreadTls.Adopt();")).toBeGreaterThan(-1);
  expect(runner.indexOf("ThreadTls.Adopt();")).toBeLessThan(
    runner.indexOf("main(engine.BaseAddress"),
  );
});
