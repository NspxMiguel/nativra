// Manifests and chunks: the part of a Steam install that travels over plain
// HTTPS, once the client connection has handed over the depot key and the
// manifest request code.

import { Writer, read, num, str, list, type Message } from "./proto";
import { CmClient, SteamError } from "./cm";
import { createDecipheriv } from "node:crypto";
import { inflateRawSync } from "node:zlib";

export type ContentServer = { host: string; vhost: string; https: boolean };

export type Chunk = {
  sha: Uint8Array;
  crc: number;
  offset: bigint;
  original: number;
  compressed: number;
};

export type ManifestFile = {
  name: string;
  size: bigint;
  flags: number;
  chunks: Chunk[];
};

const FLAG_DIRECTORY = 64;

export async function contentServers(cm: CmClient): Promise<ContentServer[]> {
  const request = new Writer().uint(1, 0).uint(2, 20).finish();
  const response = await cm.service(
    "ContentServerDirectory.GetServersForSteamPipe#1",
    request,
  );

  const servers: ContentServer[] = [];
  for (const server of list(response, 1)) {
    const type = str(server, 1) ?? "";
    if (type !== "SteamCache" && type !== "CDN") continue;
    const host = str(server, 8);
    if (!host) continue;
    servers.push({
      host,
      vhost: str(server, 9) ?? host,
      https: (str(server, 12) ?? "") !== "none",
    });
  }
  return servers;
}

export async function manifestRequestCode(
  cm: CmClient,
  appId: number,
  depotId: number,
  manifestId: string,
  branch = "public",
): Promise<bigint> {
  const request = new Writer()
    .uint(1, appId)
    .uint(2, depotId)
    .uint(3, BigInt(manifestId))
    .string(4, branch)
    .finish();
  const response = await cm.service(
    "ContentServerDirectory.GetManifestRequestCode#1",
    request,
  );
  const code = num(response, 1);
  if (code === 0n) {
    throw new SteamError(`Steam gave no manifest code for depot ${depotId}`);
  }
  return code;
}

/**
 * The manifest arrives as a zip holding one entry; reading the local header
 * directly is shorter than pulling in a zip library for a single member.
 */
function unzipSingle(buf: Uint8Array): Uint8Array {
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  if (view.getUint32(0, true) !== 0x04034b50) {
    throw new SteamError("manifest is not a zip");
  }
  const method = view.getUint16(8, true);
  const compressedSize = view.getUint32(18, true);
  const nameLength = view.getUint16(26, true);
  const extraLength = view.getUint16(28, true);
  const start = 30 + nameLength + extraLength;
  const body = buf.subarray(start, start + compressedSize);
  return method === 0 ? body : new Uint8Array(inflateRawSync(body));
}

const MAGIC_PAYLOAD = 0x71f617d0;
const MAGIC_METADATA = 0x1f4812be;
const MAGIC_END = 0x32c415ab;

export type Manifest = {
  depotId: number;
  manifestId: bigint;
  files: ManifestFile[];
  totalBytes: bigint;
  filenamesEncrypted: boolean;
};

export function parseManifest(zipped: Uint8Array): Manifest {
  const buf = unzipSingle(zipped);
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);

  let at = 0;
  let payload: Message | null = null;
  let metadata: Message | null = null;

  while (at + 8 <= buf.length) {
    const magic = view.getUint32(at, true);
    if (magic === MAGIC_END) break;
    const length = view.getUint32(at + 4, true);
    const block = buf.subarray(at + 8, at + 8 + length);
    at += 8 + length;
    if (magic === MAGIC_PAYLOAD) payload = read(block);
    else if (magic === MAGIC_METADATA) metadata = read(block);
  }
  if (!payload || !metadata) throw new SteamError("manifest is missing a block");

  const files: ManifestFile[] = [];
  let totalBytes = 0n;
  for (const mapping of list(payload, 1)) {
    const chunks: Chunk[] = [];
    for (const chunk of list(mapping, 6)) {
      const sha = chunk.get(1)?.[0];
      if (!(sha instanceof Uint8Array)) continue;
      chunks.push({
        sha,
        crc: Number(num(chunk, 2)),
        offset: num(chunk, 3),
        original: Number(num(chunk, 4)),
        compressed: Number(num(chunk, 5)),
      });
    }
    const size = num(mapping, 2);
    totalBytes += size;
    files.push({
      name: str(mapping, 1) ?? "",
      size,
      flags: Number(num(mapping, 3)),
      chunks,
    });
  }

  return {
    depotId: Number(num(metadata, 1)),
    manifestId: num(metadata, 2),
    files,
    totalBytes,
    filenamesEncrypted: num(metadata, 4) === 1n,
  };
}

export const isDirectory = (file: ManifestFile): boolean =>
  (file.flags & FLAG_DIRECTORY) !== 0;

// ------------------------------------------------------------------ crypto

/**
 * Steam encrypts every chunk, and the file names too when the manifest says
 * so: AES-256-ECB over the first block yields the IV, then AES-256-CBC.
 */
export function decryptWithDepotKey(data: Uint8Array, key: Uint8Array): Uint8Array {
  const ecb = createDecipheriv("aes-256-ecb", key, null);
  ecb.setAutoPadding(false);
  const iv = Buffer.concat([ecb.update(data.subarray(0, 16)), ecb.final()]);

  const cbc = createDecipheriv("aes-256-cbc", key, iv);
  return new Uint8Array(Buffer.concat([cbc.update(data.subarray(16)), cbc.final()]));
}

export function decryptFilename(encoded: string, key: Uint8Array): string {
  const raw = new Uint8Array(Buffer.from(encoded, "base64"));
  const plain = decryptWithDepotKey(raw, key);
  const end = plain.indexOf(0);
  return new TextDecoder().decode(end < 0 ? plain : plain.subarray(0, end));
}

/**
 * A decrypted chunk is either a zip (PK) or Valve's own LZMA wrapper (VZ).
 * Node has no LZMA, so that branch is handed to Python, which does.
 */
export async function decompressChunk(data: Uint8Array): Promise<Uint8Array> {
  if (data[0] === 0x50 && data[1] === 0x4b) return unzipSingle(data);
  if (data[0] === 0x56 && data[1] === 0x5a) return unlzma(data);
  if (data[0] === 0x56 && data[1] === 0x53) return unzstd(data);
  throw new SteamError(
    `unknown chunk container ${String.fromCharCode(data[0], data[1])}`,
  );
}

/**
 * The newer container, measured on Resident Evil Requiem: "VSZa", four bytes
 * of checksum, the zstd frame, then eleven bytes of footer that end in "zsv".
 */
function unzstd(data: Uint8Array): Uint8Array {
  const HEADER = 8;
  const FOOTER = 15;
  const tail = data.subarray(data.length - 3);
  if (tail[0] !== 0x7a || tail[1] !== 0x73 || tail[2] !== 0x76) {
    throw new SteamError("VS chunk without its footer");
  }
  const out = new Uint8Array(
    Bun.zstdDecompressSync(data.subarray(HEADER, data.length - FOOTER)),
  );
  // The footer states the size it should have come to; disagreeing means the
  // framing is wrong and every later chunk would be wrong too.
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const declared = view.getUint32(data.length - 11, true);
  if (declared !== out.length) {
    throw new SteamError(`VS chunk came to ${out.length}, not the stated ${declared}`);
  }
  return out;
}

const VZ_SCRIPT = `
import sys, lzma, struct
raw = sys.stdin.buffer.read()
props = raw[7]
lc = props % 9
rest = props // 9
lp = rest % 5
pb = rest // 5
dict_size = struct.unpack('<I', raw[8:12])[0]
size = struct.unpack('<I', raw[-6:-2])[0]
d = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
    'id': lzma.FILTER_LZMA1, 'lc': lc, 'lp': lp, 'pb': pb,
    'dict_size': max(dict_size, 4096)}])
sys.stdout.buffer.write(d.decompress(raw[12:-10], size))
`;

async function unlzma(data: Uint8Array): Promise<Uint8Array> {
  const proc = Bun.spawn(["/usr/bin/python3", "-c", VZ_SCRIPT], {
    stdin: "pipe",
    stdout: "pipe",
    stderr: "pipe",
  });
  proc.stdin.write(data);
  await proc.stdin.end();
  const out = new Uint8Array(await new Response(proc.stdout).arrayBuffer());
  const code = await proc.exited;
  if (code !== 0) {
    throw new SteamError(
      `LZMA refused the chunk: ${(await new Response(proc.stderr).text()).trim()}`,
    );
  }
  return out;
}

export const hex = (bytes: Uint8Array): string =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

/**
 * Caches fail in ways a single request cannot survive: one answers 404 for a
 * chunk another has, and some present a certificate for a different name. So
 * every fetch walks the server list instead of trusting one.
 */
async function fetchFromAny(
  servers: ContentServer[],
  path: string,
  attempts = 6,
): Promise<Uint8Array> {
  let lastError: unknown = null;
  for (let i = 0; i < Math.min(attempts, Math.max(1, servers.length)); i++) {
    const server = servers[(rotation++ + i) % servers.length];
    try {
      const response = await fetch(`https://${server.host}${path}`);
      if (!response.ok) {
        lastError = new SteamError(`HTTP ${response.status} from ${server.host}`);
        continue;
      }
      return new Uint8Array(await response.arrayBuffer());
    } catch (error) {
      lastError = error;
    }
  }
  throw lastError instanceof Error
    ? lastError
    : new SteamError(`no content server served ${path}`);
}

let rotation = 0;

export async function fetchManifest(
  servers: ContentServer[],
  depotId: number,
  manifestId: string,
  code: bigint,
): Promise<Uint8Array> {
  return fetchFromAny(servers, `/depot/${depotId}/manifest/${manifestId}/5/${code}`);
}

export async function fetchChunk(
  servers: ContentServer[],
  depotId: number,
  sha: Uint8Array,
): Promise<Uint8Array> {
  return fetchFromAny(servers, `/depot/${depotId}/chunk/${hex(sha)}`);
}
