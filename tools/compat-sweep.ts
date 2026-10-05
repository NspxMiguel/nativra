// Compatibility sweep: starts each installed game on the console through the
// app's autoplay marker, lets it run, collects the reports and classifies the
// result, so a whole library can be tested unattended.
//
//   bun tools/compat-sweep.ts [--seconds 120] [--download] <appid...>
//
// With --download a game that is not on the console yet is fetched first
// through the app's own autodownload marker. Hold the console lock
// (tools/console-lock.sh) while this runs; reports land in .cycle/sweep/<appid>/
// and one line per game in .cycle/sweep/summary.md.

import { $ } from "bun";
import { join, resolve } from "node:path";
import { mkdir, readFile, appendFile, writeFile, rm } from "node:fs/promises";

const ROOT = resolve(import.meta.dir, "..");
const XBDEV = join(ROOT, "src/xbdev.ts");
const OUT = join(ROOT, ".cycle/sweep");

export type Verdict = {
  appid: number;
  status: "renders" | "starts" | "stops" | "no-report";
  frames: number;
  fps: number;
  detail: string;
};

/** Reads the app's own reports (native-pulse.txt, native-probe.txt, crash-log.txt) into a verdict. */
export function classify(
  appid: number,
  pulse: string,
  probe: string,
  crash: string,
): Verdict {
  const frameLine = /^frames=(\d+) at ([\d.]+) a second/m.exec(pulse);
  const pick = (re: RegExp, text: string) => re.exec(text)?.[1]?.trim();
  // A 32-bit game draws through the D3D9 bridge, not the 64-bit pulse: its
  // frames are the Present calls the probe counts, over the seconds it ran.
  const presents = Number(
    pick(/^x86\.com (\d+)x IDirect3DDevice9::Present$/m, probe) ?? 0,
  );
  const seconds = Number(pick(/^x86\.seconds=([\d.]+)/m, probe) ?? 0);
  const is32 = /^x86\.(image|run)=/m.test(probe);
  const frames = is32 ? presents : frameLine ? Number(frameLine[1]) : 0;
  const fps = is32
    ? seconds > 0
      ? Math.round((presents / seconds) * 10) / 10
      : 0
    : frameLine
      ? Number(frameLine[2])
      : 0;
  const x86 =
    pick(/^x86\.run=(.*)$/m, probe) ??
    pick(/^x86\.init=(?!returned)(.*)$/m, probe);
  const failed = pick(/^x86\.failed=(.{0,160})/m, probe);
  const chain = pick(/chain=(.*)$/m, pulse);
  const crashed = crash.trim().split("\n").filter(Boolean).pop();
  if (!pulse && !probe)
    return {
      appid,
      status: "no-report",
      frames,
      fps,
      detail: "no report pulled",
    };
  if (frames > 120 && fps >= 10)
    return {
      appid,
      status: "renders",
      frames,
      fps,
      detail: `chain=${chain ?? "?"}`,
    };
  const reason =
    pick(/^probe failed: (.*)$/m, probe) ??
    failed ??
    x86 ??
    crashed ??
    (chain ? `chain=${chain}` : "no frame");
  if (/window=0x[1-9A-F]/i.test(probe) || frames > 0)
    return { appid, status: "starts", frames, fps, detail: reason };
  return { appid, status: "stops", frames, fps, detail: reason };
}

async function xbdev(
  args: string[],
  cwd = ROOT,
): Promise<{ ok: boolean; out: string }> {
  const lock = await readFile("/tmp/xbox-console.lock", "utf8").catch(() => "");
  const [owner, acquired] = lock.trim().split(/\s+/);
  if (
    owner !== "opus-x86" ||
    !Number.isFinite(Number(acquired)) ||
    Date.now() - Number(acquired) * 1000 >= 14 * 60_000
  )
    throw new Error(
      "Console lease missing or nearing 15 minutes; release and reacquire before continuing",
    );
  const run = await $`bun ${XBDEV} ${args}`.cwd(cwd).quiet().nothrow();
  const out = run.stdout.toString() + run.stderr.toString();
  if (/0x8004090a/i.test(out)) {
    await writeFile(
      join(OUT, "signed-out.txt"),
      `${new Date().toISOString()} ${args[0]}: 0x8004090a\n`,
    );
    throw new Error("Xbox signed out (0x8004090a); console work stopped");
  }
  return { ok: run.exitCode === 0, out };
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function waitStopped(limitMs = 60_000): Promise<void> {
  const until = Date.now() + limitMs;
  while (Date.now() < until) {
    if (!(await xbdev(["running", "Kiosk"])).ok) return;
    await sleep(2000);
  }
}

async function pushMarker(name: string, text: string): Promise<void> {
  const file = join(OUT, name);
  await writeFile(file, text);
  await xbdev(["push", "Kiosk", file, "LocalState"]);
}

async function download(appid: number, limitMs: number): Promise<boolean> {
  const dir = join(OUT, String(appid));
  await mkdir(dir, { recursive: true });
  await xbdev(["stop", "Kiosk"]);
  await waitStopped();
  await xbdev(["rm", "Kiosk", "autoplay.txt", "--dir", "LocalState"]);
  await pushMarker("autodownload.txt", String(appid));
  const launch = await xbdev(["launch", "Kiosk"]);
  await writeFile(join(dir, "launch.txt"), launch.out);
  if (!launch.ok)
    throw new Error(`${appid}: Kiosk launch failed; see launch.txt`);
  const until = Date.now() + limitMs;
  await sleep(20_000);
  while (Date.now() < until) {
    const dir = join(OUT, String(appid));
    await mkdir(dir, { recursive: true });
    const going = (
      await xbdev(["pull", "Kiosk", "downloading.txt", "LocalState"], dir)
    ).ok;
    if (!going) break;
    await sleep(30_000);
  }
  await rm(join(dir, "download-error.txt"), { force: true });
  await xbdev(["pull", "Kiosk", "download-error.txt", "LocalState"], dir);
  await xbdev(["rm", "Kiosk", "autodownload.txt", "--dir", "LocalState"]);
  await xbdev(["stop", "Kiosk"]);
  await waitStopped();
  return Date.now() < until;
}

async function test(appid: number, seconds: number): Promise<Verdict> {
  const dir = join(OUT, String(appid));
  await mkdir(dir, { recursive: true });
  await xbdev(["stop", "Kiosk"]);
  await waitStopped();
  // The console keeps the last run's reports; a game that writes none must not
  // inherit the previous game's frames.
  await xbdev([
    "rm",
    "Kiosk",
    "native-pulse.txt",
    "native-probe.txt",
    "unity.log",
    "x86-imports.txt",
    "x86-heartbeat.txt",
    "native-fault.txt",
    "--dir",
    "LocalState",
  ]);
  await pushMarker("autoplay.txt", String(appid));
  const launch = await xbdev(["launch", "Kiosk"]);
  await writeFile(join(dir, "launch.txt"), launch.out);
  if (!launch.ok)
    throw new Error(`${appid}: Kiosk launch failed; see launch.txt`);
  await sleep(seconds * 1000);
  // A pull that fails must not leave the previous game's report in place: it
  // was read back as this run's result.
  for (const file of [
    "native-pulse.txt",
    "native-probe.txt",
    "crash-log.txt",
    "x86-imports.txt",
    "x86-heartbeat.txt",
    "native-fault.txt",
    "unity.log",
  ]) {
    await rm(join(dir, file), { force: true });
    await xbdev(["pull", "Kiosk", file, "LocalState"], dir);
  }
  await xbdev(["shot", join(dir, "shot.png")]);
  await xbdev(["stop", "Kiosk"]);
  await waitStopped();
  await xbdev(["rm", "Kiosk", "autoplay.txt", "--dir", "LocalState"]);
  const read = (f: string) => readFile(join(dir, f), "utf8").catch(() => "");
  return classify(
    appid,
    await read("native-pulse.txt"),
    await read("native-probe.txt"),
    await read("crash-log.txt"),
  );
}

if (import.meta.main) {
  const args = process.argv.slice(2);
  const seconds = Number(args[args.indexOf("--seconds") + 1]) || 120;
  const fetch = args.includes("--download");
  const ids = args
    .filter((a, i) => /^\d+$/.test(a) && args[i - 1] !== "--seconds")
    .map(Number);
  if (ids.length === 0) {
    console.error(
      "usage: bun tools/compat-sweep.ts [--seconds 120] [--download] <appid...>",
    );
    process.exit(2);
  }
  await mkdir(OUT, { recursive: true });
  const deadline = Date.now() + 13 * 60_000;
  for (const appid of ids) {
    const available = deadline - Date.now() - (seconds + 60) * 1000;
    if (available < 30_000)
      throw new Error(
        "Batch time budget exhausted; reacquire the lock for remaining games",
      );
    if (fetch && !(await download(appid, available)))
      throw new Error(
        `${appid}: download incomplete; resume in the next lock window`,
      );
    const v = await test(appid, seconds);
    const line = `| ${v.appid} | ${v.status} | ${v.frames} | ${v.fps} | ${v.detail.replace(/\|/g, "/")} | ${new Date().toISOString()} |`;
    console.log(line);
    await appendFile(join(OUT, "summary.md"), line + "\n");
  }
}
