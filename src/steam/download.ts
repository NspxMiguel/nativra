// Turning a manifest into files on disk. Chunks are fetched a few at a time
// and written straight into place at their offset, so a partly-downloaded file
// is a real file and picking up again costs nothing.

import { mkdir, open, stat } from "node:fs/promises";
import { dirname, join } from "node:path";
import { CmClient, SteamError } from "./cm";
import {
  contentServers,
  decompressChunk,
  decryptFilename,
  decryptWithDepotKey,
  fetchChunk,
  fetchManifest,
  isDirectory,
  manifestRequestCode,
  parseManifest,
  type ContentServer,
  type Manifest,
  type ManifestFile,
} from "./depot";
import { parseVdf, windowsDepots, type Depot } from "./vdf";

export type Progress = {
  file: string;
  doneBytes: number;
  totalBytes: number;
  filesDone: number;
  filesTotal: number;
};

export type DepotPlan = {
  depot: Depot;
  manifest: Manifest;
  key: Uint8Array;
  names: Map<ManifestFile, string>;
};

/** Everything the console would need to know before writing a single byte. */
export async function plan(
  cm: CmClient,
  appId: number,
  servers: ContentServer[],
  only?: number[],
): Promise<{ name: string; depots: DepotPlan[] }> {
  const info = parseVdf(await cm.appInfo(appId, await cm.appToken(appId)));
  const root = (info["appinfo"] as Record<string, unknown>) ?? info;
  const common = root["common"] as Record<string, string> | undefined;
  const name = common?.["name"] ?? `app ${appId}`;

  const wanted = windowsDepots(info as never).filter(
    (d) => !only || only.includes(d.id),
  );

  const depots: DepotPlan[] = [];
  for (const depot of wanted) {
    const key = await cm.depotKey(appId, depot.id);
    const code = await manifestRequestCode(
      cm,
      appId,
      depot.id,
      depot.manifestId,
    );
    const manifest = parseManifest(
      await fetchManifest(servers, depot.id, depot.manifestId, code),
    );

    const names = new Map<ManifestFile, string>();
    for (const file of manifest.files) {
      names.set(
        file,
        manifest.filenamesEncrypted
          ? decryptFilename(file.name, key)
          : file.name,
      );
    }
    depots.push({ depot, manifest, key, names });
  }
  return { name, depots };
}

const WINDOWS_SEPARATOR = /\\/g;

/** Writes one depot into <root>, and answers how many bytes it wrote. */
export async function downloadDepot(
  servers: ContentServer[],
  item: DepotPlan,
  root: string,
  onProgress?: (progress: Progress) => void,
  parallel = 8,
): Promise<number> {
  const files = item.manifest.files.filter((f) => !isDirectory(f));
  const totalBytes = files.reduce((n, f) => n + Number(f.size), 0);

  let doneBytes = 0;
  let filesDone = 0;

  for (const file of files) {
    const relative = (item.names.get(file) ?? file.name).replace(
      WINDOWS_SEPARATOR,
      "/",
    );
    const target = join(root, relative);
    await mkdir(dirname(target), { recursive: true });

    // A file already the right size is taken as done: the manifest is what
    // decided that size, so a matching one came from this same manifest.
    const existing = await stat(target).catch(() => null);
    if (existing && existing.size === Number(file.size)) {
      doneBytes += Number(file.size);
      filesDone++;
      onProgress?.({
        file: relative,
        doneBytes,
        totalBytes,
        filesDone,
        filesTotal: files.length,
      });
      continue;
    }

    const handle = await open(target, "w+");
    try {
      const queue = [...file.chunks];
      while (queue.length > 0) {
        const batch = queue.splice(0, parallel);
        await Promise.all(
          batch.map(async (chunk) => {
            const raw = await fetchChunk(servers, item.depot.id, chunk.sha);
            const plain = await decompressChunk(
              decryptWithDepotKey(raw, item.key),
            );
            await handle.write(plain, 0, plain.length, Number(chunk.offset));
            doneBytes += plain.length;
          }),
        );
        onProgress?.({
          file: relative,
          doneBytes,
          totalBytes,
          filesDone,
          filesTotal: files.length,
        });
      }
    } finally {
      await handle.close();
    }
    filesDone++;
  }
  return doneBytes;
}

export async function connect(
  steamId: string,
  refreshToken: string,
): Promise<CmClient> {
  const endpoints = await CmClient.endpoints();
  let lastError: unknown = null;
  // One endpoint refusing is normal; the list exists to be tried in order.
  for (const endpoint of endpoints.slice(0, 5)) {
    const cm = new CmClient();
    try {
      await cm.connect(endpoint);
      await cm.logOn(BigInt(steamId), refreshToken);
      return cm;
    } catch (error) {
      lastError = error;
      cm.close();
      if (
        error instanceof SteamError &&
        [5, 15, 84].includes(error.eresult ?? 0)
      ) {
        throw error;
      }
    }
  }
  throw lastError instanceof Error ? lastError : new Error("no CM answered");
}

export { contentServers };
