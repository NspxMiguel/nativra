// Driving the console the way a controller would. The Device Portal exposes a
// websocket at /ext/remoteinput that takes a three-byte message per key event;
// gamepad buttons ride on the keyboard keycode channel with their own values.
//
// Format: [0x01, keycode, 0x01 for down | 0x00 for up]. 0x04 releases everything.

import type { PortalConfig } from "./portal";

export const BUTTONS: Record<string, number> = {
  a: 0xc3,
  b: 0xc4,
  x: 0xc5,
  y: 0xc6,
  rb: 0xc7,
  lb: 0xc8,
  lt: 0xc9,
  rt: 0xca,
  up: 0xcb,
  down: 0xcc,
  left: 0xcd,
  right: 0xce,
  menu: 0xcf,
  view: 0xd0,
  ls: 0xd1,
  rs: 0xd2,
  "ls-up": 0xd3,
  "ls-down": 0xd4,
  "ls-right": 0xd5,
  "ls-left": 0xd6,
  "rs-up": 0xd7,
  "rs-down": 0xd8,
  "rs-right": 0xd9,
  "rs-left": 0xda,
};

const TYPE_KEYCODE = 0x01;
const TYPE_CLEAR = 0x04;
const DOWN = 0x01;
const UP = 0x00;

export class RemoteError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "RemoteError";
  }
}

export class ConsoleRemote {
  private socket: WebSocket | null = null;

  constructor(private readonly config: PortalConfig) {}

  async open(timeoutMs = 8000): Promise<void> {
    const auth = Buffer.from(
      `${this.config.user ?? ""}:${this.config.pass ?? ""}`,
    ).toString("base64");
    const url = `wss://${this.config.host}:${this.config.port}/ext/remoteinput`;

    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(
        () => reject(new RemoteError("the console did not open the input channel")),
        timeoutMs,
      );
      const socket = new WebSocket(url, {
        headers: { Authorization: `Basic ${auth}` },
        tls: { rejectUnauthorized: false },
      } as unknown as string[]);
      socket.binaryType = "arraybuffer";
      socket.addEventListener("open", () => {
        clearTimeout(timer);
        this.socket = socket;
        resolve();
      });
      socket.addEventListener("error", () => {
        clearTimeout(timer);
        reject(new RemoteError("the input channel refused the connection"));
      });
    });
  }

  private send(keycode: number, state: number): void {
    if (!this.socket) throw new RemoteError("the input channel is not open");
    this.socket.send(new Uint8Array([TYPE_KEYCODE, keycode, state]));
  }

  /** One press: held long enough for the console to see it, then released. */
  async press(button: string, holdMs = 90): Promise<void> {
    const keycode = BUTTONS[button.toLowerCase()];
    if (keycode === undefined) {
      throw new RemoteError(`unknown button: ${button}`);
    }
    this.send(keycode, DOWN);
    await Bun.sleep(holdMs);
    this.send(keycode, UP);
    // Events that arrive back to back get coalesced; without a gap the second
    // press of a sequence is swallowed.
    await Bun.sleep(140);
  }

  async hold(button: string, ms: number): Promise<void> {
    const keycode = BUTTONS[button.toLowerCase()];
    if (keycode === undefined) throw new RemoteError(`unknown button: ${button}`);
    this.send(keycode, DOWN);
    await Bun.sleep(ms);
    this.send(keycode, UP);
    await Bun.sleep(140);
  }

  /** Releases everything, which is what the clear message is for. */
  clear(): void {
    this.socket?.send(new Uint8Array([TYPE_CLEAR]));
  }

  close(): void {
    try {
      this.clear();
      this.socket?.close();
    } catch {
      // Closing a channel the console already dropped is not an error here.
    }
    this.socket = null;
  }
}
