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
| WAVESHAPER | 562260 | 32-bit Windows | ⛔ Not working | build 348, 2026-09-26 | 32-bit. The x86 block-JIT loader maps it (base 0x400000, entry 0x6D2D98) and runs; most imports resolve (kernel32 169/212, user32 53/68). Stops at a missing kernel32 thunk (InterlockedCompareExchange). Real progress; needs more import thunks. |
| LEGO Jurassic World | 352400 | TT Games (D3D11, x64 `LEGOJurassicWorld_DX11.exe`) | ⛔ Not working | build 305, 2026-09-26 | Borrowed through Steam Families; downloads (15.3 GB, to a USB drive). Starts through the classic Steamworks bridge, creates its window, D3D11 device and swap chain, then reads a null engine resource (`+0x3795DB`) before its first frame; the missing piece is not found yet. |

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
