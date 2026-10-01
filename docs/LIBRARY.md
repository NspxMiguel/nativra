# Library compatibility catalog

This catalog sorts the owner's whole Steam library (114 entries: owned games plus the ones
shared through Steam Families) by how realistic each entry is for Nativra **today**, and
names the missing piece behind every "not yet". It exists to pick the realistic half of the
library and to see which shared blocker unlocks the most games. Data date: **2026-10-01**.

It is a planning tool, not a compatibility claim. The only measured results are in
[COMPATIBILITY.md](COMPATIBILITY.md) and the per-game notes under `docs/progress/`. Every
other row is a prediction from public data: each fact carries a source tag, and `?` means
the data does not say. It follows [COMPATIBILITY-STRATEGY.md](COMPATIBILITY-STRATEGY.md):
nothing here is a promise, unknowns stay visible, and kernel-level anti-cheat is out of
scope and never bypassed.

Sources: Steam PICS (public app info), Steam store pages, Wikipedia and Wikidata,
PCGamingWiki, the repo's test notes, and public knowledge; see
[Method and caveats](#method-and-caveats).

Sections: [Summary](#summary), [How to read the tables](#how-to-read-the-tables),
[Tier A](#tier-a-plausible-with-the-current-layer),
[Tier B](#tier-b-needs-a-specific-missing-piece-first), [Tier C](#tier-c-out-of-reach),
[Method and caveats](#method-and-caveats), [Resumo (pt-BR)](#resumo-pt-br).

## Summary

| Tier | Meaning | Games |
| --- | --- | --- |
| A | Plausible with the current layer, inputs documented | 21 |
| A? | Same, but at least one input (arch, engine or API) is inferred | 31 |
| B | Needs a named missing piece first | 38 |
| B? | Same, but the piece is inferred or the entry cannot be classified yet | 10 |
| C | Blocked by something we cannot or must not do | 14 |
| | **Total** | **114** |

A and A? together are **52 of 114 (46%)**; B and B? are 48; C is 14.

Tier A is not "working": it only means no missing platform piece is known. Seven library
games have been run on the console. Five are tier A: one is verified (Seraph's Last Stand),
one was playable and now stalls (Hades), and three stop before a first frame for three
different reasons (Cuphead, Little Nightmares, LEGO Jurassic World). The other two,
Brawlhalla and WAVESHAPER, are tier B and also stop before a frame. **The first shared
blocker is therefore the 64-bit D3D11 path itself** (first frame, start-up, storage waits):
every tier A game sits behind it. It is left out of the ranking below because it is a
stability goal, not a missing piece.

### Tier A games: the first test targets

- **Already run on the console (5):** Seraph's Last Stand, Hades, Cuphead, Little
  Nightmares, LEGO® Jurassic World. See the status column.
- **First wave, untested (10):** House Flipper, PowerWash Simulator, Car Mechanic Simulator
  2018, Sprocket, Bloons TD 6, Vampire Survivors, 9 Kings, Seen, Poly Bridge 2, Graveyard
  Keeper. Evidence-backed, with a store minimum of 4 GB RAM or less and a download of 20 GB
  or less.
- **Other evidence-backed, untested (6):** Euro Truck Simulator 2, ACE COMBAT™7: SKIES
  UNKNOWN, My Summer Car, Trailmakers, Raft, Little Nightmares Enhanced Edition. Each
  carries a memory watch (6 to 8 GB minimum RAM) or a 50 GB download; see its row.
- **Inferred, untested (31):** Dota 2, Counter-Strike 2, Just Cause 3, Company of Heroes 2,
  The Forest, Cities: Skylines, 100% Orange Juice, Poly Bridge, Hearts of Iron IV,
  Satisfactory, Stick Fight: The Game, ANOIX, Pixel Strike 3D, Cities: Skylines II, Internet
  Cafe Simulator, BROTHER!!! - Hardcore Platformer, Virtual Cottage, Kandidatos, Kandidatos
  Kart, Amazing Frog ? 2, Depths Of Horror: Mushroom Day, Movie Night, Banana Hell: Mountain
  of Madness, Super-Patriota Simulator, My dream setup, Undercroft Warriors, Rat Quest,
  Paragnosia, LivingForest, Lost in Anomaly, 3D PUZZLE - Leafless. Settle arch, engine and
  API with a depot probe before spending a download (see Method and caveats).

Downloads of 50 GB or more in these lists (store figures): ACE COMBAT™7: SKIES UNKNOWN, Dota
2, Counter-Strike 2, Just Cause 3, Cities: Skylines II. The test drive used so far holds
about 62 GB.

### Shared missing pieces, ranked by games unlocked

"Need it" counts the tier B rows that list the piece; "Only piece" counts rows where it is
the single thing missing. A game that lists several pieces is unlocked only when all of them
land. A dagger (†) marks a tier B? row, whose piece is inferred.

| # | Missing piece | Need it | Only piece | Games |
| --- | --- | --- | --- | --- |
| 1 | `X86` 32-bit translator maturity | 23 | 0 | Among Us, Assetto Corsa, Automobilista, Black Mesa, Bully: Scholarship Edition, Castle Crashers, Grand Theft Auto III, Grand Theft Auto: San Andreas, Grand Theft Auto: Vice City, Half-Life 2, Left 4 Dead, Left 4 Dead 2, Max Payne 3, POSTAL 2, Portal, Portal 2, Resident Evil 4 (2005), Spore, Super Meat Boy, The LEGO® Movie - Videogame, Two Worlds II HD, WAVESHAPER, World of Tanks Blitz |
| 2 | `D3D9` Direct3D 9 path | 17 | 1 | Automobilista, Black Mesa, Bully: Scholarship Edition, Castle Crashers, Grand Theft Auto: San Andreas, Half-Life 2, Left 4 Dead, Left 4 Dead 2, Portal, Portal 2, Resident Evil 4 (2005), Spore, Super Meat Boy, Team Fortress 2, The LEGO® Movie - Videogame, Two Worlds II HD, WAVESHAPER |
| 3 | `D3D12` Direct3D 12 | 6 | 4 | Cyberpunk 2077, Dead as Disco†, HITMAN World of Assassination, Resident Evil 4, Resident Evil Requiem, Stick It to the Stickman† |
| 4 | `ONLINE` Online services | 6 | 3 | Bro Falls: Ultimate Showdown†, Content Warning, Stumble Guys, Trove†, Warframe, World of Tanks Blitz |
| 5 | `LAUNCH` Launcher or child process | 5 | 1 | Assetto Corsa, Max Payne 3, Poppy Playtime, Trove†, Warframe |

Smaller pieces, for completeness:

| Missing piece | Need it | Only piece | Games |
| --- | --- | --- | --- |
| `MEM` Memory beyond the app budget | 4 | 2 | Amazing Frog?†, BeamNG.drive, Cyberpunk 2077, Resident Evil Requiem |
| `PROBE` Classification data | 4 | 4 | Block Warriors†, Just Ignore Them†, Masterchef Cakes Edition†, The Deed: Dynasty† |
| `D3D8` Direct3D 8 | 3 | 0 | Grand Theft Auto III, Grand Theft Auto: Vice City, POSTAL 2 |
| `D3D11-32` Direct3D 10/11 for 32-bit guests | 3 | 0 | Among Us, Max Payne 3, World of Tanks Blitz |
| `WEB` Browser engine | 2 | 2 | Cookie Clicker†, Sentience: The Android's Tale |
| `DENUVO` Denuvo anti-tamper | 2 | 1 | Mad Max, Resident Evil Requiem |
| `NET` Managed desktop runtime | 1 | 1 | Terraria |
| `AIR` Adobe AIR runtime | 1 | 1 | Brawlhalla |
| `GL` OpenGL | 1 | 1 | Stormworks: Build and Rescue |

How to read it:

1. **The 32-bit path is one investment, not two.** `X86` is listed by 23 games and `D3D9` by
   17, mostly the same ones. Delivering them together unlocks **17 games** (20 with a
   Direct3D 8 layer); one of the 17, Team Fortress 2, already has a 64-bit build and needs
   only `D3D9`. `X86` alone unlocks nothing, because every 32-bit game also needs a graphics
   path. WAVESHAPER is the first real test of this bundle.
2. **Direct3D 12** is the next biggest single piece: 6 games list it and 4 need nothing else
   (HITMAN World of Assassination, Resident Evil 4, Stick It to the Stickman†, Dead as
   Disco†). Cyberpunk 2077 and Resident Evil Requiem also need more memory than the app has,
   and Requiem adds Denuvo, so it is the farthest entry in tier B.
3. **Online services** (6 games) are the only piece for 3 multiplayer-only titles, and the
   last piece for Warframe, Trove and World of Tanks Blitz once launchers and 32-bit land.
4. **Child-process launchers** (5 games) are blocked by design today, because the app
   refuses `CreateProcess`. Games that also list a direct game-exe launch entry (Cities:
   Skylines, Hearts of Iron IV, Cyberpunk 2077) can skip it; Poppy Playtime, Warframe,
   Trove, Assetto Corsa and Max Payne 3 cannot.
5. **Memory** (4 games) is a platform cap, so only waste can be cut; 4 entries cannot be
   classified until a depot probe says what they are.

By count alone the order is: make the 64-bit D3D11 path reliable (52 tier A games), then the
32-bit bundle (17 to 20 games), then D3D12 (4 to 6), then online validation (3 to 6). Cost
is not estimated here.

### Library profile

| Engine family | Games |
| --- | --- |
| Unity | 31 (9 of them inferred) |
| Unreal Engine | 9 |
| Source / Source 2 | 12 |
| Runtime-hosted (Adobe AIR, XNA, NW.js, HTML5, GameMaker) | 5 |
| Other named engines | 32 |
| Unknown | 25 |

| Architecture evidence | Games |
| --- | --- |
| x64 build present (PICS or repo) | 63 |
| x86 only, known | 2 |
| PICS silent, other sources say x86 | 20 |
| PICS silent, other sources say x64 | 2 |
| PICS silent, unknown | 24 |
| macOS-only entries | 3 |

## How to read the tables

**What "today" means.** The yardstick for every tier, from the repo's own notes:

- 64-bit games run natively on the console's x64 CPU. The D3D11 bridge is the one proven
  graphics path (Seraph's Last Stand verified, Hades playable earlier).
- 32-bit games go through the x86 layer (a translator plus its own guest kernel). It runs a
  real MSVC test program, but no 32-bit game has reached a frame. It has a D3D9 bridge and
  no Direct3D 10/11.
- Direct3D 12, Vulkan and OpenGL are not built; a D3D9-on-D3D11 shim exists and is
  unexercised.
- The app gets about 5 GB (commits beyond ~4992 MiB are refused); Hades already uses 4.4 GB.
- The app cannot start child processes, so a launcher cannot start the game behind it.
- Kernel-level anti-cheat (EAC, BattlEye, EA AntiCheat, Vanguard) cannot work and is not
  bypassed. Chromium, Electron and NW.js games need a whole browser engine.

**Tiers.**

- **A**: plausible with the current layer. A 64-bit build exists, the renderer is D3D11 (the
  proven path) or the engine's default (Unity, Unreal 4), there is an offline mode, no
  launcher, kernel anti-cheat, browser runtime or managed desktop runtime is in the way, and
  the store's minimum RAM is under 12 GB. **A?** is the same with one or more inputs
  inferred rather than documented.
- **B**: needs a specific missing piece first; the `Missing pieces` column names it. **B?**:
  the piece is inferred, or the entry has no usable data.
- **C**: blocked by something we cannot or must not do: kernel-level anti-cheat, online-only
  service with anti-cheat, RTX-only rendering, or an entry that is not a Windows game. VAC
  is Valve's user-mode system, not a kernel driver; it matters here only for online play.

**Source tags** inside cells: no tag means Steam PICS; `(store)` is the Steam store page;
`(wiki)` is Wikipedia or Wikidata; `(pcgw)` is PCGamingWiki, seen through search snippets;
`(repo)` is this repository's test notes; `(web)` is another public page (weaker); `(pk)` is
public knowledge or a reasoned guess, unverified.

**Arch** lists `x64`, `x86` or both when PICS does; `?` means PICS says nothing, optionally
followed by a labelled guess. **Deck** is Valve's Steam Deck rating from PICS (Unrated means
none); it says nothing about Nativra beyond naming reasons such as anti-cheat or launcher
issues in the notes. **Nativra status** comes only from the repo's test notes; everything
else is `Untested`.

**Missing-piece codes** (tier B):

| Code | Piece | State today |
| --- | --- | --- |
| `X86` | 32-bit translator maturity | The x86 layer runs a real MSVC test program end to end (C runtime, threads, SEH, C++ exceptions); no 32-bit game has reached a frame yet. |
| `D3D9` | Direct3D 9 path | A D3D9-on-D3D11 shim (`native/directx-redist/d3d9`) plus `D3D9Bridge` and `X86Direct3D9` exist; no game has exercised them on the console. |
| `D3D8` | Direct3D 8 | Nothing exists; it would need a D3D8-to-D3D9 layer on top of the D3D9 shim. |
| `D3D11-32` | Direct3D 10/11 for 32-bit guests | Not in the x86 layer yet (repo notes list "Direct3D 11 for 32-bit games" as not done). |
| `D3D12` | Direct3D 12 | Not built. |
| `GL` | OpenGL | Not built. |
| `LAUNCH` | Launcher or child process | `CreateProcess` and `ShellExecute` are refused inside the app, so a launcher that starts the game as a child needs launcher support, or a direct game-exe entry. |
| `ONLINE` | Online services | Multiplayer, lobbies and friends are not validated yet; online-only games also depend on their own backends. |
| `WEB` | Browser engine | NW.js, Electron and Chromium runtimes are not available. |
| `NET` | Managed desktop runtime | No .NET Framework, XNA or FNA runtime for desktop assemblies. |
| `AIR` | Adobe AIR runtime | The captive AIR runtime starts but cannot read its application descriptor; the repo notes suspect unhooked ntdll section calls. |
| `MEM` | Memory beyond the app budget | The app gets about 5 GB (commits beyond ~4992 MiB are refused). Rule used here: Steam minimum RAM of 12 GB or more, or minimum VRAM of 6 GB or more. |
| `DENUVO` | Denuvo anti-tamper | Behaviour under the PE loader is unknown; the first run activates online. |
| `PROBE` | Classification data | No usable public data; a depot file listing is needed (see Method and caveats). |

## Tier A: plausible with the current layer

52 games. The 21 evidence-backed (A) come first, with the five already run on the console at
the top, then the 31 inferred (A?). Untested rows are in AppID order.

| AppID | Game | Tier | Arch | Engine | Graphics | Blockers and notes | Deck | Nativra status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1919460 | Seraph's Last Stand | A | x64 (osarch) | Unity 2021.3, IL2CPP (repo) | D3D11 (repo) | None. | Playable | Verified (build 244, 2026-09-25): fully playable with the controller; Steam shows In-Game. |
| 1145360 | Hades | A | x64 + x86 | Supergiant in-house C++ (repo, wiki) | D3D11 (repo) | Memory near the 5 GB cap (4.4 GB measured, repo). Regression on builds 391-395 (see status). | Verified | Playable (build 319-359, 2026-09-26): 60 fps menu, controller, sound. Builds 391-395 (progress note, 2026-09-30): deadlock or drop to Home before a frame; under investigation. |
| 268910 | Cuphead | A | x64 | Unity, Mono backend (wiki, repo, web) | D3D11 (repo) | Unity Mono (managed JIT) is unproven in Nativra, unlike IL2CPP. No first frame yet (see status). | Verified | Not working (build 391, progress note): window and message loop run; no swap chain or first frame. |
| 424840 | Little Nightmares | A | x64 | Unreal Engine 4 (wiki, store) | D3D11 (store, repo) | No frame yet (see status): UE4 start-up crawl, and XAudio 2.7 needs the packaged shim. | Verified | Not working (build 310 in COMPATIBILITY.md): D3D11 device and swap chain created, no frame; XAudio 2.7 missing (shim in progress). Build 394 note: alive for 5+ minutes in UE4 start-up, never pumps a frame. |
| 352400 | LEGO® Jurassic World | A | x86 (PICS launch) + x64 DX11 exe (repo) | TT Games in-house (pk) | D3D11 (repo) | Stops before the first frame (see status). Borrowed through Steam Families; 15.3 GB download. | Verified | Not working (build 305 in COMPATIBILITY.md; notes to build 385): window, D3D11 device and swap chain work with the Steam bridge; the process then freezes (critical-section spin) before a frame. |
| 227300 | Euro Truck Simulator 2 | A | x64 + x86 | Prism3D (wiki) | D3D11 (pcgw; OpenGL 3.3 alternative) | RAM 8 GB min (store): memory watch. | Verified | Untested |
| 502500 | ACE COMBAT™7: SKIES UNKNOWN | A | x64 | Unreal Engine 4 (wiki) | D3D11 (store: DX 11) | Disk 50 GB (store): big download. Offline campaign. | Verified | Untested |
| 516750 | My Summer Car | A | x64 (osarch) | Unity 5 (wiki) | D3D11 (Unity default; store: DX 9.0) | Mono (JIT) backend unproven here. RAM 6 GB min (store): memory watch. | Playable | Untested |
| 585420 | Trailmakers | A | x64 (osarch) | Unity (wiki) | D3D11 (store: DX 11) | RAM 8 GB min (store): memory watch. Online co-op optional. | Verified | Untested |
| 599140 | Graveyard Keeper | A | x64 + x86 (pcgw) | Unity 2018 (pcgw) | D3D11 (Unity default; store: DX 10) | Mono (JIT) likely, unproven here. Nothing else known. | Playable | Untested |
| 613100 | House Flipper | A | x64 | Unity (wiki) | D3D11 (store: DX 11) | None known. | Verified | Untested |
| 645630 | Car Mechanic Simulator 2018 | A | x64 | Unity (wiki) | D3D11 (store: DX 11) | None known. | Playable | Untested |
| 648800 | Raft | A | x64 | Unity (wiki) | D3D11 (store: DX 11) | RAM 6 GB min (store): memory watch. Online co-op optional. | Playable | Untested |
| 960090 | Bloons TD 6 | A | x64 | Unity (wiki) | D3D11 (Unity default; store lists a generic OpenGL 2.0) | None known. Ninja Kiwi account matters for online features only (pk). | Playable | Untested |
| 1062160 | Poly Bridge 2 | A | x64 + x86 | Unity (wiki) | D3D11 (Unity default; store: DX 10) | None known. | Playable | Untested |
| 1069740 | Seen | A | x64 | Unreal Engine (store legal text) | D3D11 (store: DX 11) | None known. | Playable | Untested |
| 1290000 | PowerWash Simulator | A | x64 | Unity (wiki) | D3D11 (store: DX 11) | None known. | Verified | Untested |
| 1674170 | Sprocket | A | x64 | Unity (wiki) | D3D11 (store: DX 11, SM 5.0) | None known. | Playable | Untested |
| 1794680 | Vampire Survivors | A | x64 (osarch) | Unity since v1.6 (wiki; Phaser before) | D3D11 (store: DX 11/12) | None known. | Verified | Untested |
| 2149010 | Little Nightmares Enhanced Edition | A | x64 | Unreal Engine 4 (pcgw) | D3D11 (store, pcgw) | RAM 6 GB min (store). Same engine family as Little Nightmares, the repo's x64 UE4 test. | Verified | Untested |
| 2784470 | 9 Kings | A | x64 (osarch) | Unity (wiki) | D3D11 (store: min DX 11, rec DX 12) | None known. Same developer as Seraph's Last Stand, the verified game. | Verified | Untested |
| 570 | Dota 2 | A? | x64 | Source 2 (wiki) | D3D11 (store: DX 11); Vulkan optional (wiki) | Offline only against bots in Steam offline mode (web); VAC on online play. RAM 4 GB min, disk 60 GB (store). Heavy download for a first test. | Playable | Untested |
| 730 | Counter-Strike 2 | A? | x64 (+ legacy x86 CS:GO entry) | Source 2 (wiki) | D3D11 (store: DX 11); Vulkan optional (pk) | Offline bot matches work (web; -insecure disables VAC); VAC on online play. RAM 8 GB min, disk 85 GB (store): memory watch, and too big for a small drive. | Playable | Untested |
| 225540 | Just Cause 3 | A? | x64 (depot) | APEX (wiki) | D3D11 (pk) | RAM 8 GB min, disk 54 GB (store): memory watch, big download. | Playable | Untested |
| 231430 | Company of Heroes 2 | A? | ? (x64 default since Jan 2021, web; PICS silent) | Essence 3.0 (wiki) | D3D11; D3D10 fallback (wiki) | RTS, CPU-heavy. Arch comes from news, not PICS. Disk 30 GB (store). | Playable | Untested |
| 242760 | The Forest | A? | ? (PICS lists TheForest32.exe [32]; pcgw: TheForest.exe is the x64 launch) | Unity (wiki) | D3D11; DX9 legacy option (pcgw) | Mono (JIT) backend likely and unproven here. Deck: LauncherInteractionIssues (start-up dialog). RAM 4 GB min. | Playable | Untested |
| 255710 | Cities: Skylines | A? | x64 (osarch) | Unity (wiki) | D3D11 (Unity default; store: DX 9.0c min, 11 rec) | PICS lists the Paradox launcher (dowser.exe) and a direct Cities.exe entry. Mono (JIT, pk). RAM 8 GB min, 16 rec (store). Deck: LauncherInteractionIssues. | Playable | Untested |
| 282800 | 100% Orange Juice | A? | x64 + x86 | ? (Orange_Juice in-house, pk) | D3D11 (pcgw: Steam release; store lists DX 9.0c) | VAC on online play only. If the renderer is really D3D9 it becomes tier B. | Verified | Untested |
| 367450 | Poly Bridge | A? | ? (pcgw: 64-bit OS required) | Unity (wiki) | D3D11 (Unity default, pk; store: DX 9.0) | Arch unconfirmed (2016 Unity 5 title). Deck: Unsupported (SteamOSDoesNotSupport). | Unsupported | Untested |
| 394360 | Hearts of Iron IV | A? | x64 (launcher entry only) | Clausewitz (wiki) | D3D11 default since 1.12 (web); -dx9 and -opengl exist | PICS lists the Paradox launcher (dowser.exe) as default and a direct hoi4.exe entry. CPU-heavy simulation. | Playable | Untested |
| 526870 | Satisfactory | A? | x64 | Unreal Engine 5 (wiki; UE4 before Update 8) | D3D12 default; D3D11 via -dx11, deprecated since Update 8 (web) | Memory-heavy UE5 (RAM 8 GB min, 16 rec; store). If the D3D11 mode is unusable it becomes D3D12-only (tier B). | Verified | Untested |
| 674940 | Stick Fight: The Game | A? | x64 (depot) | Unity 5 (wiki, pcgw) | D3D11 (Unity default; store: DX 9.0) | No single-player category: local play needs two or more controllers; online optional. | Verified | Untested |
| 840590 | ANOIX | A? | ? | ? | ? (store: DX 9.0c, SM 3.0) | Arch and engine unknown (2018 release). | Playable | Untested |
| 915320 | Pixel Strike 3D | A? | x64 + x86 (depots) | ? (Unity likely, pk) | D3D11 (store: DX 11) | F2P online FPS that also lists a single-player category (store). Anti-cheat unknown. | Unrated | Untested |
| 949230 | Cities: Skylines II | A? | x64 | Unity, HDRP (wiki; pk) | D3D11 (pk) | RAM 8 GB min, 16 rec; disk 60 GB (store); very heavy simulation. PICS lists the Paradox launcher as default and direct Cities2.exe entries. Deck: Unsupported. | Unsupported | Untested |
| 1136160 | Internet Cafe Simulator | A? | ? (pcgw: 64-bit OS required) | Unity 2019 (pcgw) | D3D11 (Unity default, pk) | Arch unconfirmed. | Playable | Untested |
| 1286560 | BROTHER!!! - Hardcore Platformer | A? | x64 (osarch) | ? | ? | Engine and API unknown. | Playable | Untested |
| 1369320 | Virtual Cottage | A? | x64 | ? | ? | Engine and API unknown; mouse/touch interface. | Playable | Untested |
| 1395560 | Kandidatos | A? | ? | Unity (web) | D3D11 (Unity default; store: DX 10 min, 11 rec) | Arch unconfirmed (2020 release). | Playable | Untested |
| 1415140 | Kandidatos Kart | A? | ? | ? (same studio as Kandidatos; Unity likely, pk) | ? (store: DX 10 min, 11 rec) | Arch and engine unconfirmed (2020 release). | Verified | Untested |
| 1559680 | Amazing Frog ? 2 | A? | ? (PICS lists a 32-bit depot, no 64-bit tag) | Unity (pk) | D3D11 (store: DX 11) | RAM 8 GB min, 16 rec, 4 GB VRAM; disk 21 GB (store). Deck: UnsupportedGraphicsPerformance. | Unsupported | Untested |
| 1590840 | Depths Of Horror: Mushroom Day | A? | x64 (osarch) | ? | D3D11 (store: DX 11) | Engine unknown. RAM 6 GB min (store): memory watch. | Playable | Untested |
| 1868410 | Movie Night | A? | x64 (osarch) | ? | D3D10/11 (store: DX 10 min, 11 rec) | Early Access; engine unknown. Deck: Unsupported. | Unsupported | Untested |
| 2068520 | Banana Hell: Mountain of Madness | A? | x64 | ? | ? (store: DX 9.0c) | Engine and API unknown. | Playable | Untested |
| 2089140 | Super-Patriota Simulator | A? | ? | ? | ? | No public engine or arch data (2023-24 release; store: GTX 960). | Unrated | Untested |
| 2200780 | My dream setup | A? | ? | ? | ? (store: DX 10 min, DX 12 rec) | Engine and arch unknown. | Playable | Untested |
| 2547140 | Undercroft Warriors | A? | x64 (osarch, depot) | ? | ? | Engine and API unknown. | Playable | Untested |
| 2952470 | Rat Quest | A? | ? | ? | ? | Engine, API and arch unknown. | Playable | Untested |
| 3017580 | Paragnosia | A? | ? | ? | D3D11 (store: DX 10-12 capable) | Engine and arch unknown. | Playable | Untested |
| 3027490 | LivingForest | A? | x64 | ? | ? | Engine and API unknown. | Unrated | Untested |
| 3312020 | Lost in Anomaly | A? | x64 | Unity (web) | D3D11 (Unity default, pk) | RAM 8 GB min (store; probably inflated). | Playable | Untested |
| 3651720 | 3D PUZZLE - Leafless | A? | ? | ? | ? | Engine, API and arch unknown. | Unrated | Untested |

## Tier B: needs a specific missing piece first

48 games, by AppID. B? marks an inferred piece or an entry that cannot be classified yet.

| AppID | Game | Tier | Arch | Engine | Graphics | Missing pieces | Blockers and notes | Deck | Nativra status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 220 | Half-Life 2 | B | ? (x86, pcgw) | Source (wiki) | D3D9 (pk) | `X86`, `D3D9` | Single-player, offline. | Verified | Untested |
| 400 | Portal | B | ? (x86, pk) | Source (wiki) | D3D9 (pk) | `X86`, `D3D9` | Single-player, offline. | Verified | Untested |
| 440 | Team Fortress 2 | B | x64 + x86 | Source (wiki) | D3D9 (pk) | `D3D9` | x64 build exists, so only D3D9 is missing. F2P; VAC on online servers; offline practice with bots exists (pk). | Playable | Untested |
| 500 | Left 4 Dead | B | ? (x86, pk) | Source (wiki) | D3D9 (pk) | `X86`, `D3D9` | Campaign with bots works offline (pk); VAC applies to online play only. | Playable | Untested |
| 550 | Left 4 Dead 2 | B | ? (x86, web) | Source (wiki) | D3D9 (store: DX 9.0c) | `X86`, `D3D9` | Offline campaign with bots; VAC applies to online play only. The Windows build stays 32-bit (web). | Verified | Untested |
| 620 | Portal 2 | B | ? (x86, web) | Source (wiki) | D3D9 (store: DX 9.0c) | `X86`, `D3D9` | Single-player and co-op. | Verified | Untested |
| 7520 | Two Worlds II HD | B | ? (x86, pcgw) | GRACE (wiki, pcgw) | D3D9 and D3D10 exes (pcgw: 9.0c, 10) | `X86`, `D3D9` | The D3D9 exe is TwoWorlds2.exe. DRM: Reality Pump DLM (store); an online account only for multiplayer. | Unsupported | Untested |
| 12100 | Grand Theft Auto III | B | ? (x86, pk) | RenderWare (wiki) | D3D8 (store: DX 8.1; web) | `X86`, `D3D8` | Direct3D 8 has no layer at all today. This is the Windows build; 12230 is a separate macOS entry. | Playable | Untested |
| 12110 | Grand Theft Auto: Vice City | B | ? (x86, pk) | RenderWare (wiki) | D3D8 (pk) | `X86`, `D3D8` | Direct3D 8 has no layer at all today. | Unsupported | Untested |
| 12120 | Grand Theft Auto: San Andreas | B | ? (x86, pcgw) | RenderWare (wiki) | D3D9 (pcgw: 9.0c) | `X86`, `D3D9` | Named as an intended 32-bit target in the repo's x86 notes. SecuROM listed in the store text. | Playable | Untested |
| 12200 | Bully: Scholarship Edition | B | ? (x86, pcgw) | Gamebryo (pcgw); wiki also lists RenderWare | D3D9 (pcgw: 9.0c) | `X86`, `D3D9` | Rockstar account linking is optional (store). | Playable | Untested |
| 17390 | Spore | B | ? (x86, pcgw) | ? (Maxis in-house, pk) | D3D9 (pcgw: 9.0c) | `X86`, `D3D9` | The Steam copy is activated through EA support (pcgw); EA online-authentication wording in the store text. | Unsupported | Untested |
| 40800 | Super Meat Boy | B | ? (x86, pcgw) | Tommunism Engine (wiki) | D3D9 (pcgw: 9.0c) | `X86`, `D3D9` | None known. | Verified | Untested |
| 105600 | Terraria | B | ? | XNA / .NET (wiki: Microsoft XNA) | D3D9 through XNA (store: DX 9.0c) | `NET` | Managed desktop game: needs a .NET with XNA or FNA runtime, which the app does not have. | Verified | Untested |
| 204100 | Max Payne 3 | B | ? (x86, web) | RAGE (wiki) | D3D11 (wiki) | `X86`, `D3D11-32`, `LAUNCH` | 32-bit main exe even today (web). Rockstar Social Club sign-in via a launcher entry (PlayMaxPayne3.exe; store text). Deck: LauncherInteractionIssues. | Playable | Untested |
| 204360 | Castle Crashers | B | ? (x86, pcgw; PICS has no launch or depot data) | ? (The Behemoth in-house, pk) | D3D9 (pcgw: 9.0c) | `X86`, `D3D9` | None known. | Verified | Untested |
| 223470 | POSTAL 2 | B | ? (x86, pcgw) | Unreal Engine 2 (wiki) | D3D8 or OpenGL (pcgw) | `X86`, `D3D8` | Direct3D 8 or OpenGL: neither exists today. | Verified | Untested |
| 230410 | Warframe | B | x64 (osarch); launch entry is Tools/Launcher.exe | Evolution (wiki) | D3D11 (pk) | `LAUNCH`, `ONLINE` | Own launcher/CDN downloads the content and starts the game (web): a child-process launcher. Always-online F2P account. Disk 75 GB (store). | Verified | Untested |
| 234140 | Mad Max | B | x64 | Apex (wiki) | D3D11 (store: DX 11) | `DENUVO` | Denuvo anti-tamper (store DRM notice): behaviour under a custom loader is unknown. First-time setup needs internet (Deck token). RAM 6 GB min. | Verified | Untested |
| 244210 | Assetto Corsa | B | ? (launcher x86, acs.exe x64; web) | In-house (Kunos, pk) | D3D11 (wiki) | `LAUNCH`, `X86` | AssettoCorsa.exe is a 32-bit C#/HTML launcher that starts acs.exe (web, wiki): launcher chaining, plus the 32-bit layer for the launcher itself. | Unsupported | Untested |
| 254700 | Resident Evil 4 (2005) | B | x86 (launch path Bin32/) | ? (Capcom in-house, pk) | D3D9 (pcgw: 9.0c) | `X86`, `D3D9` | None known. | Playable | Untested |
| 267530 | The LEGO® Movie - Videogame | B | ? (x86 likely, pcgw) | Nu2 (pcgw) | D3D9 (pcgw: 9.0c) | `X86`, `D3D9` | Older sibling of LEGO Jurassic World (the repo's x64 D3D11 test), but this build looks 32-bit. | Verified | Untested |
| 284160 | BeamNG.drive | B | x64 (osarch) | Torque 3D lineage (wiki) | D3D11 (store: DX 11); Vulkan experimental (wiki) | `MEM` | RAM 16 GB min and 6 GB VRAM min (store); disk 60 GB. | Playable | Untested |
| 291550 | Brawlhalla | B | x64 (repo; PICS silent) | Adobe AIR (wiki, repo) | Stage3D over D3D9/10/11 (repo) | `AIR` | AIR runtime stops at 'Application descriptor could not be found'; the descriptor is probably read through ntdll NtCreateSection/NtMapViewOfSection, which are not hooked (repo). F2P online; offline training exists (pk). | Verified | Not working (build 244 in COMPATIBILITY.md; notes to build 374): AIR reports 'Application descriptor could not be found'. |
| 304050 | Trove | B? | ? | ? (Trion in-house, pk) | D3D10 or OpenGL 3.2 (store; web) | `LAUNCH`, `ONLINE` | Glyph client (GlyphClient.exe) plus a Trion/gamigo account; always-online MMO. Arch unknown. | Playable | Untested |
| 332570 | Amazing Frog? | B? | x64 + x86 (depots) | Unity (pk) | D3D11 (Unity default, pk) | `MEM` | Store lists RAM 16 GB min, probably inflated for a small Unity game: measure before dismissing. Disk 20 GB. | Verified | Untested |
| 362890 | Black Mesa | B | ? (x86, web) | Source (wiki) | D3D9 (store: DX 9.0c) | `X86`, `D3D9` | RAM 6 GB min (store) against a 32-bit address space (web). | Playable | Untested |
| 431600 | Automobilista | B | ? (x86, pcgw) | isiMotor2 (pcgw) | D3D9 (pcgw) | `X86`, `D3D9` | None known. | Unsupported | Untested |
| 444200 | World of Tanks Blitz | B | ? (x86: Steam build is 32-bit, web) | DAVA (web) | D3D11 (web; store: DX 11 GPU) | `X86`, `D3D11-32`, `ONLINE` | Always-online MMO; Wargaming account (store). Deck: LauncherInteractionIssues. | Playable | Untested |
| 460960 | The Deed: Dynasty | B? | ? | ? (launch 'game.exe' is the stock RPG Maker MV/MZ NW.js name, pk) | ? | `PROBE` | No public engine or arch data. If it is an NW.js build it also needs a browser runtime. | Unrated | Untested |
| 561770 | Just Ignore Them | B? | ? | ? (launch 'winsetup.exe' is the Adventure Game Studio setup-tool name, pk) | ? | `PROBE` | No public engine or arch data. Deck: Unsupported (SteamOSDoesNotSupport). | Unsupported | Untested |
| 562260 | WAVESHAPER | B | x86 (repo: inspected depot; PICS silent) | GameMaker (data.win; repo) | D3D9 (repo: d3dx9) | `X86`, `D3D9` | Gameplay at ~2 fps: every guest branch still runs in the interpreter. No audio (dsound). | Verified | Playable but slow (build 424, 2026-10-01): Steam sign-in, title, menus and a level through the D3D9 bridge with the controller; menus ~20 fps, gameplay ~2 fps. |
| 573090 | Stormworks: Build and Rescue | B | x64 + x86 | In-house C++ (web) | OpenGL 3.2+ only (web) | `GL` | The game always renders with OpenGL (web, PCGW talk page); the OpenGL path is not built. | Playable | Untested |
| 635850 | Sentience: The Android's Tale | B | ? | NW.js (PICS launch entry nw.exe) | Chromium / WebGL (NW.js) | `WEB` | Needs a Chromium/NW.js runtime. Deck: Unsupported. | Unsupported | Untested |
| 704210 | Block Warriors | B? | ? | ? | ? | `PROBE` | No store page and no PICS launch or depot data (anonymous PICS). Cannot be classified yet. | Unrated | Untested |
| 945360 | Among Us | B | ? (x86: Steam build is 32-bit, web; store min lists 'Windows 10 x32bit') | Unity (wiki) | D3D11 (Unity default, pk) | `X86`, `D3D11-32` | Matchmaking is online; local and freeplay modes exist. | Playable | Untested |
| 1091500 | Cyberpunk 2077 | B | x64 (osarch) | REDengine 4 (wiki) | D3D12 (store: DX 12) | `D3D12`, `MEM` | RAM 12 GB min, 6 GB VRAM and 70 GB disk (store). PICS lists REDlauncher (redprelauncher.exe) and a direct bin/x64/Cyberpunk2077.exe entry. Long shot. | Verified | Untested |
| 1454400 | Cookie Clicker | B? | ? | HTML5/JS in a desktop wrapper (pk; NW.js or Electron, unconfirmed) | Chromium | `WEB` | Needs a browser runtime if the wrapper is Chromium-based (inferred). | Playable | Untested |
| 1590320 | Bro Falls: Ultimate Showdown | B? | ? | ? | D3D11 (store: DX 11) | `ONLINE` | Online PvP/co-op only, no single-player (store). RAM 6 GB min. Arch and engine unknown. | Playable | Untested |
| 1659040 | HITMAN World of Assassination | B | x64 (osarch) | Glacier (pk) | D3D12 only (store; web: no D3D11 fallback) | `D3D12` | IOI launcher (Launcher.exe) is the only PICS entry; Retail/HITMAN3.exe can be run directly (web). RAM 8 GB min, disk 60 GB (store). | Playable | Untested |
| 1677740 | Stumble Guys | B | x64 | Unity (pk) | D3D11 (Unity default, pk) | `ONLINE` | Multiplayer-only (store): needs online services and the game's backend. | Playable | Untested |
| 1721470 | Poppy Playtime | B | x64 | Unreal Engine 4 (ch. 1-2) and 5 (ch. 3-5) (wiki) | D3D11 or D3D12 (unverified) | `LAUNCH` | PlaytimeLauncher.exe is the only PICS launch entry (a chapter launcher, pk). RAM 8 GB min, disk 20 GB (store). | Playable | Untested |
| 2050650 | Resident Evil 4 | B | x64 (osarch) | RE Engine (wiki) | D3D12 only (store; web) | `D3D12` | RAM 8 GB min, 16 rec, 4 GB VRAM (store). First-time setup needs internet (Deck token). | Verified | Untested |
| 2085540 | Stick It to the Stickman | B? | ? | ? (Free Lives; Unity likely, pk) | D3D12 (store: DX 12 min and rec) | `D3D12` | D3D12 per the store; if the engine is Unity a D3D11 mode may exist. RAM 8 GB min. | Verified | Untested |
| 2139680 | Masterchef Cakes Edition | B? | ? | ? | ? | `PROBE` | No store page and no PICS launch or depot data (anonymous PICS). Cannot be classified yet. | Unrated | Untested |
| 2881650 | Content Warning | B | x64 | Unity (pk) | D3D11 (store: min DX 11, rec DX 12) | `ONLINE` | Co-op only, no single-player category (store): needs Steam lobbies and online services. RAM 8 GB min. | Verified | Untested |
| 3404260 | Dead as Disco | B? | ? | Unreal Engine 5 (wiki) | ? (UE5; D3D12 likely) | `D3D12` | D3D12 inferred; a D3D11 mode would make it tier A. Early Access. RAM 8 GB min. | Verified | Untested |
| 3764200 | Resident Evil Requiem | B | x64 | RE Engine (wiki) | D3D12 (store: DX 12) | `D3D12`, `MEM`, `DENUVO` | RAM 16 GB min (store); Denuvo anti-tamper (store DRM notice); first-time setup needs internet; Deck: not performant. Long shot. | Playable | Untested |

## Tier C: out of reach

14 entries. None is a target; the reasons are recorded so nobody investigates them again.

| AppID | Game | Tier | Arch | Engine | Graphics | Why out of reach | Blockers and notes | Deck | Nativra status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 320 | Half-Life 2: Deathmatch | C | x64 + x86 | Source (wiki) | D3D9 (pk) | online-only with VAC | Multiplayer-only: no single-player and no official bots (pk); VAC (user-mode, Valve's own). Online play on VAC servers needs genuine Steam client services; a lone listen server is the only offline use. | Playable | Untested |
| 360 | Half-Life Deathmatch: Source | C | x64 + x86 | Source (store) | D3D9 (pk) | online-only with VAC | Multiplayer-only: no single-player and no official bots (pk); VAC. Same situation as Half-Life 2: Deathmatch. | Playable | Untested |
| 480 | Spacewar | C | ? | ? (Steamworks SDK sample) | ? | not a game (SDK sample) | Valve's Steamworks example app (SteamWorksExample.exe), not a game. Could still serve as a Steam-API test target. | Unrated | Untested |
| 12230 | Grand Theft Auto III | C | n/a (macOS) | RenderWare via TransGaming Cider (macOS port) | n/a | macOS-only entry | macOS-only entry: PICS oslist is macos, launch is a Cider app bundle, retired at the publisher's request. The Windows build is 12100. | Unsupported | Untested |
| 12240 | Grand Theft Auto: Vice City | C | n/a (macOS) | RenderWare via TransGaming Cider (macOS port) | n/a | macOS-only entry | macOS-only entry (Cider bundle), retired at the publisher's request. The Windows build is 12110. | Unrated | Untested |
| 12250 | Grand Theft Auto: San Andreas | C | n/a (macOS) | RenderWare via TransGaming Cider (macOS port) | n/a | macOS-only entry | macOS-only entry (Cider bundle), retired at the publisher's request. The Windows build is 12120. | Playable | Untested |
| 271590 | Grand Theft Auto V Legacy | C | x64 | RAGE (wiki) | D3D11 (store: DX 10/10.1/11) | kernel-level anti-cheat | Anti-cheat (PICS Deck token UnsupportedAntiCheatConfiguration; BattlEye in GTA Online, pk). Rockstar Games Launcher/Social Club (store text). Disk 125 GB. | Unsupported | Untested |
| 447040 | Watch_Dogs 2 | C | x64 | Disrupt (pk) | D3D11 (pk) | kernel-level anti-cheat | Easy Anti-Cheat plus Denuvo (store text); Ubisoft Connect/Uplay account; PICS Deck token UnsupportedAntiCheatConfiguration. | Unsupported | Untested |
| 1085660 | Destiny 2 | C | x64 (osarch) | ? (Bungie in-house, pk) | ? | kernel-level anti-cheat | Anti-cheat (PICS Deck token UnsupportedAntiCheatConfiguration; BattlEye, pk); online-only; launcher entry destiny2launcher.exe; disk 105 GB (store). | Unsupported | Untested |
| 1097150 | Fall Guys | C | x64 (osarch) | Unity (wiki) | D3D11 (Unity default, pk) | kernel-level anti-cheat | Easy Anti-Cheat (pk) plus online-only service; Epic account (store). Deck: LauncherInteractionIssues. | Playable | Untested |
| 1304930 | The Outlast Trials | C | x64 | Unreal Engine 4 (wiki) | D3D11 (store: min DX 11, rec DX 12) | kernel-level anti-cheat | Easy Anti-Cheat bootstrapper (PICS launch entry start_protected_game.exe). RAM 8 GB min. | Verified | Untested |
| 2012840 | Portal with RTX | C | x64 | Source with NVIDIA RTX Remix (wiki, pk) | Path-traced (RTX) | RTX-only | RTX-only: needs an NVIDIA RTX GPU (store: RTX 3060 min, 16 GB RAM). | Unsupported | Untested |
| 2488620 | F1® 24 | C | x64 | Ego 4.0 (wiki) | D3D12 (store: DX 12) | kernel-level anti-cheat | EA anti-cheat (PICS launch entry EAAntiCheat.GameServiceLauncher.exe; Deck token UnsupportedAntiCheat_Other), Denuvo and EA account (store). Disk 100 GB. | Unsupported | Untested |
| 3240220 | Grand Theft Auto V Enhanced | C | x64 | RAGE (wiki) | D3D12 (pk) | kernel-level anti-cheat | Anti-cheat (PICS Deck token UnsupportedAntiCheatConfiguration; BattlEye, pk). Rockstar Games Launcher (store text). Disk 105 GB, RAM 8 GB min. | Unsupported | Untested |

## Method and caveats

- **PICS** (Steam's public app info, read anonymously with `steamcmd` on 2026-10-01) gave
  the name, type, launch entries with their `osarch`, depot architectures, the Deck rating
  and test tokens, and the VAC flag. Anonymous PICS is partial: some apps (Castle Crashers,
  Block Warriors, Masterchef Cakes Edition) return no launch or depot data, and a launch
  table can omit executables the depot ships. LEGO Jurassic World lists only a 32-bit exe,
  while its depot also carries the 64-bit D3D11 one the repo runs.
- **Store pages** (the public `appdetails` data, 2026-10-01) gave minimum and recommended
  RAM, DirectX version, disk size, DRM notices, categories and account requirements. The
  store's DirectX line is a hardware requirement, not proof of the API a game renders with:
  Unity titles often list DX 9.0c and still render with D3D11.
- **Engines and APIs** come from Wikipedia and Wikidata infoboxes, PCGamingWiki facts read
  from search snippets (the wiki refuses direct automated requests, and that was not worked
  around), a few other public pages, and public knowledge. Small indie titles with no public
  record stay `?`.
- **Not authenticated as the owner.** Nothing here used the owner's Steam session, so
  family-shared versus owned is not distinguished, and owned-only data such as depot file
  lists was not read.
- **Memory.** The app budget is about 5 GB and Hades already uses 4.4 GB. Store RAM is an
  imperfect proxy, because small developers often inflate it (Amazing Frog? states 16 GB),
  so it is a flag in the notes and the `MEM` piece applies only at 12 GB or more of minimum
  RAM or 6 GB or more of minimum VRAM.
- **Unity backends.** Seraph's Last Stand is IL2CPP and verified. Cuphead is Unity Mono,
  which needs a managed JIT, and has not reached a frame. Many older Unity titles in tier A
  are probably Mono, so Cuphead is the test that decides whether they belong in tier A at
  all.
- **Source-family games** (Valve titles in all three tiers) integrate with the Steam client
  more deeply than a typical Steamworks game (client library, VAC modules). Whether the
  classic Steamworks bridge is enough is untested (pk).
- **Steam CEG and Denuvo** wrap some executables. WAVESHAPER's CEG self-check is its current
  stop; Denuvo is flagged where the store says so and is not touched.
- **Resolving the `?` rows.** `xbdev steam info <appid>` lists a game's depots and the files
  that give an engine away (exe names, `UnityPlayer.dll`, `GameAssembly.dll`,
  `*-Shipping.exe`, `mono-2.0`, `data.win`, `*.pck`, `fna.dll`) without downloading
  anything, and `xbdev steam download <appid> --only <regex>` fetches only matching files
  for a PE-header check. Both need the owner's session, so they were not run for this
  catalog; they settle arch, engine and API for the A? and B? rows.
- **Refreshing the data.** Re-read PICS anonymously, re-run the store `appdetails` pass,
  keep the hand-reviewed columns, and bump the data date at the top.

## Resumo (pt-BR)

Catálogo da biblioteca Steam inteira (114 entradas: jogos próprios e do Steam Families), com
os dados de **2026-10-01**. As fontes são o PICS da Steam, as páginas da loja, a Wikipédia e
o Wikidata, o PCGamingWiki (por trechos de busca) e as notas de teste do repositório. Nada
aqui é promessa de compatibilidade: só o que está em `COMPATIBILITY.md` e em
`docs/progress/` foi medido no console.

**Faixas:** A = plausível com a camada atual (52 jogos, 31 deles com algum dado inferido,
marcados A?); B = falta uma peça específica (48); C = fora de alcance, por anti-cheat de
kernel, serviço só online com anti-cheat, RTX obrigatório ou entrada que não é jogo (14).

**Primeiros alvos (faixa A).** Já rodaram no console: Seraph's Last Stand, Hades, Cuphead,
Little Nightmares, LEGO® Jurassic World. Primeira onda leve, sem teste: House Flipper,
PowerWash Simulator, Car Mechanic Simulator 2018, Sprocket, Bloons TD 6, Vampire Survivors,
9 Kings, Seen, Poly Bridge 2, Graveyard Keeper.

**Peças compartilhadas, por jogos liberados:**

1. 32 bits (`X86` + `D3D9`): 17 jogos juntos (20 com uma camada Direct3D 8). Sozinha, `D3D9`
   libera só o Team Fortress 2, que já tem build de 64 bits.
2. Direct3D 12: 6 jogos, 4 dependendo só dela.
3. Serviços online: 6 jogos, 3 só multiplayer.
4. Launchers que abrem o jogo como processo filho: 5 jogos; o app recusa `CreateProcess`.
5. Memória (4 jogos, teto de cerca de 5 GB), motor de navegador (2), Terraria (.NET e XNA),
   Brawlhalla (Adobe AIR), Stormworks (OpenGL), Denuvo (2) e 4 entradas que precisam de uma
   sondagem de depot (`xbdev steam info <appid>`).

A faixa A não significa "funciona": dos cinco jogos dela que já rodaram no console, só o
Seraph's Last Stand está verificado. O primeiro bloqueio comum é o próprio caminho 64-bit
D3D11 (primeiro quadro, inicialização, esperas de armazenamento). Resident Evil Requiem pede
`D3D12`, `MEM` e `DENUVO`: é a entrada mais distante da faixa B.
