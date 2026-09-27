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

## Build 380 (1s bound + timeout counter) — the UsbFiles fix helped, isn't the whole story

Console rebooted clean, then five more runs. Survival time after the window
shows is not consistent (20-50s across runs, same console, same build,
nothing else changed) — this on its own says the remaining blocker is a race,
not a deterministic wait on one fixed thing.

The clean result: `usb.folders opened=0 missed=0 declined=0 timedout=0` — a
run that still froze and died at the same point (last call `SetFocus`, same
as every prior run) with **zero** `UsbFiles` folder-handle timeouts. So the
5-second-then-1-second freeze fixed in `UsbFiles.Folder()` was real and worth
fixing, but it is not what is blocking LEGO specifically, or not the only
thing. Checked the other bounded waits in the graphics/audio path
(`GraphicsBridge.cs` ×3, `FrameMirror.cs`, `AudioBridge.cs` — each already
has a 3-4 second bound, not an unbounded one) — none of them is a fresh
unbounded-hang bug the way `UsbFiles.Folder()` was; if they are involved it
would be several of them firing in sequence adding up, not one clear cause.

Reasonable stopping point for tonight: window + D3D11 device/swap chain
reliably work with `steambridge.txt`; something after the window is shown
and before the first frame is intermittently very slow or genuinely stuck,
severely enough that the console's watchdog (see
[../DIAGNOSTICS.md](../DIAGNOSTICS.md#unexplained-app-termination)) ends the
app before any of it reaches a log. Next real step needs either: TRACE mode
scoped to only the window-to-first-Present window (full TRACE crashes the
app itself after ~60-90s, so it cannot safely cover this), or profiling
which of the several bounded waits above are actually firing and how many
times, by adding counters to each the way `UsbFiles.TimedOut` was added.

## Build 383 — every known blocking wait ruled out

Added a counter to the sixth and last candidate found by an exhaustive grep
of every `.Wait(`/`.GetAwaiter().GetResult()` in `uwp/Kiosk/Native/`,
`uwp/Kiosk/Steam/` and `x86/Nativra.X86/Loader/`: `SteamCm.Await()` (30s
timeout, used by the background playtime/achievement sync `SteamBridge`
starts). Ran LEGO once more, patient, with all six now reported live:

    waits timedout: show=0 shownative=0 release=0 mirror.prepare=0
    audio.activate=0 steamcm.timedout=0

All six stayed at zero for the whole run, right up to the app's death at
40s. This is a complete, definitive negative result for every blocking-wait
pattern that exists in this codebase's own Windows-bridge and Steam code —
not one of them is the cause. The freeze is somewhere this kind of search
cannot find: either genuine, slow work happening in LEGO's own machine code
(a large shader compile or asset decompression that just takes a while,
unlucky enough to trip whatever kills an unresponsive foreground app), a
native deadlock inside COM/WinRT marshaling itself rather than in any of
this project's own wait calls, or the console's own broker services being
slow from a long evening of repeated installs and restarts rather than
anything reproducible in the code.

**Where this stands, honestly**: LEGO Jurassic World creates its D3D11
device, swap chain, and window reliably with `steambridge.txt` set — real,
verified progress from where this investigation started (a null-resource
read before any of that worked). It does not yet reach a playable first
frame. The next productive step is not more grep-based hunting in this
codebase; it needs either a live debugger attached during the hang (not
available from a Mac driving the console over Device Portal), or narrowing
by elimination — try the same steps against a *different* 64-bit game that
also uses a large asset set, to see whether the hang is LEGO-specific or a
general risk any big enough game would hit.
