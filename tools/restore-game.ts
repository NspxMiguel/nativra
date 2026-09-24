// Puts a downloaded game back into Nativra's storage without Steam.
//
// Reinstalling the app wipes its LocalState, and the game with it. The
// console keeps an earlier copy under DevelopmentFiles\games\<appid>, which
// Device Portal can read even though the app cannot. This copies that tree
// into a local backup (.games/<appid>, outside the cycle's scratch folder)
// and pushes it into LocalState\games\<appid>. With --from-backup the local
// copy is pushed directly.
//
// usage: bun tools/restore-game.ts [appid] [--from-backup]
import { DevicePortal } from "../src/portal";
import { mkdir, readdir, stat } from "node:fs/promises";
import { join } from "node:path";

const appId = Bun.argv.find((arg) => /^\d+$/.test(arg)) ?? "1919460";
const fromBackup = Bun.argv.includes("--from-backup");
const credentials = JSON.parse(
  await Bun.$`security find-generic-password -s claude-autonomous:XBDEV -w`.quiet().text(),
);
const portal = new DevicePortal({
  ...credentials,
  host: process.env.XBDEV_HOST ?? credentials.host,
  port: credentials.port ?? 11443,
});
const pkg = (await portal.packages()).find((item) => item.Name.toLowerCase().includes("kiosk")
  || item.Name.toLowerCase().includes("nativra"));
if (!pkg) throw new Error("Nativra is not installed");
const backup = join(import.meta.dir, "..", ".games", appId);
let count = 0;
let bytes = 0;

async function ensureFolder(parent: string, name: string) {
  try {
    await portal.makeFolder(pkg!.PackageFullName, parent, name);
  } catch {
    // Already there.
  }
}

async function fromConsole(relative = "") {
  const source = `/games/${appId}${relative}`;
  const destination = `LocalState/games/${appId}${relative}`;
  const local = backup + relative;
  await mkdir(local, { recursive: true });
  for (const item of await portal.listFiles(pkg!.PackageFullName, source, "DevelopmentFiles")) {
    const name = String(item.Name);
    if (name.includes("/") || name.includes("\\") || name === "..") throw new Error("Unexpected filename");
    if (Number(item.Type) & 16) {
      await ensureFolder(destination, name);
      await fromConsole(`${relative}/${name}`);
      continue;
    }
    const path = join(local, name);
    const kept = await stat(path).catch(() => null);
    if (!kept || kept.size !== Number(item.FileSize)) {
      const data = await portal.pullFile(pkg!.PackageFullName, name, source, "DevelopmentFiles");
      if (data.byteLength !== Number(item.FileSize)) throw new Error(`Size mismatch: ${name}`);
      await Bun.write(path, data);
    }
    await portal.pushFile(pkg!.PackageFullName, path, destination);
    count++;
    bytes += Number(item.FileSize);
  }
}

async function fromLocal(relative = "") {
  const destination = `LocalState/games/${appId}${relative}`;
  for (const entry of await readdir(backup + relative, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      await ensureFolder(destination, entry.name);
      await fromLocal(`${relative}/${entry.name}`);
      continue;
    }
    const path = join(backup + relative, entry.name);
    await portal.pushFile(pkg!.PackageFullName, path, destination);
    count++;
    bytes += (await stat(path)).size;
  }
}

await ensureFolder("LocalState", "games");
await ensureFolder("LocalState/games", appId);
await (fromBackup ? fromLocal() : fromConsole());
console.log(`restored ${appId}: ${count} files, ${Math.round(bytes / 1048576)} MiB`);
