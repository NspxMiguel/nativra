# Where everything is

A file-by-file map of the codebase, for anyone who has not seen it before —
including a future session of ours. [docs/CONTRIBUTING.md](CONTRIBUTING.md)
has the five-file quick start; this is the whole thing. Nothing here is a
design decision — see [DESIGN-BRIEF.md](DESIGN-BRIEF.md) for that — this is
just "what file does what."

## Top-level layout

```
uwp/Kiosk/          the app itself (UWP/.NET Native, runs on the console)
uwp/JitProbe/        a tiny standalone UWP app: proves the x86 block JIT can
                      generate and execute code on a console, on its own,
                      before trusting it inside the real app
uwp/TlsCarrier/       C sources for NativraTls0..15.dll and NativraTlsLarge0.dll —
                      small native DLLs that exist only to carry extra thread-local
                      storage slots for guest DLLs with large __declspec(thread) data
x86/Nativra.X86/      the 32-bit (x86-on-x64) compatibility layer: interpreter,
                      block JIT, and the "guest kernel" that answers a 32-bit
                      game's Windows calls
x86/Nativra.X86.Tests/ unit tests for the x86 layer (no console needed)
x86/harness/           a console-side test runner for the x86 layer
native/directx-redist/ C++ shim DLLs standing in for DirectX June 2010 pieces
                        the console does not ship (d3dx9_43, xaudio2_7, ...)
src/                   the Mac-side CLI ("xbdev") that drives the console over
                        Device Portal: install, launch, input, screenshots,
                        and the Steam client implementation used to download
                        games straight from Steam's content servers
tools/                 small one-purpose scripts (fast local checks, session
                        capture, diagnostics) — see below
test/                  integration tests for the Mac-side tooling
configs/emulators/     per-system emulator configuration templates (one folder
                        per system: ps1, n64, dreamcast, ...), installed onto
                        the console by `xbdev setup-retroarch` / the catalogue
docs/                  design brief, compatibility log, per-game progress notes
.github/workflows/     CI: build-uwp.yml (the app), directx-redist.yml (the
                        shim DLLs), x86-harness.yml (the x86 layer's own tests)
catalog.json            every installable package (the app itself, emulators,
                         native PC-game ports of open-source engines) with its
                         download URL — read by src/xbdev.ts and pushed to the
                         console as catalog-console.json for the in-app shop
```

## `uwp/Kiosk/` — the app

Three layers inside it:

- **Screens** (`MainPage.xaml(.cs)`, `SteamPage.xaml(.cs)`, `GamePage.xaml(.cs)`,
  `MainPage.Setup.cs`, `MainPage.Diagnostics.cs`) and app-level state
  (`Settings.cs`, `DownloadManager.cs`, `Updater.cs`, `GameStorage.cs`,
  `EmulatorShop.cs`, `Texts.cs`) — the UI and what it needs to decide what to
  show. See `docs/architecture/kiosk-root.md` for the file-by-file list.
- **`Native/`** — the translation layer: PE loading, Win32 API answers,
  graphics/audio/input bridges, file access, diagnostics. This is the
  project's real substance. See `docs/architecture/kiosk-native.md`.
- **`Steam/`** — the Steamworks client implementation (classic C++ vtable
  interfaces, downloads, depot handling) the loader hands a game that expects
  Steam to be running. Covered in the same file as `Native/`.

## `x86/` — running 32-bit games

A 32-bit game cannot run directly on a 64-bit process, so this is a small
CPU: it interprets or JIT-compiles x86 machine code and answers the Win32
calls that code makes, the same way `uwp/Kiosk/Native/` does for 64-bit
games — but from scratch, because a 32-bit game cannot call 64-bit Windows
functions directly. See `docs/architecture/x86-layer.md`.

## `src/` — the Mac-side tool (`xbdev`)

Everything from a Mac talks to the console through its Device Portal (HTTPS,
self-signed cert, CSRF token from a cookie) — installing packages, launching
and stopping apps, pushing/pulling files, taking screenshots, sending
controller input, and downloading games directly from Steam's own content
servers (a from-scratch Steam client: connects to a CM server, authenticates,
fetches manifests and depot chunks, decrypts them). See
`docs/architecture/mac-tooling.md`.

## `tools/` — one-purpose scripts

| File | What it does |
| --- | --- |
| `console-lock.sh` | one console, one session at a time — acquire/release/show |
| `preflight.sh` | catches a duplicate constant/field name before the CI build does |
| `resource-check.sh` | catches a `StaticResource` that does not exist (a page that would throw on load) |
| `xaml-check.sh` | catches malformed XAML before the CI build does |
| `capture-session.ts` | read-only console diagnostics, bounded, kept private under `.cycle/` |
| `pe_imports.py` | lists every Windows API a binary imports — the checklist for what the translation layer must answer |
| `restore-game.ts` | puts a game the console still has on disk back into Nativra's storage without re-downloading |
| `why-launch.ts` | the Device Portal's full explanation for a refused launch (the client only reports the status code) |

## How a game launch actually flows

1. The shelf (`MainPage.xaml.cs`) has a `Tile` for the game; pressing A calls
   `StartGameAsync` → `Native.NativeProbe.RunAsync(appId)`.
2. `NativeProbe` finds the game's own executable (`GameStorage.ExecutableAsync`,
   which knows the different places Unity/Unreal/TT Games/etc. put it),
   decides 32-bit vs 64-bit (`GameStorage.IsX64Async`), and picks the loader:
   `PeImage` (64-bit) or the whole `x86/` layer (32-bit).
3. `PeImage` maps the executable and its DLLs in dependency order, applies
   relocations, fixes up imports through `SystemImports` (which decides, per
   import, whether the console's own Windows answers it, a bridge in
   `Native/` answers it, or it gets a stub), sets up thread-local storage
   (`ThreadTls`, borrowing space from the carrier DLLs when a game's TLS is
   too large for the default), and hands every new thread through
   `DLL_THREAD_ATTACH` — this last part is why C++ `thread_local` state
   works at all; skipping it is what made Hades hang at start-up before it
   was added.
4. From there the game runs like it would on real Windows: it asks for a
   D3D11/D3D9 device (`GraphicsBridge`/`D3D9Bridge`, wrapping the console's
   own swap chain), creates a window (`WindowStubs`), reads input
   (`PadBridge`/`PointerBridge`/`DirectInputStub`), plays audio
   (`AudioBridge`/`XAudio27Route`), and opens files (`CrtFiles`/`UsbFiles`/
   `UserFolders`, so `fopen`/`CreateFile` reach the right place whether the
   game is on the console's own storage or a USB drive). A game linked
   against Steamworks gets `Steam/SteamClassic.cs` instead of a real Steam
   client.
5. `FrameMirror` copies each finished frame to a `WriteableBitmap` so it can
   be shown inside the app's own XAML tree (`GameImage` in `MainPage.xaml`) —
   the one route that needs nothing the platform would refuse a sideloaded
   app. `Native.Recorder` can tap the same frames to encode an MP4.
6. If something goes wrong, `StackSampler` and `FaultWatch` (and, for file
   problems, `FileWatch`) are what a diagnostic build turns on to find out
   where — see [DIAGNOSTICS.md](DIAGNOSTICS.md).

## Companion maps

- [architecture/kiosk-root.md](architecture/kiosk-root.md) — every file directly in `uwp/Kiosk/`
- [architecture/kiosk-native.md](architecture/kiosk-native.md) — every file in `uwp/Kiosk/Native/` and `uwp/Kiosk/Steam/`
- [architecture/x86-layer.md](architecture/x86-layer.md) — every file in `x86/Nativra.X86/`
- [architecture/mac-tooling.md](architecture/mac-tooling.md) — every file in `src/`
