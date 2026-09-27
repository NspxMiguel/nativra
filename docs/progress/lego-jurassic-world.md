# LEGO Jurassic World — the null resource before the first frame

Steam 352400, `LEGOJurassicWorld_DX11.exe` (x64, 40,576,824 bytes, depot download
2026-09-27). On the console (build 305) the game starts, creates its window, D3D11
device and swap chain, then faults reading through a null pointer at `+0x3795DB`.

## What the fault is (static analysis, Capstone)

`+0x379557` fills an engine texture descriptor on the stack and asks the engine's
resource factory for it:

| field | value |
|---|---|
| `+0x00` engine format | `0x70` |
| `+0x04` width | `r12d` (same as the texture created just before) |
| `+0x08` height | `r13d` (same) |
| `+0x0C` | 0 |
| `+0x10` mip levels | 1 |
| `+0x18` flags | `0x101` (bit 0 = depth target) |

`call 0x14033EC90(factory, &out, &desc)` then stores `out` in `[rsi]` and, at
`+0x3795DB`, reads `[rsi]->+0x50` without a null check. The fault means the
factory returned null.

`0x14033EC90` validates first: width 0 returns a shared default object; height 0,
mip levels 0, a negative `+0x0C`, format 0 or ≥ `0x79`, or (with flag bit 0) a
format below `0x64` all take the failure path (`0x14033F09C`, null out). Format
`0x70` with flag bit 0 passes these checks, and the texture created just before
(`+0x379446`, format `0x75` or `r15d`, same size) does not fault, so the size is
not zero. The failure is further in: the creation path at `0x14033F600`.

## Why D3D11 is not the suspect yet

GraphicsBridge logs every failed `CreateTexture2D` and `CreateDepthStencilView`
with its HRESULT, and the build 305 run logged none. The engine therefore gives up
before asking D3D11, most likely in its format translation or a capability check
(`ID3D11Device::CheckFormatSupport`, vtable slot 29, is forwarded unlogged).

## Next console run

Log `CheckFormatSupport` / `CheckMultisampleQualityLevels` answers in
GraphicsBridge, run LEGO (`APPID=352400`), and read which DXGI format the engine
asks about just before the fault. Then disassemble `0x14033F600` down to the
failing branch.

## Build 377 (CheckFormatSupport logging, crash-log.txt)

Different picture than the earlier stop. This run: `graphics=whole path
works: level 0xB000, present 0x00000000` — the D3D11 device and swap chain
were created successfully. No `CreateTexture2D`/`CreateDepthStencilView`
failure logged, no `CheckFormatSupport` refusal logged either (so it never
reached the point this build was meant to observe).

Instead, the guest's main thread goes idle: `thread 13 ... idle=48171ms at
USER32.dll!SystemParametersInfoA`, `busiest 8x USER32.dll!SystemParametersInfoA`
— eight calls to a stub that answers instantly (`WindowStubs.cs`: a constant
`Answers["SystemParametersInfoA"] = 1`, no work in it at all), then nothing
further recorded on that thread for 48 seconds, then the whole app process is
gone. No `crash-log.txt` entry (added this build specifically to catch a
managed unhandled exception) and no console crash dump — this is not an
exception of any kind reaching .NET or a native access violation with a
dump. The most consistent explanation: the guest thread enters a spin or
retry loop outside anything this build's diagnostics track (plausibly right
after the earlier-documented null-resource read, if the engine's own
try/catch around that swallows it and retries), burning CPU without making
another tracked Win32 call, until the console's own foreground-unresponsive
watchdog kills the app — see the note in
[../DIAGNOSTICS.md](../DIAGNOSTICS.md#unexplained-app-termination) added this
round; the same pattern (no exception, no dump, app gone after roughly a
minute) showed up independently today in the emulator shop and in TRACE mode.

Reproduction is not yet consistent enough between runs (an earlier attempt on
the same build stopped after only ~20s, mid-load) to be certain this is the
same failure every time. Next: `STACKS=on` alone (no TRACE, cheaper) for a
longer window to confirm the idle thread's native return address, and check
whether the earlier-documented `+0x3795DB` null-resource fault still happens
first (a debug build or a native crash handler that survives longer than 48s
would settle whether this is that fault swallowed by a retry loop, or
something upstream of it entirely).

## Build 378 (SteamBridge armed) — much further, then a frozen process

With `steambridge.txt` set (the classic Steamworks bridge answers with the
signed-in account instead of the game loading the real `steam_api64.dll` and
waiting on a Steam client that is not there — that wait was the earlier
48-second hang at `SystemParametersInfoA`, confirmed by comparison: without
the bridge the game never gets past that call; with it, it does), the game
now:

- Creates its D3D11 device and swap chain (`graphics=whole path works`).
- Fully creates and shows its window: `LoadIconA`, `LoadCursorA`,
  `AdjustWindowRect`, `SetWindowTextA`, `SetWindowPos`, `ShowWindow`,
  `UpdateWindow`, `SetForegroundWindow`, `SetFocus` — the complete sequence,
  in order.

Then, about 20-24 seconds in, the **whole process freezes** — not just the
game's thread. The `native-watch.txt` background watcher (its own dedicated
thread, unrelated to the game, writing a timestamp every second) stops
updating entirely: three pulls a few seconds apart all showed the identical
timestamp. The last stack sample before the freeze has the main thread inside
an RPC call: `~ntdll.dll → ~RPCRT4.dll → ~combase.dll →
~windows.storage.onecore.dll → ~shcore.dll → ~windows.storage.onecore.dll`
(repeated) — a cross-process COM/WinRT call into the Storage broker. LEGO's
files are on a USB drive (`usb=E:\Nativra\games\352400`); this is consistent
with a file operation falling through to the slow broker path (`UsbFiles.cs`
docs: the broker path measured ~220ms normally — this is not that, it never
returns) rather than the fast folder-handle path, on whichever file the game
reaches for once its window is up (likely a config or the first content
file). After the freeze the console's watchdog kills the whole app, same
as the earlier pattern (see [../DIAGNOSTICS.md](../DIAGNOSTICS.md)).

**This is real forward progress**: window and graphics work now; the
remaining blocker is a specific file access that hangs the storage broker
instead of returning (even an error would let the game continue). Next:
`FILEWATCH=on` together with `steambridge.txt` (not `stacks.txt`, which
combined with `steambridge.txt` triggers a separate, earlier issue in
`SteamClassic.Prebuild()` under trace — a Kiosk-side diagnostic bug to fix
separately) to name the exact path the broker call hangs on.
