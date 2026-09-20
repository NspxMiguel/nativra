// Zero-configuration BIOS/firmware/key intake.
//
// The owner's decision this module implements: this project never ships,
// mirrors or links to a BIOS, firmware or key file — see docs/BIOS.md for
// why. What it does instead is recognise a file the user already owns, by
// its size and hash rather than by trusting whatever name it arrived with,
// and file a copy of it into every installed emulator's own folder, under
// the exact filename that emulator expects. Point it at a whole folder — a
// plugged-in USB stick, say — and it does the same for everything it
// recognises in one pass. Drag one file in, done: no folder picking, no
// renaming, no emulator settings screen.
//
// "Key" is in the module's vocabulary for the day a future emulator in
// catalog.json needs an encryption key rather than a boot ROM (nothing
// installed today does) — the table and the pipeline below extend to that
// the same way they would to a new BIOS.
//
// Read src/xbdev.ts first for house style; this module follows it.

import { basename, extname, join } from "node:path";
import { mkdir, mkdtemp, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";

const UNZIP = "/usr/bin/unzip";

// ---------------------------------------------------------------- data

/**
 * Every emulator this project can install (see catalog.json) that reads a
 * BIOS/firmware/key file of its own. Not every slug below has an entry in
 * KNOWN_BIOS_FILES yet — add one the same way when a new emulator needs it.
 */
export type EmulatorId = "retroarch" | "xbsx2" | "flycast" | "dolphin" | "ppsspp" | "supermodel";

/** Where one copy of a BIOS file has to land for one emulator to find it. */
export type BiosTarget = {
  emulator: EmulatorId;
  /**
   * Folder under that emulator's own BIOS/system root — see the `roots`
   * parameter of fileIt()/scan() and defaultRoots() below. Empty string
   * means the root itself.
   */
  dir: string;
  /**
   * The filename that emulator expects. Omitted for an emulator that
   * identifies a BIOS by reading its own header instead of trusting the
   * filename (PCSX2/XBSX2 does this — see the ps2-bios entry) — the
   * source file's own name is kept as-is.
   */
  filename?: string;
  note?: string;
};

type CommonFields = {
  /** Stable key, never shown to a user, safe to log. */
  id: string;
  system: string;
  region?: string;
  /** False when the emulator has a fallback (usually HLE) and still runs without it. */
  required: boolean;
  targets: BiosTarget[];
  note?: string;
};

/** A single dumped binary — the common case (PS1, Sega CD, Dreamcast). */
export type SingleBiosEntry = CommonFields & {
  archive?: false;
  /** Exact size in bytes. Required — it is the first and cheapest check. */
  size: number;
  sha1?: string;
  md5?: string;
  /** Where a hash came from, so a wrong one can be traced and fixed. */
  hashSource?: string;
  /**
   * Opt-in only, and only for an entry where the absence of a hash is
   * structural — a system with many legitimate dumps that genuinely do not
   * share one checksum (ps2-bios) — never as a stand-in for "no verified
   * hash yet". Without it, a hashless entry can still be listed as a
   * fileIt() destination once identified some other way, but identify()
   * will not claim an arbitrary file of the right size actually is it: a
   * size this common (128KB, 512KB) matches too much else by accident.
   */
  matchBySizeAlone?: boolean;
};

/**
 * A multi-ROM arcade BIOS shipped as one MAME-style zip (Neo Geo and
 * friends). There is no single whole-archive hash worth keeping — the exact
 * set of ROMs inside varies by which optional variants a given zip bundles,
 * and libretro's own documentation does not publish one either — so these
 * are recognised by filename, not by content. See identify()'s "name"
 * confidence tier.
 */
export type ArchiveBiosEntry = CommonFields & {
  archive: true;
};

export type BiosEntry = SingleBiosEntry | ArchiveBiosEntry;

export const KNOWN_BIOS_FILES: BiosEntry[] = [
  // --- PlayStation --------------------------------------------------------
  // Sizes and MD5s: docs.libretro.com/library/beetle_psx/ (checked
  // 2026-09-20). Beetle PSX and PCSX ReARMed both read these straight out of
  // RetroArch's system directory root, no subfolder — confirmed for this
  // project by configs/retroarch.cfg, whose system_directory points at
  // E:\BIOS. Not required: both cores fall back to the open-source OpenBIOS
  // when none of the three is present, so a PS1 game still boots, just less
  // accurately — see docs/BIOS.md.
  {
    id: "ps1-scph5500",
    system: "PlayStation",
    region: "Japan",
    size: 524288,
    md5: "8dd7d5296a650fac7319bce665a6a53c",
    hashSource: "docs.libretro.com/library/beetle_psx/",
    required: false,
    targets: [{ emulator: "retroarch", dir: "", filename: "scph5500.bin" }],
  },
  {
    id: "ps1-scph5501",
    system: "PlayStation",
    region: "USA",
    size: 524288,
    md5: "490f666e1afb15b7362b406ed1cea246",
    hashSource: "docs.libretro.com/library/beetle_psx/",
    required: false,
    targets: [{ emulator: "retroarch", dir: "", filename: "scph5501.bin" }],
  },
  {
    id: "ps1-scph5502",
    system: "PlayStation",
    region: "Europe",
    size: 524288,
    md5: "32736f17079d0b2b7024407c39bd3050",
    hashSource: "docs.libretro.com/library/beetle_psx/",
    required: false,
    targets: [{ emulator: "retroarch", dir: "", filename: "scph5502.bin" }],
  },

  // --- Sega CD / Mega CD ---------------------------------------------------
  // MD5s: docs.libretro.com/library/genesis_plus_gx/ (checked 2026-09-20),
  // which lists all three as Required. That page does not give a size.
  // 131072 bytes (128KB) is the size of every published dump of these three
  // regional boot ROMs, but that figure comes from general knowledge, not
  // from the cited page — said here rather than left silently implied.
  {
    id: "segacd-bios-us",
    system: "Sega CD",
    region: "USA",
    size: 131072,
    md5: "854b9150240a198070150e4566ae1290",
    hashSource: "docs.libretro.com/library/genesis_plus_gx/",
    required: true,
    targets: [{ emulator: "retroarch", dir: "", filename: "bios_CD_U.bin" }],
  },
  {
    id: "segacd-bios-eu",
    system: "Sega CD",
    region: "Europe",
    size: 131072,
    md5: "e66fa1dc5820d254611fdcdba0662372",
    hashSource: "docs.libretro.com/library/genesis_plus_gx/",
    required: true,
    targets: [{ emulator: "retroarch", dir: "", filename: "bios_CD_E.bin" }],
  },
  {
    id: "segacd-bios-jp",
    system: "Sega CD",
    region: "Japan",
    size: 131072,
    md5: "278a9397d192149e84e820ac621a8edd",
    hashSource: "docs.libretro.com/library/genesis_plus_gx/",
    required: true,
    targets: [{ emulator: "retroarch", dir: "", filename: "bios_CD_J.bin" }],
  },

  // --- Dreamcast ------------------------------------------------------------
  // dc_boot.bin: MD5 from docs.libretro.com/library/flycast/ (checked
  // 2026-09-20), which lists it as Optional — Flycast has a "Force HLE BIOS"
  // core option and plays most games without a real one. Its size (2MB) is
  // not on that page; it is community-reported, not verified against a
  // physical dump in this session.
  //
  // Two targets on purpose: catalog.json's "flycast" slug is the standalone
  // XboxEmulationHub appx, but RetroArch's own AllCores build (also in
  // catalog.json) bundles a flycast core too. Same firmware, two homes —
  // the exact case this module exists to handle.
  {
    id: "dreamcast-boot",
    system: "Dreamcast",
    size: 2097152,
    md5: "e10c53c2f8b90bab96ead2d368858623",
    hashSource: "docs.libretro.com/library/flycast/",
    required: false,
    targets: [
      { emulator: "retroarch", dir: "dc", filename: "dc_boot.bin" },
      {
        emulator: "flycast",
        dir: "",
        filename: "dc_boot.bin",
        note:
          "Best-effort default: this project has not yet confirmed where the " +
          "standalone flycast-2.7.appx (XboxEmulationHub build) exposes its " +
          "own data folder once it is in Game mode on the console. Check with " +
          "`xbdev ls --dev flycast` the way retroarch.cfg's E:\\BIOS was, and " +
          "correct roots.flycast in defaultRoots() below once known.",
      },
    ],
  },
  {
    id: "dreamcast-flash",
    system: "Dreamcast",
    size: 131072,
    // No sha1/md5 here on purpose: docs.libretro.com/library/flycast/ does
    // not list dc_flash.bin in its BIOS table at all (only mentions it in a
    // compatibility note), so there is nothing to cite. Deliberately NOT
    // marked matchBySizeAlone either: 128KB is a common size for things
    // that are not a Dreamcast flash dump (it collides with the Sega CD
    // BIOS entries above, which do carry real hashes), so a lone size match
    // would be a guess dressed up as a finding. Net effect, stated plainly:
    // this module cannot yet auto-recognise a genuine dc_flash.bin from its
    // content alone. It stays in the table as documentation and as a
    // fileIt() target for the day a verified hash is added. In the
    // meantime dc_boot.bin plus Flycast's HLE mode (see the note on that
    // entry) covers most games without it.
    required: false,
    targets: [
      { emulator: "retroarch", dir: "dc", filename: "dc_flash.bin" },
      { emulator: "flycast", dir: "", filename: "dc_flash.bin", note: "see dreamcast-boot's note above" },
    ],
  },

  // --- Neo Geo (arcade) -----------------------------------------------------
  // docs.libretro.com/library/fbneo/ (checked 2026-09-20): the Neo Geo BIOS
  // is a MAME-style multi-ROM zip containing "MVS Asia/Europe ver. 6
  // (1 slot)" at minimum, not one file with one meaningful hash — see
  // ArchiveBiosEntry above. FBNeo searches, in order: next to the game ROM,
  // then SYSTEM_DIRECTORY/fbneo/, then SYSTEM_DIRECTORY/ root. Filing to
  // both of the last two covers every FBNeo build without needing to know
  // which search path the installed one actually uses.
  {
    id: "neogeo-mvs-bios",
    system: "Neo Geo",
    archive: true,
    required: true,
    note: 'MVS/AES arcade BIOS set — needs at least "MVS Asia/Europe ver. 6 (1 slot)".',
    targets: [
      { emulator: "retroarch", dir: "", filename: "neogeo.zip" },
      { emulator: "retroarch", dir: "fbneo", filename: "neogeo.zip" },
    ],
  },
  {
    id: "neogeo-cd-bios",
    system: "Neo Geo CD",
    archive: true,
    required: true,
    targets: [
      { emulator: "retroarch", dir: "", filename: "neocdz.zip" },
      { emulator: "retroarch", dir: "fbneo", filename: "neocdz.zip" },
    ],
  },

  // --- PlayStation 2 ----------------------------------------------------------
  // pcsx2.net/docs/setup/bios/ (checked 2026-09-20): PCSX2 — and so XBSX2,
  // the same engine ported to UWP — reads the console model and region out
  // of the BIOS image's own header, not out of its filename, unlike every
  // RetroArch core above. That page states the main .bin is "always exactly
  // 4 MB"; no fixed filename is expected, so none is given as a target
  // below and fileIt() keeps the source file's own name.
  //
  // No hash is kept here on purpose, and this is a deliberate difference
  // from PS1/Sega CD: a real PS2 has one of many valid BIOS revisions
  // depending on its hardware version and region, so there is no fixed
  // small set of "correct" checksums to check against the way three cover
  // every PS1. matchBySizeAlone is set because this is the structural case
  // that flag exists for, not a stand-in for a hash we failed to find —
  // PCSX2 itself does not check a hash either, it parses the header, which
  // this module does not attempt to replicate; size is the closest cheap
  // proxy, and 4MB on the nose is distinctive enough to accept.
  {
    id: "ps2-bios",
    system: "PlayStation 2",
    size: 4194304,
    matchBySizeAlone: true,
    required: true,
    note:
      "PCSX2's own dumping tool (biosdrain — see docs/BIOS.md) also writes " +
      "companion .rom0/.rom1/.nvm files alongside the main .bin. Only the " +
      ".bin is tracked here; XBSX2 needs it to boot, the rest is optional.",
    targets: [{ emulator: "xbsx2", dir: "" }],
  },
];

// ---------------------------------------------------------------- roots

/**
 * This project's own convention: RetroArch is configured (configs/retroarch.cfg,
 * system_directory) to read its BIOS from `<drive>\BIOS`, and `xbdev
 * setup-retroarch` is what actually sends that file to the console — so the
 * "retroarch" entry below is a measured fact, not a guess.
 *
 * Nothing else in catalog.json has had its own BIOS folder confirmed against
 * a real console yet, so the rest of this map is a placeholder default: a
 * reasonable per-emulator subfolder under the same E:\BIOS root, not a
 * measured one. Check each one with `xbdev ls --dev <app>` the way
 * retroarch.cfg's path was, and correct it here once confirmed.
 */
export function defaultRoots(driveRoot: string): Partial<Record<EmulatorId, string>> {
  return {
    retroarch: join(driveRoot, "BIOS"),
    xbsx2: join(driveRoot, "BIOS", "ps2"),
    flycast: join(driveRoot, "BIOS", "dc"),
    dolphin: join(driveRoot, "BIOS", "gc-wii"),
    ppsspp: join(driveRoot, "BIOS", "psp"),
    supermodel: join(driveRoot, "BIOS", "model3"),
  };
}

// ---------------------------------------------------------------- identify

export type IdentifyConfidence =
  | "hash" // a listed sha1/md5 matched — as sure as this table can be
  | "size" // only size matched; this entry carries no hash to check (ps2-bios)
  | "name"; // an archive entry, recognised by filename only — see ArchiveBiosEntry

export type IdentifyResult = {
  entry: BiosEntry;
  confidence: IdentifyConfidence;
};

async function hashFile(path: string, algorithm: "sha1" | "md5"): Promise<string> {
  const hasher = new Bun.CryptoHasher(algorithm);
  hasher.update(await Bun.file(path).arrayBuffer());
  return hasher.digest("hex");
}

/**
 * Identifies a single file against KNOWN_BIOS_FILES. Cheap on purpose: size
 * is a stat, checked against every candidate first; a hash is computed only
 * for a file whose size already matches something, and at most once per
 * algorithm actually needed to tell candidates apart.
 */
export async function identify(filePath: string): Promise<IdentifyResult | null> {
  const file = Bun.file(filePath);
  if (!(await file.exists())) return null;

  // Archives first: they are told apart by filename, not by hashing the
  // whole zip — see the comment on ArchiveBiosEntry.
  const name = basename(filePath).toLowerCase();
  const archiveMatch = KNOWN_BIOS_FILES.find(
    (entry): entry is ArchiveBiosEntry =>
      Boolean(entry.archive) && entry.targets.some((target) => (target.filename ?? "").toLowerCase() === name),
  );
  if (archiveMatch) return { entry: archiveMatch, confidence: "name" };

  const size = file.size;
  const candidates = KNOWN_BIOS_FILES.filter(
    (entry): entry is SingleBiosEntry => !entry.archive && entry.size === size,
  );
  if (candidates.length === 0) return null;

  let sha1: string | undefined;
  let md5: string | undefined;
  for (const entry of candidates) {
    if (entry.sha1) {
      sha1 ??= await hashFile(filePath, "sha1");
      if (sha1 === entry.sha1) return { entry, confidence: "hash" };
    }
    if (entry.md5) {
      md5 ??= await hashFile(filePath, "md5");
      if (md5 === entry.md5) return { entry, confidence: "hash" };
    }
  }

  // Nothing hashed matched. Only a candidate that opted into matchBySizeAlone
  // (ps2-bios — see its comment above) can still be identified from this,
  // and only if it is the single unambiguous one at this exact size; a
  // hashless entry that did NOT opt in (dreamcast-flash) is left unmatched
  // on purpose rather than guessed from size alone.
  const sizeOnly = candidates.filter((entry) => entry.matchBySizeAlone);
  if (sizeOnly.length === 1) return { entry: sizeOnly[0], confidence: "size" };

  // Size matched something, but the content did not match any known hash —
  // a corrupt dump, a hacked BIOS, or a region docs.libretro.com does not
  // list. Not confident enough to claim it silently.
  return null;
}

// ---------------------------------------------------------------- fileIt

export type FiledCopy = { emulator: EmulatorId; path: string };
export type SkippedTarget = { emulator: EmulatorId; reason: string };

export type FileItReport = {
  source: string;
  entry: BiosEntry;
  confidence: IdentifyConfidence;
  copies: FiledCopy[];
  skipped: SkippedTarget[];
};

/**
 * Copies one already-recognisable file to every emulator destination that
 * wants it, under that emulator's own expected filename. `roots` maps an
 * emulator id to that emulator's own BIOS/system root on disk — see
 * defaultRoots() for this project's E:\ drive layout. An emulator missing
 * from `roots` is skipped, not an error: the caller may simply not have it
 * installed.
 *
 * Returns null when the file is not one of KNOWN_BIOS_FILES.
 */
export async function fileIt(
  filePath: string,
  roots: Partial<Record<EmulatorId, string>>,
): Promise<FileItReport | null> {
  const found = await identify(filePath);
  if (!found) return null;

  const copies: FiledCopy[] = [];
  const skipped: SkippedTarget[] = [];

  for (const target of found.entry.targets) {
    const root = roots[target.emulator];
    if (!root) {
      skipped.push({ emulator: target.emulator, reason: "no root configured for this emulator" });
      continue;
    }
    const destDir = target.dir ? join(root, target.dir) : root;
    const destName = target.filename ?? basename(filePath);
    const dest = join(destDir, destName);

    if (dest === filePath) {
      // Scanning the BIOS folder that already holds the correctly named
      // file: nothing to copy, but it did land where it belongs.
      copies.push({ emulator: target.emulator, path: dest });
      continue;
    }

    await mkdir(destDir, { recursive: true });
    await Bun.write(dest, Bun.file(filePath));
    copies.push({ emulator: target.emulator, path: dest });
  }

  return { source: filePath, entry: found.entry, confidence: found.confidence, copies, skipped };
}

// ---------------------------------------------------------------- zip helpers
//
// Same technique as src/icons.ts's entries()/readEntry(): shell out to the
// system unzip rather than add a zip-parsing dependency.

async function zipEntryNames(zip: string): Promise<string[]> {
  const proc = Bun.spawn([UNZIP, "-Z1", zip], { stdout: "pipe", stderr: "ignore" });
  const text = await new Response(proc.stdout).text();
  await proc.exited;
  return text.split("\n").filter(Boolean);
}

async function extractZipEntry(zip: string, entry: string, outFile: string): Promise<void> {
  const proc = Bun.spawn([UNZIP, "-p", zip, entry], { stdout: "pipe", stderr: "ignore" });
  const bytes = new Uint8Array(await new Response(proc.stdout).arrayBuffer());
  await proc.exited;
  await Bun.write(outFile, bytes);
}

/**
 * Looks inside a .zip for a single BIOS file bundled under the wrong name,
 * alongside a readme — the shape a "PS1 bios pack.zip" someone shares
 * usually takes. Every entry is extracted to a scratch folder and run
 * through the normal fileIt() path, which trades a little extra I/O for
 * reusing the exact same recognition and copying logic rather than a
 * second, size-peeking one. A zip that is itself a known archive entry
 * (neogeo.zip and the like) never reaches this function: identify() already
 * matches those whole, by filename, before scan() would consider peeking.
 */
async function scanZip(zipPath: string, roots: Partial<Record<EmulatorId, string>>): Promise<FileItReport[]> {
  const filed: FileItReport[] = [];
  const names = (await zipEntryNames(zipPath)).filter((name) => !name.endsWith("/"));
  if (names.length === 0) return filed;

  const scratch = await mkdtemp(join(tmpdir(), "xbdev-bios-"));
  try {
    for (const name of names) {
      const staged = join(scratch, basename(name) || "entry");
      await extractZipEntry(zipPath, name, staged);
      const report = await fileIt(staged, roots);
      if (report) filed.push({ ...report, source: `${zipPath}!${name}` });
    }
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
  return filed;
}

// ---------------------------------------------------------------- scan

export type ScanReport = {
  filed: FileItReport[];
  /** Files that matched nothing — worth showing the user, not an error. */
  unrecognised: string[];
};

/**
 * Walks a folder — typically a mounted USB stick — and files everything it
 * recognises in one pass, including a BIOS bundled inside a .zip. This is
 * the "plug the stick in and it just works" entry point.
 */
export async function scan(folderPath: string, roots: Partial<Record<EmulatorId, string>>): Promise<ScanReport> {
  const filed: FileItReport[] = [];
  const unrecognised: string[] = [];

  const walk = async (dir: string): Promise<void> => {
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      if (entry.name.startsWith(".")) continue; // dotfiles: scratch/metadata, not user content
      const full = join(dir, entry.name);

      if (entry.isDirectory()) {
        await walk(full);
        continue;
      }

      const direct = await fileIt(full, roots);
      if (direct) {
        filed.push(direct);
        continue;
      }

      if (extname(entry.name).toLowerCase() === ".zip") {
        const fromZip = await scanZip(full, roots);
        if (fromZip.length > 0) {
          filed.push(...fromZip);
          continue;
        }
      }

      unrecognised.push(full);
    }
  };

  await walk(folderPath);
  return { filed, unrecognised };
}
