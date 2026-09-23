# Nativra

> [!WARNING]
> **AINDA NÃO ESTÁ PRONTO PARA UTILIZAÇÃO. EM FASE DE TESTES E DESENVOLVIMENTO.**
>
> O Nativra é feito por **uma pessoa só**, que também tem outros compromissos.
> É um projeto de hobby, disponibilizado de graça, sem uma equipe por trás.
> Estou investindo meu próprio dinheiro em diversas ferramentas de IA, meu
> tempo e muito esforço para tirar essa ideia do papel — quebrando a cabeça,
> errando, testando e tentando de novo.
>
> Ver um jogo abrir pela primeira vez é uma conquista, **não significa que o
> app esteja pronto**. Não há prazo prometido, garantia de compatibilidade ou
> suporte imediato. Peço paciência e respeito por esse trabalho.
>
> — **NSPXMIGUEL**

> **Not ready for use — testing and development only.** This is a one-person,
> self-funded hobby project, shared for free alongside other commitments.
> AI tools cost real money; implementation and console testing take substantial
> personal time and effort. The first game opening is a milestone, not a
> finished product. There is no promised deadline, compatibility guarantee or
> immediate support. Please be patient and respectful.

An experimental native PC-game host for Xbox Series X|S in official Developer Mode.

**Experimental pre-alpha · proof of concept — not ready for everyday play.**
Created by **NSPXMIGUEL**.

[First milestone](docs/progress/2026-09-23.md) ·
[Releases](https://github.com/NspxMiguel/nativra/releases) ·
[Contributing](docs/CONTRIBUTING.md) · [License](LICENSE)

![Seraph's Last Stand menu running on Xbox Series X](docs/progress/seraph-menu-build174.png)

Real console capture, build 174. Menu rendering is confirmed; stable gameplay
and online features are not. The screenshot is not generated artwork.

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

**Experimental, not a finished PC-game compatibility layer.** Seraph's Last
Stand's actual main menu has rendered on Xbox Series X. This is not yet proof
of a stable, playable match. See the [console milestone and original
screenshot](docs/progress/2026-09-23.md).

The measured presentation path was capped at 20 updates per second, independently
of the game's render loop. Removing that cap and launching a selected Steam
game from the app are implemented but awaiting console validation. Native window
message dispatch still needs stability testing. Sound is not working in the
measured run (FMOD falls back to silent output); SteamAPI initialization fails.
Multiplayer, achievements, friend invitations and LEGO Jurassic World are not
validated. No license or authentication checks are bypassed.

See [docs/ISSUES.md](docs/ISSUES.md) — the rest of the work is partitioned, and
a lot of it needs no console.

## Using it

    bun src/xbdev.ts connect      # point at the console once
    bun src/xbdev.ts install <package files>
    bun src/xbdev.ts steam games  # what the account owns
    bun src/xbdev.ts launch kiosk

Builds happen on a hosted Windows runner; no Windows machine is needed locally.

Use official Xbox Developer Mode and your own licensed games. No game files,
BIOS, keys or Steam sessions are supplied. Download the pre-alpha package from
Releases; its notes explain installation and limitations. Internal folder names
and the package identity still use `Kiosk` to preserve compatibility.

## Licence

Free to use, change and share. Not to sell — see [LICENSE](LICENSE) and
[NOTICE.md](NOTICE.md).

This project does not bypass game licensing and will not accept code that does.
