import { expect, test } from "bun:test";
import { classify } from "./compat-sweep";

test("a game drawing steadily renders", () => {
  const v = classify(1145360, "at=1\nframes=7603 at 58.3 a second work=2.34ms\nmirror=mirroring chain=built 1920x1080\n", "window=0x1861E51EEB0\n", "");
  expect(v.status).toBe("renders");
  expect(v.frames).toBe(7603);
});

test("a window without a swap chain starts but names the chain", () => {
  const v = classify(268910, "frames=0 at 0.0 a second work=none\nmirror=not started chain=not built\n", "window=0x1F00\nexe.alive=True\n", "");
  expect(v.status).toBe("starts");
  expect(v.detail).toContain("chain=not built");
});

test("a 32-bit game that stops reports the layer's reason", () => {
  const v = classify(562260, "", "x86.init=returned\nx86.run=fault at 0x0020F300\n", "");
  expect(v.status).toBe("stops");
  expect(v.detail).toBe("fault at 0x0020F300");
});

test("nothing pulled is not a verdict", () => {
  expect(classify(1, "", "", "").status).toBe("no-report");
});
