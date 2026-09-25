// The Steam client connection. A game's depot key and its list of depots only
// exist here — the Web API does not hand either one out — so downloading
// anything means speaking the client protocol.
//
// The modern connection managers accept a websocket over TLS, which removes
// the old encryption handshake: TLS already covers it. What is left is the
// message framing, and that is four lines.

import { Writer, read, num, str, raw, list, type Message } from "./proto";
import { gunzipSync } from "node:zlib";

const EMSG = {
  multi: 1,
  serviceMethodResponse: 147,
  serviceMethodCallFromClient: 151,
  heartbeat: 703,
  logOff: 706,
  logOnResponse: 751,
  loggedOff: 757,
  getDepotDecryptionKey: 5438,
  getDepotDecryptionKeyResponse: 5439,
  logon: 5514,
  picsProductInfoRequest: 8903,
  picsProductInfoResponse: 8904,
  picsAccessTokenRequest: 8905,
  picsAccessTokenResponse: 8906,
} as const;

const PROTO_MASK = 0x80000000;

export class SteamError extends Error {
  constructor(
    message: string,
    readonly eresult?: number,
  ) {
    super(message);
    this.name = "SteamError";
  }
}

type Pending = {
  resolve: (value: { header: Message; body: Message }) => void;
  reject: (reason: Error) => void;
};

export class CmClient {
  private socket: WebSocket | null = null;
  private sessionId = 0;
  private steamId = 0n;
  private nextJob = 1n;
  private pending = new Map<string, Pending>();
  private heartbeat: ReturnType<typeof setInterval> | null = null;
  private closed = false;
  /** Sees every message, answered or not; for probes that study the protocol. */
  onMessage: ((emsg: number, header: Message, body: Message) => void) | null =
    null;

  /** Steam publishes its own websocket endpoints; picking one is a plain GET. */
  static async endpoints(): Promise<string[]> {
    const url =
      "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/" +
      "?cellid=0&cmtype=websockets";
    const data = (await (await fetch(url)).json()) as {
      response?: { serverlist?: Array<{ endpoint?: string }> };
    };
    const servers = data.response?.serverlist ?? [];
    return servers
      .map((s) => s.endpoint)
      .filter((e): e is string => Boolean(e));
  }

  async connect(endpoint: string): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const socket = new WebSocket(`wss://${endpoint}/cmsocket/`);
      socket.binaryType = "arraybuffer";
      const fail = () => reject(new SteamError(`could not reach ${endpoint}`));
      socket.addEventListener("open", () => {
        this.socket = socket;
        resolve();
      });
      socket.addEventListener("error", fail);
      socket.addEventListener("close", () => {
        this.closed = true;
        for (const [, p] of this.pending) {
          p.reject(new SteamError("the connection closed while waiting"));
        }
        this.pending.clear();
      });
      socket.addEventListener("message", (event) => {
        this.onPacket(new Uint8Array(event.data as ArrayBuffer));
      });
    });
  }

  // ------------------------------------------------------------- framing

  private send(emsg: number, body: Uint8Array, header: Writer): void {
    if (!this.socket) throw new SteamError("not connected");
    const headerBytes = header.finish();
    const packet = new Uint8Array(8 + headerBytes.length + body.length);
    const view = new DataView(packet.buffer);
    view.setUint32(0, (emsg | PROTO_MASK) >>> 0, true);
    view.setUint32(4, headerBytes.length, true);
    packet.set(headerBytes, 8);
    packet.set(body, 8 + headerBytes.length);
    this.socket.send(packet);
  }

  private baseHeader(): Writer {
    const header = new Writer();
    if (this.steamId !== 0n) header.fixed64(1, this.steamId);
    if (this.sessionId !== 0) header.uint(2, this.sessionId);
    return header;
  }

  private onPacket(packet: Uint8Array): void {
    if (packet.length < 8) return;
    const view = new DataView(
      packet.buffer,
      packet.byteOffset,
      packet.byteLength,
    );
    const rawEmsg = view.getUint32(0, true);
    const emsg = rawEmsg & ~PROTO_MASK;
    if ((rawEmsg & PROTO_MASK) === 0) return; // pre-protobuf messages are not used here

    const headerLength = view.getUint32(4, true);
    const header = read(packet.subarray(8, 8 + headerLength));
    const body = packet.subarray(8 + headerLength);

    if (emsg === EMSG.multi) {
      this.onMulti(read(body));
      return;
    }

    if (emsg === EMSG.logOnResponse) {
      this.sessionId = Number(num(header, 2));
      this.steamId = num(header, 1);
    }

    // A reply carries back the job number the request went out with; a service
    // call is matched by name instead, since Steam answers those on job id too.
    this.onMessage?.(emsg, header, read(body));

    const jobTarget = num(header, 11);
    const key =
      jobTarget !== 0n && jobTarget !== 0xffffffffffffffffn
        ? `job:${jobTarget}`
        : `emsg:${emsg}`;

    const waiting = this.pending.get(key) ?? this.pending.get(`emsg:${emsg}`);
    if (!waiting) return;
    this.pending.delete(key);
    this.pending.delete(`emsg:${emsg}`);

    const eresult = Number(num(header, 13, 0n));
    if (eresult !== 0 && eresult !== 1) {
      waiting.reject(
        new SteamError(str(header, 14) ?? `Steam eresult ${eresult}`, eresult),
      );
      return;
    }
    waiting.resolve({ header, body: read(body) });
  }

  /** Steam batches messages; the batch may also be gzipped. */
  private onMulti(multi: Message): void {
    const unzippedSize = Number(num(multi, 1));
    let payload = raw(multi, 2);
    if (!payload) return;
    if (unzippedSize > 0) payload = new Uint8Array(gunzipSync(payload));

    let at = 0;
    const view = new DataView(
      payload.buffer,
      payload.byteOffset,
      payload.byteLength,
    );
    while (at + 4 <= payload.length) {
      const size = view.getUint32(at, true);
      at += 4;
      if (at + size > payload.length) break;
      this.onPacket(payload.subarray(at, at + size));
      at += size;
    }
  }

  private await_(
    key: string,
    timeoutMs = 30000,
  ): Promise<{ header: Message; body: Message }> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(key);
        reject(new SteamError(`Steam did not answer ${key}`));
      }, timeoutMs);
      this.pending.set(key, {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
    });
  }

  // ------------------------------------------------------------- logon

  /**
   * The field is called access_token in the schema, but what the client puts
   * there is the refresh token from the QR sign-in.
   */
  async logOn(steamId: bigint, refreshToken: string): Promise<void> {
    const body = new Writer()
      .uint(1, 65580) // protocol_version
      .uint(3, 0) // cell_id
      .uint(5, 1771) // client_package_version
      .string(6, "english")
      .uint(7, 20) // client_os_type: Windows 10
      .bool(8, true) // Persistent QR refresh-token session.
      .bytes(30, new Uint8Array(0)) // machine_id
      .string(96, "Xbox Series X")
      .uint(100, 0) // client_instance_id
      .bool(102, true) // supports_rate_limit_response
      .string(108, refreshToken)
      .finish();

    const header = new Writer().fixed64(1, steamId).uint(2, 0);
    const waiting = this.await_(`emsg:${EMSG.logOnResponse}`, 45000);
    this.send(EMSG.logon, body, header);

    const { body: response } = await waiting;
    const eresult = Number(num(response, 1, 2n));
    if (eresult !== 1) throw new SteamError(`logon refused`, eresult);

    this.steamId = num(response, 20) || steamId;
    const seconds = Number(num(response, 3, 9n));
    this.heartbeat = setInterval(
      () => {
        if (this.closed) return;
        try {
          this.send(EMSG.heartbeat, new Writer().finish(), this.baseHeader());
        } catch {
          // A dead socket is discovered by the next real call, not here.
        }
      },
      Math.max(5, seconds) * 1000,
    );
  }

  // ------------------------------------------------------------- calls

  private jobHeader(): { header: Writer; key: string } {
    const job = this.nextJob++;
    const header = this.baseHeader().fixed64(10, job);
    return { header, key: `job:${job}` };
  }

  /** Any message by number, answered on its job; for protocol probes. */
  async request(emsg: number, body: Uint8Array): Promise<Message> {
    const { header, key } = this.jobHeader();
    const waiting = this.await_(key, 15000);
    this.send(emsg, body, header);
    return (await waiting).body;
  }

  /** A message nobody answers by job. */
  notify(emsg: number, body: Uint8Array): void {
    this.send(emsg, body, this.baseHeader());
  }

  /** A unified service call, addressed by name rather than by message number. */
  async service(name: string, request: Uint8Array): Promise<Message> {
    const { header, key } = this.jobHeader();
    header.string(12, name);
    const waiting = this.await_(key);
    this.send(EMSG.serviceMethodCallFromClient, request, header);
    return (await waiting).body;
  }

  async depotKey(appId: number, depotId: number): Promise<Uint8Array> {
    const { header, key } = this.jobHeader();
    const body = new Writer().uint(1, depotId).uint(2, appId).finish();
    const waiting = this.await_(key);
    this.send(EMSG.getDepotDecryptionKey, body, header);

    const { body: response } = await waiting;
    const eresult = Number(num(response, 1, 2n));
    if (eresult !== 1) {
      throw new SteamError(`no depot key for ${depotId}`, eresult);
    }
    const depotKey = raw(response, 3);
    if (!depotKey) throw new SteamError(`empty depot key for ${depotId}`);
    return depotKey;
  }

  /** PICS will not describe an app without the token it hands out first. */
  async appToken(appId: number): Promise<bigint> {
    const { header, key } = this.jobHeader();
    const body = new Writer().uint(2, appId).finish();
    const waiting = this.await_(key);
    this.send(EMSG.picsAccessTokenRequest, body, header);

    const { body: response } = await waiting;
    for (const token of list(response, 3)) {
      if (Number(num(token, 1)) === appId) return num(token, 2);
    }
    return 0n;
  }

  /** Returns the app's binary KeyValues buffer, which lists its depots. */
  async appInfo(appId: number, token: bigint): Promise<Uint8Array> {
    const { header, key } = this.jobHeader();
    const app = new Writer().uint(1, appId);
    if (token !== 0n) app.uint(2, token);
    const body = new Writer().message(2, app).bool(7, true).finish();

    const waiting = this.await_(key, 45000);
    this.send(EMSG.picsProductInfoRequest, body, header);

    const { body: response } = await waiting;
    for (const info of list(response, 1)) {
      if (Number(num(info, 1)) !== appId) continue;
      const buffer = raw(info, 5);
      if (buffer) return buffer;
      if (num(info, 3) === 1n) {
        throw new SteamError(`PICS refused ${appId}: missing token`);
      }
    }
    throw new SteamError(`PICS said nothing about ${appId}`);
  }

  close(): void {
    this.closed = true;
    if (this.heartbeat) clearInterval(this.heartbeat);
    try {
      this.socket?.close();
    } catch {
      // Closing an already-dead socket is not a problem worth reporting.
    }
  }
}
