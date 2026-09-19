// Drives the real CLI binary against a stand-in Device Portal. Everything the
// console will exercise — auth, the CSRF handshake, multipart upload, install
// polling, launch, settings, screenshots, file transfer — runs here first.

import { test, expect, beforeAll, afterAll } from "bun:test";
import { startFakePortal, type FakePortalState } from "./fake-portal";
import { join, dirname } from "node:path";
import { mkdir } from "node:fs/promises";

const ROOT = dirname(import.meta.dir);
const CLI = join(ROOT, "src", "xbdev.ts");
const CERT_DIR = "/tmp/xbdev-certs";

let portal: ReturnType<typeof startFakePortal>;
let state: FakePortalState;
let env: Record<string, string>;

beforeAll(async () => {
  portal = startFakePortal({ certDir: CERT_DIR, port: 0 });
  state = portal.state;
  env = {
    ...process.env,
    XBDEV_HOST: "127.0.0.1",
    XBDEV_PORT: String(portal.port),
    XBDEV_USER: portal.user,
    XBDEV_PASS: portal.pass,
    XBDEV_LANG: "en",
  } as Record<string, string>;
});

afterAll(() => {
  portal.server.stop(true);
});

async function run(args: string[]) {
  const proc = Bun.spawn(["bun", CLI, ...args], {
    env,
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, stderr] = await Promise.all([
    new Response(proc.stdout).text(),
    new Response(proc.stderr).text(),
  ]);
  const code = await proc.exited;
  return { code, stdout, stderr, out: stdout + stderr };
}

test("status reaches the console and reports what it found", async () => {
  const result = await run(["status"]);
  expect(result.code).toBe(0);
  expect(result.out).toContain("XBOXONE-FAKE");
  expect(result.out).toContain("Xbox Series X");
  expect(result.out).toContain("installed apps: 1");
});

test("wrong credentials are reported as such, not as a dead console", async () => {
  const proc = Bun.spawn(["bun", CLI, "status"], {
    env: { ...env, XBDEV_PASS: "errado" },
    stdout: "pipe",
    stderr: "pipe",
  });
  const out = (await new Response(proc.stdout).text()) + (await new Response(proc.stderr).text());
  await proc.exited;
  expect(out).toContain("401");
});

test("apps lists what the console has installed", async () => {
  const result = await run(["apps"]);
  expect(result.code).toBe(0);
  expect(result.out).toContain("RetroArch");
  expect(result.out).toContain("1.0.0.0");
});

test("install uploads the package and waits for the console to finish", async () => {
  const before = state.packages.length;
  const fixture = join("/tmp", "xbdev-fixture", "Demo_1.0.0.0_x64.msixbundle");
  await mkdir(dirname(fixture), { recursive: true });
  await Bun.write(fixture, "not a real package, but a real upload");

  const result = await run(["install", fixture]);
  expect(result.code).toBe(0);
  expect(result.out).toContain("installed");

  // The upload carried the file under its own name, as the portal expects.
  expect(state.uploads.length).toBe(1);
  expect(state.uploads[0].files).toContain("Demo_1.0.0.0_x64.msixbundle");
  expect(state.uploads[0].endpoint).toBe("Demo_1.0.0.0_x64.msixbundle");
  expect(state.packages.length).toBe(before + 1);
}, 30000);

test("every write call carried the CSRF token the portal demands", () => {
  expect(state.csrfIssued).toBeGreaterThan(0);
  expect(state.rejectedWithoutCsrf).toBe(0);
});

test("launch and stop address the package the console named", async () => {
  const launch = await run(["launch", "retroarch"]);
  expect(launch.code).toBe(0);
  expect(state.launched).toContain("RetroArch_1.0.0.0_x64__8wekyb3d8bbwe");

  const stop = await run(["stop", "retroarch"]);
  expect(stop.code).toBe(0);
  expect(state.terminated).toContain("RetroArch_1.0.0.0_x64__8wekyb3d8bbwe");
});

test("launching something that is not installed fails loudly", async () => {
  const result = await run(["launch", "naoexiste"]);
  expect(result.code).not.toBe(0);
});

test("gamemode finds the resource setting and switches it", async () => {
  const result = await run(["gamemode"]);
  expect(result.code).toBe(0);
  const setting = state.settings.find((s) => s.Name === "DefaultUWPContentTypeToGame");
  expect(setting?.Value).toBe("true");
  expect(result.out).toContain("game mode on");
});

test("screenshot lands as a real file", async () => {
  const target = "/tmp/xbdev-fixture/tela.png";
  const result = await run(["shot", target]);
  expect(result.code).toBe(0);
  const file = Bun.file(target);
  expect(await file.exists()).toBe(true);
  // PNG magic number, so a truncated or HTML response would fail here.
  const head = new Uint8Array(await file.slice(0, 4).arrayBuffer());
  expect(Array.from(head)).toEqual([0x89, 0x50, 0x4e, 0x47]);
});

test("push sends a config file into an app's folder, and ls sees it", async () => {
  const config = join(ROOT, "configs", "retroarch.cfg");
  const push = await run(["push", "retroarch", config]);
  expect(push.code).toBe(0);
  expect(state.files["retroarch.cfg"]).toBeGreaterThan(0);

  const list = await run(["ls", "retroarch"]);
  expect(list.out).toContain("retroarch.cfg");
});

test("settings can be read and written by name", async () => {
  const read = await run(["settings"]);
  expect(read.out).toContain("TVResolution");

  const write = await run(["settings", "TVResolution", "4K"]);
  expect(write.code).toBe(0);
  expect(state.settings.find((s) => s.Name === "TVResolution")?.Value).toBe("4K");
});

// --- adversarial: prove the stub is actually strict, so the tests above mean
// something, and push the client through the paths that fail on a real console.

test("the stub refuses a write with no CSRF token, so the check above is real", async () => {
  const auth = `Basic ${Buffer.from(`${portal.user}:${portal.pass}`).toString("base64")}`;
  const res = await fetch(
    `https://127.0.0.1:${portal.port}/api/taskmanager/app?package=Zg==`,
    { method: "POST", headers: { Authorization: auth }, tls: { rejectUnauthorized: false } } as RequestInit,
  );
  expect(res.status).toBe(403);
});

test("the stub refuses anything unauthenticated", async () => {
  const res = await fetch(`https://127.0.0.1:${portal.port}/api/os/machinename`, {
    tls: { rejectUnauthorized: false },
  } as RequestInit);
  expect(res.status).toBe(401);
});

test("a package and its dependencies upload together, bundle first", async () => {
  const dir = "/tmp/xbdev-fixture/multi";
  await mkdir(dir, { recursive: true });
  const bundle = join(dir, "Game_2.0.0.0_x64.msixbundle");
  const vclibs = join(dir, "Microsoft.VCLibs.x64.14.00.appx");
  const cert = join(dir, "Game.cer");
  await Bun.write(bundle, "bundle");
  await Bun.write(vclibs, "framework");
  await Bun.write(cert, "certificate");

  const before = state.uploads.length;
  // Deliberately out of order: the CLI is what must sort them.
  const result = await run(["install", cert, vclibs, bundle]);
  expect(result.code).toBe(0);

  const upload = state.uploads[before];
  expect(upload.endpoint).toBe("Game_2.0.0.0_x64.msixbundle");
  expect(upload.files[0]).toBe("Game_2.0.0.0_x64.msixbundle");
  expect(upload.files).toContain("Microsoft.VCLibs.x64.14.00.appx");
  expect(upload.files[upload.files.length - 1]).toBe("Game.cer");
}, 30000);

test("a console that answers nothing is reported as offline, not as a crash", async () => {
  const proc = Bun.spawn(["bun", CLI, "status"], {
    // Nothing listens on this port.
    env: { ...env, XBDEV_PORT: "11" },
    stdout: "pipe",
    stderr: "pipe",
  });
  const out = (await new Response(proc.stdout).text()) + (await new Response(proc.stderr).text());
  const code = await proc.exited;
  expect(code).not.toBe(0);
  expect(out.toLowerCase()).toContain("did not answer");
});

test("install rejects a file that does not exist instead of hanging", async () => {
  const result = await run(["install", "/tmp/xbdev-fixture/nao-existe.msixbundle"]);
  expect(result.code).not.toBe(0);
});
