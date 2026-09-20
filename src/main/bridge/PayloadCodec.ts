import { Buffer } from 'node:buffer';

import { BridgeErrorCode, BridgeProtocolError } from './FrameCodec.ts';

export const MAX_ECHO_DELAY_MS = 60_000;
const MAX_TEXT_BYTES = 1024 * 1024;
const MAX_LIST_ITEMS = 64;

export interface HostHandshake {
  hostVersion: string;
  sdkRevision: string;
  supportedGames: string[];
  capabilities: string[];
}

export interface EchoRequest {
  message: string;
  delayMs: number;
}

export interface BridgeProgress {
  completed: number;
  total: number;
}

export function encodeHandshake(value: HostHandshake): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.hostVersion);
  writer.writeString(value.sdkRevision);
  writer.writeStrings(value.supportedGames);
  writer.writeStrings(value.capabilities);
  return writer.toBuffer();
}

export function decodeHandshake(payload: Uint8Array): HostHandshake {
  const reader = new PayloadReader(payload);
  const value = {
    hostVersion: reader.readString(),
    sdkRevision: reader.readString(),
    supportedGames: reader.readStrings(),
    capabilities: reader.readStrings(),
  };
  reader.complete();
  return value;
}

export function encodeEchoRequest(message: string, delayMs = 0): Buffer {
  if (!Number.isInteger(delayMs) || delayMs < 0 || delayMs > MAX_ECHO_DELAY_MS) malformed('Invalid echo delay');
  const writer = new PayloadWriter();
  writer.writeUInt32(delayMs);
  writer.writeString(message);
  return writer.toBuffer();
}

export function decodeEchoRequest(payload: Uint8Array): EchoRequest {
  const reader = new PayloadReader(payload);
  const delayMs = reader.readUInt32();
  if (delayMs > MAX_ECHO_DELAY_MS) malformed('Echo delay exceeds limit');
  const message = reader.readString();
  reader.complete();
  return { message, delayMs };
}

export function encodeText(value: string): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value);
  return writer.toBuffer();
}

export function decodeText(payload: Uint8Array): string {
  const reader = new PayloadReader(payload);
  const value = reader.readString();
  reader.complete();
  return value;
}

export function encodeProgress(completed: number, total: number): Buffer {
  if (!Number.isInteger(completed) || !Number.isInteger(total) || total <= 0 || completed < 0 || completed > total) {
    malformed('Invalid progress values');
  }
  const payload = Buffer.allocUnsafe(8);
  payload.writeUInt32LE(completed, 0);
  payload.writeUInt32LE(total, 4);
  return payload;
}

export function decodeProgress(payload: Uint8Array): BridgeProgress {
  if (payload.length !== 8) malformed('Progress payload must contain eight bytes');
  const bytes = Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength);
  const completed = bytes.readUInt32LE(0);
  const total = bytes.readUInt32LE(4);
  if (total === 0 || completed > total) malformed('Invalid progress values');
  return { completed, total };
}

class PayloadWriter {
  readonly #parts: Buffer[] = [];

  writeUInt32(value: number): void {
    const bytes = Buffer.allocUnsafe(4);
    bytes.writeUInt32LE(value);
    this.#parts.push(bytes);
  }

  writeString(value: string): void {
    const bytes = Buffer.from(value, 'utf8');
    if (bytes.length > MAX_TEXT_BYTES) malformed('Text field exceeds limit');
    this.writeUInt32(bytes.length);
    this.#parts.push(bytes);
  }

  writeStrings(values: string[]): void {
    if (values.length > MAX_LIST_ITEMS) malformed('List exceeds item limit');
    this.writeUInt32(values.length);
    values.forEach((value) => this.writeString(value));
  }

  toBuffer(): Buffer {
    return Buffer.concat(this.#parts);
  }
}

class PayloadReader {
  readonly #bytes: Buffer;
  #offset = 0;

  constructor(payload: Uint8Array) {
    this.#bytes = Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength);
  }

  readUInt32(): number {
    this.#require(4);
    const value = this.#bytes.readUInt32LE(this.#offset);
    this.#offset += 4;
    return value;
  }

  readString(): string {
    const length = this.readUInt32();
    if (length > MAX_TEXT_BYTES) malformed('Text field exceeds limit');
    this.#require(length);
    try {
      const value = new TextDecoder('utf-8', { fatal: true })
        .decode(this.#bytes.subarray(this.#offset, this.#offset + length));
      this.#offset += length;
      return value;
    } catch {
      malformed('Text field is not valid UTF-8');
    }
  }

  readStrings(): string[] {
    const count = this.readUInt32();
    if (count > MAX_LIST_ITEMS) malformed('List exceeds item limit');
    return Array.from({ length: count }, () => this.readString());
  }

  complete(): void {
    if (this.#offset !== this.#bytes.length) malformed('Payload has trailing bytes');
  }

  #require(length: number): void {
    if (length > this.#bytes.length - this.#offset) malformed('Payload ended unexpectedly');
  }
}

function malformed(message: string): never {
  throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, message);
}
