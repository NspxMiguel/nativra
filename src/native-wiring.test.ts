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
const messages = await Bun.file("uwp/Kiosk/Native/WindowMessages.cs").text();
const raw = await Bun.file("uwp/Kiosk/Native/RawInputBridge.cs").text();

test("raw input is registered, bounded, delivered and released through window dispatch", () => {
  expect(probe).toContain("RawInputBridge.Install(imports);");
  expect(raw).toContain("packetOrder.Count >= 256");
  expect(raw).toContain("PointerBridge.PostRaw(target, handle);");
  expect(raw).toContain("Marshal.WriteInt32(size, required);");
  expect(raw).toContain("if (capacity < required) return Fail(122);");
  expect(raw).toContain("(registration.Flags & 0x30) == 0x30");
  expect(pointer).toContain("RawInputBridge.SuppressesLegacy(1)");
  expect(pointer).toContain("RawInputBridge.SuppressesLegacy(0)");
  expect(messages).toContain("RawInputBridge.Release(extra.ToInt64())");
});

test("window dispatch invokes the registered procedure and writes keyboard state", () => {
  expect(probe).toContain("WindowMessages.Install(imports);");
  expect(messages).toContain("state.Values[-4] = entry.Procedure;");
  expect(messages).toContain("return Call(procedure, window,");
  expect(messages).toContain(
    "Marshal.WriteByte(keys, key, down ? (byte)0x80 : (byte)0);",
  );
  expect(pointer).toContain("WindowMessages.InputWindow");
  expect(pointer).not.toContain("Post(WM_WINDOWPOSCHANGED, 0, 0)");
});

test("input and callback ownership last until the native game exits", () => {
  expect(probe).toContain("runner.IsAlive; tick = Math.Min(tick + 1, 500)");
  expect(probe).not.toContain("tick < 500; tick++");
  expect(probe).toContain("GC.KeepAlive(imports);");
});

test("host input survives a disconnected pad and handles key release", () => {
  expect(pointer).not.toContain("if (pads.Count == 0) return;");
  expect(pointer).toContain("host[0xD9] ? 1 : 0");
  expect(pointer).toContain("GamepadButtons.DPadUp");
  expect(mainPage).toContain("new KeyEventHandler(OnGameKeyUp), true");
  expect(mainPage).toContain("Native.PointerBridge.ReleaseHostKeys();");
  expect(mainPage).toContain("HostKey((int)e.OriginalKey, true)");
  expect(mainPage).toContain("HostKey((int)e.OriginalKey, false)");
  expect(mainPage).not.toContain("HostKey((int)e.Key,");
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
  expect(chain).toContain(
    "GetDelegateForFunctionPointer<GraphicsBridge.QueryInterfaceDelegate>",
  );
});

test("the back buffer preserves the game's requested pixel format", () => {
  expect(graphics).toContain(
    "Marshal.WriteInt32(desc, 8, format == 0 ? 87 : format);",
  );
  expect(mirror).toContain("pixelFormat == 28 || pixelFormat == 29");
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
