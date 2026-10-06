import { expect, test } from "bun:test";
import { downloadFailure } from "./compat-sweep";

test("a failed depot download must not become a guest startup verdict", () => {
  const report =
    "2026-10-06T03:11:22Z app 3651720 at 10% _Mac.app\\Contents\\Resources\\Data\\sharedassets1.assets.resS\r\nSystem.Security.Cryptography.CryptographicException: The input data is not a complete block.\r\n";
  expect(downloadFailure(3651720, report)).toBe(
    "System.Security.Cryptography.CryptographicException: The input data is not a complete block.",
  );
  expect(downloadFailure(1868410, report)).toBeUndefined();
  expect(downloadFailure(3651720, "")).toBeUndefined();
});
