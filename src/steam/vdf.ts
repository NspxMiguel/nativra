// Binary KeyValues — how PICS describes an app. The depots, their manifest
// ids and which of them a Windows build needs all live in this tree.

export type Node = { [key: string]: Node | string };

const NONE = 0x00;
const STRING = 0x01;
const INT32 = 0x02;
const FLOAT32 = 0x03;
const POINTER = 0x04;
const WIDESTRING = 0x05;
const COLOR = 0x06;
const UINT64 = 0x07;
const END = 0x08;
const INT64 = 0x0a;
const END_ALT = 0x0b;

/**
 * PICS answers in text KeyValues for some apps and binary for others, so the
 * caller should not have to know which — measured on appid 3764200, which
 * comes back as text.
 */
export function parseVdf(buf: Uint8Array): Node {
  let at = 0;
  while (at < buf.length && (buf[at] === 0x20 || buf[at] === 0x09 || buf[at] === 0x0a || buf[at] === 0x0d)) {
    at++;
  }
  return buf[at] === 0x22 || buf[at] === 0x2f
    ? parseTextVdf(new TextDecoder().decode(buf))
    : parseBinaryVdf(buf);
}

export function parseTextVdf(text: string): Node {
  let i = 0;

  const skip = () => {
    while (i < text.length) {
      if (text[i] === "/" && text[i + 1] === "/") {
        while (i < text.length && text[i] !== "\n") i++;
      } else if (/\s/.test(text[i])) {
        i++;
      } else {
        break;
      }
    }
  };

  const token = (): string | null => {
    skip();
    if (i >= text.length) return null;
    if (text[i] === "{" || text[i] === "}") return text[i++];
    if (text[i] === '"') {
      i++;
      let out = "";
      while (i < text.length && text[i] !== '"') {
        if (text[i] === "\\" && i + 1 < text.length) {
          i++;
          out += text[i] === "n" ? "\n" : text[i] === "t" ? "\t" : text[i];
        } else {
          out += text[i];
        }
        i++;
      }
      i++;
      return out;
    }
    const start = i;
    while (i < text.length && !/[\s{}"]/.test(text[i])) i++;
    return text.slice(start, i);
  };

  const object = (): Node => {
    const node: Node = {};
    while (true) {
      const key = token();
      if (key === null || key === "}") break;
      const value = token();
      if (value === null) break;
      node[key] = value === "{" ? object() : value;
    }
    return node;
  };

  // The document is one named root: "appinfo" { ... }
  const rootName = token();
  const open = token();
  if (rootName === null) return {};
  if (open !== "{") return {};
  return { [rootName]: object() };
}

export function parseBinaryVdf(buf: Uint8Array): Node {
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const decoder = new TextDecoder();
  let at = 0;

  const cstring = (): string => {
    const start = at;
    while (at < buf.length && buf[at] !== 0) at++;
    const text = decoder.decode(buf.subarray(start, at));
    at++;
    return text;
  };

  const object = (): Node => {
    const node: Node = {};
    while (at < buf.length) {
      const type = buf[at++];
      if (type === END || type === END_ALT) break;
      const key = cstring();
      switch (type) {
        case NONE:
          node[key] = object();
          break;
        case STRING:
          node[key] = cstring();
          break;
        case WIDESTRING: {
          // Rare enough that Steam's own tools warn about it; read it as UTF-16.
          const start = at;
          while (at + 1 < buf.length && !(buf[at] === 0 && buf[at + 1] === 0)) at += 2;
          node[key] = new TextDecoder("utf-16le").decode(buf.subarray(start, at));
          at += 2;
          break;
        }
        case INT32:
        case POINTER:
        case COLOR:
          node[key] = String(view.getInt32(at, true));
          at += 4;
          break;
        case FLOAT32:
          node[key] = String(view.getFloat32(at, true));
          at += 4;
          break;
        case UINT64:
          node[key] = view.getBigUint64(at, true).toString();
          at += 8;
          break;
        case INT64:
          node[key] = view.getBigInt64(at, true).toString();
          at += 8;
          break;
        default:
          // An unknown type means the offset is already wrong; stopping beats
          // returning a tree built out of misread bytes.
          return node;
      }
    }
    return node;
  };

  return object();
}

export type Depot = {
  id: number;
  name: string;
  manifestId: string;
  size: number;
  os: string | null;
  arch: string | null;
  isDlc: boolean;
  sharedFrom: number | null;
};

/**
 * The depots a Windows install needs, in the order the client would take them.
 * Shared depots (Redistributables and the like) and other platforms are left
 * out: they are listed for every app and are not the game.
 */
export function windowsDepots(appInfo: Node, branch = "public"): Depot[] {
  const root = (appInfo["appinfo"] as Node) ?? appInfo;
  const depots = root["depots"] as Node | undefined;
  if (!depots) return [];

  const out: Depot[] = [];
  for (const [key, value] of Object.entries(depots)) {
    const id = Number(key);
    if (!Number.isInteger(id) || typeof value === "string") continue;

    const config = (value["config"] as Node) ?? {};
    const manifests = (value["manifests"] as Node) ?? {};
    const entry = manifests[branch];
    const manifestId =
      typeof entry === "string" ? entry : ((entry as Node)?.["gid"] as string) ?? null;
    if (!manifestId) continue;

    const os = typeof config["oslist"] === "string" ? config["oslist"] : null;
    const arch = typeof config["osarch"] === "string" ? config["osarch"] : null;
    const shared = value["depotfromapp"];

    out.push({
      id,
      name: typeof value["name"] === "string" ? value["name"] : `depot ${id}`,
      manifestId,
      size: Number((value["maxsize"] as string) ?? 0),
      os,
      arch,
      isDlc: value["dlcappid"] !== undefined,
      sharedFrom: typeof shared === "string" ? Number(shared) : null,
    });
  }

  return out
    .filter((d) => d.sharedFrom === null)
    .filter((d) => d.os === null || d.os.split(",").includes("windows"));
}
