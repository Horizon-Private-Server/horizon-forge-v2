import { Buffer } from 'node:buffer';

import { BridgeErrorCode, BridgeProtocolError } from './BridgeProtocol.ts';

const MAX_TEXT_BYTES = 1024 * 1024;
export const MAX_LIST_ITEMS = 64;

export class PayloadWriter {
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

  writeUInt32s(values: number[]): void {
    if (values.length > MAX_LIST_ITEMS) malformed('List exceeds item limit');
    this.writeUInt32(values.length);
    values.forEach((value) => this.writeUInt32(value));
  }

  toBuffer(): Buffer {
    return Buffer.concat(this.#parts);
  }
}

export class PayloadReader {
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

  readUInt32s(): number[] {
    const count = this.readUInt32();
    if (count > MAX_LIST_ITEMS) malformed('List exceeds item limit');
    return Array.from({ length: count }, () => this.readUInt32());
  }

  complete(): void {
    if (this.#offset !== this.#bytes.length) malformed('Payload has trailing bytes');
  }

  #require(length: number): void {
    if (length > this.#bytes.length - this.#offset) malformed('Payload ended unexpectedly');
  }
}

export function malformed(message: string): never {
  throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, message);
}
