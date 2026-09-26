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
| Hades | 1145360 | Supergiant engine (x64, D3D11, SDL2, FMOD, runtime HLSL via D3DCompiler_47) | ⛔ Not working | build 282, 2026-09-26 | Reaches the main menu at 60 fps (2.3 ms per frame), running from a USB drive ([screenshot](screenshots/hades-main-menu.png)). Not yet: controller in menus, audio (FMOD), the menu's Bink background movie. |
| Little Nightmares | 424840 | Unreal Engine 4 | ⬜ Untested | — | Audio uses XAudio 2.7 from the 2010 DirectX redistributable, which the console lacks. |
| WAVESHAPER | 562260 | 32-bit Windows | ⛔ Not working | 2026-09-23 | 32-bit executables are not supported. |
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
