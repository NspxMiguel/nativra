import { resolve } from "node:path";

export type Rating = "verified" | "playable" | "untested" | "not-working";

/** Measured results only; rendering and installation failures do not prove playability. */
export function compatibilityFromMarkdown(
  markdown: string,
): Record<string, Rating> {
  const games: Record<string, Rating> = {};
  for (const line of markdown.split("\n")) {
    const cells = line.split("|").map((cell) => cell.trim());
    if (!/^\d+$/.test(cells[2] ?? "")) continue;
    const appId = cells[2];
    if (appId in games)
      throw new Error(`Duplicate compatibility app ID: ${appId}`);
    const rating = (cells[4] ?? "").replace(/\*\*/g, "");
    games[appId] = /Verified/.test(rating)
      ? "verified"
      : /Playable/.test(rating)
        ? "playable"
        : /Not working/.test(rating)
          ? "not-working"
          : "untested";
  }
  if (Object.keys(games).length === 0)
    throw new Error("No measured compatibility rows found");
  return games;
}

if (import.meta.main) {
  const root = resolve(import.meta.dir, "..");
  const games = compatibilityFromMarkdown(
    await Bun.file(`${root}/docs/COMPATIBILITY.md`).text(),
  );
  await Bun.write(
    `${root}/uwp/Kiosk/Assets/compatibility.json`,
    `${JSON.stringify(games, null, 2)}\n`,
  );
}
