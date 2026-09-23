import { expect, test } from "bun:test";

const shader = await Bun.file("uwp/Kiosk/Shaders/Fsr1/Upscale.hlsl").text();
const build = await Bun.file("uwp/Kiosk/Shaders/build.cmd").text();
const origin = await Bun.file("uwp/Kiosk/Shaders/Fsr1/UPSTREAM.md").text();

test("FSR preparation builds actual separate EASU and RCAS shader passes", () => {
  expect(shader).toContain('#include "ffx_fsr1.h"');
  expect(shader).toContain("FsrEasuF(");
  expect(shader).toContain("FsrRcasF(");
  expect(shader).toContain("position.xy >= uint2(OutputSize)");
  expect(build).toContain("/T cs_5_0");
  expect(build).toContain("/D PASS_EASU=1");
  expect(build).toContain("/D PASS_EASU=0");
  expect(origin).toContain("a21ffb8f6c13233ba336352bdff293894c706575");
  expect(origin).toContain("No user-facing FSR toggle is enabled");
});
