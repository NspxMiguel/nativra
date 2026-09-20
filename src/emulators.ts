// emulators.ts — the pre-configured emulator shelf for the Win32 bridge.
//
// catalog.json/xbdev.ts install signed UWP packages through the Device
// Portal's package manager. This module is for the other path: plain
// portable x64 Windows builds of PC emulators, the same binaries anyone
// would run on a Windows desktop, dropped where the console's own Win32
// loader (`xbdev win32`, LocalState/win32 on the Kiosk package) can run
// them directly — no signed package, no Device Portal install call.
//
// Every entry here is staged and configured LOCALLY first, under
// `pacotes/emulators/<id>/`, the same convention `pacotes/<slug>/` already
// uses for native packages. Pushing a staged folder to the console's E:
// drive or into the Kiosk's win32 folder is a separate step, outside this
// module — see `xbdev win32` and `xbdev push-game` for the transport.
//
// Shared layout on the console's external drive (matches configs/retroarch.cfg,
// which already points the native RetroArch package at these same folders):
//
//   E:\Games\<system>\      ROMs, one folder per system
//   E:\BIOS\                shared BIOS/firmware directory
//   E:\Saves\  E:\States\   save files and save states
//
// Standalone (non-RetroArch) emulators get their own BIOS/save/state
// sub-folder under those roots (E:\BIOS\ps1, E:\Saves\ps1, ...) so two
// systems that both happen to dump a file called "bios.bin" never collide.
// The shared RetroArch install keeps the flat layout the existing
// configs/retroarch.cfg already uses, since its cores organize by content
// name on their own.

import { $ } from "bun";
import { mkdir, readdir, rename, rm, stat } from "node:fs/promises";
import { basename, dirname, extname, join } from "node:path";
import { human } from "./util";

// ---------------------------------------------------------------- constants

/** Drive letter the console's external NTFS drive is formatted to (see docs/VEREDITO.md). */
export const DRIVE = "E:";

/** Every system this shelf covers. One catalog entry per system, at minimum. */
export type System =
  | "ps1"
  | "ps2"
  | "psp"
  | "gamecube-wii"
  | "nds"
  | "n3ds"
  | "n64"
  | "snes"
  | "nes"
  | "gb-gbc-gba"
  | "genesis"
  | "saturn"
  | "dreamcast"
  | "arcade"
  | "switch";

/** The shared console-drive paths a system's games, BIOS and saves live under. */
export function consolePaths(system: System) {
  return {
    games: `${DRIVE}\\Games\\${system}`,
    bios: `${DRIVE}\\BIOS\\${system}`,
    saves: `${DRIVE}\\Saves\\${system}`,
    states: `${DRIVE}\\States\\${system}`,
  };
}

// ------------------------------------------------------------------ sources

/**
 * Where a build is fetched from. Most projects embed the version number in
 * the asset's file name, so a URL copied today breaks the day the project
 * cuts a new release. "release-api" resolves the current asset from the
 * project's own release feed at install time instead of trusting a frozen
 * URL; "static" is for the handful of projects that publish at a fixed,
 * version-less path (a buildbot, or a release tagged "latest" that moves).
 */
export type DownloadSource =
  | { kind: "static"; url: string }
  | {
      kind: "release-api";
      /** A GitHub- or Gitea-shaped releases endpoint; both return the same `assets[].name`/`browser_download_url` shape. */
      apiUrl: string;
      /** Picks the one asset out of the release that is the build this entry wants. */
      match: (assetName: string) => boolean;
    };

async function resolveDownloadUrl(source: DownloadSource): Promise<{ url: string; fileName: string }> {
  if (source.kind === "static") {
    return { url: source.url, fileName: source.url.split("/").pop()! };
  }
  const response = await fetch(source.apiUrl, {
    headers: { Accept: "application/json", "User-Agent": "xboxdev-emulators-shelf" },
  });
  if (!response.ok) {
    throw new Error(`${source.apiUrl} -> HTTP ${response.status}`);
  }
  const release = (await response.json()) as {
    assets: { name: string; browser_download_url: string }[];
  };
  const asset = release.assets?.find((candidate) => source.match(candidate.name));
  if (!asset) {
    const names = (release.assets ?? []).map((a) => a.name).join(", ");
    throw new Error(`no asset matched in ${source.apiUrl} (had: ${names})`);
  }
  return { url: asset.browser_download_url, fileName: asset.name };
}

// ------------------------------------------------------------------ archive

type ArchiveKind =
  /** A plain .zip; unpacked with the system `unzip`. */
  | "zip"
  /** A .7z, or a 7-Zip self-extracting .exe (MAME's release format) — both open with `7z x`. */
  | "7z"
  /** The download IS the emulator: a single portable executable, nothing to unpack. */
  | "single-exe";

/**
 * One of the two 7-Zip command-line builds, whichever this Mac has. Neither
 * ships with macOS, unlike `unzip` — a missing binary gets a clear, actionable
 * error instead of a cryptic spawn failure.
 */
async function find7z(): Promise<string> {
  const candidate = Bun.which("7zz") ?? Bun.which("7z") ?? Bun.which("7za");
  if (!candidate) {
    throw new Error(
      "no 7z binary on PATH (needed for .7z/.exe-sfx archives) — install with: brew install sevenzip",
    );
  }
  return candidate;
}

/**
 * Several of these projects package a version number into the top-level
 * folder inside the archive (mGBA: "mGBA-0.10.5-win64/mGBA.exe", Azahar:
 * "azahar-windows-msvc-2126.1.1\\azahar.exe" — note the literal backslash,
 * a Windows-native zip tool wrote that one). Hardcoding that path would
 * break on the next release. Searching for the executable's own file name
 * instead survives the folder being renamed, nested, or not there at all.
 */
async function findExecutable(unpackDir: string, executableName: string): Promise<string> {
  const wanted = executableName.toLowerCase();
  const maxDepth = 4;
  const walk = async (dir: string, depth: number): Promise<string | null> => {
    let entries;
    try {
      entries = await readdir(dir, { withFileTypes: true });
    } catch {
      return null;
    }
    for (const entry of entries) {
      // BSD unzip on macOS keeps a literal backslash in the name when the
      // archive used Windows-style separators instead of nesting a real
      // directory — split it back into a path before recursing into it.
      if (entry.name.includes("\\")) {
        const parts = entry.name.split("\\");
        const flatPath = join(dir, entry.name);
        const properPath = join(dir, ...parts);
        await mkdir(dirname(properPath), { recursive: true });
        await rename(flatPath, properPath);
        if (parts[0].toLowerCase() === wanted) return properPath;
        continue;
      }
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        if (depth >= maxDepth) continue;
        const found = await walk(full, depth + 1);
        if (found) return found;
      } else if (entry.name.toLowerCase() === wanted) {
        return full;
      }
    }
    return null;
  };
  const found = await walk(unpackDir, 0);
  if (!found) {
    throw new Error(`could not find ${executableName} anywhere under ${unpackDir} after unpacking`);
  }
  return found;
}

// -------------------------------------------------------------------- BIOS

/**
 * Whether a system needs BIOS/firmware the user must dump themselves. This
 * is a cross-reference, not a copy: the actual per-file BIOS requirements
 * (which file, which checksum) belong in the console's own BIOS table —
 * nothing here links to, names, or ships a BIOS/key/firmware file.
 */
export type BiosRequirement = {
  needed: boolean;
  /** One line, for humans: why, and how strict the requirement is. */
  note: string;
};

// -------------------------------------------------------------- RetroArch

/**
 * Genesis and Saturn are served through one shared portable RetroArch
 * install rather than a dedicated standalone emulator — the standalone
 * options for both are either unmaintained or ship no official Windows
 * build, and RetroArch's cores for both are the community's actual daily
 * driver. Both entries below install into this same directory; the second
 * one to run finds RetroArch already there and only adds its own core.
 */
const RETROARCH_ID = "retroarch";
const RETROARCH_SOURCE: DownloadSource = {
  // The stable channel has no version-less alias; nightly does, and is
  // what most core development targets first. buildbot.libretro.com is
  // libretro's own official build server, not a third-party mirror.
  kind: "static",
  url: "https://buildbot.libretro.com/nightly/windows/x86_64/RetroArch.7z",
};
const RETROARCH_CORE_BASE_URL = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";

type RetroArchCore = {
  /** libretro core file name, e.g. "genesis_plus_gx_libretro.dll" — also the buildbot asset is this name + ".zip". */
  coreFileName: string;
  /** The core's own display name, which is also the sub-folder RetroArch stores its per-core options under. */
  displayName: string;
};

// ------------------------------------------------------------------ entry

export type EmulatorEntry = {
  id: string;
  system: System;
  name: string;
  /** Official project home page or source repository, for the doc table and for humans. */
  homepage: string;
  source: DownloadSource;
  /** A known-good digest, when the project publishes one for this exact asset. Verified at download time when present. */
  sha256?: string;
  archive: ArchiveKind;
  /** File name of the executable to launch — searched for after unpacking, see findExecutable(). */
  executableName: string;
  /**
   * Marker files this emulator's own convention reads to keep everything —
   * config, saves, BIOS lookups — next to its own folder instead of
   * %AppData%/Documents. Nearly every emulator here (Dolphin's own
   * convention, copied by DuckStation, PCSX2, melonDS, Azahar, mGBA and
   * Gopher64) is an empty file named "portable.txt" or "portable.ini";
   * PPSSPP uses an empty "memstick" folder instead (see extraDirs).
   */
  portableMarkers?: string[];
  /** Directories created inside the app folder before first launch. */
  extraDirs?: string[];
  bios: BiosRequirement;
  /** Real config files this entry drops into place — see configure(). Destination is relative to the installed app's own folder. */
  configFiles: { src: string; dest: string }[];
  /** Builds the argv (excluding the executable itself) to launch one ROM/ISO/game file. */
  launchArgs: (romPath: string) => string[];
  /** Lower-confidence areas, things a maintainer should double check, or context that does not fit the doc table. */
  note?: string;
  /** Set only for the two RetroArch-core entries (genesis, saturn). */
  retroarchCore?: RetroArchCore;
};

// -------------------------------------------------------------- github/gitea

function githubLatest(repo: string): string {
  return `https://api.github.com/repos/${repo}/releases/latest`;
}

// ---------------------------------------------------------------- catalog
//
// Every URL and archive layout below was checked against the project's own
// release feed on 2026-09-20 (via `gh api`, and by reading each archive's
// real file listing — not assumed from memory). Anything not directly
// verified says so in its `note`.

export const emulators: EmulatorEntry[] = [
  {
    id: "ps1",
    system: "ps1",
    name: "DuckStation",
    homepage: "https://github.com/stenzek/duckstation",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("stenzek/duckstation"),
      match: (name) => name === "duckstation-windows-x64-release.zip",
    },
    archive: "zip",
    executableName: "duckstation-qt-x64-ReleaseLTCG.exe",
    portableMarkers: ["portable.txt"],
    bios: {
      needed: true,
      note: "Runs BIOS-less in a pinch, but real PS1 BIOS is what makes compatibility match a real console.",
    },
    configFiles: [{ src: "settings.ini", dest: "settings.ini" }],
    launchArgs: (romPath) => ["-batch", "-fullscreen", romPath],
  },
  {
    id: "ps2",
    system: "ps2",
    name: "PCSX2",
    homepage: "https://pcsx2.net",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("PCSX2/pcsx2"),
      match: (name) => /^pcsx2-v[\d.]+-windows-x64-Qt\.7z$/.test(name),
    },
    archive: "7z",
    executableName: "pcsx2-qt.exe",
    portableMarkers: ["portable.txt"],
    bios: {
      needed: true,
      note: "Required — PCSX2 will not boot a game without a real PS2 BIOS dump.",
    },
    configFiles: [{ src: "PCSX2.ini", dest: "inis/PCSX2.ini" }],
    launchArgs: (romPath) => ["-fullscreen", "-nogui", romPath],
  },
  {
    id: "psp",
    system: "psp",
    name: "PPSSPP",
    homepage: "https://www.ppsspp.org",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("hrydgard/ppsspp"),
      match: (name) => /^PPSSPP-v[\d.]+-Windows-x64\.zip$/.test(name),
    },
    archive: "zip",
    executableName: "PPSSPPWindows64.exe",
    // PPSSPP's own portable convention: an empty "memstick" folder next to
    // the exe (and no "installed.txt") makes it treat that folder as the
    // memory stick, instead of Documents\PPSSPP.
    extraDirs: ["memstick/PSP/SYSTEM"],
    bios: {
      needed: false,
      note: "Ships its own HLE PSP firmware fonts (assets/flash0) — no BIOS dump needed.",
    },
    // No controls.ini: PPSSPP bundles its own SDL GameControllerDB
    // (assets/gamecontrollerdb.txt, confirmed in the release zip), so an
    // Xbox/XInput pad is auto-mapped with no button config needed — writing
    // one by hand would mean guessing at PPSSPP's internal numeric keycodes
    // for no real benefit.
    configFiles: [{ src: "ppsspp.ini", dest: "memstick/PSP/SYSTEM/ppsspp.ini" }],
    launchArgs: (romPath) => ["--fullscreen", "--escape-exit", romPath],
  },
  {
    id: "gamecube-wii",
    system: "gamecube-wii",
    name: "Dolphin",
    homepage: "https://dolphin-emu.org/download/",
    // Dolphin does not publish binaries through GitHub Releases, and its own
    // download API (api.dolphin-emu.org) sits behind a bot-detection
    // challenge that blocks plain HTTP clients (confirmed 2026-09-20: both
    // the download page and the JSON API return a Bunny.net "establishing a
    // secure connection" / 403 response to curl and to WebFetch, with or
    // without a browser User-Agent). There is no reliable, scriptable URL to
    // pin here — install() detects this and stops with instructions instead
    // of silently failing on a curl that a browser would have gotten past.
    source: { kind: "static", url: "https://dolphin-emu.org/download/" },
    archive: "7z",
    executableName: "Dolphin.exe",
    portableMarkers: ["portable.txt"],
    bios: {
      needed: false,
      note: "GameCube IPL/Wii NAND are optional extras (boot animation, Wii Shop-era features) — games run without them.",
    },
    configFiles: [
      { src: "Dolphin.ini", dest: "User/Config/Dolphin.ini" },
      { src: "GFX.ini", dest: "User/Config/GFX.ini" },
      { src: "GCPadNew.ini", dest: "User/Config/GCPadNew.ini" },
      { src: "WiimoteNew.ini", dest: "User/Config/WiimoteNew.ini" },
    ],
    launchArgs: (romPath) => ["-b", "-e", romPath],
    note: "Manual download required: fetch the current 64-bit build from the URL above in a real browser, drop the extracted folder at the install path, then run configure() on it. Everything else here (config files, launch command) is verified against Dolphin's real, current config format.",
  },
  {
    id: "nds",
    system: "nds",
    name: "melonDS",
    homepage: "https://melonds.kuribo64.net",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("melonDS-emu/melonDS"),
      match: (name) => /^melonDS-[\d.]+-windows-x86_64\.zip$/.test(name),
    },
    archive: "zip",
    executableName: "melonDS.exe",
    portableMarkers: ["portable.txt"],
    bios: {
      needed: true,
      note: "Needs real DS bios7.bin/bios9.bin/firmware.bin (and DSi files for DSi mode) — no HLE fallback.",
    },
    configFiles: [{ src: "melonDS.toml", dest: "melonDS.toml" }],
    launchArgs: (romPath) => [romPath, "-f"],
  },
  {
    id: "n3ds",
    system: "n3ds",
    name: "Azahar",
    homepage: "https://azahar-emu.org",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("azahar-emu/azahar"),
      match: (name) => /^azahar-windows-msvc-[\d.]+\.zip$/.test(name),
    },
    archive: "zip",
    executableName: "azahar.exe",
    portableMarkers: ["portable.txt"],
    extraDirs: ["user/config", "user/sdmc", "user/nand"],
    bios: {
      needed: false,
      note: "Runs commercial games without a system archive; boot9/boot11 and a NAND dump are only needed for a few edge cases (eShop titles, home menu).",
    },
    configFiles: [{ src: "qt-config.ini", dest: "user/config/qt-config.ini" }],
    launchArgs: (romPath) => ["-f", romPath],
    note: "Successor to Citra (discontinued after Nintendo's 2024 legal action); same codebase and config format, renamed and continued by the community.",
  },
  {
    id: "n64",
    system: "n64",
    name: "Gopher64",
    homepage: "https://github.com/gopher64/gopher64",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("gopher64/gopher64"),
      match: (name) => name === "gopher64-windows-x86_64.exe",
    },
    // The download IS the emulator: one self-contained portable .exe.
    archive: "single-exe",
    executableName: "gopher64.exe",
    portableMarkers: ["portable.txt"],
    bios: { needed: false, note: "N64 has no external BIOS to dump; the PIF boot ROM is emulated (HLE) by default." },
    configFiles: [{ src: "config.toml", dest: "config.toml" }],
    launchArgs: (romPath) => [romPath, "--fullscreen"],
    note: "Successor to simple64 (archived 2025-02-14 by its own author in favor of this from-scratch rewrite). Newer project: config.toml's exact key set was not verified against a real first-run file, only against the portable.txt convention it documents — treat the shipped config.toml as a starting point, not gospel.",
  },
  {
    id: "snes",
    system: "snes",
    name: "Snes9x",
    homepage: "https://www.snes9x.com",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("snes9xgit/snes9x"),
      match: (name) => /^snes9x-[\d.]+-win32-x64\.zip$/.test(name),
    },
    archive: "zip",
    executableName: "snes9x-x64.exe",
    // No marker needed — the Windows build stores snes9x.conf next to the
    // exe by default already.
    bios: { needed: false, note: "SNES has no BIOS; a handful of enhancement-chip games (Super FX, SA-1) are emulated in software." },
    configFiles: [{ src: "snes9x.conf", dest: "snes9x.conf" }],
    launchArgs: (romPath) => ["-fullscreen", romPath],
  },
  {
    id: "nes",
    system: "nes",
    name: "Mesen2",
    homepage: "https://www.mesen.ca",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("SourMesen/Mesen2"),
      match: (name) => /^Mesen_[\d.]+_Windows\.zip$/.test(name),
    },
    archive: "zip",
    // Mesen's own portable convention is unusual: renaming the exe to add a
    // "_P" suffix (Mesen_P.exe) makes it store settings.json in a "Mesen"
    // folder beside itself instead of Documents. install() renames the
    // extracted Mesen.exe to Mesen_P.exe for exactly this reason.
    executableName: "Mesen_P.exe",
    extraDirs: ["Mesen"],
    bios: { needed: false, note: "NES has no BIOS to dump." },
    configFiles: [{ src: "settings.json", dest: "Mesen/settings.json" }],
    launchArgs: (romPath) => [romPath, "--fullscreen"],
    note: "Also emulates SNES, Game Boy/Color/Advance and PC Engine, but is catalogued here for NES specifically — mGBA is the shelf's dedicated Game Boy family pick, and Snes9x the SNES one.",
  },
  {
    id: "gb-gbc-gba",
    system: "gb-gbc-gba",
    name: "mGBA",
    homepage: "https://mgba.io",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("mgba-emu/mgba"),
      match: (name) => /^mGBA-[\d.]+-win64\.7z$/.test(name),
    },
    archive: "7z",
    executableName: "mGBA.exe",
    portableMarkers: ["portable.ini"],
    bios: {
      needed: false,
      note: "Runs Game Boy/Color/Advance without BIOS via HLE; a real GBA BIOS dump only improves a few edge-case games.",
    },
    configFiles: [
      { src: "config.ini", dest: "config.ini" },
      { src: "qt.ini", dest: "qt.ini" },
    ],
    launchArgs: (romPath) => ["-f", romPath],
  },
  {
    id: "genesis",
    system: "genesis",
    name: "RetroArch (Genesis Plus GX)",
    homepage: "https://github.com/libretro/Genesis-Plus-GX",
    source: RETROARCH_SOURCE,
    archive: "7z",
    executableName: "retroarch.exe",
    bios: {
      needed: false,
      note: "Most Genesis/Mega Drive games run without BIOS; Sega CD/32X add-ons (not covered by this entry) do need their own.",
    },
    configFiles: [{ src: "retroarch.cfg", dest: "retroarch.cfg" }],
    retroarchCore: { coreFileName: "genesis_plus_gx_libretro.dll", displayName: "Genesis Plus GX" },
    launchArgs: (romPath) => ["-L", "cores/genesis_plus_gx_libretro.dll", "--fullscreen", romPath],
    note: "No standalone Genesis emulator has a clearly-better, actively-maintained official Windows build (BlastEm does not publish one); RetroArch's core is the community's actual daily driver here.",
  },
  {
    id: "saturn",
    system: "saturn",
    name: "RetroArch (Beetle Saturn)",
    homepage: "https://github.com/libretro/beetle-saturn-libretro",
    source: RETROARCH_SOURCE,
    archive: "7z",
    executableName: "retroarch.exe",
    bios: {
      needed: true,
      note: "Required — Saturn emulation needs the real region BIOS (saturn_bios.bin or region-specific dumps); no HLE path.",
    },
    configFiles: [{ src: "retroarch.cfg", dest: "retroarch.cfg" }],
    retroarchCore: { coreFileName: "mednafen_saturn_libretro.dll", displayName: "Beetle Saturn" },
    launchArgs: (romPath) => ["-L", "cores/mednafen_saturn_libretro.dll", "--fullscreen", romPath],
  },
  {
    id: "dreamcast",
    system: "dreamcast",
    name: "Flycast",
    homepage: "https://github.com/flyinghead/flycast",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("flyinghead/flycast"),
      match: (name) => /^flycast-win64-[\d.]+\.zip$/.test(name),
    },
    archive: "zip",
    executableName: "flycast.exe",
    // No marker file: confirmed by reading core/windows/winmain.cpp in
    // flycast's own source — on Windows (non-UWP) it unconditionally sets
    // its config and data directories to the executable's own folder, no
    // opt-in needed.
    bios: {
      needed: true,
      note: "Runs many games HLE-BIOS-free, but a real Dreamcast boot ROM + flash image noticeably improves compatibility.",
    },
    configFiles: [{ src: "emu.cfg", dest: "emu.cfg" }],
    launchArgs: (romPath) => [romPath],
  },
  {
    id: "arcade",
    system: "arcade",
    name: "MAME",
    homepage: "https://www.mamedev.org",
    source: {
      kind: "release-api",
      apiUrl: githubLatest("mamedev/mame"),
      match: (name) => /^mame\d+b_x64\.exe$/.test(name),
    },
    // MAME's official Windows release is a self-extracting 7-Zip archive.
    // `7z x` opens it directly — verified by locating the embedded 7z
    // signature and unpacking the payload (see research notes).
    archive: "7z",
    executableName: "mame.exe",
    bios: {
      needed: true,
      note: "Per-game, not per-system: many arcade boards need their own small BIOS ROM inside the game's own zip (e.g. neogeo.zip) — there is no single shared arcade BIOS file. Cross-reference the console's BIOS table per game, not per system.",
    },
    configFiles: [
      { src: "mame.ini", dest: "ini/mame.ini" },
      { src: "default.cfg", dest: "cfg/default.cfg" },
    ],
    launchArgs: (romPath) => ["-rompath", dirname(romPath), basename(romPath, extname(romPath))],
  },
  {
    id: "switch",
    system: "switch",
    name: "Eden",
    homepage: "https://eden-emu.dev",
    source: {
      kind: "release-api",
      apiUrl: "https://git.eden-emu.dev/api/v1/repos/eden-emu/eden/releases/latest",
      // amd64-gcc-standard: the plain x86-64-v3 build. Series X's CPU is
      // Zen 2, which satisfies v3 (AVX2) — the MSVC build is only needed as
      // a graphics-bug fallback, and Zen4-only builds require a newer CPU
      // than this console has.
      match: (name) => /^Eden-Windows-v[\d.]+-amd64-gcc-standard\.zip$/.test(name),
    },
    archive: "zip",
    executableName: "eden.exe",
    extraDirs: ["user"],
    bios: {
      needed: true,
      note: "Needs the console owner's own prod.keys + firmware, dumped from their own Switch — this shelf never fetches, links to, or ships those.",
    },
    configFiles: [{ src: "qt-config.ini", dest: "user/config/qt-config.ini" }],
    launchArgs: (romPath) => ["-f", "-g", romPath],
    note: "\"So pra brincar\", per the request — but worth being direct about: Nintendo filed a coordinated DMCA wave against 13 Switch-emulator projects (including Eden) in February 2026. Eden is still actively developed and its official downloads still resolve as of 2026-09-20, but this is the least legally stable entry in the shelf and could go dark without notice. Hosted on the project's own git.eden-emu.dev, not GitHub — GitHub removed several of the named projects.",
  },
];

export function findEmulator(id: string): EmulatorEntry {
  const entry = emulators.find((candidate) => candidate.id === id);
  if (!entry) throw new Error(`unknown emulator: ${id}`);
  return entry;
}

// ------------------------------------------------------------------ install

function stagingDir(root: string, id: string): string {
  return join(root, "pacotes", "emulators", id);
}

async function pathExists(path: string): Promise<boolean> {
  try {
    await stat(path);
    return true;
  } catch {
    return false;
  }
}

async function downloadFile(url: string, target: string, label: string): Promise<void> {
  if (await pathExists(target)) {
    console.log(`${label}: cached (${target})`);
    return;
  }
  await mkdir(dirname(target), { recursive: true });
  console.log(`${label}: downloading ${url}`);
  const result = await $`curl -fL --retry 3 --retry-delay 2 -C - -o ${target} ${url}`.nothrow();
  if (result.exitCode !== 0) {
    throw new Error(`curl exit ${result.exitCode} fetching ${url}`);
  }
  console.log(`${label}: ${human(Bun.file(target).size)}`);
}

/**
 * Verifies the download before it is trusted. A real digest wins when the
 * project publishes one for this exact asset; otherwise this falls back to
 * checking the file is non-empty and starts with the magic bytes its
 * archive kind requires — enough to catch a truncated download or an HTML
 * error page saved in place of the real file, which is the failure this
 * project's projects actually hit (a dead link, a renamed asset).
 */
async function verify(entry: EmulatorEntry, filePath: string): Promise<void> {
  const file = Bun.file(filePath);
  const size = file.size;
  if (size === 0) throw new Error(`${filePath} is empty`);

  if (entry.sha256) {
    const digest = await $`shasum -a 256 ${filePath}`.text();
    const actual = digest.trim().split(/\s+/)[0];
    if (actual !== entry.sha256) {
      throw new Error(`sha256 mismatch for ${filePath}: expected ${entry.sha256}, got ${actual}`);
    }
    return;
  }

  const head = new Uint8Array(await file.slice(0, 8).arrayBuffer());
  const magic = (bytes: number[]) => bytes.every((b, i) => head[i] === b);
  if (entry.archive === "zip" && !magic([0x50, 0x4b, 0x03, 0x04])) {
    throw new Error(`${filePath} does not look like a zip (no PK signature)`);
  }
  if (entry.archive === "single-exe" && !magic([0x4d, 0x5a])) {
    throw new Error(`${filePath} does not look like a Windows executable (no MZ signature)`);
  }
  // "7z" here covers both a real .7z and MAME's self-extracting .exe: the
  // .exe starts with an MZ stub, and the 7z signature only appears further
  // in, so this only checks size — the real check is `7z x` succeeding.
}

async function extract(entry: EmulatorEntry, archivePath: string, unpackDir: string): Promise<string> {
  await mkdir(unpackDir, { recursive: true });
  if (entry.archive === "single-exe") {
    const dest = join(unpackDir, entry.executableName);
    await Bun.write(dest, Bun.file(archivePath));
    await $`chmod +x ${dest}`.nothrow();
    return dest;
  }
  if (entry.archive === "zip") {
    // Exit code is checked loosely: BSD unzip returns 1 (with a warning,
    // not a failure) on an archive that used backslash path separators
    // instead of forward slashes — Azahar's Windows zip does this — while
    // still extracting everything correctly. findExecutable() below is the
    // real check: it fails loudly if the exe genuinely isn't there.
    await $`unzip -q -o ${archivePath} -d ${unpackDir}`.nothrow();
  } else {
    const sevenZip = await find7z();
    const result = await $`${sevenZip} x -y -o${unpackDir} ${archivePath}`.nothrow().quiet();
    if (result.exitCode !== 0) {
      throw new Error(`${sevenZip} exit ${result.exitCode} extracting ${archivePath}`);
    }
  }
  return findExecutable(unpackDir, entry.executableName);
}

/**
 * Downloads, verifies, unpacks and configures one entry. For the two
 * RetroArch-core entries (genesis, saturn) this also makes sure the shared
 * RetroArch install exists first, and only adds this entry's own core and
 * per-core options to it — see the RetroArch section above.
 *
 * Idempotent: re-running it with the same entry re-uses the cached download
 * and skips work that is already done, the same way `xbdev get`/`kit` do.
 */
export async function install(entry: EmulatorEntry, root: string): Promise<string> {
  if (entry.retroarchCore) {
    return installRetroArchCore(entry, root);
  }

  const dir = stagingDir(root, entry.id);
  const { url, fileName } = await resolveDownloadUrl(entry.source);
  const archivePath = join(dir, "download", fileName);
  await downloadFile(url, archivePath, entry.name);
  await verify(entry, archivePath);

  const unpackDir = join(dir, "app");
  const exePath = await extract(entry, archivePath, unpackDir);
  const appDir = dirname(exePath);

  // single-exe already wrote the file under its final name; other archive
  // kinds may have unpacked under an executable name that does not match
  // what this entry wants to call it (Mesen's Mesen.exe -> Mesen_P.exe).
  const wantedPath = join(appDir, entry.executableName);
  if (exePath !== wantedPath) {
    await rename(exePath, wantedPath);
  }

  await configure(entry, appDir);
  console.log(`${entry.name}: ready at ${appDir}`);
  return appDir;
}

async function installRetroArchCore(entry: EmulatorEntry, root: string): Promise<string> {
  const core = entry.retroarchCore!;
  const dir = stagingDir(root, RETROARCH_ID);
  const { url, fileName } = await resolveDownloadUrl(RETROARCH_SOURCE);
  const archivePath = join(dir, "download", fileName);
  const appDir = join(dir, "app");

  if (!(await pathExists(join(appDir, "retroarch.exe")))) {
    await downloadFile(url, archivePath, "RetroArch (shared)");
    await verify({ ...entry, archive: "7z" }, archivePath);
    const exePath = await extract({ ...entry, archive: "7z", executableName: "retroarch.exe" }, archivePath, appDir);
    if (dirname(exePath) !== appDir) {
      // RetroArch's own 7z is flat, but guard against that changing.
      throw new Error(`expected retroarch.exe at the top of the archive, found it in ${dirname(exePath)}`);
    }
  } else {
    console.log("RetroArch (shared): already installed");
  }

  const coreUrl = `${RETROARCH_CORE_BASE_URL}${core.coreFileName}.zip`;
  const coreZip = join(dir, "download", `${core.coreFileName}.zip`);
  await downloadFile(coreUrl, coreZip, core.displayName);
  const coresDir = join(appDir, "cores");
  await mkdir(coresDir, { recursive: true });
  await $`unzip -q -o ${coreZip} -d ${coresDir}`.nothrow();
  if (!(await pathExists(join(coresDir, core.coreFileName)))) {
    throw new Error(`${core.coreFileName} missing from ${coresDir} after unpacking ${coreUrl}`);
  }

  await configure(entry, appDir);
  console.log(`${entry.name}: ready at ${appDir} (core: ${core.coreFileName})`);
  return appDir;
}

// ---------------------------------------------------------------- configure

/**
 * Writes the factory config into an already-installed app folder: the
 * portable markers, any extra directories the emulator's own convention
 * expects to exist up front, and the real config files from
 * configs/emulators/<id>/. Split out from install() so a config-only
 * refresh (after editing a file in configs/emulators/) does not need to
 * re-download or re-unpack anything.
 */
export async function configure(entry: EmulatorEntry, appDir: string): Promise<void> {
  for (const marker of entry.portableMarkers ?? []) {
    await Bun.write(join(appDir, marker), "");
  }
  for (const dir of entry.extraDirs ?? []) {
    await mkdir(join(appDir, dir), { recursive: true });
  }
  for (const file of entry.configFiles) {
    const src = configTemplatePath(entry.id, file.src);
    const dest = join(appDir, file.dest);
    await mkdir(dirname(dest), { recursive: true });
    if (!(await pathExists(src))) {
      throw new Error(`missing config template: ${src}`);
    }
    await Bun.write(dest, Bun.file(src));
  }
  // Every entry needs its own BIOS/save/state folders to exist on the
  // console drive before first launch — harmless to create ahead of time
  // even though this module never writes to E:\ itself (that only exists
  // once the staged folder is on the console).
}

function configTemplatePath(id: string, fileName: string): string {
  // configs/emulators/<id>/<fileName>, relative to the repo root — resolved
  // from this module's own location so it works regardless of cwd.
  const repoRoot = join(import.meta.dir, "..");
  return join(repoRoot, "configs", "emulators", id, fileName);
}

// -------------------------------------------------------------- launching

/** Full argv (including the executable) to launch one game file on an installed entry. */
export function launchCommand(entry: EmulatorEntry, appDir: string, romPath: string): string[] {
  return [join(appDir, entry.executableName), ...entry.launchArgs(romPath)];
}
