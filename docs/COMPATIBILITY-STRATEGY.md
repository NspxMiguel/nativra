# Compatibility research and validation

Nativra implements reusable operating-system contracts around unmodified,
legitimately owned PC games. It is not a collection of per-game ports.
This is a research plan, not a claim that 1,000 games have been analyzed or tested.

## Study contracts, then test representative games

| Area | Contracts and dependencies to cover |
| --- | --- |
| Executables | PE architecture, imports and delay imports, export lookup, relocations, TLS, unwind, DLL initialization and runtime libraries |
| Operating system | Threads, synchronization, timers, exceptions, virtual memory, files, registry, environment and process lifecycle |
| Presentation | Window messages, DXGI ownership and resizing, D3D resource lifetime, feature queries and frame delivery |
| Audio | Endpoint discovery, COM identity, WASAPI activation, formats, buffers, clocks and device changes |
| Input | XInput, raw input, keyboard, pointer, focus, disconnection and controller-to-mouse profiles |
| Services | Networking, certificates, Steam authentication, game ownership, cloud saves, achievements, friends and invitations |
| Constraints | Memory budget, x86 versus x64, graphics API availability, external launchers, drivers and protected services |

Use documented Windows behavior and small reproducible contract tests, then
compare with the console. An import list is only a starting point: games also
load libraries dynamically, query COM interfaces, and take different paths while
playing. A successful return value without the promised output is not support.
Optional capabilities must be reported as absent when unimplemented.

For example, Windows documents a null result for unsuccessful
[GetProcAddress](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getprocaddress)
lookups. Returning a callable placeholder instead made Unity register nonexistent
rendering extensions in unrelated plugins. Similarly, audio endpoint discovery
requires an actual
[IMMEndpoint interface](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immendpoint),
not merely an object labeled as an audio device.

## Build a broad catalog without inventing compatibility

The long-term research target is a catalog of up to 1,000 popular PC games.
Every popularity list must record its source, date and metric: Steam concurrent
players are not a worldwide ranking across all stores.

Group entries by engine and runtime, executable architecture, graphics and audio
APIs, input system, store, launcher and anti-cheat requirements. Mark unknown
fields explicitly. Use public documentation for games not owned; inspect local
binaries only when legitimately available. Do not download unowned game depots
or publish proprietary binaries.

Select a small representative test set first: Unity IL2CPP/Mono, Unreal,
GameMaker, Godot and other native engines, as available in the owner's library.
Prioritize shared blockers by how many catalog entries depend on them, rather
than assuming that one working engine proves all its games work.

Current execution priority remains Seraph's Last Stand, followed by LEGO Jurassic
World and other lightweight owned games. WAVESHAPER's inspected depot is x86;
the current loader accepts x64 only. Catalog presence is not executable support.

## Anti-cheat and platform boundaries

UWP runs in an
[AppContainer with explicitly granted resources](https://learn.microsoft.com/en-us/windows/msix/msix-container).
Xbox UWP also has documented
[resource and architecture constraints](https://learn.microsoft.com/en-us/previous-versions/windows/uwp/xbox-apps/system-resource-allocation).
Native x86-64 instructions do not confer desktop Windows privileges, unrestricted
memory, or arbitrary kernel-driver installation.

Riot's [Vanguard documentation](https://www.riotgames.com/en/news/vanguard-on-demand)
describes a driver and platform security requirements; on-demand operation does
not eliminate that driver. Accordingly, PC VALORANT support is not a user-mode
API-shim task we can promise within official Xbox Dev Mode. Fortnite and other
protected titles need separate, vendor-supported compatibility assessments.
Their native Xbox editions do not establish compatibility for their PC builds.

Research here means documenting requirements and supported integration paths,
not disabling anti-cheat, fabricating attestation, bypassing ownership checks or
testing unsupported protected sessions with the owner's account. Online play
without such blockers still needs genuine networking and service validation.

## Evidence required for a playable label

Record exact app commit, game/depot version, console configuration and input type.
Verify repeated cold launches, gameplay, audio, pause/resume within the running
game, death/restart, saves, controller reconnection and a sustained session.
Test mouse/keyboard independently. Measure memory and frame-time behavior;
presentation counters alone are not measured TV frame rate.

Track Steam login/library/download separately from in-game SteamAPI, achievements,
friends, invitations, cloud saves and online sessions. A menu screenshot proves
only that menu. Remote input proves that path, not physical-controller operation.
Publish sanitized results with failures and untested items visible.
