# Measured compatibility blockers

Sweep date: 2026-10-05. Installed build: 502. The newest public release was
495; Windows rejected its installation because 502 was already installed.
Counts below cover only newly measured games, not the planning estimates in
[LIBRARY.md](LIBRARY.md). A game is counted once per observed primary blocker.
Screenshots and raw reports remain local under `.cycle/sweep/<appid>/`.

| Rank | Blocker / layer | Games hit | Games | First concrete evidence |
| --- | --- | --- | --- | --- |
| 1 | UWP delegate marshalling / native loader | 16 | Seraph's Last Stand (1919460), Hades (1145360), Cuphead (268910), My Summer Car (516750), Poly Bridge 2 (1062160), 9 Kings (2784470), Vampire Survivors (1794680), Graveyard Keeper (599140), Bloons TD 6 (960090), Seen (1069740), Sprocket (1674170), Virtual Cottage (1369320), Stick Fight: The Game (674940), 100% Orange Juice (282800), Kandidatos Kart (1415140), BROTHER!!! - Hardcore Platformer (1286560) | `probe failed: $BlockedFromReflection_1_5cd930df: EETypeRva:0x00031EE8 is missing delegate marshalling data` |
| 2 | x86 SHLWAPI path functions | 3 | Poly Bridge (367450), ANOIX (840590), Kandidatos (1395560) | `x86.run=missing import shlwapi.dll!PathCanonicalizeW` |

All sixteen native-loader failure screenshots show the Nativra shelf with a game-start failure, not game
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
| Console storage exhaustion; app moving downloads | 2 | Little Nightmares (424840), LEGO Jurassic World (352400) | Screenshots: `console full, moving to USB` (Little Nightmares, after 67%) and `console full, moving…` (LEGO, after the last observed 48%). |

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
