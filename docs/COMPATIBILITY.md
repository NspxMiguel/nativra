# Game compatibility

Tested on an Xbox Series X in Developer Mode, with games downloaded from the
owner's own Steam library by Nativra itself. Ratings follow the Steam Deck
idea, with one addition for games not tried yet.

| Rating | Meaning |
| --- | --- |
| ✅ **Verified** | Starts, plays with the controller, no known issue. |
| 🟡 **Playable** | Runs and can be played, with the exceptions listed. |
| ⛔ **Not working** | Does not reach gameplay yet; the note says where it stops. |
| ⬜ **Untested** | Not tried on the console yet. |

| Game | Steam app | Engine | Rating | Last tested | Notes |
| --- | --- | --- | --- | --- | --- |
| Seraph's Last Stand | 1919460 | Unity 2021.3 (IL2CPP, D3D11) | ✅ Verified | build 244, 2026-09-25 | Fully playable with the controller (hold View + Menu to switch to mouse and keyboard). Steam sign-in shows "In-Game" to friends. Steam achievements not tested yet. |
| Brawlhalla | 291550 | Adobe AIR (x64) | ⛔ Not working | build 244, 2026-09-25 | Downloads and starts; the AIR runtime loads and opens its application descriptor, then reports "Application descriptor could not be found". |
| Hades | 1145360 | Supergiant engine (x64, D3D11, SDL2, FMOD, runtime HLSL via D3DCompiler_47) | 🟡 Playable | build 319, 2026-09-26 | Played on the console from a USB drive: 60 fps, controller and sound work ([menu](screenshots/hades-main-menu.png)). From build 357 SDL reads the pad through the XInput bridge (build 359: `XInput reads` counting, native controller mode, Device Portal input drove the menu, a save and combat, recorded at ~52–55 fps by the in-app recorder). Exceptions: app memory runs near the 5 GB limit (4.4 GB), long sessions not tested; A also played the console's interface click (fixed in build 320). |
| Little Nightmares | 424840 | Unreal Engine 4 | ⛔ Not working | build 310, 2026-09-26 | Downloads (9 GB, to a USB drive); the loader picks `Atlas\Binaries\Win64\LittleNightmares.exe`, libcurl and PhysX start, and with the console GPU presented under a PC card's identity (Unreal skips Microsoft's vendor id) it creates its D3D11 device and swap chain. No frame yet. Audio needs XAudio 2.7, which the console lacks (in progress). |
| WAVESHAPER | 562260 | GameMaker (32-bit PE32, D3D9, OpenAL Soft) | 🟡 Playable | build 438, 2026-10-02 | First 32-bit game played on the console: Steam sign-in through the bridge, menus and levels at ~59 fps through the D3D9 bridge and the x86 JIT, sound through the DirectSound bridge (mixed in real time, no underruns; [game over screen](screenshots/waveshaper-game-over.png)). Exception: with the default desktop controller mapping START confirms and A clicks; hold View + Menu for a plain pad. See [progress/waveshaper-console.md](progress/waveshaper-console.md). |
| LEGO Jurassic World | 352400 | TT Games (D3D11, x64 `LEGOJurassicWorld_DX11.exe`) | ⛔ Not working | build 305, 2026-09-26 | Borrowed through Steam Families; downloads (15.3 GB, to a USB drive). Starts through the classic Steamworks bridge, creates its window, D3D11 device and swap chain, then reads a null engine resource (`+0x3795DB`) before its first frame; the missing piece is not found yet. |
| Castle Crashers | 204360 | XNA-style C++ (32-bit PE32, D3D9, SteamStub) | ⛔ Not working | build 463, 2026-10-04 | The SteamStub wrapper wants a genuine `steamclient.dll` and a Steam-signed app ownership ticket; the host answers the client interfaces but cannot yet fetch the ticket. |
| Super Meat Boy | 40800 | Custom C++ (32-bit PE32, D3D9, libcurl) | ⛔ Not working | build 463, 2026-10-04 | Creates its window and resource pool, then stops at the D3DX functions the bundled `d3dx9_43` shim does not have yet (shader compile, texture loading). |
| POSTAL 2 | 223470 | Unreal Engine 2 (32-bit PE32) | ⛔ Not working | build 463, 2026-10-04 | Engine initialises (log, CPU detection, object subsystem) and then hangs after cleaning temporary load files. |
| Cuphead | 268910 | Unity 2017.4 (Mono, x64, D3D11) | 🟡 Playable | build 479, 2026-10-05 | Reaches the title screen at 59.9 fps with the Rewired input stack running; takes about two minutes to load. Controller input is not confirmed yet: Rewired reports it cannot initialise XInput and falls back to raw HID. |
| Among Us | 945360 | Unity 2017.4 (IL2CPP, x64, D3D11) | 🟡 Playable | build 479, 2026-10-05 | Shows the privacy-policy screen and menus at 34 fps; the game polls XInput. Pressing through the portal does not reach it (a real controller is needed to confirm gameplay). |

Every other game in the library is sorted by how realistic it is today, with the missing
piece behind each "not yet", in the planning catalog [LIBRARY.md](LIBRARY.md).

Performance note: Xbox Developer Mode runs sideloaded apps as *apps* unless
`DefaultUWPContentTypeToGame` is on (`bun src/xbdev.ts gamemode`, then
restart). As an app Nativra gets a fraction of the CPU and GPU; a console
system update can turn the setting off again.

## Compatibilidade de jogos (pt-BR)

Testado num Xbox Series X em Modo Desenvolvedor, com jogos baixados da própria
biblioteca Steam pelo Nativra. As notas seguem a ideia do Steam Deck:
**✅ Verificado** (roda e joga sem problema conhecido), **🟡 Jogável** (roda,
com as exceções da tabela), **⛔ Não roda** (não chega ao jogo; a nota diz onde
para) e **⬜ Não testado**. A tabela acima vale para as duas línguas.
