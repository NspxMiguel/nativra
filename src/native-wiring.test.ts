import { expect, test } from "bun:test";

const probe = await Bun.file("uwp/Kiosk/Native/NativeProbe.cs").text();
const tls = await Bun.file("uwp/Kiosk/Native/ThreadTls.cs").text();
const loader = await Bun.file("uwp/Kiosk/Native/LoaderStubs.cs").text();
const pe = await Bun.file("uwp/Kiosk/Native/PeImage.cs").text();
const graphics = await Bun.file("uwp/Kiosk/Native/GraphicsBridge.cs").text();
const chain = await Bun.file("uwp/Kiosk/Native/FakeSwapChain.cs").text();
const mirror = await Bun.file("uwp/Kiosk/Native/FrameMirror.cs").text();
const pointer = await Bun.file("uwp/Kiosk/Native/PointerBridge.cs").text();
const mainPage = await Bun.file("uwp/Kiosk/MainPage.xaml.cs").text();

test("host input survives a disconnected pad and handles key release", () => {
  expect(pointer).not.toContain("if (pads.Count == 0) return;");
  expect(pointer).toContain("host[0xD9] ? 1 : 0");
  expect(pointer).toContain("GamepadButtons.DPadUp");
  expect(mainPage).toContain("new KeyEventHandler(OnGameKeyUp), true");
  expect(mainPage).toContain("Native.PointerBridge.ReleaseHostKeys();");
});

test("Unity native plugins are mapped and initialized with extensionless lookup", () => {
  expect(probe).toContain('TryGetItemAsync("Plugins")');
  expect(probe).toContain('TryGetItemAsync("x86_64")');
  expect(probe).toContain("foreach (var name in initializedModules)");
  expect(loader).toContain("if (name.IndexOf('.') < 0) name += \".dll\";");
});

test("swap-chain interface getters preserve the IID and return owned references", () => {
  expect(chain).toContain("private static TwoOut device;");
  expect(chain).toContain("private static TwoOut coreWindow;");
  expect(chain).toContain("Query(ownerDevice, riid, result)");
  expect(chain).toContain("return Query(held, riid, surface);");
  expect(chain).not.toContain("Marshal.WriteIntPtr(surface, held)");
});

test("mirror reserves its frame buffer before copying and ignores window alpha", () => {
  const take = mirror.slice(mirror.indexOf("public static void Take()"));
  expect(take.indexOf("Interlocked.Exchange(ref busy, 1)")).toBeLessThan(
    take.indexOf("Marshal.Copy("),
  );
  expect(take).toContain("into[alpha] = 255;");
  expect(take).toContain("if (!queued)");
});

test("resizing rebinds the mirror and queued UI frames retain their own bitmap", () => {
  expect(chain).toContain("OnResize?.Invoke(width, height, format, held);");
  expect(graphics).toContain("FakeSwapChain.OnResize =");
  expect(mirror).toContain("lock (frameGate) TakeLocked();");
  expect(mirror).toContain("var showingPicture = picture;");
  expect(mirror).toContain("if (version != generation) return;");
  expect(chain).toContain("GetDelegateForFunctionPointer<GraphicsBridge.QueryInterfaceDelegate>");
});

test("swap-chain description is only freed by the finally block after entering try", () => {
  const method = graphics.slice(
    graphics.indexOf("private static int MakeChainOn("),
    graphics.indexOf("private static bool Show("),
  );
  const guarded = method.slice(method.indexOf("try\n"));
  expect(guarded.match(/Marshal\.FreeHGlobal\(desc\);/g)).toHaveLength(1);
  expect(guarded).toMatch(/finally\s*\{\s*Marshal\.FreeHGlobal\(desc\);/);
});

test("only the selected game executable is mapped alongside its libraries", () => {
  expect(probe).toContain(
    "!file.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase)",
  );
});

test("game TLS uses Windows-owned slots without replacing the system vector", () => {
  expect(pe).not.toContain("extern uint TlsAlloc");
  expect(pe).not.toContain("Marshal.WriteIntPtr(existing, slot");
  expect(tls).toContain('LoadPackagedLibrary("NativraTls"');
  expect(tls).toContain('"NativraTlsConfigure"');
  expect(tls).not.toContain("Marshal.WriteIntPtr(teb");
  expect(tls).not.toContain("state.Table");
});

test("thread TLS reports adoption failures", () => {
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
