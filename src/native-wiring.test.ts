import { expect, test } from "bun:test";

const probe = await Bun.file("uwp/Kiosk/Native/NativeProbe.cs").text();
const tls = await Bun.file("uwp/Kiosk/Native/ThreadTls.cs").text();

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
    /ThreadTls\.Adopt\(\);\s+var result = StartModule\(image\);/,
  );
  const runner = probe.slice(
    probe.indexOf("var runner = new System.Threading.Thread"),
  );
  expect(runner.indexOf("ThreadTls.Adopt();")).toBeGreaterThan(-1);
  expect(runner.indexOf("ThreadTls.Adopt();")).toBeLessThan(
    runner.indexOf("main(engine.BaseAddress"),
  );
});
