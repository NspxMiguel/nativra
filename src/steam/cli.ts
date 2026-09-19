// xbdev steam — the account's library, what a game is made of, and pulling one
// down. The session comes from the console: he signs in there by QR and the
// tokens are read back with xbdev pull.

import { join } from "node:path";
import { statfs } from "node:fs/promises";
import { t } from "../i18n";
import { human } from "../util";
import { connect, contentServers, downloadDepot, plan } from "./download";
import { collections, familyApps } from "./collections";

export type Session = {
  account: string;
  refresh: string;
  access: string;
  steamid: string;
};

export async function loadSession(root: string): Promise<Session | null> {
  const file = Bun.file(join(root, "steam.json"));
  if (!(await file.exists())) return null;
  const session = (await file.json()) as Session;
  return session.refresh && session.steamid ? session : null;
}

async function ownedGames(session: Session): Promise<
  Array<{ appid: number; name: string; minutes: number }>
> {
  const query = new URLSearchParams({
    access_token: session.access,
    steamid: session.steamid,
    include_appinfo: "true",
    include_played_free_games: "true",
  });
  const url =
    "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?" + query;
  const data = (await (await fetch(url)).json()) as {
    response?: {
      games?: Array<{ appid: number; name: string; playtime_forever: number }>;
    };
  };
  return (data.response?.games ?? [])
    .map((g) => ({ appid: g.appid, name: g.name, minutes: g.playtime_forever }))
    .sort((a, b) => b.minutes - a.minutes);
}

async function freeBytes(dir: string): Promise<number> {
  try {
    const fs = await statfs(dir);
    return Number(fs.bsize) * Number(fs.bavail);
  } catch {
    return Number.POSITIVE_INFINITY;
  }
}

export async function runSteam(root: string, args: string[]): Promise<void> {
  const session = await loadSession(root);
  if (!session) {
    console.error(t("steam.needsession"));
    process.exit(2);
  }

  const [subcommand, ...rest] = args;

  // The collections and the family library are what make the console's list
  // look like the one in his Steam client.
  if (subcommand === "shelf") {
    const cm = await connect(session.steamid, session.refresh);
    try {
      const shelves = await collections(cm);
      const family = await familyApps(session.access, session.steamid);
      const payload = {
        generated: new Date().toISOString(),
        collections: shelves,
        family: family.map((app) => ({ appid: app.appid, name: app.name })),
      };
      const target = rest[0] ?? join(root, "pacotes", "shelf.json");
      await Bun.write(target, JSON.stringify(payload));
      for (const shelf of shelves) {
        console.log(`${shelf.name}: ${shelf.added.length}`);
      }
      console.log(`familia: ${family.length}`);
      return;
    } finally {
      cm.close();
    }
  }

  if (!subcommand || subcommand === "games") {
    const games = await ownedGames(session);
    console.log(t("steam.signedin", { account: session.account }));
    console.log(t("steam.games", { count: games.length }));
    for (const game of games) {
      const hours = Math.round(game.minutes / 60);
      console.log(`  ${String(game.appid).padStart(8)}  ${game.name}${hours ? ` (${hours}h)` : ""}`);
    }
    return;
  }

  const appId = Number(rest[0]);
  if (!Number.isInteger(appId) || appId <= 0) {
    console.error(t("cmd.steam"));
    process.exit(2);
  }

  console.log(t("steam.connecting"));
  const cm = await connect(session.steamid, session.refresh);
  try {
    const servers = await contentServers(cm);
    if (servers.length === 0) throw new Error("Steam listed no content servers");

    if (subcommand === "info") {
      const { name, depots } = await plan(cm, appId, servers);
      console.log(name);
      let total = 0n;
      for (const item of depots) {
        total += item.manifest.totalBytes;
        console.log(
          t("steam.depot", {
            id: item.depot.id,
            files: item.manifest.files.length,
            size: human(Number(item.manifest.totalBytes)),
          }),
        );
      }
      console.log(human(Number(total)));
      return;
    }

    if (subcommand === "download") {
      const dirFlag = rest.indexOf("--dir");
      const baseDir = dirFlag >= 0 ? rest[dirFlag + 1] : join(root, "jogos");
      const depotFlag = rest.indexOf("--depot");
      const only = depotFlag >= 0 ? [Number(rest[depotFlag + 1])] : undefined;

      console.log(t("steam.planning", { name: String(appId) }));
      const { name, depots } = await plan(cm, appId, servers, only);
      const total = depots.reduce((n, d) => n + Number(d.manifest.totalBytes), 0);
      const target = join(baseDir, String(appId));

      // Filling his disk is a real way to break the machine, so the check
      // happens before the first byte, not after forty gigabytes.
      const free = await freeBytes(baseDir.startsWith("/") ? "/" : root);
      if (free < total * 1.05) {
        console.error(
          t("steam.space", { needed: human(total * 1.05 - free), dir: baseDir }),
        );
        process.exit(1);
      }

      console.log(t("steam.downloading", { name, size: human(total) }));
      let written = 0;
      let lastReport = 0;
      for (const item of depots) {
        await downloadDepot(servers, item, target, (progress) => {
          const now = Date.now();
          if (now - lastReport < 1000) return;
          lastReport = now;
          const percent = Math.floor(((written + progress.doneBytes) / total) * 100);
          console.log(t("steam.progress", { percent, file: progress.file }));
        });
        written += Number(item.manifest.totalBytes);
      }
      console.log(t("steam.done", { name, dir: target }));
      return;
    }

    console.error(t("cmd.steam"));
    process.exit(2);
  } finally {
    cm.close();
  }
}
