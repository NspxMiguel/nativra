import { expect, test } from "bun:test";
import { classify } from "./compat-sweep";

test("a native loader failure retains the first concrete diagnostic", () => {
  const verdict = classify(
    1919460,
    "",
    "probe failed: EETypeRva:0x00031EE8 is missing delegate marshalling data\n",
    "",
  );
  expect(verdict.status).toBe("stops");
  expect(verdict.detail).toContain("missing delegate marshalling data");
});

test("an x86 import failure takes precedence over its host wrapper", () => {
  const verdict = classify(
    367450,
    "",
    "x86.run=missing import shlwapi.dll!PathCanonicalizeW\n" +
      "probe failed: PlatformNotSupportedException: The 32-bit layer stopped\n",
    "",
  );
  expect(verdict.status).toBe("stops");
  expect(verdict.detail).toBe("missing import shlwapi.dll!PathCanonicalizeW");
});
