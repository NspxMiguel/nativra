# Intake: magnet, .torrent and Telegram, in

Three ways to hand the app a game without a PC in the loop: a magnet link, a
`.torrent` file, or a Telegram reference. All three end up in the same place —
a filed game under `E:\Games\<system>\` on the console (see
`docs/VEREDITO.md`, section 7) — through the same queue.

## The flow

```
addMagnet(uri)          ┐
addTorrentFile(path)    ├─► Job(queued) ─► downloading ─► processing ─► ┬─► filed
addTelegram(ref)        ┘                                              │
                                                                        └─► needs-system ─► chooseSystem() ─► filed
```

1. One of the three `add*` functions in `src/intake.ts` validates the input
   locally (a magnet URI looks like one, a `.torrent` file's first byte is
   `d` — a bencoded dictionary has to start with one) and hands it to a
   `Downloader`.
2. The `Downloader` is what actually moves bytes, and it is meant to do that
   **on the console**, not on whatever machine runs this queue. `start`
   returns a handle; a caller — a CLI loop, a future UI — calls `pump(jobId)`
   (or `pumpAll()`) on whatever interval it wants a fresh progress bar, and
   the job's `bytesDone`/`bytesTotal`/`speedBps`/`etaSeconds` update from
   there. Nothing in this module runs on a hidden timer.
3. When the transport reports the download complete, the queue unpacks
   anything that looks like an archive (zip, rar, 7z, a lone gzip) and runs
   `detect()` from `src/formats.ts` on what comes out.
4. A **high-confidence** match files itself automatically, by calling the
   same `Downloader`'s `file(handle, system)`. Anything less certain —
   medium confidence, low confidence, or nothing recognised at all — stops at
   `needs-system` and waits.
5. The picker screen calls `chooseSystem(jobId, system)` with whatever the
   player picked (pre-selectable from `job.suggestedSystem`, when `detect()`
   had a guess just not a confident one). That calls `file()` and closes the
   job.

## The seams

**`Downloader`** (`src/intake.ts`) is the boundary between the job queue and
wherever a transfer actually happens: `start`, `poll`, `file`, `cancel`. Two
implementations exist today — `TorrentDownloader` and `TelegramDownloader` —
and both are stubs: every method throws `NotImplementedError` with a message
saying so. That is deliberate. The queue logic, the job model, the
unpack-then-detect pipeline, the confidence gate — all of that is real and
tested; only the piece that would need to talk to a torrent client or the
Telegram API running somewhere is missing, because that piece does not exist
yet. A real implementation gets dropped in by constructing
`new IntakeQueue({ downloaders: { torrent, telegram } })` — nothing in
`IntakeQueue` itself has to change. Whatever eventually implements the real
torrent and Telegram sides most naturally lives alongside the console-facing
code the app already has (the Kiosk app, `uwp/`), since that is the process
positioned to talk to the console's own network stack and storage; this
module does not assume a specific channel back to it (Device Portal
extension, a companion listener, or something else) because that decision
belongs to whoever builds that side.

`file(handle, system)` is a second seam worth calling out on its own: only
the transport implementation knows where its own staging area put the
finished download, so it is the one responsible for moving it into
`E:\Games\<system>\` and reporting back where things ended up
(`job.filedPaths`). The queue never touches that path itself.

**`detect()`** (`src/formats.ts`) is a pure function over a file path: no
knowledge of jobs, downloads, or the console. It reads a file's extension
and, where the extension alone would be a guess (`.iso`, `.bin`, `.3ds`,
`.cue`, …), its header bytes, and returns `{ system, confidence, reason }` or
`null`. `intake.ts` is the only thing that turns a `confidence` into a
decision (`"high"` auto-files, anything else waits for a human), which keeps
that policy in one place instead of scattered across every format check.

## What this does and does not know

The mechanism does not assume `outputPaths` are paths on the machine running
this code — only that they are paths `detect()` can read from wherever it
runs. For a transport that lives in the same place as this queue, that is
just a local path. For one that lives on the console, the real
implementation either needs to expose those files somewhere this process can
read them, or run identification remotely and hand back a result instead of
a path. Either is a legitimate way to satisfy the seam; this module does not
pick one, because that is an architecture decision for whoever builds the
real transport, not a detail of the queue.

## No sources, no trackers, no index

This project ships no tracker list, no index or search site, and no channel
name or content URL of any kind. `addMagnet`, `addTorrentFile` and
`addTelegram` take exactly what the person using the app already has in
hand — a magnet link they got somewhere else, a `.torrent` file they already
downloaded, a Telegram message or file they already have — and do nothing
with it beyond queuing the transfer and identifying what comes out. Finding
something to download is entirely on the user; this is the plumbing after
that point, not before it.
