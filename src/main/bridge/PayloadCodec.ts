import { Buffer } from 'node:buffer';

import type { DevelopmentIsoResult, ForgeHostStatus, Progress, UyaIsoIdentity } from '../../types/ForgeApi.js';
import { BridgeErrorCode, BridgeProtocolError } from './BridgeProtocol.ts';
import type { DevelopmentIsoRequest, EchoRequest } from '../../types/BridgePayloads.js';

export const MAX_ECHO_DELAY_MS = 60_000;
const MAX_TEXT_BYTES = 1024 * 1024;
const MAX_LIST_ITEMS = 64;

export function encodeHandshake(value: ForgeHostStatus): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.hostVersion);
  writer.writeString(value.sdkRevision);
  writer.writeStrings(value.supportedGames);
  writer.writeStrings(value.capabilities);
  return writer.toBuffer();
}

export function decodeHandshake(payload: Uint8Array): ForgeHostStatus {
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

export function decodeProgress(payload: Uint8Array): Progress {
  if (payload.length !== 8) malformed('Progress payload must contain eight bytes');
  const bytes = Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength);
  const completed = bytes.readUInt32LE(0);
  const total = bytes.readUInt32LE(4);
  if (total === 0 || completed > total) malformed('Invalid progress values');
  return { completed, total };
}

export function encodeUyaIsoValidation(value: UyaIsoIdentity): Buffer {
  const writer = new PayloadWriter();
  writer.writeBoolean(value.isSupported);
  writer.writeString(value.game);
  writer.writeString(value.region);
  writer.writeString(value.revision);
  writer.writeString(value.serial);
  writer.writeUInt64(value.size);
  writer.writeString(value.fingerprint);
  writer.writeString(value.diagnostic);
  return writer.toBuffer();
}

export function decodeUyaIsoValidation(payload: Uint8Array): UyaIsoIdentity {
  const reader = new PayloadReader(payload);
  const value = {
    isSupported: reader.readBoolean(),
    game: reader.readString(),
    region: reader.readString(),
    revision: reader.readString(),
    serial: reader.readString(),
    size: reader.readUInt64(),
    fingerprint: reader.readString(),
    diagnostic: reader.readString(),
  };
  reader.complete();
  return value;
}

export function encodeDevelopmentIsoRequest(value: DevelopmentIsoRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.sourcePath);
  writer.writeString(value.targetPath);
  writer.writeString(value.fingerprint);
  writer.writeBoolean(value.overwrite);
  return writer.toBuffer();
}

export function decodeDevelopmentIsoRequest(payload: Uint8Array): DevelopmentIsoRequest {
  const reader = new PayloadReader(payload);
  const value = {
    sourcePath: reader.readString(),
    targetPath: reader.readString(),
    fingerprint: reader.readString(),
    overwrite: reader.readBoolean(),
  };
  reader.complete();
  return value;
}

export function encodeDevelopmentIso(value: DevelopmentIsoResult): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.path);
  writer.writeUInt64(value.size);
  writer.writeString(value.fingerprint);
  return writer.toBuffer();
}

export function decodeDevelopmentIso(payload: Uint8Array): DevelopmentIsoResult {
  const reader = new PayloadReader(payload);
  const value = { path: reader.readString(), size: reader.readUInt64(), fingerprint: reader.readString() };
  reader.complete();
  return value;
}

class PayloadWriter {
  readonly #parts: Buffer[] = [];

  writeUInt32(value: number): void {
    const bytes = Buffer.allocUnsafe(4);
    bytes.writeUInt32LE(value);
    this.#parts.push(bytes);
  }

  writeUInt64(value: number): void {
    if (!Number.isSafeInteger(value) || value < 0) malformed('Invalid 64-bit integer');
    const bytes = Buffer.allocUnsafe(8);
    bytes.writeBigUInt64LE(BigInt(value));
    this.#parts.push(bytes);
  }

  writeBoolean(value: boolean): void {
    this.#parts.push(Buffer.of(value ? 1 : 0));
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

  readUInt64(): number {
    this.#require(8);
    const value = Number(this.#bytes.readBigUInt64LE(this.#offset));
    this.#offset += 8;
    if (!Number.isSafeInteger(value)) malformed('64-bit integer exceeds JavaScript safe range');
    return value;
  }

  readBoolean(): boolean {
    this.#require(1);
    const value = this.#bytes[this.#offset++];
    if (value > 1) malformed('Boolean field must be zero or one');
    return value === 1;
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
