#!/usr/bin/env bun
// Read-only, bounded console diagnostics. Private evidence stays under .cycle.
import { chmod, mkdir, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { DevicePortal } from "../src/portal";

const seconds = Number(Bun.argv[2] ?? 60);
if (!Number.isFinite(seconds) || seconds < 2 || seconds > 300) {
  throw new Error("Duration must be between 2 and 300 seconds");
}
const stored = Bun.spawnSync([
  "security",
  "find-generic-password",
  "-s",
  "claude-autonomous:XBDEV",
  "-w",
]);
if (stored.exitCode !== 0) throw new Error("Xbox credentials are unavailable");
const config = JSON.parse(stored.stdout.toString());
if (process.env.XBDEV_HOST) config.host = process.env.XBDEV_HOST;
const portal = new DevicePortal(config);
const packages = await portal.packages();
const pkg = packages.find((item) =>
  item.PackageFullName.startsWith("NSPX.Kiosk_"),
);
if (!pkg) throw new Error("Nativra is not installed");
const directory = resolve(
  import.meta.dir,
  "../.cycle/diagnostics",
  new Date().toISOString().replace(/[:.]/g, "-"),
);
await mkdir(directory, { recursive: true, mode: 0o700 });
await chmod(directory, 0o700);
async function save(name: string, data: string | Uint8Array) {
  await writeFile(resolve(directory, name), data, { mode: 0o600 });
}
const samples: unknown[] = [];
const failures: string[] = [];
const started = performance.now();
const revision = Bun.spawnSync(["git", "rev-parse", "HEAD"], {
  cwd: resolve(import.meta.dir, ".."),
})
  .stdout.toString()
  .trim();

// These records may contain machine metadata. Never publish the directory.
for (const [name, read] of [
  ["os.json", () => portal.osInfo()],
  ["console.json", () => portal.xboxInfo()],
] as const) {
  try {
    await save(name, JSON.stringify(await read(), null, 2));
  } catch {
    failures.push(name);
  }
}
while (performance.now() - started < seconds * 1000) {
  const at = new Date().toISOString();
  try {
    samples.push({
      at,
      elapsedMs: Math.round(performance.now() - started),
      system: await portal.systemPerf(),
    });
  } catch {
    failures.push(`systemPerf:${at}`);
  }
  try {
    samples.push({
      at: new Date().toISOString(),
      elapsedMs: Math.round(performance.now() - started),
      pulse: new TextDecoder().decode(
        await portal.pullFile(
          pkg.PackageFullName,
          "native-pulse.txt",
          "LocalState",
        ),
      ),
    });
  } catch {
    failures.push(`pulse:${at}`);
  }
  // No concurrent samples or additional console-side logging.
  await Bun.sleep(2000);
}
await save("system-samples.json", JSON.stringify(samples, null, 2));
for (const [file, folder] of [
  ["native-probe.txt", "LocalState"],
  ["native-pulse.txt", "LocalState"],
  ["Player.log", "AC/OddGiant/Seraph's Last Stand"],
]) {
  try {
    await save(
      file!,
      await portal.pullFile(pkg.PackageFullName, file!, folder!),
    );
  } catch {
    failures.push(file!);
  }
}
try {
  await save("screen.png", await portal.screenshot());
} catch {
  failures.push("screen.png");
}
try {
  const response = await portal.request(
    "GET",
    "/api/debug/dump/usermode/dumps",
  );
  if (!response.ok) throw new Error("Crash index unavailable");
  const data = (await response.json()) as {
    CrashDumps?: Array<{
      PackageFullName: string;
      FileName: string;
      FileDate: string;
      FileSize: number;
    }>;
  };
  await save(
    "crash-index.json",
    JSON.stringify(
      (data.CrashDumps ?? []).filter(
        (dump) => dump.PackageFullName === pkg.PackageFullName,
      ),
      null,
      2,
    ),
  );
} catch {
  failures.push("crash-index.json");
}
await save(
  "session.json",
  JSON.stringify(
    {
      package: pkg.PackageFullName,
      collectorRevision: revision,
      // This is an operator label, not a claim derived from the installed binary.
      installedBuildLabel: Bun.argv[3] ?? null,
      startedAt: new Date(
        Date.now() - (performance.now() - started),
      ).toISOString(),
      durationSeconds: Math.round((performance.now() - started) / 1000),
      sampleIntervalSeconds: 2,
      samples: samples.length,
      failures,
      scope:
        "System metrics include other console processes; not game-only GPU/CPU measurements.",
      privacy:
        "Private evidence. Review and redact before sharing. No credentials are serialized.",
    },
    null,
    2,
  ),
);
console.log(
  `Saved ${samples.length} diagnostic records; ${failures.length} unavailable sources.`,
);
console.log(directory);
