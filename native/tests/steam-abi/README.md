# Synthetic Steam ABI caller

`build.cmd` uses MSVC x64 to build a test DLL. The Windows x86 harness workflow
sets `NATIVRA_STEAM_ABI_FIXTURE` and runs `SteamInterfaceContractTests`, passing
production reverse-P/Invoke pointers into this caller. It invokes every
SteamUser016 slot plus flat factories, the bare classic accessor and the flat
64-bit SteamID return. It checks output canaries, failure results and the MSVC
hidden class-return convention. No Steam SDK headers, runtime, account or game
binaries are required or redistributed.

The host application is x64. Its x86 guest path is an interpreter, not a native
x86 host interface: `GuestSteamTests` executes synthetic x86 machine code and
checks ECX, argument cleanup and hidden return buffers for SteamUser016–019.
The native x64 caller does not certify a Windows x86 reverse-P/Invoke ABI.

Reference cross-check (no declarations copied): Proton
`bc896217ffcd67f892f952b010f898fe7bd0f0a1`,
`lsteamclient/cppISteamUser_SteamUser016.cpp` through `SteamUser019.cpp`,
`cppISteamClient_SteamClient012.cpp`. The SDK license in
`lsteamclient/LICENSE` restricts source redistribution; SDK headers remain
outside this repository. The caller's types and implementations are independent
synthetic definitions, not a vendored SDK. This fixture proves only the tested
contracts; it does not identify Dokimon's faulting slot or certify gameplay.
