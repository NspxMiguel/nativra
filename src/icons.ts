// Pulls each app's own logo out of its package, so the console's home screen
// shows artwork instead of a coloured letter. An .appx is a zip and the
// manifest names the logo; a bundle carries the real .appx inside it.

import { mkdir, rm } from "node:fs/promises";
import { join } from "node:path";

const UNZIP = "/usr/bin/unzip";

async function entries(zip: string): Promise<string[]> {
  const proc = Bun.spawn([UNZIP, "-Z1", zip], { stdout: "pipe", stderr: "ignore" });
  const text = await new Response(proc.stdout).text();
  await proc.exited;
  return text.split("\n").filter(Boolean);
}

async function readEntry(zip: string, entry: string): Promise<Uint8Array | null> {
  const proc = Bun.spawn([UNZIP, "-p", zip, entry], { stdout: "pipe", stderr: "ignore" });
  const buffer = new Uint8Array(await new Response(proc.stdout).arrayBuffer());
  await proc.exited;
  return buffer.length > 0 ? buffer : null;
}

/** The x64 payload inside a bundle; bundles for other architectures are ignored. */
function innerPackage(names: string[]): string | null {
  const inner = names.filter(
    (n) => /\.(appx|msix)$/i.test(n) && /x64/i.test(n) && !/\.appxsym$/i.test(n),
  );
  return inner[0] ?? null;
}

/**
 * The manifest names one logo per size; the file on disk usually carries a
 * scale qualifier (Logo.scale-200.png), so the name is matched as a stem.
 */
function logoStems(manifest: string): string[] {
  const stems: string[] = [];
  for (const attribute of [
    "Square150x150Logo",
    "Square310x310Logo",
    "Square44x44Logo",
    "Logo",
    "Square70x70Logo",
  ]) {
    const match = manifest.match(new RegExp(`${attribute}="([^"]+)"`));
    if (match) stems.push(match[1].replace(/\\/g, "/").replace(/\.png$/i, ""));
  }
  return stems;
}

/** Prefers the largest scale available, which is what a television wants. */
function pickLogo(names: string[], stem: string): string | null {
  const wanted = names.filter((n) => {
    const normalised = n.replace(/\\/g, "/");
    return (
      normalised.toLowerCase().startsWith(stem.toLowerCase()) &&
      normalised.toLowerCase().endsWith(".png") &&
      !/targetsize/i.test(normalised)
    );
  });
  if (wanted.length === 0) return null;
  const scale = (name: string) => Number(name.match(/scale-(\d+)/i)?.[1] ?? 100);
  wanted.sort((a, b) => scale(b) - scale(a));
  return wanted[0];
}

export type IconResult = { slug: string; file?: string; reason?: string };

/**
 * Writes <outDir>/<slug>.png and answers what happened, because a package
 * without artwork is a normal outcome, not a failure.
 */
export async function extractIcon(
  slug: string,
  packagePath: string,
  outDir: string,
  scratch: string,
): Promise<IconResult> {
  let zip = packagePath;
  let names = await entries(zip);
  if (names.length === 0) return { slug, reason: "unreadable package" };

  const inner = innerPackage(names);
  if (inner) {
    const payload = await readEntry(zip, inner);
    if (!payload) return { slug, reason: "bundle without an x64 payload" };
    await mkdir(scratch, { recursive: true });
    zip = join(scratch, `${slug}.appx`);
    await Bun.write(zip, payload);
    names = await entries(zip);
  }

  const manifestName = names.find((n) => /^appxmanifest\.xml$/i.test(n));
  if (!manifestName) return { slug, reason: "no manifest" };
  const manifestBytes = await readEntry(zip, manifestName);
  if (!manifestBytes) return { slug, reason: "unreadable manifest" };
  const manifest = new TextDecoder().decode(manifestBytes);

  for (const stem of logoStems(manifest)) {
    const entry = pickLogo(names, stem);
    if (!entry) continue;
    const png = await readEntry(zip, entry);
    if (!png) continue;
    await mkdir(outDir, { recursive: true });
    const file = join(outDir, `${slug}.png`);
    await Bun.write(file, png);
    return { slug, file };
  }
  return { slug, reason: "package carries no logo" };
}

export async function cleanScratch(scratch: string): Promise<void> {
  await rm(scratch, { recursive: true, force: true });
}
