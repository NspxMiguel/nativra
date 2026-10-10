import { expect, test } from "bun:test";
import { compatibilityFromMarkdown } from "./generate-compatibility";

const row = (id: number, rating: string) =>
  `| Game | ${id} | Engine | ${rating} | build | notes |`;
test("measured badges never promote rendering or installation observations", () => {
  expect(
    compatibilityFromMarkdown(
      [
        row(1, "✅ **Verified**"),
        row(2, "🟡 Playable"),
        row(3, "⛔ Not working"),
        row(4, "🟡 Renders"),
        row(5, "⛔ Download blocked; runtime untested"),
        row(6, "⚠️ Installation blocked"),
        row(7, "⬜ Untested"),
      ].join("\n"),
    ),
  ).toEqual({
    1: "verified",
    2: "playable",
    3: "not-working",
    4: "untested",
    5: "untested",
    6: "untested",
    7: "untested",
  });
});
test("duplicate app IDs and empty input fail instead of publishing misleading data", () => {
  expect(() =>
    compatibilityFromMarkdown(
      row(1, "Verified") + "\n" + row(1, "Not working"),
    ),
  ).toThrow();
  expect(() => compatibilityFromMarkdown("# Game compatibility")).toThrow();
});
test("bundled compatibility matches the source document", async () => {
  const source = await Bun.file(
    new URL("../docs/COMPATIBILITY.md", import.meta.url),
  ).text();
  const bundled = await Bun.file(
    new URL("../uwp/Kiosk/Assets/compatibility.json", import.meta.url),
  ).json();
  expect(bundled).toEqual(compatibilityFromMarkdown(source));
  expect(bundled[1919460]).toBe("not-working");
  expect(bundled[562260]).toBe("playable");
});
