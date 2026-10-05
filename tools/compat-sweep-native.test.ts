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
