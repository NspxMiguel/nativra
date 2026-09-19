# XboxDev

Turns an Xbox Series X|S into a native game machine through the console's own
Developer Mode — no PC, no streaming, no jailbreak. `xbdev` drives the console's
Device Portal from a Mac: it finds the console, installs the whole catalogue and
flips every app into game mode with one command.

```bash
bun src/xbdev.ts find          # locate the console on the network
bun src/xbdev.ts connect <ip>  # store address + Device Portal credentials
bun src/xbdev.ts kit           # download and install everything, then enable game mode
```

## Your Steam library, partly

The console will never run the Steam client — but a good part of what people
*buy* on Steam is an engine that has been reimplemented in the open, and those
run natively here. You supply the game data you already own; the engine on the
console loads it.

Measured inside the RetroArch package shipped by this catalogue — **218 distinct
cores**, among them:

| Core | The PC game it runs |
| --- | --- |
| `boom3` | **Doom 3** |
| `vitaquake2`, `vitaquake3` | **Quake II**, **Quake III Arena** |
| `tyrquake`, `prboom` | **Quake**, **Doom / Doom II** |
| `openlara` | **Tomb Raider** |
| `ecwolf` | **Wolfenstein 3D**, Spear of Destiny |
| `dosbox_pure`, `dosbox_core`, `dosbox_svn` | the **DOS** catalogue |
| `scummvm` | LucasArts and Sierra adventures |
| `nxengine` | Cave Story |
| `reminiscence` | Flashback |
| `easyrpg` | RPG Maker 2000/2003 games |
| `fbneo`, `cannonball`, `mrboom` | arcade, OutRun, Bomberman |

So: buy Doom on Steam, copy the `.wad` to the drive, and it runs on the console
natively — no PC in the loop and nothing streamed. That is as close to "Steam on
Xbox" as the hardware allows, and it is honest about what it is.

## What runs natively

Everything below executes on the console itself, off an external NTFS drive.

**PC games, natively**

| Package | Runs |
| --- | --- |
| GZDoom | Doom, Doom II, Heretic, Hexen, Strife, and every mod or total conversion |
| Raze | Duke Nukem 3D, Blood, Shadow Warrior, Redneck Rampage, Powerslave |
| DOSBox Pure | The DOS catalogue — Warcraft, Command & Conquer, X-COM, Dune II, Tyrian |
| ScummVM | Monkey Island, Day of the Tentacle, Grim Fandango, Broken Sword, Sam & Max |
| OpenBOR / Ikemen GO | Beats of Rage and M.U.G.E.N engines |
| Ruffle | Flash games |

**Emulation**

| Package | System |
| --- | --- |
| Xenia Canary | Xbox 360 |
| XBSX2 | PlayStation 2 |
| Dolphin | GameCube, Wii |
| Flycast | Dreamcast, Naomi, Atomiswave |
| PPSSPP | PSP |
| Supermodel | Sega Model 3 arcade |
| RetroArch | ~200 systems through libretro cores |

## What this cannot do

Steam, Epic, GOG and commercial PC games **do not run on an Xbox**, and no amount
of work changes that. Three hard limits in the console itself:

1. **The console only executes signed packages** (MSIX/UWP). There is no path to
   running a loose `.exe`.
2. **A UWP app can only load DLLs bundled inside its own package**
   (`LoadPackagedLibrary`). Every Steam game loads DLLs off disk at runtime.
3. **Steam and Epic are closed-source Win32 programs.** You cannot recompile what
   you do not have.

A "Proton for Xbox" would mean reimplementing Wine inside those constraints, and
the result would still be blocked by DRM and anti-cheat. This project does not
pretend otherwise.

## Dual boot

Developer Mode lives on its own partition. The retail system, its games and its
saves are untouched, and switching between the two is a menu item — `Leave
Developer Mode` in Dev Home reboots into retail, the activation app brings it
back. Nothing here is destructive or irreversible.

## Resources in Developer Mode

An app gets 1GB of RAM, 2–4 shared CPU cores and 45% of the GPU. A **game** gets
5GB, four exclusive cores plus two shared, and the whole GPU — and only a game can
see an external drive. `xbdev gamemode` makes that switch; without it the
emulators are both slow and blind to the hard drive.

## External drive

Format it **NTFS** — exFAT is not read in this path. The drive shows up as `E:`
inside RetroArch. Suggested layout:

```
E:\Games\<system>\     ROMs and game data
E:\BIOS\               BIOS files
E:\Saves\  E:\States\  saves and save states
```

## Requirements

- An Xbox Series X|S with Developer Mode activated (a one-time $19 Microsoft
  developer account, already held here since 2021).
- Device Portal enabled on the console, with a username and password.
- Bun on the Mac side.
