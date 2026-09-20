// Working out which system a downloaded file belongs to, from its extension
// and — where the extension alone lies — its header bytes.
//
// Some extensions are unambiguous (.nds, .gba): nothing else worth worrying
// about reuses them, so the extension is the answer. Others are not: .iso is
// used by a data disc, a PS1 game, a PS2 game, a GameCube game and a Wii game
// alike, .bin is used by a Genesis cartridge dump and by a dozen different
// CD-based systems' raw tracks, and .3ds collides with an Autodesk 3ds Max
// scene file. For those, the header is read and checked against the magic
// bytes each format actually documents.
//
// Every fact asserted here is the kind that is public and load-bearing for
// the format (a real emulator or tool checks the same bytes to boot the
// file) — GBATEK for GBA/NDS, 3dbrew for 3DS, switchbrew for Switch, Pan Docs
// for Game Boy, WiiBrew/Dolphin for GameCube and Wii, ECMA-119 for ISO9660,
// MAME for CHD. Where a check instead relies on something recalled rather
// than a primary source (the FDS header forms, the exact Sega ID strings),
// the confidence is marked down and the comment says so, per the brief: mark
// it low confidence rather than guess.

import { stat } from "node:fs/promises";
import { dirname, join } from "node:path";
import { human } from "./util";

// ---------------------------------------------------------------- contracts

export type Confidence = "high" | "medium" | "low";

export type DetectResult = {
  system: SystemId;
  confidence: Confidence;
  reason: string;
};

/**
 * Every system a detector below can name. Kept as a closed list so a picker
 * screen has something concrete to offer, and so `system` is always one of
 * these rather than a free-form string a detector made up on the spot.
 */
export const SYSTEMS = [
  "nds",
  "3ds",
  "gba",
  "gb",
  "gbc",
  "nes",
  "fds",
  "snes",
  "n64",
  "gc",
  "wii",
  "psx",
  "ps2",
  "psp",
  "saturn",
  "segacd",
  "genesis",
  "dreamcast",
  "switch",
] as const;

export type SystemId = (typeof SYSTEMS)[number];

// ------------------------------------------------------------ byte helpers

const HEAD_WINDOW = 64 * 1024;

async function readHead(path: string, maxLength = HEAD_WINDOW): Promise<Uint8Array> {
  const buffer = await Bun.file(path).slice(0, maxLength).arrayBuffer();
  return new Uint8Array(buffer);
}

async function fileSize(path: string): Promise<number> {
  return (await stat(path)).size;
}

function extOf(path: string): string {
  const lower = path.toLowerCase();
  const dot = lower.lastIndexOf(".");
  return dot === -1 ? "" : lower.slice(dot);
}

function bytesEqual(bytes: Uint8Array, offset: number, expected: number[]): boolean {
  if (offset < 0 || offset + expected.length > bytes.length) return false;
  for (let i = 0; i < expected.length; i++) {
    if (bytes[offset + i] !== expected[i]) return false;
  }
  return true;
}

/** Raw bytes read as Latin-1, so every byte value round-trips as one char — good enough for ASCII magic strings, not for real text. */
function ascii(bytes: Uint8Array, offset: number, length: number): string {
  if (offset < 0 || offset + length > bytes.length) return "";
  return new TextDecoder("latin1").decode(bytes.subarray(offset, offset + length));
}

// ------------------------------------------------------- shared disc logic

/**
 * The Primary Volume Descriptor lives at logical sector 16, but "sector 16"
 * means a different byte offset depending on whether the dump kept the raw
 * 2352-byte CD sectors (and, if so, whether they are Mode 1 or Mode 2/XA) or
 * was stripped to plain 2048-byte sectors. This tries each in turn instead of
 * assuming one.
 */
function findIso9660Pvd(head: Uint8Array): number | null {
  const layouts = [
    { sector: 2048, skip: 0 }, // stripped to plain data sectors
    { sector: 2352, skip: 16 }, // raw Mode 1 (12-byte sync + 4-byte header)
    { sector: 2352, skip: 24 }, // raw Mode 2 Form 1 / XA (+ 8-byte subheader)
  ];
  for (const { sector, skip } of layouts) {
    const offset = sector * 16 + skip;
    if (offset + 6 > head.length) continue;
    if (head[offset] === 0x01 && ascii(head, offset + 1, 5) === "CD001") return offset;
  }
  return null;
}

/**
 * The checks shared by .gcm/.iso and by a raw CD track from .bin or .cue.
 * Order matters: GameCube and Wii are checked first because they are not
 * ISO9660 at all (their own disc header lives at a fixed offset instead), so
 * they would otherwise fall through to the generic ISO9660 branch and be
 * missed entirely.
 */
function detectRawDisc(head: Uint8Array, size: number): DetectResult | null {
  if (bytesEqual(head, 0x1c, [0xc2, 0x33, 0x9f, 0x3d])) {
    return { system: "gc", confidence: "high", reason: "GameCube disc magic word (0xC2339F3D) at offset 0x1C" };
  }
  if (bytesEqual(head, 0x18, [0x5d, 0x1c, 0x9e, 0xa3])) {
    return { system: "wii", confidence: "high", reason: "Wii disc magic word (0x5D1C9EA3) at offset 0x18" };
  }

  // Sega discs stamp themselves with plain ASCII text near the start of the
  // data track. The exact byte offset shifts with the sector layout (see
  // findIso9660Pvd above), so this searches a window instead of one offset.
  const window = ascii(head, 0, Math.min(head.length, 32 * 1024));
  if (window.includes("SEGA SEGAKATANA")) {
    return { system: "dreamcast", confidence: "medium", reason: '"SEGA SEGAKATANA" text found near the start of the disc (Dreamcast dev codename)' };
  }
  if (window.includes("SEGA SEGASATURN")) {
    return { system: "saturn", confidence: "medium", reason: '"SEGA SEGASATURN" text found near the start of the disc' };
  }
  if (window.includes("SEGADISCSYSTEM")) {
    // The exact literal (padding, spacing) is recalled from community
    // documentation of the Sega CD header rather than a primary Sega source,
    // so this stays at medium rather than high even though the hit is real.
    return { system: "segacd", confidence: "medium", reason: '"SEGADISCSYSTEM" text found near the start of the disc' };
  }

  // A Genesis/Mega Drive cartridge dump sometimes ends up with a .bin
  // extension too. The header names the console in plain ASCII at 0x100.
  if (ascii(head, 0x100, 16).startsWith("SEGA")) {
    return { system: "genesis", confidence: "high", reason: 'console name field ("SEGA...") at cartridge header offset 0x100' };
  }

  // Fall back to a plain ISO9660 filesystem. PS1 and PS2 both stamp
  // "PLAYSTATION" as the System Identifier there, so that alone does not
  // separate them — disc size does, since a CD tops out far below where a
  // DVD starts. It is a heuristic, not a spec, hence "medium".
  const pvd = findIso9660Pvd(head);
  if (pvd !== null) {
    const systemId = ascii(head, pvd + 8, 32).trim();
    if (systemId.startsWith("PLAYSTATION")) {
      const system: SystemId = size > 900 * 1024 * 1024 ? "ps2" : "psx";
      return {
        system,
        confidence: "medium",
        reason: `ISO9660 System Identifier is "PLAYSTATION"; guessed ${system} from disc size (${human(size)})`,
      };
    }
    // A real ISO9660 filesystem with no console-specific marker: a PC game,
    // a plain data disc, or a system this table does not cover. Nothing
    // honest to report.
    return null;
  }

  return null;
}

/**
 * A .cue is a text index naming the track file(s) it goes with — it carries
 * no header bytes of its own. This finds the first referenced file next to
 * the sheet and runs the same raw-disc check against that.
 */
async function detectCueSheet(cuePath: string): Promise<DetectResult | null> {
  const text = await Bun.file(cuePath)
    .text()
    .catch(() => null);
  if (!text) return null;

  const match = text.match(/FILE\s+"([^"]+)"\s+BINARY/i) ?? text.match(/FILE\s+(\S+)\s+BINARY/i);
  if (!match) return null; // does not look like a cue sheet this understands

  const trackPath = join(dirname(cuePath), match[1]);
  const trackFile = Bun.file(trackPath);
  if (!(await trackFile.exists())) return null; // the track is not sitting next to the sheet (yet?)

  const [size, head] = await Promise.all([fileSize(trackPath), readHead(trackPath)]);
  return detectRawDisc(head, size);
}

// -------------------------------------------------------------- detectors

type DetectContext = {
  path: string;
  size: number;
  head: Uint8Array;
};

type Detector = {
  extensions: string[];
  run: (ctx: DetectContext) => Promise<DetectResult | null> | DetectResult | null;
};

const DETECTORS: Detector[] = [
  {
    extensions: [".nds"],
    // Not reused by anything else worth worrying about. The cartridge
    // header also carries a fixed logo checksum (GBATEK documents 0xCF56 at
    // offset 0x15C) that could verify this further, but that exact value is
    // recalled rather than confirmed here, so it is left unchecked instead
    // of asserted as fact.
    run: () => ({ system: "nds", confidence: "high", reason: "unambiguous extension (.nds)" }),
  },
  {
    extensions: [".3ds"],
    // The one genuinely famous collision: .3ds is also Autodesk 3ds Max.
    // Extension alone is not an answer here — see NCSD, the 3DS cartridge
    // header magic, at offset 0x100 (3dbrew).
    run: ({ head }) =>
      bytesEqual(head, 0x100, [0x4e, 0x43, 0x53, 0x44])
        ? { system: "3ds", confidence: "high", reason: 'NCSD magic at offset 0x100 (3DS cartridge image header)' }
        : null,
  },
  {
    extensions: [".cia"],
    // A CIA's first four bytes are a little-endian header-size field, which
    // is 0x2020 for the standard CTR Importable Archive layout (3dbrew).
    run: ({ head }) => {
      const headerSize = head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24);
      if (headerSize === 0x2020) {
        return { system: "3ds", confidence: "high", reason: "CIA header size field is 0x2020, the standard archive header length" };
      }
      return { system: "3ds", confidence: "medium", reason: ".cia extension is not reused elsewhere, but the header size field was not the usual 0x2020" };
    },
  },
  {
    extensions: [".gba"],
    // GBATEK's "FixedValue": every valid header has byte 0x96 at 0xB2.
    run: ({ head }) =>
      head[0xb2] === 0x96
        ? { system: "gba", confidence: "high", reason: "fixed header byte 0x96 at offset 0xB2 (GBATEK)" }
        : { system: "gba", confidence: "medium", reason: ".gba extension is unambiguous, but the fixed byte at 0xB2 was not 0x96" },
  },
  {
    extensions: [".gb", ".gbc"],
    // The first 8 bytes of the Nintendo boot logo, required at 0x104 on
    // every real cartridge (Pan Docs). The CGB flag at 0x143 then says
    // whether the same header belongs to a plain GB or a GBC title.
    run: ({ head, path }) => {
      const logo = [0xce, 0xed, 0x66, 0x66, 0xcc, 0x0d, 0x00, 0x0b];
      if (!bytesEqual(head, 0x104, logo)) return null;
      const cgbFlag = head[0x143];
      const isColor = extOf(path) === ".gbc" || cgbFlag === 0x80 || cgbFlag === 0xc0;
      return {
        system: isColor ? "gbc" : "gb",
        confidence: "high",
        reason: "Nintendo boot logo present at offset 0x104 (Pan Docs)",
      };
    },
  },
  {
    extensions: [".nes"],
    // The iNES container header — nearly every .nes dump has one.
    run: ({ head }) =>
      bytesEqual(head, 0, [0x4e, 0x45, 0x53, 0x1a])
        ? { system: "nes", confidence: "high", reason: 'iNES header magic "NES\\x1A"' }
        : { system: "nes", confidence: "medium", reason: ".nes extension is unambiguous, but no iNES header was found (headerless dump?)" },
  },
  {
    extensions: [".fds"],
    // Two dump conventions exist: the fwNES-style header some tools prepend,
    // and a raw dump that starts with the disk-side block identifier text.
    // Both byte sequences are recalled from NESDev community documentation
    // rather than a primary Nintendo source, so this stays at "low" even on
    // a hit — the extension-only fallback below is more honest at "medium".
    run: ({ head }) => {
      if (bytesEqual(head, 0, [0x46, 0x44, 0x53, 0x1a])) {
        return { system: "fds", confidence: "low", reason: 'fwNES-style "FDS\\x1A" header (recalled, not verified against a primary source)' };
      }
      if (ascii(head, 0, 32).includes("*NINTENDO-HVC*")) {
        return { system: "fds", confidence: "low", reason: 'raw disk-side block identifier text (recalled, not verified against a primary source)' };
      }
      return { system: "fds", confidence: "medium", reason: ".fds extension is not reused elsewhere, but neither known header form was found" };
    },
  },
  {
    extensions: [".sfc", ".smc"],
    // SNES cartridges have no fixed-offset magic bytes at all: the internal
    // header (title, checksum/complement pair, mapping mode) sits at 0x7FC0
    // or 0xFFC0 depending on LoROM vs HiROM, and a .smc dump may additionally
    // carry a 512-byte copier header shifting everything. None of that is a
    // reliable "is this SNES" check, only a "how is it mapped" one — so this
    // is extension-only, honestly.
    run: () => ({ system: "snes", confidence: "medium", reason: "extension only — SNES ROMs have no fixed-offset magic bytes to confirm against" }),
  },
  {
    extensions: [".n64", ".z64", ".v64"],
    // The three extensions are really the same ROM in three byte orders, and
    // the first four bytes name which one regardless of what the file was
    // renamed to — every N64 emulator uses exactly this to fix byte order.
    run: ({ head }) => {
      if (bytesEqual(head, 0, [0x80, 0x37, 0x12, 0x40])) {
        return { system: "n64", confidence: "high", reason: "big-endian (native/.z64) byte-order magic" };
      }
      if (bytesEqual(head, 0, [0x37, 0x80, 0x40, 0x12])) {
        return { system: "n64", confidence: "high", reason: "byte-swapped (.v64) byte-order magic" };
      }
      if (bytesEqual(head, 0, [0x40, 0x12, 0x37, 0x80])) {
        return { system: "n64", confidence: "high", reason: "little-endian/word-swapped (.n64) byte-order magic" };
      }
      return null; // the extension alone does not even promise a byte order, let alone N64 at all
    },
  },
  {
    extensions: [".gcm", ".iso"],
    // ".iso" is the whole reason this table reads headers instead of trusting
    // extensions: it is used by GameCube, Wii, PS1, PS2 and plain data discs
    // alike. See detectRawDisc for the actual checks.
    run: ({ head, size }) => detectRawDisc(head, size),
  },
  {
    extensions: [".bin"],
    // The single most overloaded extension here: Genesis cartridge dumps,
    // raw CD tracks for half a dozen CD-based systems, and generic binary
    // blobs all use it. Same checks as .iso/.gcm; returns null rather than
    // guess when none of them hit.
    run: ({ head, size }) => detectRawDisc(head, size),
  },
  {
    extensions: [".cue"],
    run: ({ path }) => detectCueSheet(path),
  },
  {
    extensions: [".gdi"],
    // A GDI is a small text index — track count, then one fixed-column line
    // per track — that is essentially exclusive to Dreamcast GD-ROM dumps.
    run: async ({ path }) => {
      const text = await Bun.file(path)
        .text()
        .catch(() => null);
      if (!text) return null;
      const firstLine = text.trim().split(/\r?\n/, 1)[0]?.trim();
      if (firstLine && /^\d+$/.test(firstLine)) {
        return { system: "dreamcast", confidence: "high", reason: "GDI track list (bare track count on line 1), a shape essentially unique to Dreamcast dumps" };
      }
      return null;
    },
  },
  {
    extensions: [".rvz", ".wia"],
    // Compressed GameCube/Wii disc containers (Dolphin's RVZ and its WIA
    // predecessor). Both wrap the disc in their own chunked/compressed
    // structure rather than a raw byte-for-byte image, so the offset checks
    // detectRawDisc relies on do not apply — telling GC from Wii here would
    // need real parsing of that container format, which this does not do.
    // Recognising the container would not change the outcome (still "ask the
    // picker"), so this does not even assert the magic bytes.
    run: () => null,
  },
  {
    extensions: [".wbfs"],
    // WBFS wraps Wii-only content by construction (WiiBrew).
    run: ({ head }) =>
      bytesEqual(head, 0, [0x57, 0x42, 0x46, 0x53])
        ? { system: "wii", confidence: "high", reason: 'WBFS magic "WBFS" at offset 0' }
        : null,
  },
  {
    extensions: [".nsp"],
    // A .nsp is a raw PFS0 partition filesystem (switchbrew).
    run: ({ head }) =>
      bytesEqual(head, 0, [0x50, 0x46, 0x53, 0x30])
        ? { system: "switch", confidence: "high", reason: 'PFS0 magic at offset 0 (Nintendo Submission Package is a raw partition filesystem)' }
        : null,
  },
  {
    extensions: [".xci"],
    // The cartridge image header magic sits at 0x100 (switchbrew).
    run: ({ head }) =>
      bytesEqual(head, 0x100, [0x48, 0x45, 0x41, 0x44])
        ? { system: "switch", confidence: "high", reason: '"HEAD" magic at offset 0x100 (cartridge image header)' }
        : null,
  },
  {
    extensions: [".chd"],
    // CHD is a container, not a system: the same "MComprHD" tag (MAME) wraps
    // CD, GD-ROM, hard disk and laserdisc images alike, and telling them
    // apart needs the metadata tags inside the file, which this does not
    // parse. Confirming the container does not change the outcome, so this
    // is honest about returning nothing rather than guessing a system.
    run: () => null,
  },
  {
    extensions: [".pbp"],
    // "\0PBP" is the PSP EBOOT/package magic. The same container also holds
    // PS1 Classics repackaged for PSP/Vita — telling those apart needs the
    // PARAM.SFO segment inside the file, not parsed here — so this defaults
    // to the far more common case at reduced confidence rather than at high.
    run: ({ head }) =>
      bytesEqual(head, 0, [0x00, 0x50, 0x42, 0x50])
        ? { system: "psp", confidence: "medium", reason: '"\\0PBP" magic at offset 0; could also be a repackaged PS1 Classic, not checked here' }
        : null,
  },
  {
    extensions: [".cso"],
    // CISO ("CISO" magic at offset 0) is used for both PSP and PS2
    // compressed images with nothing in the header that tells them apart.
    run: () => null,
  },
  {
    extensions: [".zso"],
    // ZSO ("ZISO" magic) is, in practice, an almost PS2-exclusive tool
    // convention — hence "medium" rather than "high".
    run: ({ head }) =>
      ascii(head, 0, 4) === "ZISO"
        ? { system: "ps2", confidence: "medium", reason: '"ZISO" magic at offset 0 — used almost exclusively for PS2 compressed images in practice' }
        : null,
  },
];

// ----------------------------------------------------------------- detect

/**
 * Identifies which system a downloaded file is for. Reads the extension
 * first; where the extension alone is ambiguous (or just to double-check
 * one that shouldn't be), reads the file's header bytes and checks them
 * against what that format actually documents.
 *
 * Returns `null` when nothing here recognises the file, or when the file is
 * a real instance of a container format that does not, by itself, name one
 * system (CHD, RVZ/WIA, a generic ISO9660 disc, ambiguous CISO) — in every
 * one of those cases the honest answer is "ask the picker screen", not a
 * guess dressed up as a result.
 */
export async function detect(filePath: string): Promise<DetectResult | null> {
  const ext = extOf(filePath);
  const candidates = DETECTORS.filter((d) => d.extensions.includes(ext));
  if (candidates.length === 0) return null;

  let size: number;
  try {
    size = await fileSize(filePath);
  } catch {
    return null; // gone, or not readable — nothing to detect
  }
  const head = await readHead(filePath);
  const ctx: DetectContext = { path: filePath, size, head };

  for (const detector of candidates) {
    const result = await detector.run(ctx);
    if (result) return result;
  }
  return null;
}

// ------------------------------------------------------------- archives

/**
 * The archive container formats intake.ts unpacks before running `detect()`
 * on what comes out. Not a system — a torrent or a Telegram upload can wrap
 * any of the formats above in any of these.
 */
export type ArchiveKind = "zip" | "rar" | "sevenzip" | "gzip";

export async function archiveKindOf(filePath: string): Promise<ArchiveKind | null> {
  const head = await readHead(filePath, 32);
  if (
    bytesEqual(head, 0, [0x50, 0x4b, 0x03, 0x04]) ||
    bytesEqual(head, 0, [0x50, 0x4b, 0x05, 0x06]) ||
    bytesEqual(head, 0, [0x50, 0x4b, 0x07, 0x08])
  ) {
    return "zip";
  }
  if (ascii(head, 0, 4) === "Rar!") return "rar"; // covers both RAR4 and RAR5's shared prefix
  if (bytesEqual(head, 0, [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c])) return "sevenzip";
  if (bytesEqual(head, 0, [0x1f, 0x8b])) return "gzip";
  return null;
}
