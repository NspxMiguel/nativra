// Getting a game onto the console without a PC in the loop: hand this a
// magnet link, a .torrent file, or a Telegram reference, and it tracks the
// download through to a filed game.
//
// The transfer itself is meant to run on the console, not here — this module
// only queues the request and polls for progress. Torrent and Telegram are
// each behind the same `Downloader` seam (start/poll/file/cancel), and both
// implementations below are stubs that say so rather than pretend to work:
// the console-side pieces they will eventually drive do not exist yet (see
// docs/INTAKE.md). Nothing about *where* to get a magnet, a .torrent file or
// a Telegram reference from lives here — this project supplies the plumbing,
// the user supplies the link.
//
// Once a download reports done, whatever came out is unpacked (if it is an
// archive) and run through `detect()` from ./formats. A high-confidence match
// files itself automatically; anything less honest — including "nothing
// matched at all" — waits in `needs-system` for the picker screen to call
// `chooseSystem`.

import { mkdir, readdir } from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import {
  archiveKindOf,
  detect,
  type ArchiveKind,
  type Confidence,
  type DetectResult,
} from "./formats";

// -------------------------------------------------------------- the source

export type DownloadSource =
  | { kind: "magnet"; uri: string }
  | { kind: "torrent-file"; path: string }
  | { kind: "telegram"; ref: string };

export type Transport = "torrent" | "telegram";

function transportFor(source: DownloadSource): Transport {
  return source.kind === "telegram" ? "telegram" : "torrent";
}

// ------------------------------------------------------------ the job model

export type JobState =
  | "queued" // accepted, about to ask the downloader to start
  | "downloading" // the transport is pulling bytes
  | "processing" // download finished: unpacking (if needed) and identifying it
  | "needs-system" // detect() could not tell; waiting on chooseSystem()
  | "filed" // system known (detected or chosen) and the game is in place
  | "failed"
  | "cancelled";

export type Job = {
  id: string;
  source: DownloadSource;
  state: JobState;

  // Progress, for a UI to draw a bar from. bytesTotal and speedBps are
  // whatever the transport last reported; both start unknown/zero.
  bytesDone: number;
  bytesTotal: number | null;
  speedBps: number;
  etaSeconds: number | null;

  createdAt: number;
  updatedAt: number;

  /** Where the transport says the finished download landed, before unpacking. */
  downloadedPaths?: string[];
  /** The files actually run through detect(), after unpacking any archive. */
  outputPaths?: string[];
  /** Where the transport put things once filed under a system. */
  filedPaths?: string[];

  /** What detect() found, if anything — kept even when confidence was too low to auto-file. */
  detected?: DetectResult | null;
  /** A low/medium-confidence guess, offered as a pre-selected hint on the picker screen. */
  suggestedSystem?: string;
  /** The system the job was actually filed under, once state is "filed". */
  system?: string;

  error?: string;
};

// ----------------------------------------------------------- the transport

export type DownloaderHandle = string;

export type DownloaderStatus = {
  state: "downloading" | "completed" | "failed";
  bytesDone: number;
  bytesTotal: number | null;
  speedBps: number;
  /** Paths as the transport's own host sees them. Set once state is "completed". */
  outputPaths?: string[];
  error?: string;
};

/**
 * The seam between this job queue and wherever a download actually happens.
 * `start`/`poll`/`cancel` cover the transfer; `file` is the separate step of
 * moving a finished, identified download into `E:\Games\<system>\` on the
 * console (see docs/VEREDITO.md section 7 for that layout) — only the
 * transport implementation knows where its own staging area put things, so
 * it is the one that has to move them.
 *
 * A real implementation is expected to be dropped in — via `IntakeQueue`'s
 * constructor — without touching anything in this file.
 */
export interface Downloader {
  readonly transport: Transport;
  start(source: DownloadSource): Promise<DownloaderHandle>;
  poll(handle: DownloaderHandle): Promise<DownloaderStatus>;
  file(handle: DownloaderHandle, system: string): Promise<string[]>;
  cancel(handle: DownloaderHandle): Promise<void>;
}

export class NotImplementedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "NotImplementedError";
  }
}

const NOT_WIRED =
  "not wired to a console yet — this is the seam, not the implementation (see docs/INTAKE.md)";

/**
 * Placeholder for a torrent client running on the console: handing it a
 * magnet link or a .torrent file, and reporting progress and the eventual
 * file placement back. Every method throws until that side exists.
 */
export class TorrentDownloader implements Downloader {
  readonly transport = "torrent" as const;

  async start(_source: DownloadSource): Promise<DownloaderHandle> {
    throw new NotImplementedError(`torrent transport ${NOT_WIRED}`);
  }

  async poll(_handle: DownloaderHandle): Promise<DownloaderStatus> {
    throw new NotImplementedError(`torrent transport ${NOT_WIRED}`);
  }

  async file(_handle: DownloaderHandle, _system: string): Promise<string[]> {
    throw new NotImplementedError(`torrent transport ${NOT_WIRED}`);
  }

  async cancel(_handle: DownloaderHandle): Promise<void> {
    throw new NotImplementedError(`torrent transport ${NOT_WIRED}`);
  }
}

/** Same seam as TorrentDownloader, for a Telegram-sourced file or link. */
export class TelegramDownloader implements Downloader {
  readonly transport = "telegram" as const;

  async start(_source: DownloadSource): Promise<DownloaderHandle> {
    throw new NotImplementedError(`telegram transport ${NOT_WIRED}`);
  }

  async poll(_handle: DownloaderHandle): Promise<DownloaderStatus> {
    throw new NotImplementedError(`telegram transport ${NOT_WIRED}`);
  }

  async file(_handle: DownloaderHandle, _system: string): Promise<string[]> {
    throw new NotImplementedError(`telegram transport ${NOT_WIRED}`);
  }

  async cancel(_handle: DownloaderHandle): Promise<void> {
    throw new NotImplementedError(`telegram transport ${NOT_WIRED}`);
  }
}

// ------------------------------------------------------------------ unpack

const UNZIP = "/usr/bin/unzip"; // matches src/icons.ts — always present on macOS

async function extractArchive(kind: ArchiveKind, archivePath: string, outDir: string): Promise<void> {
  await mkdir(outDir, { recursive: true });

  if (kind === "gzip") {
    // A lone .gz decompresses in memory rather than via a subprocess: fine
    // for a single ROM-sized file, not meant for anything huge.
    const compressed = new Uint8Array(await Bun.file(archivePath).arrayBuffer());
    const decompressed = Bun.gunzipSync(compressed);
    const outName = basename(archivePath).replace(/\.gz$/i, "") || "decompressed";
    await Bun.write(join(outDir, outName), decompressed);
    return;
  }

  if (kind === "zip") {
    const proc = Bun.spawn([UNZIP, "-o", "-q", archivePath, "-d", outDir], {
      stdout: "ignore",
      stderr: "pipe",
    });
    const exitCode = await proc.exited;
    if (exitCode !== 0) {
      throw new Error(`extracting ${basename(archivePath)} failed (unzip exit ${exitCode}): ${await new Response(proc.stderr).text()}`);
    }
    return;
  }

  if (kind === "rar") {
    const unrar = Bun.which("unrar");
    const unar = unrar ? null : Bun.which("unar");
    const bin = unrar ?? unar;
    if (!bin) throw new Error("no rar extractor on PATH (looked for unrar, unar)");
    const args = unrar ? ["x", "-y", archivePath, `${outDir}/`] : ["-o", outDir, archivePath];
    const proc = Bun.spawn([bin, ...args], { stdout: "ignore", stderr: "pipe" });
    const exitCode = await proc.exited;
    if (exitCode !== 0) {
      throw new Error(`extracting ${basename(archivePath)} failed (${basename(bin)} exit ${exitCode}): ${await new Response(proc.stderr).text()}`);
    }
    return;
  }

  // sevenzip
  const bin = Bun.which("7z") ?? Bun.which("7zz") ?? Bun.which("7za");
  if (!bin) throw new Error("no 7z extractor on PATH (looked for 7z, 7zz, 7za)");
  const proc = Bun.spawn([bin, "x", "-y", `-o${outDir}`, archivePath], { stdout: "ignore", stderr: "pipe" });
  const exitCode = await proc.exited;
  if (exitCode !== 0) {
    throw new Error(`extracting ${basename(archivePath)} failed (7z exit ${exitCode}): ${await new Response(proc.stderr).text()}`);
  }
}

async function listFilesRecursively(dir: string): Promise<string[]> {
  const found: string[] = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) found.push(...(await listFilesRecursively(full)));
    else found.push(full);
  }
  return found;
}

/**
 * Expands every archive in `paths` into its contents (one level — an archive
 * of archives is left to whoever calls detect() next) and returns the flat
 * list of files that are actually worth running detect() on. A path that is
 * not an archive passes through unchanged.
 */
async function unpack(paths: string[]): Promise<string[]> {
  const results: string[] = [];
  for (const path of paths) {
    const kind = await archiveKindOf(path).catch(() => null);
    if (!kind) {
      results.push(path);
      continue;
    }
    const outDir = join(dirname(path), `.unpacked-${basename(path)}`);
    await extractArchive(kind, path, outDir);
    results.push(...(await listFilesRecursively(outDir)));
  }
  return results;
}

// -------------------------------------------------------------- the queue

const CONFIDENCE_RANK: Record<Confidence, number> = { low: 0, medium: 1, high: 2 };

function etaFrom(status: DownloaderStatus): number | null {
  if (status.bytesTotal === null || status.speedBps <= 0) return null;
  const remaining = status.bytesTotal - status.bytesDone;
  return remaining > 0 ? Math.round(remaining / status.speedBps) : 0;
}

export type IntakeOptions = {
  downloaders?: Partial<Record<Transport, Downloader>>;
};

/**
 * The queue of intake jobs. Construct one with real `Downloader`s once the
 * console-side transports exist; without any, it falls back to the stubs
 * above, so every job it creates ends up `failed` with a clear reason —
 * that is the mechanism working correctly with nothing plugged into it yet.
 *
 * Nothing here runs on a hidden timer. A caller — a CLI loop, a future UI —
 * drives progress by calling `pump`/`pumpAll` on whatever interval it wants
 * a fresh bar, which keeps this module pure enough to test without a real
 * download in flight.
 */
export class IntakeQueue {
  private readonly jobs = new Map<string, Job>();
  private readonly handles = new Map<string, DownloaderHandle>();
  private readonly downloaders: Record<Transport, Downloader>;

  constructor(options: IntakeOptions = {}) {
    this.downloaders = {
      torrent: options.downloaders?.torrent ?? new TorrentDownloader(),
      telegram: options.downloaders?.telegram ?? new TelegramDownloader(),
    };
  }

  list(): Job[] {
    return [...this.jobs.values()];
  }

  get(jobId: string): Job | undefined {
    return this.jobs.get(jobId);
  }

  async addMagnet(uri: string): Promise<Job> {
    if (!/^magnet:\?/i.test(uri)) {
      throw new Error("not a magnet URI (expected it to start with magnet:?)");
    }
    return this.enqueue({ kind: "magnet", uri });
  }

  async addTorrentFile(path: string): Promise<Job> {
    const file = Bun.file(path);
    if (!(await file.exists())) throw new Error(`no such file: ${path}`);
    const first = new Uint8Array(await file.slice(0, 1).arrayBuffer());
    // Every bencoded .torrent metainfo file is a dictionary, and a bencoded
    // dictionary always opens with 'd' — a cheap, real sanity check before
    // handing it to a transport (BitTorrent's bencode spec).
    if (first[0] !== 0x64) {
      throw new Error(`${path} does not look like a .torrent file (bencoded dictionaries start with "d")`);
    }
    return this.enqueue({ kind: "torrent-file", path });
  }

  async addTelegram(ref: string): Promise<Job> {
    const trimmed = ref.trim();
    if (!trimmed) throw new Error("empty Telegram reference");
    return this.enqueue({ kind: "telegram", ref: trimmed });
  }

  private async enqueue(source: DownloadSource): Promise<Job> {
    const now = Date.now();
    const job: Job = {
      id: crypto.randomUUID(),
      source,
      state: "queued",
      bytesDone: 0,
      bytesTotal: null,
      speedBps: 0,
      etaSeconds: null,
      createdAt: now,
      updatedAt: now,
    };
    this.jobs.set(job.id, job);

    const downloader = this.downloaders[transportFor(source)];
    try {
      const handle = await downloader.start(source);
      this.handles.set(job.id, handle);
      job.state = "downloading";
    } catch (err) {
      job.state = "failed";
      job.error = err instanceof Error ? err.message : String(err);
    }
    job.updatedAt = Date.now();
    return job;
  }

  /** Advances one job by a single poll step. Safe to call on a job that is not downloading — it just answers the current state. */
  async pump(jobId: string): Promise<Job> {
    const job = this.jobs.get(jobId);
    if (!job) throw new Error(`no such intake job: ${jobId}`);
    if (job.state !== "downloading") return job;

    const downloader = this.downloaders[transportFor(job.source)];
    const handle = this.handles.get(jobId);
    if (!handle) {
      job.state = "failed";
      job.error = "lost the downloader handle for this job";
      job.updatedAt = Date.now();
      return job;
    }

    const status = await downloader.poll(handle);
    job.bytesDone = status.bytesDone;
    job.bytesTotal = status.bytesTotal;
    job.speedBps = status.speedBps;
    job.etaSeconds = etaFrom(status);
    job.updatedAt = Date.now();

    if (status.state === "failed") {
      job.state = "failed";
      job.error = status.error ?? "download failed";
      return job;
    }
    if (status.state === "completed") {
      job.state = "processing";
      job.downloadedPaths = status.outputPaths ?? [];
      await this.finish(job, downloader, handle);
    }
    return job;
  }

  /** Advances every job currently downloading — the convenient call for a single poll loop driving the whole queue. */
  async pumpAll(): Promise<Job[]> {
    const active = this.list().filter((j) => j.state === "downloading");
    return Promise.all(active.map((j) => this.pump(j.id)));
  }

  private async finish(job: Job, downloader: Downloader, handle: DownloaderHandle): Promise<void> {
    try {
      const files = await unpack(job.downloadedPaths ?? []);
      job.outputPaths = files;

      let best: DetectResult | null = null;
      for (const file of files) {
        const result = await detect(file);
        if (result && (!best || CONFIDENCE_RANK[result.confidence] > CONFIDENCE_RANK[best.confidence])) {
          best = result;
        }
        if (best?.confidence === "high") break; // good enough — stop looking
      }
      job.detected = best;

      if (best && best.confidence === "high") {
        await this.fileJob(job, downloader, handle, best.system);
      } else {
        job.suggestedSystem = best?.system;
        job.state = "needs-system";
      }
    } catch (err) {
      job.state = "failed";
      job.error = err instanceof Error ? err.message : String(err);
    }
    job.updatedAt = Date.now();
  }

  private async fileJob(job: Job, downloader: Downloader, handle: DownloaderHandle, system: string): Promise<void> {
    job.filedPaths = await downloader.file(handle, system);
    job.system = system;
    job.state = "filed";
    job.updatedAt = Date.now();
  }

  /** What the picker screen calls once someone says which console a `needs-system` job is for. Files the game and closes the job. */
  async chooseSystem(jobId: string, system: string): Promise<Job> {
    const job = this.jobs.get(jobId);
    if (!job) throw new Error(`no such intake job: ${jobId}`);
    if (job.state !== "needs-system") {
      throw new Error(`job ${jobId} is not waiting on a system choice (state: ${job.state})`);
    }
    const downloader = this.downloaders[transportFor(job.source)];
    const handle = this.handles.get(jobId);
    if (!handle) throw new Error(`job ${jobId} has no downloader handle left to file from`);
    await this.fileJob(job, downloader, handle, system);
    return job;
  }

  async cancel(jobId: string): Promise<void> {
    const job = this.jobs.get(jobId);
    if (!job) return;
    const handle = this.handles.get(jobId);
    if (handle && (job.state === "queued" || job.state === "downloading")) {
      const downloader = this.downloaders[transportFor(job.source)];
      await downloader.cancel(handle).catch(() => {}); // best effort — the job is going away either way
    }
    job.state = "cancelled";
    job.updatedAt = Date.now();
  }
}

// ------------------------------------------------------- convenience API

// A ready-to-use queue for callers who do not need their own Downloader
// wiring. With no real transports plugged in, every job it creates fails
// immediately with NotImplementedError's message — see the class doc above.
const defaultQueue = new IntakeQueue();

export function addMagnet(uri: string): Promise<Job> {
  return defaultQueue.addMagnet(uri);
}

export function addTorrentFile(path: string): Promise<Job> {
  return defaultQueue.addTorrentFile(path);
}

export function addTelegram(ref: string): Promise<Job> {
  return defaultQueue.addTelegram(ref);
}

export function chooseSystem(jobId: string, system: string): Promise<Job> {
  return defaultQueue.chooseSystem(jobId, system);
}

export function getJob(jobId: string): Job | undefined {
  return defaultQueue.get(jobId);
}

export function listJobs(): Job[] {
  return defaultQueue.list();
}

export function pump(jobId: string): Promise<Job> {
  return defaultQueue.pump(jobId);
}

export function pumpAll(): Promise<Job[]> {
  return defaultQueue.pumpAll();
}

export function cancelJob(jobId: string): Promise<void> {
  return defaultQueue.cancel(jobId);
}
