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

test("categorized installation diagnostics remain separate from runtime failures", () => {
  const report =
    "2026-10-24T12:00:00Z app 312530 at 50% game.exe\r\ncategory=short-encrypted-chunk recoverable=True identity=312530/1/101/game.exe chunk=51200 done=51200 total=102400 stage=install\r\n";
  expect(downloadFailure(312530, report)).toContain(
    "category=short-encrypted-chunk",
  );
  expect(downloadFailure(312530, report)).toContain("stage=install");
  expect(downloadFailure(424840, report)).toBeUndefined();
});
