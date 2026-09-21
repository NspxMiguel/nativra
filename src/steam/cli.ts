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

/**
 * Renews the short-lived access token from the long-lived refresh token.
 *
 * Steam hands out an access token that lasts hours and a refresh token that
 * lasts months. Nothing here was renewing the first, so the moment it aged
 * out every call started coming back as a refusal — and the refusal is not
 * JSON, so what the owner saw was "Failed to parse JSON" rather than "your
 * session expired". The renewed token is written back beside the other, so
 * the next run starts with a live one.
 */
async function renew(root: string, session: Session): Promise<boolean> {
  try {
    const answer = await fetch(
      "https://api.steampowered.com/IAuthenticationService/GenerateAccessTokenForApp/v1/",
      {
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({
          refresh_token: session.refresh,
          steamid: session.steamid,
        }),
      },
    );
    if (!answer.ok) return false;

    const data = (await answer.json()) as {
      response?: { access_token?: string };
    };
    const fresh = data.response?.access_token;
    if (!fresh) return false;

    session.access = fresh;
    await Bun.write(join(root, "steam.json"), JSON.stringify(session, null, 2));
    return true;
  } catch {
    return false;
  }
}

/**
 * Asks Steam something, renewing the session once if it says no.
 *
 * One retry and no more: a refusal that survives a fresh token is not about
 * the token, and retrying it again only turns a clear failure into a slow one.
 */
async function ask<T>(
  root: string,
  session: Session,
  build: (session: Session) => string,
): Promise<T> {
  let answer = await fetch(build(session));
  if (answer.status === 401 || answer.status === 403) {
    if (!(await renew(root, session))) {
      throw new Error(
        "Steam will not renew this session: the refresh token has expired.\n" +
          "Sign in again from the console — open the app, choose Shop, and\n" +
          "scan the code with the Steam app on your phone.",
      );
    }
    answer = await fetch(build(session));
  }
  if (!answer.ok) {
    throw new Error("Steam answered " + answer.status + " " + answer.statusText);
  }
  return (await answer.json()) as T;
}

async function ownedGames(root: string, session: Session): Promise<
  Array<{ appid: number; name: string; minutes: number }>
> {
  const data = await ask<{
    response?: {
      games?: Array<{ appid: number; name: string; playtime_forever: number }>;
    };
  }>(root, session, (live) =>
    "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?" +
    new URLSearchParams({
      access_token: live.access,
      steamid: live.steamid,
      include_appinfo: "true",
      include_played_free_games: "true",
    }),
  );
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
    const games = await ownedGames(root, session);
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
      // A record of what exists, wherever it was put: the console's home
      // screen reads this to list what has been downloaded.
      const indexFile = join(root, "pacotes", "downloaded.json");
      let index: Array<{ appid: number; name: string; dir: string }> = [];
      try {
        index = (await Bun.file(indexFile).json()) as typeof index;
      } catch {
        // First download: the file does not exist yet.
      }
      index = index.filter((item) => item.appid !== appId);
      index.push({ appid: appId, name, dir: target });
      await Bun.write(indexFile, JSON.stringify(index, null, 2));

      console.log(t("steam.done", { name, dir: target }));
      return;
    }

    console.error(t("cmd.steam"));
    process.exit(2);
  } finally {
    cm.close();
  }
}
