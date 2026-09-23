# Nativra v0.1.0-alpha.1 — first native game-menu milestone

An experimental milestone, **not a stable release** and not a claim of general
PC-game compatibility. Made by NSPXMIGUEL for official Xbox Developer Mode.

## The milestone

Seraph's Last Stand (Steam 1919460) displayed its actual main menu on Xbox Series X
using the console's CPU and Direct3D 11, without streaming. The original capture
and measured findings are in [the progress report](https://github.com/NspxMiguel/nativra/blob/main/docs/progress/2026-09-23.md).

The screenshot is from build 174. This package is the unchanged CI artifact
from build 179, source `e06af074f7544e046aa063cc33d18424261ff03c`.
Build 179 compiles successfully but its newer launch/performance changes have
not yet passed the complete console test. The screenshot does not validate them.

## Included

- Steam QR sign-in, owned/shared library, search and filters.
- New Downloaded filter with PT/EN text.
- Native PE loader, packaged Windows-owned TLS and Unity plugin loading.
- Back-buffer resize and pixel-format fixes that enabled the visible menu.
- Selected-game launch wiring and removal of the mirror's 20 FPS hard cap.
  Neither implies stable gameplay or a verified target frame rate yet.

## Known limitations

- Startup/input stability is still under investigation; crashes can occur.
- One native game load per app process; restart Nativra before another launch.
- Audio is not working in the measured run.
- SteamAPI initialization, multiplayer, achievements and invitations are not working/validated.
- LEGO Jurassic World is unverified. 4K gameplay and smooth 60 FPS are unverified.
- Current generated splash artwork was rejected after TV review; replacement is pending.
- No license bypass; only use games you own. No games, BIOS or keys are bundled.

## Install for development testing

1. Enable official Xbox Developer Mode and Device Portal on your console.
2. Back up Nativra's LocalState, including your private session and downloaded
   games. Uninstalling removes app data. Never attach these backups to issues.
3. Extract `kiosk-uwp.zip`. Use the `Kiosk_1.0.0.0_Test` bundle and its **x64**
   dependencies; JitProbe is a separate diagnostic app, not required to play.
4. If an older Nativra/Kiosk is installed, uninstall it after your backup.
   Install-over was unreliable on the tested console.
5. Install the bundle and dependencies through Device Portal, or use
   `bun src/xbdev.ts install <bundle> <x64 dependencies>` after `connect`.
6. Open Nativra and sign in yourself with Steam's QR flow. Never send anyone
   your password, QR challenge, Steam session file or refresh token.

Source and screenshots: PolyForm Noncommercial project terms; third-party game
art and bundled runtime components remain subject to their respective rights.
This project is not affiliated with Microsoft, Xbox or Valve.
