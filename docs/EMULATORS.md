# The emulator shelf

This is the other install path, next to `catalog.json`'s signed UWP packages:
plain portable x64 Windows builds — the same binaries anyone would run on a
Windows desktop — mapped and run through the console's own Win32 loader
instead of installed as a signed package. The catalog, and the code that
downloads, verifies, unpacks and configures each entry, lives in
`src/emulators.ts`; the actual factory config files are under
`configs/emulators/<id>/`.

Every URL and archive layout below was checked against the project's own
release feed on 2026-09-20 — by querying its release API and, for several
entries, by reading the real file listing inside the downloaded archive —
not assumed from a remembered file name. Where something could not be
verified to the same standard, that is called out below and in the matching
entry's `note` field in `src/emulators.ts`, rather than presented as settled.

## The table

| System | Emulator | Why this one | Needs the user's own BIOS? |
| --- | --- | --- | --- |
| PlayStation (PS1) | [DuckStation](https://github.com/stenzek/duckstation) | The standard for PS1 emulation today: fast, accurate, actively developed, and ships an official portable `windows-x64-release.zip` with no installer. | Runs BIOS-less in a pinch, but a real PS1 BIOS dump is what makes compatibility match a real console. |
| PlayStation 2 (PS2) | [PCSX2](https://pcsx2.net) | The only PS2 emulator that matters; official Qt-based Windows x64 build (`.7z`, no installer). Shares its input-binding code with DuckStation (same maintainer), so the two configs deliberately look alike. | **Required.** PCSX2 will not boot a game without a real PS2 BIOS dump. |
| PSP | [PPSSPP](https://www.ppsspp.org) | The only serious PSP emulator; official standalone Windows x64 build, distinct from the UWP-wrapped `ppsspp_uwp.zip` `catalog.json` already ships. | No — ships its own HLE firmware fonts. |
| GameCube / Wii | [Dolphin](https://dolphin-emu.org/download/) | The only serious GameCube/Wii emulator. **Manual install** — see below. | No — GameCube IPL / Wii NAND are optional extras, not required to run games. |
| Nintendo DS | [melonDS](https://github.com/melonDS-emu/melonDS) | The most accurate actively-developed DS emulator with an official portable Windows x64 build. | **Required.** Needs real `bios7.bin`/`bios9.bin`/`firmware.bin`; no HLE fallback. |
| Nintendo 3DS | [Azahar](https://azahar-emu.org) | Successor to Citra, which Nintendo's 2024 legal action shut down; Azahar is the same codebase, continued by the community, with an official portable Windows x64 build (`azahar-windows-msvc-*.zip`). | No — runs commercial games without a system archive; a NAND dump is only needed for a few edge cases. |
| Nintendo 64 | [Gopher64](https://github.com/gopher64/gopher64) | Successor to simple64 (archived by its own author on 2025-02-14 in favor of this from-scratch rewrite). Single self-contained portable `.exe`, and its own shipped default already binds an Xbox/XInput pad correctly — see the note below. | No — the PIF boot ROM is emulated (HLE). |
| SNES | [Snes9x](https://www.snes9x.com) | Fast, broadly compatible, official Windows x64 build, portable by default (no marker file needed). | No, beyond a couple of enhancement-chip games (Super FX, SA-1) emulated in software. |
| NES | [Mesen2](https://www.mesen.ca) | The most accurate NES emulator available, with an official portable Windows build. Also covers SNES/Game Boy/GBA/PC Engine, but is catalogued here for NES specifically — see below. | No. |
| Game Boy / Color / Advance | [mGBA](https://mgba.io) | The standard choice for the whole Game Boy family in one program; official portable Windows x64 `.7z`. | No — runs without BIOS via HLE; a real GBA BIOS only improves a few edge cases. |
| Mega Drive / Genesis | RetroArch — [Genesis Plus GX](https://github.com/libretro/Genesis-Plus-GX) core | No standalone Genesis emulator has a clearly-better, actively-maintained official Windows build (BlastEm does not publish one) — RetroArch's core is the community's actual daily driver here. | No, for cartridge games; Sega CD/32X add-ons are out of scope for this entry. |
| Saturn | RetroArch — [Beetle Saturn](https://github.com/libretro/beetle-saturn-libretro) core (Mednafen Saturn) | Saturn emulation is one of the cases where RetroArch's core genuinely is the best-supported option, ahead of the smaller standalone Saturn projects. | **Required** — real region BIOS, no HLE path. |
| Dreamcast | [Flycast](https://github.com/flyinghead/flycast) | The standard Dreamcast emulator, and — confirmed by reading its own source — portable on Windows *unconditionally*, no marker file needed. | Runs many games BIOS-free (HLE), but a real boot ROM + flash image noticeably improves compatibility. |
| Arcade | [MAME](https://www.mamedev.org) | The reference arcade emulator; official Windows binaries ship as a self-extracting 7-Zip archive on GitHub Releases. | **Per-game, not per-system** — many arcade boards need their own small BIOS ROM inside that game's own romset zip (e.g. `neogeo.zip`); there is no single shared arcade BIOS file. |
| Nintendo Switch | [Eden](https://eden-emu.dev) | "So pra brincar", per the request. Most actively developed of the post-Yuzu/Citron forks, with an official portable Windows x64 build. **Read the legal note below before treating this as settled.** | **Required** — the console owner's own `prod.keys` + firmware, dumped from their own Switch. This shelf never fetches, links to, or ships those. |

## Notes worth reading before shipping this

**Dolphin is the one manual install.** Dolphin does not publish binaries
through GitHub Releases, and its own download API
(`api.dolphin-emu.org`) sits behind bot-detection that returns a
"establishing a secure connection" challenge / 403 to every plain HTTP
client tested against it on 2026-09-20 — `curl`, and Claude's own WebFetch
tool, with and without a browser User-Agent. There is no reliable, scriptable
URL to pin in the catalog. `install()` for the `gamecube-wii` entry reflects
this: it does not attempt a download, and expects the console owner to fetch
the current 64-bit build from `https://dolphin-emu.org/download/` in a real
browser once, after which `configure()` drops the same real, verified config
files (`Dolphin.ini`, `GFX.ini`, `GCPadNew.ini`, `WiimoteNew.ini`) as every
other entry.

**Eden (Switch) sits on the least stable legal ground in this shelf.**
Nintendo filed a coordinated DMCA wave against 13 named Switch-emulator
projects — including Eden — in February 2026. As of 2026-09-20 Eden is still
actively developed and its official downloads (hosted on the project's own
`git.eden-emu.dev`, not GitHub — GitHub removed several of the named
projects) still resolve. That could change without notice; this is flagged
explicitly rather than presented as a durable install source.

**Genesis and Saturn share one RetroArch install, not two.** Both entries
install into the same portable RetroArch folder (`pacotes/emulators/retroarch/`
on this Mac, before anything is pushed to the console) and add only their
own core `.dll` and per-core `.opt` options file. This is a **separate**
RetroArch install from the signed UWP RetroArch `catalog.json` already
ships — same console, same `E:\` drive, deliberately mirrored config, but a
different binary form (plain portable Windows build vs. signed appx).

**Where a controller mapping is missing, it is missing on purpose.**
Several entries do not ship an explicit gamepad remap file: Gopher64 (N64),
Snes9x, mGBA, and Eden. In every one of these cases the emulator's own
default input handling was checked — either by reading its source
(Gopher64's `get_default_profile()` already binds `SDL_GAMEPAD_BUTTON_*`,
which is exactly what an Xbox pad reports through SDL) or by its
well-established behavior (Snes9x and mGBA both auto-configure a standard
XInput pad) — and found to already do the right thing without help. The
alternative was guessing at each project's internal numeric button/key
encoding, which for at least one of them (Gopher64's tagged-enum JSON) risks
silently discarding the *entire* config file on a parse failure, which is a
worse outcome than shipping nothing. Where a mapping format was independently
verified against source instead — DuckStation, PCSX2, Dolphin, melonDS,
Azahar, MAME's option file conventions — a real, explicit binding is shipped.

**MAME ships no controller mapping file, for a different reason.** Arcade
control layouts genuinely differ per game (a fighter wants 6 buttons, a
shooter wants 1-2, a driving cabinet wants a wheel) in a way none of the
other, single-controller-shape systems in this shelf do. `mame.ini`'s paths
and video settings are still real, verified factory config.

**PPSSPP and Gopher64's saves live inside their own app folder, not
`E:\Saves\<system>`.** Every entry here uses its emulator's own
"portable" convention (almost always an empty `portable.txt`/`portable.ini`
marker; PPSSPP uses an empty `memstick` folder instead), which keeps saves
on the console's external drive either way, since the whole app folder is
installed there. Most entries additionally redirect saves/states to the
shared `E:\Saves\<system>` / `E:\States\<system>` folders once a confirmed
config key for that existed; PPSSPP and Gopher64 do not expose one that was
verified with the same confidence, so their saves stay in their own portable
folder — still on `E:\`, just not pooled with the rest.

## Systems not covered here

`catalog.json` already ships **native, UWP-packaged** builds for several of
these systems — RetroArch (PS1/N64/SNES/NES/Genesis and ~200 more cores),
XBSX2 (PS2), Dolphin (GameCube/Wii), PPSSPP (PSP), and Flycast (Dreamcast) —
built by third parties (XboxEmulationHub and others) specifically as signed
Xbox packages. This shelf is deliberately a second, independent path for the
same systems: the plain portable Windows build anyone would run on a PC,
mapped through the console's own Win32 loader instead. The two are not meant
to replace each other yet — this shelf is the newer mechanism.
