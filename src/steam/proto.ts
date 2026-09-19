// A protobuf codec with no schema: fields go in and come out by number.
// Steam's messages are read once and thrown away, so generated classes would
// cost more than they save — what matters is that the field numbers are right,
// and those are recorded in docs/STEAM-DEPOT.md.

export type Field = bigint | Uint8Array;

export class Writer {
  private parts: Uint8Array[] = [];

  private varint(value: bigint): void {
    const bytes: number[] = [];
    let v = value;
    do {
      let b = Number(v & 0x7fn);
      v >>= 7n;
      if (v !== 0n) b |= 0x80;
      bytes.push(b);
    } while (v !== 0n);
    this.parts.push(new Uint8Array(bytes));
  }

  private tag(field: number, wire: number): void {
    this.varint(BigInt((field << 3) | wire));
  }

  uint(field: number, value: number | bigint): this {
    this.tag(field, 0);
    this.varint(BigInt(value));
    return this;
  }

  bool(field: number, value: boolean): this {
    return this.uint(field, value ? 1 : 0);
  }

  fixed64(field: number, value: bigint): this {
    this.tag(field, 1);
    const buf = new Uint8Array(8);
    new DataView(buf.buffer).setBigUint64(0, value, true);
    this.parts.push(buf);
    return this;
  }

  bytes(field: number, value: Uint8Array): this {
    this.tag(field, 2);
    this.varint(BigInt(value.length));
    this.parts.push(value);
    return this;
  }

  string(field: number, value: string): this {
    return this.bytes(field, new TextEncoder().encode(value));
  }

  message(field: number, inner: Writer): this {
    return this.bytes(field, inner.finish());
  }

  finish(): Uint8Array {
    const total = this.parts.reduce((n, p) => n + p.length, 0);
    const out = new Uint8Array(total);
    let at = 0;
    for (const part of this.parts) {
      out.set(part, at);
      at += part.length;
    }
    return out;
  }
}

/** Field number to every value seen, because repeated fields are normal here. */
export type Message = Map<number, Field[]>;

export function read(buf: Uint8Array): Message {
  const out: Message = new Map();
  const view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  let i = 0;

  const varint = (): bigint => {
    let value = 0n;
    let shift = 0n;
    while (i < buf.length) {
      const b = buf[i++];
      value |= BigInt(b & 0x7f) << shift;
      shift += 7n;
      if ((b & 0x80) === 0) break;
    }
    return value;
  };

  const push = (field: number, value: Field) => {
    const list = out.get(field);
    if (list) list.push(value);
    else out.set(field, [value]);
  };

  while (i < buf.length) {
    const tag = Number(varint());
    const field = tag >> 3;
    const wire = tag & 7;
    if (wire === 0) {
      push(field, varint());
    } else if (wire === 1) {
      push(field, view.getBigUint64(i, true));
      i += 8;
    } else if (wire === 2) {
      const length = Number(varint());
      push(field, buf.subarray(i, i + length));
      i += length;
    } else if (wire === 5) {
      push(field, BigInt(view.getUint32(i, true)));
      i += 4;
    } else {
      break;
    }
  }
  return out;
}

export const num = (m: Message, field: number, fallback = 0n): bigint => {
  const value = m.get(field)?.[0];
  return typeof value === "bigint" ? value : fallback;
};

export const str = (m: Message, field: number): string | null => {
  const value = m.get(field)?.[0];
  return value instanceof Uint8Array ? new TextDecoder().decode(value) : null;
};

export const raw = (m: Message, field: number): Uint8Array | null => {
  const value = m.get(field)?.[0];
  return value instanceof Uint8Array ? value : null;
};

export const list = (m: Message, field: number): Message[] =>
  (m.get(field) ?? [])
    .filter((v): v is Uint8Array => v instanceof Uint8Array)
    .map(read);
