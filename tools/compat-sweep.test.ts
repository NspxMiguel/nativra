import { expect, test } from "bun:test";
import { classify } from "./compat-sweep";

test("a game drawing steadily renders", () => {
  const v = classify(
    1145360,
    "at=1\nframes=7603 at 58.3 a second work=2.34ms\nmirror=mirroring chain=built 1920x1080\n",
    "window=0x1861E51EEB0\n",
    "",
  );
  expect(v.status).toBe("renders");
  expect(v.frames).toBe(7603);
});

test("a window without a swap chain starts but names the chain", () => {
  const v = classify(
    268910,
    "frames=0 at 0.0 a second work=none\nmirror=not started chain=not built\n",
    "window=0x1F00\nexe.alive=True\n",
    "",
  );
  expect(v.status).toBe("starts");
  expect(v.detail).toContain("chain=not built");
});

test("a 32-bit game that stops reports the layer's reason", () => {
  const v = classify(
    562260,
    "",
    "x86.init=returned\nx86.run=fault at 0x0020F300\n",
    "",
  );
  expect(v.status).toBe("stops");
  expect(v.detail).toBe("fault at 0x0020F300");
});

test("nothing pulled is not a verdict", () => {
  expect(classify(1, "", "", "").status).toBe("no-report");
});

test("a 32-bit game counts its D3D9 presents as frames", () => {
  const probe =
    "x86.run=running\nx86.com 2400x IDirect3DDevice9::Present\nx86.seconds=60.0\nx86.window=0x10010 dispatched=5\n";
  const v = classify(562260, "frames=0 at 0.0 a second", probe, "");
  expect(v.frames).toBe(2400);
  expect(v.fps).toBe(40);
  expect(v.status).toBe("renders");
});

test("a 32-bit run never inherits the pulse's frames", () => {
  const stale = "frames=810 at 21.4 a second work=none";
  const probe =
    "x86.run=exited with code 53\nx86.seconds=5.5\nx86.window=0x0 dispatched=0\n";
  const v = classify(204360, stale, probe, "");
  expect(v.frames).toBe(0);
  expect(v.status).not.toBe("renders");
});

test("live x86 heartbeat supplies Present counters before the guest exits", () => {
  const heartbeat =
    "x86.eip=0x10001000\nx86.com-calls=IDirect3DDevice9::SetTexture=9000, IDirect3DDevice9::Present=3600\n";
  const v = classify(40800, "frames=0 at 0.0 a second", "", "", heartbeat, 120);
  expect(v.frames).toBe(3600);
  expect(v.fps).toBe(30);
  expect(v.detail).toContain("visual/gameplay unverified");
});

test("heartbeat without presents never borrows native pulse frames", () => {
  const v = classify(
    223470,
    "frames=6000 at 60.0 a second",
    "",
    "",
    "x86.eip=0x10001000\nx86.com-calls=\n",
    120,
  );
  expect(v.frames).toBe(0);
  expect(v.status).not.toBe("renders");
});

test("Present can be the first heartbeat counter", () => {
  const v = classify(
    40800,
    "",
    "",
    "",
    "x86.eip=0x1000\nx86.com-calls=IDirect3DDevice9::Present=1800\n",
    60,
  );
  expect(v.frames).toBe(1800);
  expect(v.fps).toBe(30);
});

test("host initialization failures name the missing platform contract", () => {
  const v = classify(
    268910,
    "",
    "probe failed: delegate marshalling data is missing\n",
    "",
  );
  expect(v.status).toBe("stops");
  expect(v.detail).toBe("delegate marshalling data is missing");
});
