# `uwp/Kiosk/Native/` and `uwp/Kiosk/Steam/` — the translation layer

Part of [../ARCHITECTURE.md](../ARCHITECTURE.md). This is the project's real
substance: everything a Windows game calls that the console does not
natively provide, answered well enough that the game cannot tell the
difference. Companion map for the rest of the app:
[kiosk-root.md](kiosk-root.md).

Every file lives in `uwp/Kiosk/Native/` unless marked `Steam/`.

## PE loading and thread setup

| File | What it does |
| --- | --- |
| `PeImage.cs` | Maps a PE binary into memory: sections, relocations, import table |
| `X86Launch.cs` | The 32-bit entry point: hands a PE32 game to the `x86/` layer instead |
| `ThreadTls.cs` | Reserves extra thread-local storage via the carrier DLLs (`uwp/TlsCarrier/`) for games whose TLS is too large for the default allotment |
| `ThreadRank.cs` | Keeps game threads at below-normal priority and the screen thread above it, so the UI stays responsive |

## Import resolution

| File | What it does |
| --- | --- |
| `SystemImports.cs` | For every import a game makes: answer with the real system function, a bridge in this folder, or a stub — this is the router everything else plugs into |
| `Win32Shim.cs` | Generates a stub for an unimplemented import that just returns zero, and keeps the "recent calls" ring used for diagnostics |
| `LoaderStubs.cs` | `LoadLibrary`/`GetProcAddress` at runtime, for a game that resolves things dynamically instead of at link time |
| `ImageLookup.cs` | Maps an address back to "which loaded image, what offset" — for crash/stack reports |
| `ModuleFileName.cs` | Answers `GetModuleFileName` with the game's own path, not the host app's |

## Window system

| File | What it does |
| --- | --- |
| `WindowStubs.cs` | Fake window handles, monitor enumeration, screen dimensions; also where a message box's text is captured for diagnostics |
| `WindowMessages.cs` | Window class registration, subclassing, and message dispatch |
| `DisplayStubs.cs` | Display settings (`EnumDisplaySettings` and friends) matching what a game expects |

## Graphics

| File | What it does |
| --- | --- |
| `GraphicsBridge.cs` | Intercepts DXGI swap-chain creation and redirects it onto the console's own `CoreWindow`; also where D3D11 device-capability calls are logged (`CheckFormatSupport`) |
| `D3D9Bridge.cs` | Direct3D 9 games, via a packaged `d3d9.dll` |
| `X86Direct3D9.cs` | The 32-bit side of the same thing, for a 32-bit game's D3D9 calls |
| `SurfaceBridge.cs` | Mirrors a game's frame to a console surface without waiting on the GPU |
| `FakeSwapChain.cs` | A DXGI swap chain backed by a CPU-visible texture |
| `FakeOutput.cs` | A DXGI output object reporting the console's real display modes |
| `DxgiDescriptions.cs` | Fills in the swap-chain description structures a game queries |
| `FrameMirror.cs` | Copies each finished frame from GPU to CPU memory and into a `WriteableBitmap`, so it can be shown inside the app's own XAML tree |

## Audio

| File | What it does |
| --- | --- |
| `AudioBridge.cs` | A fake COM enumerator chain that hands back the console's real audio client |
| `XAudio27Route.cs` | Routes `CoCreateInstance` for XAudio 2.7's CLSID to a packaged XAudio 2.7 implementation |

## Input

| File | What it does |
| --- | --- |
| `PadBridge.cs` | The Xbox controller as XInput, the way a PC game expects; also sets the `SDL_JOYSTICK_*` environment hints so SDL2 games read the pad through here instead of around it |
| `PointerBridge.cs` | The virtual mouse pointer, driven by the right stick in PC mode; also the merged key state (physical pad + Device Portal input) other bridges read |
| `RawInputBridge.cs` | Fake raw mouse/keyboard input, backed by the controller |
| `HidBridge.cs` | Lists the controller as a HID device, for a game that discovers input that way |
| `DirectInputStub.cs` | An empty but well-formed DirectInput 8 interface, enough to satisfy initialization checks |
| `KeyboardMessages.cs` | Translates controller buttons into Windows scan codes and key-transition bits |
| `PointerPosition.cs` | Sub-pixel pointer motion, clamped to the screen |
| `ControllerMode.cs` | The PC-mode/native-controller-mode switch (held View+Menu) |

## Files and storage

| File | What it does |
| --- | --- |
| `CrtFiles.cs` | Routes the C runtime's file calls (`fopen`, `_open`, `stat`...) through `CreateFileFromAppW`, so they work on a USB drive from a packaged app |
| `UsbFiles.cs` | The fast path: a Win32 `HANDLE` straight from the removable-storage folder handle (~9 ms) instead of the file broker (~220 ms) |
| `UserFolders.cs` | Maps `Documents`/`Saved Games`/`AppData`/etc. to the app's own `LocalState\profile`, so a game's saves land somewhere real |
| `FileWatch.cs` | Logs file opens, reads, mappings and failures — the diagnostic used to chase Brawlhalla's missing application descriptor |

## Diagnostics

| File | What it does |
| --- | --- |
| `NativeProbe.cs` | Runs a game and reports how far it got and which imports were missing — the main measurement tool |
| `StackSampler.cs` | Samples every guest thread's stack (`SuspendThread`/`GetThreadContext`) to diagnose a hang |
| `FaultWatch.cs` | A vectored exception handler that records the crash code, faulting address, and nearby stack words |
| `SuspendWatch.cs` | Stops a game's own thread-suspension calls from freezing the console's own UI thread along with it |
| `Recorder.cs` | Encodes the mirrored frames to MP4 with the console's hardware H.264 encoder, armed by `record.txt` |
| `RedistProbe.cs` | Checks whether the DirectX-redistributable shim DLLs (`native/directx-redist/`) are present, to decide whether to route to them |

## COM and process control

| File | What it does |
| --- | --- |
| `ComProxy.cs` | Builds a COM vtable that intercepts only the methods it needs to and forwards the rest untouched — what every graphics/audio bridge above is built on |
| `ComStubs.cs` | Truthful refusals for COM classes nobody implements (`REGDB_E_CLASSNOTREG`) instead of a null object a game might dereference |
| `ProcessStubs.cs` | Refuses `CreateProcess`/`ShellExecute` — a sideloaded app cannot spawn children, and pretending otherwise would just crash later |
| `TimerStubs.cs` | A real multimedia timer backed by the system clock, not a no-op |
| `SspiStub.cs` | A well-formed SSPI function table that declines every operation (curl's start-up checks for the table's existence, not that it succeeds) |
| `PlainAnswers.cs` | Real answers for the odds and ends: HID, IME, version resources |
| `ShellPath.cs` | `SHLWAPI` path/string functions the console does not expose |

## `Steam/` — the Steamworks a game gets instead of the real client

| File | What it does |
| --- | --- |
| `SteamBridge.cs` | The flat Steamworks API (`SteamAPI_*`), backed by the signed-in account |
| `SteamClassic.cs` | The classic C++ vtable interfaces older games link against directly (MSVC x64 ABI, `CSteamID`-by-value quirks and all) |
| `Proto.cs` | A minimal protobuf codec — just enough for the one-shot messages Steam's own protocol uses here |
| `SteamCm.cs` | Connects to a Steam CM server over TLS/WebSocket to authenticate and fetch manifests |
| `SteamDepot.cs` | Parses depot manifests and chunk metadata |
| `SteamDownload.cs` | Downloads chunks to console (or USB) storage, resuming an interrupted download and verifying against the manifest |
| `SteamStats.cs` | Playtime and achievements over the same connection |
| `SteamVdf.cs` | Parses Valve's KeyValues (VDF) format, used by PICS app config |
