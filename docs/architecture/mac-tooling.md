# `src/` — the Mac-side tool (`xbdev`)

Part of [../ARCHITECTURE.md](../ARCHITECTURE.md). Everything from a Mac talks
to the console through its Device Portal (WDP): install packages, launch and
stop apps, push/pull files, take screenshots, send controller input — plus a
from-scratch Steam client for downloading games straight from Valve's own
servers.

## Main CLI

| File | What it does |
| --- | --- |
| `xbdev.ts` | The entry point, ~30 subcommands (also under Portuguese aliases: `conectar`, `abrir`, `fechar`, ...): `find`/`connect`/`status` (locate and remember the console), `apps`/`install`/`uninstall`, `launch`/`stop`, `shot` (screenshot), `push`/`pull` (file transfer), `bios`/`emulators`/`setup-retroarch`, `gamemode`, `sync` (push the installed-app list + catalogue to the console), `verify` (package integrity), `press` (controller input), `record`/`recordings` (drive the in-app recorder), `push-game` (mirror a game folder in without Steam), `steam` (hands off to the Steam CLI) |
| `portal.ts` | The Device Portal client: HTTPS on :11443, self-signed certificate, CSRF token from a cookie, and every WDP call (packages, files, app control) as a typed method |
| `remote.ts` | `ConsoleRemote` — sends button presses and chords straight to the console, the channel `xbdev press` and Device Portal gamepad input both use |

## Support

| File | What it does |
| --- | --- |
| `util.ts` | Shared helpers: human-readable byte sizes, package-file detection, dependency ordering, console identity checks |
| `net.ts` | Finds consoles on the local network |
| `icons.ts` | Extracts/embeds game icons into packages |
| `i18n.ts` | The CLI's own pt/en strings (separate from the app's `Texts.cs`) |
| `formats.ts` | Package/codec format definitions |
| `bios.ts` | BIOS/key-file handling for emulators that need one |
| `emulators.ts` | Installing and configuring emulators (RetroArch and friends) from the catalogue |

## `src/steam/` — the from-scratch Steam client

| File | What it does |
| --- | --- |
| `cli.ts` | The `xbdev steam` subcommand: QR sign-in, session/token refresh, library queries, download orchestration |
| `download.ts` | Manifest → disk: fetches chunks from content servers, writes at the right offset, resumes a partial download |
| `depot.ts` | Manifest parsing, chunk fetching, decompression, filename decryption, content-server enumeration |
| `cm.ts` | `CmClient` — the connection to a Steam CM server over TLS/WebSocket: login, product-info requests, depot decryption keys, all in protobuf |
| `proto.ts` | A schema-less protobuf codec: read/write fields by number (varint/fixed64/bytes) |
| `vdf.ts` | Parses Valve's KeyValues (VDF) format, used by PICS app config and manifests |
| `collections.ts` | Steam collections and Family-shared app enumeration |

## Where a Steam download actually goes

`xbdev steam download <appid>` (or the app's own `AutoDownloadAsync`, which
writes `autodownload.txt` and the console picks it up) → `steam/cli.ts` loads
the saved session → `steam/cm.ts` connects and asks for the depot's
decryption key → `steam/depot.ts` reads the manifest → `steam/download.ts`
fetches each chunk from a content server, decrypts and decompresses it, and
writes it straight into the console's (or a USB drive's) storage over the
Device Portal — the game never has to be "installed" by Steam itself, because
there is no Steam client on the console; the account's own entitlement is
still what is checked, and no license or DRM check is bypassed.
