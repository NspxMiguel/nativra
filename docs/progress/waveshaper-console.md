# WAVESHAPER on the console: first 32-bit Steam game in gameplay (2026-10-01)

WAVESHAPER (Steam app 562260) is a small GameMaker game: a 32-bit PE32
`WAVESHAPER.exe` with the runner, OpenAL Soft and the Steam CEG wrapper
linked in, plus `data.win`, `steam_api.dll` and `D3DX9_43.dll`. It is the
first 32-bit game to go through the whole chain on the console: Steam
sign-in through the bridge, Direct3D 9 through the D3D9-on-D3D11 bridge,
the controller, and a level being played.

## How it was tested

`tools/compat-sweep.ts` drives the installed app through its autoplay
marker and reads the reports back; `xbdev press` sends controller buttons
through Device Portal and `xbdev shot` takes the screen. The app was
installed straight from each CI release (`kiosk-build-N`).

## What stood in the way, in order

1. **The console ran an old build.** Builds were published but never
   installed: everything fixed in the layer that day had only run on the
   Mac and in CI. Each build is now installed from its release before a
   console test.
2. **The game's own `steam_api.dll` won over the Steam bridge.** The
   Windows-like DLL search found the file in the game folder before the
   bridge was consulted, and the real library looked for a running Steam.
   `GuestProcess.HostServed` now lists the DLLs the host answers in the
   game's place; neither the name search nor a path load maps them.
3. **The /GS cookie was overwritten.** `D3DADAPTER_IDENTIFIER9` is 1100
   bytes for a 32-bit program and 1104 on x64 (LARGE_INTEGER alignment).
   The COM bridge passed the game's stack buffer straight to the 64-bit
   `d3d9`, which wrote its padding over the cookie, and the game stopped in
   `__report_gsfailure` (`int 0x29`). The bridge's new `n:Bytes` code gives
   the host its own padded copy and returns only the guest's bytes.
4. **Every second call to a COM method failed on the console.** The bridge
   read a cached delegate's `DeclaringType`, reflection metadata .NET Native
   does not keep (`MissingMetadataException` on
   `CheckDeviceMultiSampleType`). Replaced with a set.
5. **The game was stopped after about two minutes.** Its entry point ran
   under the 200M-block budget meant for DllMain. Games now run until they
   exit; the probe report is also written every 30 s while they play.
6. **No controller.** XInput was only wired into the 64-bit loader, and the
   32-bit path skipped the code that marks a game as running and steps the
   pointer bridge, so the page dropped the pad's keys. Both fixed; the game
   reads XInput every frame.
7. **The sweep reused stale reports.** A failed pull left the previous
   run's report in place. Reports are now removed before each pull, and a
   32-bit game's frames are its `IDirect3DDevice9::Present` calls.

## Where it stands

Title screen, menus (about 20 fps) and a level (about 2 fps). With the
default desktop controller mapping, START confirms and A clicks; holding
Menu+View switches to a plain XInput pad.

Next:

- **Speed.** The JIT ends every block at a branch and steps the branch in
  the interpreter (`x86.blocks=… run, … interpreted` are equal), so each
  few instructions cost a trip through managed code. Translating branches
  and chaining blocks is in progress.
- **Sound.** OpenAL Soft finds no `dsound.dll`; a DirectSound for 32-bit
  games is in progress.
