# BIOS, firmware and keys

This project never ships, mirrors or links to a BIOS, firmware or key file.
What it ships instead is `src/bios.ts`: drop a file you already own onto the
external drive — from a USB stick, an internal disk, a network share, it does
not matter where it came from — and it is recognised and filed into every
installed emulator's own folder, under the exact filename that emulator
expects. No folder picking, no renaming, no emulator settings screen.

Recognition is by content (file size, then a hash), never by filename. A file
named `whatever.bin` that is actually a real PS1 Europe BIOS dump is still
found and renamed to `scph5502.bin` on the way in; a file that merely happens
to be named `scph5502.bin` but is not one is not filed anywhere.

Most systems in this project's catalog need none of this at all — see the
first section below.

## Systems that need no BIOS at all

| System | Emulator (this project) | Why |
| --- | --- | --- |
| NES, SNES | RetroArch cores | Cartridge-based; the game itself is everything the console needs to run. (A handful of add-on carts — Sufami Turbo, Satellaview/BS‑X — need their own small base ROM; ordinary NES/SNES games do not.) |
| Genesis / Mega Drive | RetroArch cores | Cartridge-based, same as NES/SNES. Only the CD add-on (Sega CD/Mega CD) needs a boot ROM — see below. |
| Nintendo 64 | RetroArch cores | Cartridge-based. (The Japan-only 64DD add-on needs its own IPL ROM; ordinary N64 games do not.) |
| GameCube, Wii | Dolphin | Dolphin boots with a high-level (HLE) reimplementation of the boot ROM by default. A real `IPL.bin`/Wii NAND can be added for extra accuracy, but normal games run without one. |
| PSP | PPSSPP | Built from the start as a full high-level reimplementation of the PSP's OS — no real PSP firmware dump is used or needed. |
| Xbox 360 | Xenia Canary | A from-scratch, high-level reimplementation of the 360 kernel — no kernel/firmware dump is used or needed. |
| DOS games | DOSBox Pure | DOSBox supplies its own clean-room BIOS and DOS-compatible shell; see the next section. |
| Flash games | Ruffle | A from-scratch, from-source reimplementation of the Flash Player runtime in Rust — no Adobe Flash Player binary is used or needed. |
| LucasArts/Sierra-era adventure games | ScummVM | ScummVM interprets each game's own scripts directly; it needs the game's data files (which the user must own) but no separate interpreter/BIOS binary. |
| M.U.G.E.N-style fighting games | Ikemen GO | Same shape as ScummVM: it needs a game's character/screenpack data, not a BIOS. |
| Beat 'em up engine games | OpenBOR | Same shape again: needs a game's `.pak`, not a BIOS. |

**A closely related but distinct case, worth being honest about:** GZDoom and
Raze are native PC engines, not emulators, and need no BIOS — but they do need
the original commercial game's data file (an IWAD for GZDoom, a GRP for Raze)
to play the actual game. That is copyrighted game data, governed by the same
"bring your own legally-owned copy" rule as a ROM, just not a BIOS. See the
next section for one case where that requirement disappears entirely.

## Systems with a free/open replacement

- **DOS, via DOSBox Pure.** DOSBox does not emulate a licensed IBM PC BIOS or
  Microsoft's DOS — it ships its own independently written, open-source BIOS
  and a DOS-compatible shell (its internal `Z:\` drive and `COMMAND.COM`).
  This is already built into `dosbox-pure` in this project's catalog; there is
  nothing to add.
- **Doom, via GZDoom.** [Freedoom](https://freedoom.github.io/) is a complete,
  free, GPL-licensed IWAD — a full replacement game, not a copy of id
  Software's, built specifically to be legally distributed. GZDoom runs it
  directly with no BIOS and no purchase. It does not unlock id Software's own
  *Doom*/*Doom II* campaigns — for those the user still needs their own
  legally-owned copy — but it is a genuine, legal way to have GZDoom running a
  real game with nothing else supplied.

No other system in this catalog has a free/open replacement this project is
confident enough to name and recommend. In particular: unofficial "Universal
BIOS" (`uni-bios.rom`) replacements circulate for Neo Geo, but they are
independently written homebrew of unverified provenance and licensing, not an
official or clearly-licensed open-source project — this project does not vet,
bundle or recommend one, and treats Neo Geo as requiring the user's own dump
below.

## Systems that genuinely require the user's own dump

`src/bios.ts` knows how to recognise and file these once the user has them.
It never helps obtain them. All facts below (filenames, sizes, hashes) are
cross-checked against docs.libretro.com and pcsx2.net, current as of
2026-09-20 — see the comments beside each entry in `src/bios.ts` for the exact
page cited per fact, and for the handful of numbers that are common emulation
knowledge rather than something either page states outright.

| System | Emulator here | Files | Notes |
| --- | --- | --- | --- |
| PlayStation | RetroArch (Beetle PSX / PCSX ReARMed cores) | `scph5500.bin` (Japan), `scph5501.bin` (USA), `scph5502.bin` (Europe) — 512KB each | Optional: both cores fall back to the open-source **OpenBIOS** reimplementation when none of the three is present, so a game still boots, just less accurately. |
| Sega CD / Mega CD | RetroArch (Genesis Plus GX core) | `bios_CD_U.bin`, `bios_CD_E.bin`, `bios_CD_J.bin` — 128KB each | Required — Genesis Plus GX will not run a Sega CD/Mega CD game without the matching region's boot ROM. |
| Dreamcast | Flycast (standalone), RetroArch's bundled Flycast core | `dc_boot.bin` (2MB), `dc_flash.bin` (128KB) | Optional: Flycast has a "Force HLE BIOS" mode and plays most games without either file. `dc_flash.bin` has no publicly documented hash this project could verify, so `src/bios.ts` cannot yet auto-recognise a real dump of it from its content alone — see the comment on it in the source. |
| Neo Geo (arcade) | RetroArch (FBNeo core) | `neogeo.zip` — a multi-ROM MAME-style archive, at minimum "MVS Asia/Europe ver. 6 (1 slot)" | Required. Neo Geo CD needs the separate `neocdz.zip`. |
| PlayStation 2 | XBSX2 | one `.bin`, always exactly 4MB | Required. Unlike the others, PCSX2 (and so XBSX2, the same engine) reads the console model and region out of the file's own header rather than trusting its filename, so there is no single fixed name or hash to check — any legitimate PS2's dump is a different, valid BIOS. |
| Sega Model 3 (arcade) | Supermodel | — | Not a separate file: each Model 3 game's own MAME-style ROM zip already bundles that stepping's boot code. There is nothing standalone to file here; owning the game's ROM set is owning its BIOS. |

### What the guided extraction looks like, system by system

Every method below reads a chip or a disc the user's own console already
contains and writes the result to removable media they already own. None of
it is provided, linked or hosted by this project — these are pointers to
*what kind of tool this is*, not to a copy of anyone's firmware.

- **PlayStation.** With the console already accepting homebrew (through an
  exploit such as FreePSXBoot, or an existing modchip), a dumping tool such as
  UniROM or Caetla has a "Dump BIOS" option that reads the BIOS and writes it
  to a memory card. From there it is copied to a PC — directly if the memory
  card reader is available, or by way of a PS2/PS3 with a memory card adaptor.

- **Sega CD / Mega CD.** The boot ROM lives on a chip on the console's own
  board. The common hobbyist route is a flash cart or SD-based loader capable
  of running homebrew on real Sega CD/Mega CD hardware, which includes a
  BIOS-dumping tool; the exact tool depends on the specific console revision
  and loader available. This is the most hardware-dependent of the group.

- **Dreamcast.** A self-boot CD-R containing a BIOS-dumping homebrew tool
  (the Dreamcast's GD-ROM drive can boot burned CD-Rs on original hardware,
  no mod needed) reads `dc_boot.bin`/`dc_flash.bin` and writes them out —
  typically to an SD card by way of a Dreamcast SD adaptor, or to a VMU/serial
  link on setups without one.

- **Neo Geo (arcade).** This one is not a software dump: the BIOS is a set of
  ROM chips on an MVS motherboard or inside an AES cartridge. Reading it means
  opening the case and reading those chips with a universal EPROM/flash
  programmer — ordinary hobbyist hardware, but hardware nonetheless.

- **PlayStation 2.** PCSX2's own documentation names the tool directly:
  **biosdrain**, run from a PS2 already modified with FreeMcBoot or
  FreeDVDBoot, launched through uLaunchELF, writing to a FAT32/MBR-formatted
  USB drive. It produces the main `.bin` plus several companion files
  (`.rom0`, `.rom1`, `.nvm`, prefixed with the console's own model ID) — see
  [pcsx2.net/docs/setup/bios/](https://pcsx2.net/docs/setup/bios/) for the
  current, authoritative steps, which change less often than a copy pasted
  here would stay accurate.

- **Sega Model 3.** No separate extraction: dumping the game's own arcade PCB
  ROM set (again, an EPROM programmer job) is dumping its BIOS, since the two
  are not distinct files.

## Why this project does not host or link to these files

Three reasons, and only the first is strictly legal — the other two are why
"just this once" would not actually make anything better for anyone using
this project:

1. **Copyright.** A BIOS/firmware dump is the console manufacturer's own
   copyrighted code. Distributing it — or linking to somewhere that does —
   without a license to do so is infringement, full stop, regardless of how
   old the hardware is or how the file is labelled.
2. **Provenance.** A BIOS pulled from a random link is unverifiable: it could
   be corrupted, patched, or bundled with something that has nothing to do
   with emulation. A file the user dumped from their own console has none of
   that uncertainty, which is exactly why `src/bios.ts` checks size and hash
   before trusting anything, instead of trusting a filename or a source.
3. **It would not survive.** A repository or a release that carries copyrighted
   firmware gets its hosting pulled, which breaks the parts of this project
   that have nothing to do with BIOS files at all. Keeping this project to
   "recognise and file what you already own" is what lets it keep existing.
