# Measured compatibility blockers

## October 10 layer fixes: console verification pending

The broad build 502 results below remain historical evidence. Build 511 later
rendered Cuphead and Super Meat Boy, and Hades progressed to a D3D11 swap chain.
These observations do not demonstrate gameplay or controller delivery.

| Layer | Concrete evidence | Branch correction | Remaining verification |
| --- | --- | --- | --- |
| Controller discovery and state | Build 526 Cuphead menu renders at 60 Presents/s; guarded injection leaves GetState entry/null/invalid/failure/button counters zero, HID lists 2/opens 0. Missing SetupDiGetDevicePropertyW exception is gone; USB index and XInput initialization errors remain. Super Meat Boy 526 injection produces 48 button reads with retained A mask 0x1000. | Shared x64/x86 XInput now merges portal triggers/stick keys with a physical pad, tracks per-slot state packets and reports active-state reads/changes/buttons. Guest import-dispatch tests verify injected state and output bounds. Unified device-property lookup exposes controller identity/class/hardware IDs/parent/container with typed, bounded outputs. SetupAPI class/interface/enumerator filters now avoid advertising the HID pad as a USB device; console validation pending. | Diagnose Cuphead failure before bridge entry; new bounded loader trace records symbol-resolution outcomes. Verify the discovery filters and x64 Unity button delivery; controller-driven gameplay remains open. Poll counts alone are insufficient. |
| Native import tracing / data ABI | October 8 Hades pulse: 1920x1080 chain, zero Presents, AV at EngineWin64s+0x3A2921. Static disassembly identifies a read of imported MSVCP140 std::cout from IAT RVA 0x4A28E0. | System export tracing now queries page protection and wraps only executable addresses. Data and unclassified addresses retain their original pointer. | Build 520 retest now shows the main menu: 4079 frames at 60.1 Presents/s; previous AV absent. Input and gameplay still unverified; renderer label is Microsoft Basic Render Driver. No Vulkan path observed. A subsequent launch stalled loading BasecampBugReporter.Native.dll before frames; retain both outcomes. |
| x86 image placement / growing heap | Build 520 Super Meat Boy: heap allocation of 397065836 bytes refused at image page 0x3B400000, then null-address ReadFile fault, zero Presents. | Preferred PE32 image bases now honor the same heap avoidance used for relocated bases; avoidance stores a page count rather than an end-page index. Regression loads a relocated image and grows the heap across its old preferred base. | Build 522 allocation/intro retest succeeds: 2183 Presents in latest heartbeat, Team Meat logo visible, 39 XInput reads in overlay. Build 526 exact A-state delivery proven by button-only counter/mask; gameplay remains unverified. Steam session still absent. |
| x86 SHLWAPI | Three historical PathCanonicalizeW failures below. | PathCanonicalizeA/W registered; drive/UNC roots, dot segments, literal forward slashes, in-place calls and MAX_PATH bounds tested. | Retest Poly Bridge, ANOIX and Kandidatos; record the next actual blocker. |
| x86 BCrypt | Two historical BCryptGenRandom failures below. | System RNG, RNG pseudo-handle and explicit RNG provider lifecycle implemented using cryptographic host RNG with bounded allocations. | Retest Leafless and Super-Patriota; record actual progress. |
| x86 WinRT / API sets | Pixel Strike import report lists Ro initialization/activation and HSTRING exports. | WinRT API sets route to combase; per-thread COM/WinRT apartment state and HSTRING lifetime implemented. Missing activation classes return REGDB_E_CLASSNOTREG with null outputs. | Retest Pixel Strike. Guest WinRT class factories remain absent; successful import resolution is not object support. |

Seraph build 526 also reaches its equipment/Start menu: 6795 frames at 60/s,
XInput polls present, no injected input test. The historical build 502 marshalling
error is absent in this fresh run (.cycle/sweep/1919460).

References for guest contracts:
[SetupDiGetClassDevsW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetclassdevsw),
[SetupDiGetDevicePropertyW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw),
[PathCanonicalizeW](https://learn.microsoft.com/en-us/windows/win32/api/shlwapi/nf-shlwapi-pathcanonicalizew),
[BCryptGenRandom](https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/nf-bcrypt-bcryptgenrandom),
[RoInitialize](https://learn.microsoft.com/en-us/windows/win32/api/roapi/nf-roapi-roinitialize),
[HSTRING creation](https://learn.microsoft.com/en-us/windows/win32/api/winstring/nf-winstring-windowscreatestring),
[fast-pass references](https://learn.microsoft.com/en-us/windows/win32/api/winstring/nf-winstring-windowscreatestringreference)
and [duplication](https://learn.microsoft.com/en-us/windows/win32/api/winstring/nf-winstring-windowsduplicatestring).
Wine behavior was inspected at the pinned revision in [PROTON-PARITY.md](PROTON-PARITY.md).
No game binaries or upstream implementation were committed.

## Historical broad sweep

Sweep dates: 2026-10-05–06. Installed build: 502. The newest public release was
495; Windows rejected its installation because 502 was already installed.
Counts below cover only newly measured games, not the planning estimates in
[LIBRARY.md](LIBRARY.md). A game is counted once per observed primary blocker.
Screenshots and raw reports remain local under `.cycle/sweep/<appid>/`.

| Rank | Blocker / layer | Games hit | Games | First concrete evidence |
| --- | --- | --- | --- | --- |
| 1 | UWP delegate marshalling / native loader | 26 | Seraph's Last Stand (1919460), Hades (1145360), Cuphead (268910), My Summer Car (516750), Poly Bridge 2 (1062160), 9 Kings (2784470), Vampire Survivors (1794680), Graveyard Keeper (599140), Bloons TD 6 (960090), Seen (1069740), Sprocket (1674170), Virtual Cottage (1369320), Stick Fight: The Game (674940), 100% Orange Juice (282800), Kandidatos Kart (1415140), BROTHER!!! - Hardcore Platformer (1286560), My dream setup (2200780), Undercroft Warriors (2547140), Rat Quest (2952470), Paragnosia (3017580), LivingForest (3027490), Lost in Anomaly (3312020), Movie Night (1868410), Banana Hell: Mountain of Madness (2068520), Depths Of Horror: Mushroom Day (1590840), Internet Cafe Simulator (1136160) | `probe failed: $BlockedFromReflection_1_5cd930df: EETypeRva:0x00031EE8 is missing delegate marshalling data` |
| 2 | x86 SHLWAPI path functions | 3 | Poly Bridge (367450), ANOIX (840590), Kandidatos (1395560) | `x86.run=missing import shlwapi.dll!PathCanonicalizeW` |
| 3 | x86 BCrypt random generation | 2 | 3D PUZZLE - Leafless (3651720), Super-Patriota Simulator (2089140) | `x86.run=missing import bcrypt.dll!BCryptGenRandom` |
| 4 | x86 WinRT initialization / API-set forwarding | 1 | Pixel Strike 3D (915320) | `x86.run=missing import kernel32.dll!RoInitialize` |

All 26 native-loader failure screenshots show the Nativra shelf with a game-start failure, not game
frames. Menu, gameplay and input were not reached. The diagnostic asks for
a `MarshalDelegate` directive in `rd.xml`; this sweep does not modify engine code.

Poly Bridge selects a 32-bit `polybridge.exe` and runs the JIT for 7.8 seconds;
ANOIX runs its 32-bit `Anoix.exe` for 0.6 seconds and Kandidatos runs for 8.4
seconds before the same missing SHLWAPI import. All three have no guest window, no D3D9 request, zero pad
reads and empty heartbeat COM calls; no menu/gameplay/render or input test was reached.

A pre-existing `download-error.txt` dated 2026-10-02 concerns POSTAL 2 (223470)
and disk exhaustion (`0x80070027`). It is stale and is not counted as a blocker
for any game in this sweep.

## Download-stage evidence (not counted as guest executions)

| Blocker / layer | Games hit | Game | Evidence |
| --- | --- | --- | --- |
| Console storage exhaustion; app moving downloads | 3 | Little Nightmares (424840), LEGO Jurassic World (352400), Amazing Frog ? 2 (1559680) | Screenshots: `console full, moving to USB` (Little Nightmares, after 67%) and `console full, moving…` (LEGO, after the last observed 48%). Amazing Frog failed at 35%, moving 6.4 GB to Console: `System.Exception: The disk is full. (Exception from HRESULT: 0x80070027)` at 04:35:45 UTC. |
| Steam depot chunk decryption (recovered on retry) | 1 | 3D PUZZLE - Leafless (3651720) | `System.Security.Cryptography.CryptographicException: The input data is not a complete block.` Fresh error at 2026-10-06 03:11:22 UTC, 10%, `_Mac.app\Contents\Resources\Data\sharedassets1.assets.resS`. |
| Steam job timeout (recovered on retry) | 1 | Movie Night (1868410) | `System.TimeoutException: Steam did not answer job:2`, 0%, 2026-10-06 03:14:09 UTC. Download succeeded on retry; primary runtime blocker is native delegate marshalling. |

Leafless initially failed chunk decryption and then an incomplete launch found no
executable (`0x80070002`); its screenshot still showed download at 17%. The app
automatically resumed the partial installation and recovered. A fresh 90-second
retest selected a 32-bit Unity executable and ran the JIT for 8.9 seconds before
`bcrypt.dll!BCryptGenRandom` stopped it. No window, D3D9 request or pad reads;
heartbeat COM calls empty. The decryption issue is a recovered installation
attempt, and the current primary blocker is BCrypt (counted once above).

Super-Patriota Simulator hits the same BCrypt import after 0.6 seconds of x86
execution, also without a window, D3D9 request, pad reads or COM calls.

Pixel Strike 3D selects its 32-bit Unity executable and stops after 0.6 seconds
at `kernel32.dll!RoInitialize`; no window, D3D9 request, pad reads or COM calls.
The import report also lists unserved WinRT API-set imports, but the runtime
line above is the first observed blocker.

The controller now clears old download errors before a new attempt and rejects
errors matching the requested appid before launch (nine controller tests pass).

Five old Nativra dumps (builds 453, 458 and 475) occupied 1,781,314,531 bytes.
All were archived on the Mac with size/header verification and SHA-256 hashes
before deletion from the console. A subsequent dump index was empty. This is
storage maintenance, not evidence of a new crash in any swept game.

The downloader restarts a partial file from its first chunk; only complete files
are skipped (`uwp/Kiosk/Steam/SteamDownload.cs`). The sweep now preserves an
active download across console-lock windows rather than repeatedly stopping it.

After removing only the two partial installations created by this sweep through
the app's `forget.txt`, a fresh drive probe (2026-10-06 00:07 UTC) reported
11,194,597,376 bytes free on the USB drive. The catalog lists LEGO's download as
15.3 GB. Both cases remain explicitly untested at the guest-runtime stage.

After batch 18, the app removed 13 completed sweep-owned installations through
`forget.txt`; no baseline game was selected. A fresh probe at 2026-10-06
02:29:59 UTC again reported 11,194,597,376 bytes free on USB, and internal
games returned to the original three IDs. All diagnostic reports and screenshots
were retained on the Mac.

After batch 25, Internet Cafe Simulator hit a fresh disk-full error at 64%
(`System.Exception: The disk is full. (Exception from HRESULT: 0x80070027)`,
2026-10-06 03:57:24 UTC) while moving 177 MB to USB. This attempt is archived;
the download recovered on retry and the subsequent 90-second attempt failed
native delegate marshalling. The app removed only the 14 completed
sweep-owned installations added since the previous cleanup through `forget.txt`.
The marker disappeared and internal storage returned to its three baseline game
IDs; the partial Internet Cafe installation was preserved and resumed. A fresh
crash-dump index was empty. No baseline game or other data was selected.

A fresh drive probe at 2026-10-06 04:12:18 UTC reported 6,993,346,560 bytes
free on USB with the recovered Internet Cafe installation present. The drive
probe marker was removed after collecting this measurement.

After the Amazing Frog capacity failure, the app removed that sweep partial and
the completed Internet Cafe and Pixel Strike sweep installations. The fresh
2026-10-06 04:38:24 UTC probe returned USB free space to 11,194,597,376 bytes,
exactly the original baseline. Internal games also returned to the baseline IDs;
no active download or forget marker remained, and the crash-dump index was empty.
