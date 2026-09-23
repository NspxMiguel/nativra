import { expect, test } from "bun:test";

const loader = await Bun.file("uwp/Kiosk/Native/LoaderStubs.cs").text();

test("runtime capability probes do not fabricate absent plugin or system exports", () => {
  expect(loader).toContain(
    "return own != IntPtr.Zero ? own : MissingProcedure();",
  );
  expect(loader).toContain(
    "if (!imports.Answers.ContainsKey(key)) return MissingProcedure();",
  );
  expect(loader).toContain("SetLastError(127);");
  expect(loader).toContain(
    "invented ? IntPtr.Zero : GetProcAddress(module, wanted)",
  );
});
