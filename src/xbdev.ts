#!/usr/bin/env bun
// xbdev — drives an Xbox Series X|S in Developer Mode from this Mac.
// Finds the console, installs native packages, flips apps into game mode.

import { DevicePortal, probe, PortalError, type InstalledPackage } from "./portal";
import { t } from "./i18n";
import { $ } from "bun";
import {
  human,
  isPackageFile,
  installOrder,
  isForThisConsole,
  parseIdentity,
} from "./util";
import { readdir, mkdir } from "node:fs/promises";
import { extractIcon, cleanScratch } from "./icons";
import { runSteam } from "./steam/cli";
import { ConsoleRemote, BUTTONS } from "./remote";
import { join, dirname } from "node:path";

const ROOT = dirname(import.meta.dir);
const PACKAGE_DIR = join(ROOT, "pacotes");
const KEYCHAIN_SERVICE = "claude-autonomous:XBDEV";

type Catalog = {
  packages: CatalogEntry[];
  dependencies: CatalogEntry[];
};

type CatalogEntry = {
  slug: string;
  name: string;
  kind?: string;
  runs?: string;
  url: string;
  console?: string[];
  priority?: number;
  gameMode?: boolean;
  note?: string;
  via?: string;
  protocol?: string;
};

type StoredConfig = { host: string; port: number; user?: string; pass?: string };

// ---------------------------------------------------------------- config

async function loadConfig(): Promise<StoredConfig | null> {
  // An explicit address in the environment wins over the keychain: it is how a
  // second console, or a test harness, is pointed at without disturbing the
  // stored one.
  if (process.env.XBDEV_HOST) {
    return {
      host: process.env.XBDEV_HOST,
      port: Number(process.env.XBDEV_PORT ?? 11443),
      user: process.env.XBDEV_USER,
      pass: process.env.XBDEV_PASS,
    };
  }
  try {
    const raw =
      await $`security find-generic-password -s ${KEYCHAIN_SERVICE} -w`.quiet().text();
    return JSON.parse(raw.trim());
  } catch {
    return null;
  }
}

async function saveConfig(config: StoredConfig): Promise<void> {
  const payload = JSON.stringify(config);
  await $`security add-generic-password -U -a ${process.env.USER} -s ${KEYCHAIN_SERVICE} -w ${payload}`.quiet();
}

async function portalOrExit(): Promise<DevicePortal> {
  const config = await loadConfig();
  if (!config) {
    console.error(t("need.connect"));
    process.exit(2);
  }
  return new DevicePortal(config);
}

async function loadCatalog(): Promise<Catalog> {
  return Bun.file(join(ROOT, "catalog.json")).json();
}

// ---------------------------------------------------------------- helpers

async function findPackageFiles(dir: string): Promise<string[]> {
  const found: string[] = [];
  const walk = async (current: string) => {
    for (const entry of await readdir(current, { withFileTypes: true })) {
      const full = join(current, entry.name);
      if (entry.isDirectory()) await walk(full);
      else if (isPackageFile(entry.name) && isForThisConsole(full)) found.push(full);
    }
  };
  await walk(dir);
  return found.sort(installOrder);
}

// ---------------------------------------------------------------- download

async function download(entry: CatalogEntry): Promise<string[]> {
  await mkdir(PACKAGE_DIR, { recursive: true });
  const fileName = entry.url.split("/").pop()!;
  const target = join(PACKAGE_DIR, entry.slug, fileName);
  await mkdir(dirname(target), { recursive: true });

  const existing = Bun.file(target);
  if (await existing.exists()) {
    console.log(t("get.cached", { name: entry.name }));
  } else {
    console.log(t("get.downloading", { name: entry.name }));
    if (entry.via === "gh") {
      // Our own builds live in a private repo, so the gh CLI carries the auth.
      const repo = entry.url.split("/").slice(3, 5).join("/");
      const asset = entry.url.split("/").pop()!;
      const result = await $`gh release download --repo ${repo} --pattern ${asset} --dir ${dirname(target)} --clobber`
        .nothrow()
        .quiet();
      if (result.exitCode !== 0) {
        throw new Error(`gh release download exit ${result.exitCode}`);
      }
    } else {
      // curl rather than fetch: resumes a partial file, retries on a dropped
      // connection, and streams straight to disk instead of buffering hundreds
      // of megabytes in memory.
      const result =
        await $`curl -fL --retry 3 --retry-delay 2 -C - -o ${target} ${entry.url}`.nothrow();
      if (result.exitCode !== 0) {
        throw new Error(`curl exit ${result.exitCode}`);
      }
    }
    console.log(
      t("get.done", { name: entry.name, size: human(Bun.file(target).size) }),
    );
  }

  // Zips hold the real package plus its dependencies — unpack and hand those back.
  if (fileName.toLowerCase().endsWith(".zip")) {
    const unpacked = join(PACKAGE_DIR, entry.slug, "unpacked");
    if (!(await Bun.file(join(unpacked, ".done")).exists())) {
      await mkdir(unpacked, { recursive: true });
      await $`unzip -o -q ${target} -d ${unpacked}`.quiet();
      await Bun.write(join(unpacked, ".done"), "");
    }
    return findPackageFiles(unpacked);
  }

  return [target];
}

// ---------------------------------------------------------------- commands

async function cmdFind(): Promise<void> {
  const selfAddress = (await $`ipconfig getifaddr en0`.quiet().text().catch(() => "")).trim();
  const network = selfAddress.split(".").slice(0, 3).join(".") || "10.0.0";
  console.log(t("find.scanning", { net: `${network}.0/24` }));

  const candidates = Array.from({ length: 254 }, (_, i) => `${network}.${i + 1}`);
  const hits: string[] = [];
  await Promise.all(
    candidates.map(async (host) => {
      if (await probe(host)) hits.push(host);
    }),
  );

  if (hits.length === 0) {
    console.log(t("find.none"));
    process.exit(1);
  }
  for (const host of hits) console.log(t("find.found", { host }));
}

async function cmdConnect(args: string[]): Promise<void> {
  const host = args[0];
  if (!host) {
    console.error(t("connect.missing"));
    process.exit(2);
  }
  const user = args[1] ?? process.env.XBDEV_USER;
  const pass = args[2] ?? process.env.XBDEV_PASS;
  await saveConfig({ host, port: 11443, user, pass });
  console.log(t("connect.saved", { host }));
}

async function cmdStatus(): Promise<void> {
  const config = await loadConfig();
  if (!config) {
    console.error(t("need.connect"));
    process.exit(2);
  }
  const portal = new DevicePortal(config);
  try {
    const name = await portal.machineName();
    console.log(`${t("status.name")}: ${name}`);
    try {
      const info = (await portal.xboxInfo()) as Record<string, string>;
      if (info.OsVersion) console.log(`${t("status.os")}: ${info.OsVersion}`);
      if (info.DevMode) console.log(`dev mode: ${info.DevMode}`);
      if (info.ConsoleType) console.log(`${t("status.type")}: ${info.ConsoleType}`);
    } catch {
      // /ext/xbox/info is Xbox-only and may be absent on older builds.
    }
    const packages = await portal.packages();
    console.log(`${t("status.apps")}: ${packages.length}`);
  } catch (error) {
    console.error(t("status.offline", { host: config.host }));
    if (error instanceof PortalError && error.status === 401) {
      console.error("401 — Device Portal user/password needed");
    }
    process.exit(1);
  }
}

async function cmdApps(): Promise<void> {
  const portal = await portalOrExit();
  const packages = await portal.packages();
  if (packages.length === 0) {
    console.log(t("apps.none"));
    return;
  }
  for (const pkg of packages.sort((a, b) => a.Name.localeCompare(b.Name))) {
    const version = pkg.Version
      ? `${pkg.Version.Major}.${pkg.Version.Minor}.${pkg.Version.Build}.${pkg.Version.Revision}`
      : "";
    console.log(`${pkg.Name.padEnd(36)} ${version}`);
  }
}

async function cmdCatalog(): Promise<void> {
  const catalog = await loadCatalog();
  console.log(t("catalog.header"));
  console.log();
  for (const entry of catalog.packages.sort(
    (a, b) => (a.priority ?? 9) - (b.priority ?? 9),
  )) {
    const kind =
      entry.kind === "emulator" ? t("catalog.emulator") : t("catalog.pcgame");
    console.log(`  ${entry.slug.padEnd(14)} ${kind.padEnd(12)} ${entry.name}`);
    if (entry.runs) console.log(`  ${"".padEnd(14)} ${entry.runs}`);
    console.log();
  }
}

async function cmdGet(args: string[]): Promise<void> {
  const catalog = await loadCatalog();
  const all = [...catalog.dependencies, ...catalog.packages];
  const wanted =
    args.length > 0 ? all.filter((entry) => args.includes(entry.slug)) : all;

  for (const entry of wanted) {
    try {
      const files = await download(entry);
      for (const file of files) console.log(`   ${file.replace(ROOT + "/", "")}`);
    } catch (error) {
      console.error(
        t("get.failed", { name: entry.name, error: String(error) }),
      );
    }
  }
}

async function installFiles(
  portal: DevicePortal,
  label: string,
  files: string[],
): Promise<boolean> {
  if (files.length === 0) return false;
  console.log(t("install.sending", { name: label }));
  try {
    await portal.installPackage(files);
    console.log(t("install.waiting"));
    await portal.waitForInstall();
    console.log(t("install.done", { name: label }));
    return true;
  } catch (error) {
    const detail =
      error instanceof PortalError
        ? `${error.message} ${error.status ?? ""} ${error.body?.slice(0, 200) ?? ""}`
        : String(error);
    console.error(t("install.failed", { name: label, error: detail }));
    return false;
  }
}

async function cmdInstall(args: string[]): Promise<void> {
  const portal = await portalOrExit();
  const files = args.sort(installOrder);
  const installed = await installFiles(
    portal,
    files[0]?.split("/").pop() ?? "package",
    files,
  );
  // A failed install has to be visible to whatever called this, not just
  // printed — scripts chain on the exit code.
  if (!installed) process.exit(1);
}

async function cmdGameMode(): Promise<void> {
  const portal = await portalOrExit();
  console.log(t("gamemode.looking"));
  const settings = await portal.settings();
  // The console exposes this under a name that has moved between OS versions;
  // match on intent rather than on one hardcoded string.
  // Measured on a Series X running OS 10.0.26100: the switch is called
  // DefaultUWPContentTypeToGame. Earlier builds used other names, so match on
  // intent — a setting about games that is about apps/UWP/content type.
  const candidate = settings.find((setting) => {
    const name = (setting.Name ?? "").toLowerCase();
    if (name === "defaultappmode") return true; // older console builds
    if (!name.includes("game")) return false;
    return (
      name.includes("uwp") ||
      name.includes("app") ||
      name.includes("contenttype") ||
      name.includes("gamemode")
    );
  });
  if (!candidate) {
    console.log(t("gamemode.manual"));
    for (const setting of settings) {
      console.log(`   ${setting.Name} = ${setting.Value}`);
    }
    return;
  }
  const value = candidate.Type === "Bool" ? "true" : "Game";
  await portal.setSetting(candidate.Name, value);
  console.log(t("gamemode.set", { name: candidate.Name }));
}

async function cmdKit(): Promise<void> {
  const portal = await portalOrExit();
  const catalog = await loadCatalog();
  const queue = [
    ...catalog.dependencies,
    ...catalog.packages.sort((a, b) => (a.priority ?? 9) - (b.priority ?? 9)),
  ];

  console.log(t("kit.start"));
  let ok = 0;
  let fail = 0;
  for (const [index, entry] of queue.entries()) {
    console.log(
      t("kit.step", { n: index + 1, total: queue.length, name: entry.name }),
    );
    try {
      const files = await download(entry);
      const installed = await installFiles(portal, entry.name, files);
      installed ? ok++ : fail++;
    } catch (error) {
      console.error(t("get.failed", { name: entry.name, error: String(error) }));
      fail++;
    }
  }
  console.log(t("kit.done", { ok, fail }));
  await cmdGameMode().catch(() => {});
}

async function findPackage(
  portal: DevicePortal,
  needle: string,
): Promise<InstalledPackage | undefined> {
  const packages = await portal.packages();
  const lower = needle.toLowerCase();
  return (
    packages.find((pkg) => pkg.Name.toLowerCase() === lower) ??
    packages.find((pkg) => pkg.Name.toLowerCase().includes(lower))
  );
}

async function cmdLaunch(args: string[]): Promise<void> {
  const portal = await portalOrExit();
  const pkg = await findPackage(portal, args[0] ?? "");
  if (!pkg) {
    console.error(`? ${args[0]}`);
    process.exit(1);
  }
  await portal.launch(pkg);
  console.log(`-> ${pkg.Name}`);
}

async function cmdStop(args: string[]): Promise<void> {
  const portal = await portalOrExit();
  const pkg = await findPackage(portal, args[0] ?? "");
  if (!pkg) {
    console.error(`? ${args[0]}`);
    process.exit(1);
  }
  await portal.terminate(pkg);
  console.log(`x ${pkg.Name}`);
}

async function cmdShot(args: string[]): Promise<void> {
  const portal = await portalOrExit();
  const data = await portal.screenshot();
  const file = args[0] ?? `xbox-${Date.now()}.png`;
  await Bun.write(file, data);
  console.log(t("shot.saved", { file }));
}

async function cmdSettings(args: string[]): Promise<void> {
  const portal = await portalOrExit();
  if (args.length === 0) {
    for (const setting of await portal.settings()) {
      console.log(`${(setting.Name ?? "").padEnd(34)} ${setting.Value}`);
    }
    return;
  }
  if (args.length === 1) {
    const settings = await portal.settings();
    const found = settings.find((setting) => setting.Name === args[0]);
    console.log(found ? JSON.stringify(found, null, 2) : "?");
    return;
  }
  await portal.setSetting(args[0], args[1]);
  console.log(`${args[0]} = ${args[1]}`);
}


async function cmdPush(args: string[]): Promise<void> {
  const [appName, localPath, remoteDir = ""] = args;
  if (!appName || !localPath) {
    console.error("xbdev push <app> <file> [remote-dir]");
    process.exit(2);
  }
  const portal = await portalOrExit();
  const pkg = await findPackage(portal, appName);
  if (!pkg) {
    console.error(`? ${appName}`);
    process.exit(1);
  }
  await portal.pushFile(pkg.PackageFullName, localPath, remoteDir);
  console.log(`-> ${pkg.Name}:${remoteDir}/${localPath.split("/").pop()}`);
}

async function cmdPull(args: string[]): Promise<void> {
  const [appName, fileName, remoteDir = ""] = args;
  if (!appName || !fileName) {
    console.error("xbdev pull <app> <file> [remote-dir]");
    process.exit(2);
  }
  const portal = await portalOrExit();
  const pkg = await findPackage(portal, appName);
  if (!pkg) {
    console.error(`? ${appName}`);
    process.exit(1);
  }
  const data = await portal.pullFile(pkg.PackageFullName, fileName, remoteDir);
  await Bun.write(fileName, data);
  console.log(`<- ${fileName} (${human(data.byteLength)})`);
}

async function cmdLs(args: string[]): Promise<void> {
  const [appName, remoteDir = ""] = args;
  if (!appName) {
    console.error("xbdev ls <app> [remote-dir]");
    process.exit(2);
  }
  const portal = await portalOrExit();
  const pkg = await findPackage(portal, appName);
  if (!pkg) {
    console.error(`? ${appName}`);
    process.exit(1);
  }
  for (const item of await portal.listFiles(pkg.PackageFullName, remoteDir)) {
    const name = String(item.Name ?? item.Id ?? "?");
    const size = Number(item.SizeInBytes ?? 0);
    const isFolder = Number(item.Type ?? 0) === 16 || size === 0;
    console.log(`${isFolder ? "d" : "-"} ${name.padEnd(40)} ${size ? human(size) : ""}`);
  }
}

/** Point RetroArch at the external drive and give it the console-grade defaults. */
async function cmdSetupRetroarch(): Promise<void> {
  const portal = await portalOrExit();
  const pkg = await findPackage(portal, "retroarch");
  if (!pkg) {
    console.error("RetroArch is not installed — run: xbdev kit");
    process.exit(1);
  }
  const config = join(ROOT, "configs", "retroarch.cfg");
  if (!(await Bun.file(config).exists())) {
    console.error(`missing ${config}`);
    process.exit(1);
  }
  await portal.pushFile(pkg.PackageFullName, config, "");
  console.log("retroarch.cfg sent — directories now point at E:\\");
}


/**
 * Read every downloaded package's manifest and report what the console would
 * make of it. Catches a truncated download or a wrong-architecture build here
 * rather than halfway through installing the kit.
 */
async function cmdVerify(): Promise<void> {
  const catalog = await loadCatalog();
  let bad = 0;
  let checked = 0;

  for (const entry of [...catalog.dependencies, ...catalog.packages]) {
    const dir = join(PACKAGE_DIR, entry.slug);
    if (!(await Bun.file(join(dir)).exists()) && !(await pathExists(dir))) {
      console.log(`${"-".padEnd(2)} ${entry.name.padEnd(22)} ${t("verify.missing")}`);
      continue;
    }
    let files: string[] = [];
    try {
      files = (await findPackageFiles(dir)).filter((file) => !file.endsWith(".cer"));
    } catch {
      files = [];
    }
    if (files.length === 0) {
      console.log(`x  ${entry.name.padEnd(22)} ${t("verify.missing")}`);
      bad++;
      continue;
    }

    for (const file of files) {
      checked++;
      const manifest = await readManifest(file);
      if (!manifest) {
        console.log(`x  ${entry.name.padEnd(22)} ${t("verify.unreadable")}`);
        bad++;
        continue;
      }
      const identity = parseIdentity(file, manifest);
      if (identity.ok) {
        console.log(
          `ok ${entry.name.padEnd(22)} ${identity.name} ${identity.version} ` +
            `[${identity.architectures.join(",") || "neutral"}]`,
        );
      } else {
        console.log(`x  ${entry.name.padEnd(22)} ${identity.problem}`);
        bad++;
      }
    }
  }

  console.log();
  console.log(t("verify.summary", { checked, bad }));
  if (bad > 0) process.exit(1);
}

async function pathExists(path: string): Promise<boolean> {
  return (await $`test -d ${path}`.nothrow().quiet()).exitCode === 0;
}

/** Bundles nest a package inside; both are zips, so unzip reaches either. */
async function readManifest(file: string): Promise<string | null> {
  const direct = await $`unzip -p ${file} AppxManifest.xml`.nothrow().quiet();
  if (direct.exitCode === 0 && direct.stdout.length > 0) return direct.stdout.toString();

  const bundle = await $`unzip -p ${file} AppxMetadata/AppxBundleManifest.xml`
    .nothrow()
    .quiet();
  if (bundle.exitCode === 0 && bundle.stdout.length > 0) return bundle.stdout.toString();

  return null;
}

/**
 * Rebooting is how the game-mode switch takes effect, but it interrupts
 * whatever is on screen — so it asks for --sim the way keel does.
 */
async function cmdRestart(args: string[]): Promise<void> {
  if (!args.includes("--sim") && !args.includes("--yes")) {
    console.error(t("restart.confirm"));
    process.exit(2);
  }
  const portal = await portalOrExit();
  await portal.restart();
  console.log(t("restart.sent"));
}

/**
 * Removing a package matters for our own builds: a package signed by a
 * different certificate cannot update one already installed, and the console
 * reports that as a bare access-denied.
 */
async function cmdUninstall(args: string[]): Promise<void> {
  const portal = await portalOrExit();
  const pkg = await findPackage(portal, args[0] ?? "");
  if (!pkg) {
    console.error(`? ${args[0]}`);
    process.exit(1);
  }
  await portal.uninstall(pkg);
  console.log(t("uninstall.done", { name: pkg.Name }));
}


/**
 * The console refuses to let a sideloaded app enumerate other packages, so the
 * Mac does it: cross what is installed with the catalogue and write the result
 * into Kiosk's own folder, where it can read it without any privilege.
 */
/** A subtitle has to be readable from the sofa, so it gets one clause. */
function shorten(text: string, limit = 58): string {
  if (text.length <= limit) return text;
  const cut = text.slice(0, limit);
  const lastBreak = Math.max(cut.lastIndexOf(","), cut.lastIndexOf(" "));
  return `${cut.slice(0, lastBreak > 20 ? lastBreak : limit).trim()}...`;
}

async function cmdSyncKiosk(): Promise<void> {
  const portal = await portalOrExit();
  const config = (await loadConfig())!;
  const catalog = await loadCatalog();
  const installed = await portal.packages();

  const iconDir = join(PACKAGE_DIR, "icons");
  const scratch = join(PACKAGE_DIR, ".icon-scratch");

  const entries: Array<Record<string, unknown>> = [];
  for (const entry of catalog.packages) {
    if (entry.slug === "kiosk") continue;
    const match = installed.find((pkg) => {
      const name = pkg.Name.toLowerCase();
      const slug = entry.slug.replace(/-.*$/, "").toLowerCase();
      return name.includes(slug) || name.includes(entry.name.toLowerCase());
    });
    if (!match) continue;

    // The package identity is what lets the app launch through the console's
    // own Device Portal, which reaches the apps that register no protocol.
    entries.push({
      slug: entry.slug,
      title: match.Name,
      subtitle: shorten(entry.runs?.replace(/^PC NATIVE:\s*/, "") ?? ""),
      protocol: entry.protocol ?? null,
      packageFullName: match.PackageFullName,
      appId: match.PackageRelativeId,
      icon: (await iconFor(entry.slug, iconDir, scratch)) ? `icon-${entry.slug}.png` : null,
    });
  }

  const payload = { generated: new Date().toISOString(), apps: entries };
  const file = join(PACKAGE_DIR, "apps.json");
  await Bun.write(file, JSON.stringify(payload, null, 2));

  const kiosk = await findPackage(portal, "kiosk");
  if (!kiosk) {
    console.error("Kiosk is not installed");
    process.exit(1);
  }
  // ApplicationData.Current.LocalFolder is the LocalState subfolder; the root
  // of LocalAppData is a different place the app cannot read.
  await portal.pushFile(kiosk.PackageFullName, file, "LocalState");

  // The console's own portal credentials, so the app can ask it to launch
  // anything. They never leave the console they already belong to.
  const portalFile = join(PACKAGE_DIR, "portal.json");
  await Bun.write(
    portalFile,
    JSON.stringify({
      host: config.host,
      port: config.port ?? 11443,
      user: config.user ?? "",
      pass: config.pass ?? "",
    }),
  );
  await portal.pushFile(kiosk.PackageFullName, portalFile, "LocalState");

  // The catalogue rides along so the app can offer what is NOT installed yet.
  // The repository is private, so the console cannot fetch it from GitHub; the
  // download URLs inside it are public releases and work from the console.
  const installedSlugs = new Set(entries.map((item) => item.slug));
  const catalogFile = join(PACKAGE_DIR, "catalog-console.json");
  await Bun.write(
    catalogFile,
    JSON.stringify({
      generated: new Date().toISOString(),
      packages: catalog.packages
        .filter((entry) => entry.slug !== "kiosk")
        .map((entry) => ({
          slug: entry.slug,
          name: entry.name,
          kind: entry.kind,
          note: shorten(entry.runs?.replace(/^PC NATIVE:\s*/, "") ?? ""),
          url: entry.url,
          installed: installedSlugs.has(entry.slug),
        })),
    }),
  );
  await portal.pushFile(kiosk.PackageFullName, catalogFile, "LocalState");

  // Reinstalling wipes LocalState and with it the Steam sign-in. When a copy
  // of the session was taken, put it back rather than making him scan again.
  const sessionFile = join(ROOT, "steam.json");
  if (await Bun.file(sessionFile).exists()) {
    await portal.pushFile(kiosk.PackageFullName, sessionFile, "LocalState");
    console.log("sessao da Steam devolvida ao console");
  }

  // His own Steam collections and the family library, so the console's list
  // matches the one in his client. Written by: xbdev steam shelf
  const shelfFile = join(PACKAGE_DIR, "shelf.json");
  if (await Bun.file(shelfFile).exists()) {
    await portal.pushFile(kiosk.PackageFullName, shelfFile, "LocalState");
  }

  // Which games have been seen running on a console. Empty until one has.
  const testedFile = join(PACKAGE_DIR, "tested.json");
  if (!(await Bun.file(testedFile).exists())) {
    await Bun.write(testedFile, JSON.stringify({ appids: [] }));
  }
  await portal.pushFile(kiosk.PackageFullName, testedFile, "LocalState");

  // The portal refuses to overwrite a file that is already there, so only the
  // missing ones go up; a changed icon is handled by deleting it first.
  const present = new Set(
    (await portal.listFiles(kiosk.PackageFullName, "LocalState"))
      .map((item) => String(item.Id ?? item.Name ?? "")),
  );

  let pushedIcons = 0;
  for (const item of entries) {
    if (!item.icon) continue;
    if (present.has(item.icon as string)) continue;
    const local = join(iconDir, `${item.slug}.png`);
    const staged = join(iconDir, item.icon as string);
    await Bun.write(staged, Bun.file(local));
    await portal.pushFile(kiosk.PackageFullName, staged, "LocalState");
    pushedIcons++;
  }

  await cleanScratch(scratch);
  const launchable = entries.filter((item) => item.protocol).length;
  console.log(t("sync.done", { total: entries.length, launchable }));
  console.log(`${pushedIcons} icones enviados`);
}

/** Answers whether an icon for this slug now exists on disk. */
async function iconFor(slug: string, outDir: string, scratch: string): Promise<boolean> {
  const existing = Bun.file(join(outDir, `${slug}.png`));
  if (await existing.exists()) return true;

  const dir = join(PACKAGE_DIR, slug);
  let files: string[] = [];
  try {
    files = await readdir(dir);
  } catch {
    return false;
  }
  const pkg = files.find((f) => /\.(appx|msix)(bundle)?$/i.test(f));
  if (!pkg) return false;

  const result = await extractIcon(slug, join(dir, pkg), outDir, scratch);
  if (result.reason) console.log(`  ${slug}: ${result.reason}`);
  return Boolean(result.file);
}

function usage(): void {
  const commands: Array<[string, string]> = [
    ["find", t("cmd.find")],
    ["connect <ip> [user] [pass]", t("cmd.connect")],
    ["status", t("cmd.status")],
    ["apps", t("cmd.apps")],
    ["catalog", t("cmd.catalog")],
    ["get [slug...]", t("cmd.get")],
    ["install <file...>", t("cmd.install")],
    ["kit", t("cmd.kit")],
    ["launch <name>", t("cmd.launch")],
    ["stop <name>", t("cmd.stop")],
    ["shot [file]", t("cmd.shot")],
    ["settings [name] [value]", t("cmd.settings")],
    ["gamemode", t("cmd.gamemode")],
    ["push <app> <file> [dir]", t("cmd.push")],
    ["pull <app> <file> [dir]", t("cmd.pull")],
    ["ls <app> [dir]", t("cmd.ls")],
    ["setup-retroarch", t("cmd.setupRetroarch")],
    ["verify", t("cmd.verify")],
    ["restart --sim", t("cmd.restart")],
    ["uninstall <app>", t("cmd.uninstall")],
    ["sync", t("cmd.sync")],
    ["steam <games|info|download>", t("cmd.steam")],
    ["press <botao...>", t("cmd.press")],
  ];
  console.log(`${t("cli.usage")}: xbdev <comando>`);
  console.log();
  for (const [name, description] of commands) {
    console.log(`  ${name.padEnd(28)} ${description}`);
  }
}

const [command, ...args] = process.argv.slice(2);
const handlers: Record<string, (args: string[]) => Promise<void>> = {
  find: cmdFind,
  achar: cmdFind,
  connect: cmdConnect,
  conectar: cmdConnect,
  status: cmdStatus,
  apps: cmdApps,
  catalog: cmdCatalog,
  catalogo: cmdCatalog,
  get: cmdGet,
  baixar: cmdGet,
  install: cmdInstall,
  instalar: cmdInstall,
  kit: cmdKit,
  launch: cmdLaunch,
  abrir: cmdLaunch,
  stop: cmdStop,
  fechar: cmdStop,
  shot: cmdShot,
  foto: cmdShot,
  settings: cmdSettings,
  ajustes: cmdSettings,
  gamemode: cmdGameMode,
  push: cmdPush,
  pull: cmdPull,
  ls: cmdLs,
  "setup-retroarch": cmdSetupRetroarch,
  verify: cmdVerify,
  conferir: cmdVerify,
  restart: cmdRestart,
  reiniciar: cmdRestart,
  uninstall: cmdUninstall,
  remover: cmdUninstall,
  sync: cmdSyncKiosk,
  sincronizar: cmdSyncKiosk,
  steam: (args: string[]) => runSteam(ROOT, args),
  press: cmdPress,
};

/** Drives the console with the controller channel of the Device Portal. */
async function cmdPress(args: string[]): Promise<void> {
  const config = await loadConfig();
  if (!config) {
    console.error(t("need.connect"));
    process.exit(2);
  }
  if (args.length === 0) {
    console.log(Object.keys(BUTTONS).join(" "));
    return;
  }
  const remote = new ConsoleRemote({ host: config.host, port: config.port ?? 11443, user: config.user, pass: config.pass });
  await remote.open();
  try {
    for (const arg of args) {
      const [button, repeat] = arg.split("*");
      const times = Number(repeat ?? 1);
      for (let i = 0; i < Math.max(1, times); i++) {
        await remote.press(button);
      }
      console.log(`${button}${times > 1 ? " x" + times : ""}`);
    }
  } finally {
    remote.close();
  }
}

const handler = command ? handlers[command] : undefined;
if (!handler) {
  usage();
  process.exit(command ? 2 : 0);
}

try {
  await handler(args);
} catch (error) {
  console.error(t("err.generic", { error: String(error) }));
  process.exit(1);
}
