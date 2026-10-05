# Proton/Wine parity plan

Target: **57 of 114 library entries** download, open and run on Xbox without
manual workarounds. A Present counter, title screen or successful import resolution
is not a gameplay pass. The catalog's tier counts are planning cohorts, not unlock
counts, and overlap must not be added together.

## Reference baseline (2026-10-05)

History-bearing reference clones live outside this project in
`/tmp/nativra-parity-ref`. Wine has 200 commits of history; the other initial
clones have 100. Deepen individual histories before treating a shallow boundary
as the origin of a behavior. No upstream implementation has been vendored.

| Project | Inspected revision | Relevant source |
| --- | --- | --- |
| [Valve Wine](https://github.com/ValveSoftware/wine) | `dc26e61847081a1b5cb0733dc30feba6ee575482` | `dlls/{ntdll,kernelbase,setupapi,cfgmgr32,xinput1_3,dinput,winebus,mf,xaudio2_7}` and DLL `.spec` exports |
| [Proton](https://github.com/ValveSoftware/Proton) | `5b89db940e0ebe3a137a6009a3589232fe084c09` | `proton`, `steam_helper/steam.c`, `lsteamclient`, `vrclient` |
| [DXVK](https://github.com/doitsujin/dxvk) | `f20f363191c019cbf0d792e4e8f660d46ebecb6c` | `src/d3d9/d3d9_adapter.cpp`, `src/util/config/config.cpp`, `src/d3d9/d3d9_caps.h` |
| [vkd3d-proton](https://github.com/HansKristian-Work/vkd3d-proton) | `b206eb6680fb92a64ae57bfccc7454a138f76887` | `libs/vkd3d`, D3D12 tests and application workarounds |
| [FAudio](https://github.com/FNA-XNA/FAudio) | `6839b88e304a046ae1a609ff14a085371e02a4e0` | `src/FAudio.c`, `src/F3DAudio.c` |
| [vkd3d](https://codeberg.org/vkd3d/vkd3d) | `11551ec9e744ea6de3120bb43c6ffe7f1f6c2bdd` | `libs/vkd3d-shader`, `tests/vkd3d_api.c` |
| [wine-mono](https://github.com/wine-mono/wine-mono) | `294d8d927f5cc059d30ce94ceffa7cb4ce8cc468` | runtime/FNA packaging and submodule inventory |
| [wine-gecko](https://gitlab.winehq.org/wine/wine-gecko) | `6911b9d825f1a6e31dcfab5b6f0e587a01395e87` | Mozilla/Wine browser integration; 100-commit clone |

Gecko was acquired from WineHQ after the guessed GitHub mirrors failed. Its
browser integration is not interchangeable with Chromium/NW.js.

History examples that guide the work: Wine `c000d95` normalizes device paths for
DirectInput's cache; `f0181da` avoids reopening the current directory; `288e4ab`
adds MF topology tracing. DXVK `8d252af` preserves DOTPRODUCT3 color inputs and
`3200bb1` fixes a D3D11 staging-buffer loop. Proton `fa493fa` removes gamedrive
workarounds: historical title overrides are not automatically valid defaults.
FAudio `75e5be6` guards Doppler calculation near zero distance. These are research
leads, not claims that their equivalent has been implemented here.

## Layer matrix

Paths below are relative to this repository. P0 means shared baseline or directly
observed blocker; P1 means a next cohort; P2 means substantial new infrastructure.

| Layer | Nativra evidence | Gap / next contract | Priority and affected catalog cohort |
| --- | --- | --- | --- |
| kernel32/kernelbase/ntdll memory, heap, SEH, threads | `x86/Nativra.X86/Loader/GuestKernel.{Memory,Threads,Seh,Kernel32}.cs`; `GuestHeap.cs`; x64 `uwp/Kiosk/Native/{SystemImports,ThreadTls,ProcessStubs}.cs` | x86 implements a guest kernel; x64 mixes host APIs and overrides. Audit success stubs, section mapping, wait/exception behavior and unsupported imports against Wine tests. Linux wineserver patches cannot be copied directly. | P0: all Windows entries; X86 explicitly blocks 23 B games |
| user32/win32u windows/messages | `WindowStubs.cs`, `WindowMessages.cs`, `KeyboardMessages.cs`; guest `GuestKernel.User32*.cs` | Hooks, focus and message lifecycle remain partial; Rewired logs hook failure. Window creation alone is not input validation. | P0: 52 A/A? plus the x86 cohort |
| XInput/DirectInput/HID/SDL | `PadBridge.MakeHandlers`, `HidBridge.Install`, `DirectInputStub.cs`, `RawInputBridge.cs`; `native/directx-redist/dinput8` | XInput keystroke/audio discovery and x64 SetupAPI/cfgmgr32 identity/traversal implemented in the first two layers; pending console validation. x86 SetupAPI currently enumerates no devices. Physical controller verification still required. | P0: controller-dependent portion of 52 A/A?; 31 Unity overall (9 inferred), not 31 proven fixes |
| D3D9 formats/caps/shaders | `native/directx-redist/d3d9/{d3d9_main,d3d9_format,dxso,d3d9_ff}.cpp`; `D3D9Bridge.cs`, `X86Direct3D9.cs` | Check advertised usages against actual resource paths; shader/fixed-function correctness and reset/state restoration. Compare DXVK format queries and regression tests. | P0: 17 B entries list D3D9, overlapping X86 |
| D3D10/11 | `GraphicsBridge.cs`, `FakeSwapChain.cs`, `SurfaceBridge.cs` | x64 startup and memory stability across engines; no general x86 D3D11 COM bridge. | P0: 52 A/A?; 3 catalog B entries list D3D11-32 (Among Us now has x64 runtime evidence) |
| D3D8 / D3D12 | no implemented translator in current native inventory | D3D8-to-9 and D3D12 are separate infrastructure. vkd3d-proton requires Vulkan; Xbox's existing D3D11 bridge cannot host it unchanged. | P1 D3D8: 3; P2 D3D12: 6, only 4 list it alone |
| D3DX9 / shader compiler | `native/directx-redist/d3dx9_43`, `d3dcompiler_43`; `X86ShaderCompiler.cs`; packaged compiler 47 | D3DX image/mesh/effect coverage and texture defaults. `D3DX_FROM_FILE` is already fixed in `9d5649c`; do not repeat the black-screen diagnosis. | P0: shared D3D9 cohort, 17; runtime compiler also used by x64 games |
| XAudio/X3DAudio/XAPO/DirectSound | `native/directx-redist/{xaudio2_7,x3daudio1_7,xapofx1_5}`; `AudioBridge.cs`; guest `GuestDirectSound.cs` | Compare voice lifecycle, callbacks, effects and spatial edge cases with FAudio. Successful creation is not proof of audio. | P1: API prevalence not counted by catalog; DirectSound measured in WAVESHAPER |
| MF / wmvcore / quartz | host import routing in `SystemImports.cs` and COM routing in `ComStubs.cs`; no full media pipeline in native inventory | Decoder/source resolver/topology/video texture integration. Wine's GStreamer/media conversion needs a different Xbox backend. Intro skipping is not parity. | P1: catalog does not count video dependencies; inventory before claiming a number |
| CRT / VC runtimes | `native/vc-redist/fetch.ps1`; guest `GuestKernel.Msvcrt*.cs`; `SystemImports.AppRuntimeName` | Align runtime version routing, C++ exceptions, locale, file semantics. Preserve shipped runtime choice when compatible. | P0: all native engine cohorts; no additive unlock count |
| Steam API, steamclient, tickets | `SteamBridge.cs`, `SteamClassic.cs`, `X86Steam.cs`; `GuestSteam.cs` | Real ownership-ticket retrieval and interface/callback parity. Proton lsteamclient bridges the genuine client; it does not invent ownership. Castle Crashers remains blocked. | P0: common Steam dependency; Source family 12 overall; ONLINE lists 6 |
| Unity Mono / desktop .NET | Cuphead title on build 495; `LoaderStubs.cs`, `ThreadTls.cs`; wine-mono reference | Unity's shipped Mono already executes. Input/JIT/loading stability must be distinguished from a missing runtime. Desktop CLR plus XNA/FNA for Terraria remains absent. | P0 Unity: 31 overall; P1 desktop NET: 1 explicit B entry |
| Vulkan/OpenGL | no backend found in native inventory | Host graphics API translation required; DXVK and vkd3d are behavioral references, not drop-in Xbox drivers. | P2: GL explicitly blocks 1; Vulkan-only count unknown |
| Saves/AppData/filesystem | `UserFolders.cs`, `ShellPath.cs`, `UsbFiles.cs`, `X86Files.cs`; guest `GuestKernel.Files.cs` | Validate persistent per-game paths, case behavior, sharing and restart/relaunch. Proton prefix layout differs from UWP storage. | P0: every game that saves; no catalog count |
| Registry | `RegistryBridge.cs` (x64), guest `GuestKernel.Libraries.cs` | Per-game typed persistence implemented; x64 awaiting Xbox validation. Enumeration, notifications, access rights and runtime/install probes remain incomplete. | P1: shared, count unknown |
| Locale/codepages | guest `GuestKernel.{Runtime,MsvcrtText}.cs`; host APIs | Buffer lengths, conversion failure/flags, locale names; do not apply Proton's per-title en-US override globally. | P1: shared, count unknown |
| Fonts/GDI | guest `GuestKernel.{Gdi,GdiText}.cs`, host import routing | Font discovery/metrics/text rendering contract; separate game-packaged fonts from system fonts. | P1: shared, count unknown |
| Steam helper / child processes / browser / VR | `ProcessStubs.cs`, `LoaderStubs.cs`; no general process host | Proton helper launch/URL routing assumes process support. Browser and VR need their own runtime/platform. Gecko alone does not unblock NW.js. | P2: LAUNCH 5, WEB 2; VR not counted |

## Delivery and evidence

1. Input discovery/export parity, tested independently of game titles.
2. D3D9 capability/resource contract and regression coverage, then kernel/import
   gaps grouped by API family from sweep reports.
3. Media/runtime/Steam contracts prioritized by observed cohorts.

Each implementation records its exact reference and tests in `.codex-report.md`.
Run the x86 suite before every commit. Build/deploy the branch through CI, and
hold the console lock for at most 15 minutes per bounded sweep. Existing sweep
classification says `renders` from Present counts; treat that only as a heartbeat
until screenshots and controller-driven gameplay confirm it. Never promote a
catalog prediction to a compatibility result.

The starting compatibility table has one Verified entry and four Playable entries;
several Playable notes still require input or long-session validation. **57/114
has not been demonstrated.** Existing uncommitted console evidence predates this
parity work and must retain its build numbers.

## Reproducible guest import audit

Run `~/.dotnet/dotnet run --project tools/wine-import-audit -- <wine-checkout> <output.tsv>`.
This instantiates the real guest kernel registrations and compares named public
x86 exports from Wine's DLL specifications. It records Wine's export kind
(including `stub`) separately. Registered is not synonymous with implemented
correctly. Host-only bridges, packaged native DLLs, ordinals and private exports
are outside this inventory, so these numbers are not whole-app coverage scores.

At the pinned Wine revision, after the heap layer: kernel32 573/1320, ntdll
17/1209, user32 461/828, gdi32 182/532, advapi32 126/587, guest SetupAPI 13/617.
The x64 cfgmgr32 implementation is deliberately absent from this guest-only
inventory. This audit identified the shared ntdll heap entry-point gap: six
Rtl heap exports now share allocation state with kernel32, including zeroed
reallocation and in-place failure semantics. Heap exception-generation flags and
independent heap arenas remain incomplete.

Proton's `lsteamclient/cppISteamAppTicket_STEAMAPPTICKET_INTERFACE_VERSION001.cpp`
forwards ownership-ticket data to the actual client interface. Its
`steam_helper/steam.c` initializes the Steam/VR registry through their bridge
modules. These are not substitutes for Nativra's missing signed-ticket source.
The inspected `proton` script's forced NVAPI, address-space and atiadlxx choices
are conditional; none is a safe global Xbox default merely because Proton has it.
