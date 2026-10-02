# HL2:DM dedicated server as a 32-bit workload (2026-10-01)

## Why this program

With the console offline, the 32-bit layer still needed a real program to
run. Valve's dedicated servers download anonymously with `steamcmd` (no
licence, no account), ship their Windows build as plain 32-bit PE32 files,
and are the same Source engine binaries (tier0, vstdlib, engine,
materialsystem, vphysics, vgui2, steamclient…) that the owner's Source games
use — Half-Life 2, Portal, HL2:DM, HLDM:Source and Black Mesa are in the
library. A server has no renderer, so it exercises everything except
graphics.

```sh
# ~1 GB, Windows build, anonymous
steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir ~/nativra-work/hl2dm \
         +login anonymous +app_update 232370 +quit
```

(L4D2's server, 222860, also downloads anonymously but brings a 9.5 GB
content depot; `download_depot` for only its binaries needs a licence.)

## How it runs off the console

`x86/Nativra.X86.Run` runs any 32-bit exe through the layer and prints the
console's report (`x86.init`, `x86.run`, modules, imports per DLL, the guest
log, exceptions with module+offset and stack, unserved imports):

```sh
dotnet x86/Nativra.X86.Run/bin/Release/net8.0/Nativra.X86.Run.dll --budget 4000000000 \
  ~/nativra-work/hl2dm srcds.exe -console -game hl2mp -insecure +sv_lan 1 +maxplayers 2 +map dm_lockdown
```

`--trace FILE` writes every served call with its arguments and result,
`--imports FILE` every import's state. On an arm64 Mac the layer
interprets (the whole start-up takes ~25 s); with the x64 .NET runtime under
Rosetta (`~/.dotnet-x64/dotnet …`) the JIT runs too.

## What it found, in order

Each stop was a real, general bug or gap, not something specific to this
server:

1. **LoadLibrary never initialised the DLLs it brought along.** Mapping
   `dedicated.dll` mapped `tier0.dll`/`vstdlib.dll` as its imports but only
   ran `dedicated.dll`'s DllMain; tier0's allocator was never set up and a
   method was called on a null object. Windows initialises new dependencies
   first; now so does the layer (`AttachModulesFrom`).
2. **Missing kernel32**: `GetLogicalProcessorInformation` (tier0's CPU
   detection), then console-buffer calls, toolhelp thread snapshots,
   `OpenThread`, `CallNtPowerInformation`.
3. **ReadFile/WriteFile ignored OVERLAPPED** — an overlapped read went to the
   current position instead of its offset. Fixed, with
   `GetOverlappedResult`, events, and later completion ports.
4. **Import cycles**: FreeType and HarfBuzz import each other; the second
   one's imports were bound to placeholders. Now re-pointed once the DLL is
   mapped.
5. **LoadLibrary dropped the folder** and looked everything up by name in
   the game's root, so `hl2mp\bin\server.dll` was never found — and an
   unknown DLL got a fake handle, so the engine carried on with a null
   `CreateInterface`. Now: a Windows-like search path (the loading DLL's
   folder, the program's folder, the current directory, PATH), path-named
   loads, and NULL/`ERROR_MOD_NOT_FOUND` for a DLL that is nowhere.
6. **steamclient.dll's static initialisers** needed a little over the 5e7
   blocks a DllMain was allowed; the budget is now 1e9. Then
   `CreateIoCompletionPort` → completion ports implemented.
7. **Vectored exception handlers** (`AddVectoredExceptionHandler`) were
   missing; now they run before the frame chain.
8. psapi, real module paths in `GetModuleFileName`,
   `RtlNtStatusToDosError`, `GetLogicalProcessorInformationEx`.

9. **Real networking** (2906bb8): ws2_32/wsock32 on `System.Net.Sockets`
   — TCP/UDP, select/WSAPoll, WSAEventSelect, overlapped WSASend/WSARecv
   with completion ports, background DNS. On the way, wsock32's ordinals
   10–12 turned out to be in a different order from ws2_32's, so the
   server's `ioctlsocket(FIONBIO)` had been answered by `inet_ntoa`.
10. **System DLL handles now hold a real PE image** (a5e9c36): steamclient
    read ws2_32's headers through its handle, which pointed at nothing. Each
    stand-in handle now maps a small PE32 whose export table lists what the
    host serves — the shape DRM wrappers that walk export tables also need.
11. `iphlpapi!GetAdaptersAddresses` (one adapter, the host's IPv4).
12. **VirtualAlloc handed out the heap's own pages**: the guest heap maps its
    region only as it grows, so its unmapped part looked free; tier0's
    small-block heap then freed a pointer it did not own. Fixed.

13. **advapi32's security family was stubs that lied** (`EqualSid` always
    false, `IsValidSid` always true, `GetTokenInformation` unsupported — and
    steamclient logged "GetTokenInformation failed. GetLastError returned: 50"
    right after `GetLengthSid` was missing). There is now one local user for
    the whole guest, `S-1-5-21-…-1001`, in binary SID layout, and everything
    derives from it: the process token (`TokenUser`, `TokenGroups`,
    `TokenOwner`, `TokenPrimaryGroup`, privileges, `TokenIntegrityLevel`,
    elevation/type/session), `LookupAccountSid`/`LookupAccountName` round
    trips, string SIDs, `CheckTokenMembership` (the user is a member of
    Users and only deny-only in Administrators), `LookupPrivilegeValue`/
    `AdjustTokenPrivileges` (with `ERROR_NOT_ALL_ASSIGNED`), well-known SIDs,
    absolute security descriptors and ACLs.
14. `ole32!CLSIDFromString`, `IIDFromString`, `StringFromCLSID`/`IID`.
15. **setupapi device enumeration** (`SetupDiClassGuidsFromName`,
    `SetupDiGetClassDevs`, `EnumDeviceInfo`, `…Interfaces`, `…InstanceId`,
    `…RegistryProperty`, destroy): real handles and cursors over device sets
    that are empty, since the guest has no plug-and-play tree; enumeration
    ends with `ERROR_NO_MORE_ITEMS`, the answer of a machine without that
    class.
16. `kernel32!ReplaceFileA/W` (swap in a replacement, optional backup, the
    documented partial-failure codes).
17. **crypt32 certificate stores and chains** for steamclient's signature
    checks: memory/system stores, `CERT_CONTEXT` with a filled `CERT_INFO`
    (version, serial, issuer/subject, validity, public key, extensions) and
    reference counting, add dispositions, enumeration, and
    `CertGetCertificateChain` built by the host's X.509 engine and laid out
    as `CERT_CHAIN_CONTEXT`/`CERT_SIMPLE_CHAIN`/`CERT_CHAIN_ELEMENT` with the
    trust-status bits.
18. **`DisconnectEx`**: Valve's socket layer asserts when the extension
    function pointer is NULL ("DisconnectEx function pointer is NULL"). It
    is now served through `WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER)`; with
    `TF_REUSE_SOCKET` the guest socket gets a fresh host socket.

Result: the server goes through its entire start-up — console, Breakpad,
VPK mounting (real file I/O on the game's content), steamclient, the mod's
`server.dll` ("server.dll loaded for Half-Life 2 Deathmatch"), 8 threads —
opens its ports ("Network: IP …, mode MP, dedicated Yes, ports 27015 SV /
27005 CL"), initialises Steam for a LAN server ("SteamAPI_Init(): Loaded
local 'steamclient.dll' OK"), starts the Squirrel VM, loads its config and
activates the server ("SV_ActivateServer: setting tickrate to 66.7", "Server
is hibernating", "Assigned anonymous gameserver Steam ID", "VAC secure mode
disabled"). With a 1e9-block budget it then idles in its main loop with no
exception, and it is genuinely serving: it holds UDP 27015/27005/27020 and
TCP 27015, and an A2S_INFO query from the host (`\xff\xff\xff\xffTSource Engine
Query\0`, answered with a challenge, then again with it) returns "Half-Life 2
Deathmatch", map `dm_lockdown`, folder `hl2mp`, 0 of 2 players, dedicated,
Windows, version 10889068.

On the Windows runner it first stopped in Steam's `tier0_s.dll` just after
`SteamAPI_Init`: **`DuplicateHandle` copied handles as they were**, so a
thread handing itself to another as `DuplicateHandle(GetCurrentThread())`
gave it the pseudo-handle, and waiting on that pseudo-handle reported it
signalled. Steam's client judged a worker thread finished, freed its
`CThread` while it ran, and the thread faulted calling the freed object's
`OnExit`. On a Mac the worker happened to finish first. The pseudo-handle
now duplicates into a real handle to the calling thread. On the way,
`FindFirstFile` learned to list `.` and `..` like Windows.

CI (`.github/workflows/x86-workload.yml`) downloads the server anonymously
and runs this under the JIT on the Windows runner on every x86 change, until
the server logs "VAC secure mode disabled." (`--until`): about 60 s.

All fixes have unit tests; CI's Windows runner checks the JIT path.
