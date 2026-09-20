# Kiosk

An Xbox Series X|S in developer mode, running PC games natively.

Not streaming. Not a remote desktop. The game is downloaded by the console
itself, from the player's own Steam account, and executed on the console's own
processor — which is an x86-64 running Windows NT, and has been all along.

## Why this is possible at all

An Xbox is not a different computer from a PC. It is the same architecture
running the same kernel, with a different set of rules about what a program is
allowed to do. A developer-mode console will run a packaged app, and a packaged
app can map an ordinary Windows binary into itself: sections, relocations,
imports, exception tables, thread local storage. Of the 931 functions the first
game tried to import, 753 resolved to the console's own Windows.

The remaining 178 are the project. They are not translation — nothing is being
emulated — they are the handful of libraries a packaged app does not have
loaded, answered by hand. The window system is the big one, because a console
has no windows.

## What works today

- **Sign in to Steam** on the console, by QR code, natively.
- **The full library**: owned games and family-shared ones, with the account's
  real collections, filters and search.
- **Downloading on the console**, from Steam's own content servers: the client
  protocol, depot keys, manifests, chunks, and all three container formats.
- **A game detail screen** with artwork, playtime, and an install dialog that
  asks where to put it.
- **Loading a game's binaries** — the engine of a commercial Unity game maps,
  relocates, resolves and runs inside the app.
- **Remote control of the console** from a terminal, for testing.

## What does not work yet

The picture. Everything up to it does: the engine starts, finishes its own
initialisation, pumps its message loop, asks Direct3D for a device and gets one
at feature level 11_0, and creates its swap chain through this bridge. The app
can also build a device, a swap chain and a frame entirely by itself and put it
on the console's screen, which proves the path exists.

What fails is the last step — handing the game's finished swap chain to the
surface it should appear on. A packaged application reaches that surface through
a COM interface the native compiler will not generate a stub for, and the
console refuses to hand its own window to an application built out of XAML.
Both routes are written; neither has landed yet.

Sound has a bridge — the device a packaged app is not allowed to create is
built by hand, with the console's real audio engine behind it — and it is
switched off until the picture works, because a half-working audio path stops
an engine mid-start.

See [docs/ISSUES.md](docs/ISSUES.md) — the rest of the work is partitioned, and
a lot of it needs no console.

## Using it

    bun src/xbdev.ts connect      # point at the console once
    bun src/xbdev.ts install <package files>
    bun src/xbdev.ts steam games  # what the account owns
    bun src/xbdev.ts launch kiosk

Builds happen on a hosted Windows runner; no Windows machine is needed locally.

## Licence

Free to use, change and share. Not to sell — see [LICENSE](LICENSE) and
[NOTICE.md](NOTICE.md).

This project does not bypass game licensing and will not accept code that does.
