# Measured compatibility blockers

Sweep date: 2026-10-05. Installed build: 502. The newest public release was
495; Windows rejected its installation because 502 was already installed.
Counts below cover only newly measured games, not the planning estimates in
[LIBRARY.md](LIBRARY.md). A game is counted once per observed primary blocker.
Screenshots and raw reports remain local under `.cycle/sweep/<appid>/`.

| Rank | Blocker / layer | Games hit | Games | First concrete evidence |
| --- | --- | --- | --- | --- |
| 1 | UWP delegate marshalling / native loader | 3 | Seraph's Last Stand (1919460), Hades (1145360), Cuphead (268910) | `probe failed: $BlockedFromReflection_1_5cd930df: EETypeRva:0x00031EE8 is missing delegate marshalling data` |

The Seraph, Hades and Cuphead screenshots show the Nativra shelf with a game-start failure, not game
frames. Menu, gameplay and input were not reached. The diagnostic asks for
a `MarshalDelegate` directive in `rd.xml`; this sweep does not modify engine code.

A pre-existing `download-error.txt` dated 2026-10-02 concerns POSTAL 2 (223470)
and disk exhaustion (`0x80070027`). It is stale and is not counted as a blocker
for any game in this sweep.
