import { Buffer } from 'node:buffer';

export const BRIDGE_HEADER_SIZE = 24;
export const BRIDGE_MAX_PAYLOAD_LENGTH = 64 * 1024 * 1024;
export const BRIDGE_PROTOCOL_VERSION = 1;

export const BridgeMessageKind = {
  Handshake: 1,
  Request: 2,
  Result: 3,
  Progress: 4,
  Cancel: 5,
  Error: 6,
} as const;

export const BridgeOpcode = {
  Control: 0,
  Echo: 1,
} as const;

export const BridgeErrorCode = {
  None: 0,
  InvalidMagic: 1,
  UnsupportedVersion: 2,
  InvalidMessageKind: 3,
  InvalidFlags: 4,
  UnknownOpcode: 5,
  InvalidStatus: 6,
  InvalidRequestId: 7,
  PayloadTooLarge: 8,
  InvalidHeader: 9,
  MalformedPayload: 10,
  Cancelled: 11,
  InternalError: 12,
} as const;

export type BridgeMessageKind = typeof BridgeMessageKind[keyof typeof BridgeMessageKind];
export type BridgeOpcode = typeof BridgeOpcode[keyof typeof BridgeOpcode];
export type BridgeErrorCode = typeof BridgeErrorCode[keyof typeof BridgeErrorCode];

export interface BridgeFrame {
  kind: BridgeMessageKind;
  opcode: BridgeOpcode;
  status: BridgeErrorCode;
  requestId: number;
  payload: Uint8Array;
}

interface DecodedHeader extends Omit<BridgeFrame, 'payload'> {
  payloadLength: number;
}

const magic = Buffer.from('HFG2', 'ascii');
const validKinds = new Set<number>(Object.values(BridgeMessageKind));
const validOpcodes = new Set<number>(Object.values(BridgeOpcode));
const validErrorCodes = new Set<number>(Object.values(BridgeErrorCode).filter((value) => value !== 0));

export class BridgeProtocolError extends Error {
  readonly code: BridgeErrorCode;

  constructor(code: BridgeErrorCode, message: string) {
    super(message);
    this.name = 'BridgeProtocolError';
    this.code = code;
  }
}

export function encodeErrorMessage(message: string): Buffer {
  const bytes = Buffer.from(message, 'utf8');
  if (bytes.length === 0) {
    throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, 'Error message must not be empty');
  }
  const payload = Buffer.allocUnsafe(4 + bytes.length);
  payload.writeUInt32LE(bytes.length, 0);
  bytes.copy(payload, 4);
  return payload;
}

export function decodeErrorMessage(payload: Uint8Array): string {
  const bytes = Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength);
  if (bytes.length < 4 || bytes.readUInt32LE(0) !== bytes.length - 4) {
    throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, 'Invalid error message length');
  }
  try {
    const message = new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(4));
    if (message.length === 0) throw new Error('empty');
    return message;
  } catch {
    throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, 'Error message is not valid nonempty UTF-8');
  }
}

export function encodeFrame(frame: BridgeFrame): Buffer {
  const payload = Buffer.from(frame.payload.buffer, frame.payload.byteOffset, frame.payload.byteLength);
  validateHeader(frame.kind, 0, frame.opcode, frame.status, frame.requestId, payload.length, 0);
  if (frame.kind === BridgeMessageKind.Error) decodeErrorMessage(payload);

  const encoded = Buffer.allocUnsafe(BRIDGE_HEADER_SIZE + payload.length);
  magic.copy(encoded, 0);
  encoded.writeUInt16LE(BRIDGE_PROTOCOL_VERSION, 4);
  encoded.writeUInt8(frame.kind, 6);
  encoded.writeUInt8(0, 7);
  encoded.writeUInt16LE(frame.opcode, 8);
  encoded.writeUInt16LE(frame.status, 10);
  encoded.writeUInt32LE(frame.requestId, 12);
  encoded.writeUInt32LE(payload.length, 16);
  encoded.writeUInt32LE(0, 20);
  payload.copy(encoded, BRIDGE_HEADER_SIZE);
  return encoded;
}

export class BridgeFrameDecoder {
  readonly #header = Buffer.allocUnsafe(BRIDGE_HEADER_SIZE);
  #headerBytes = 0;
  #pending: DecodedHeader | undefined;
  #payload: Buffer | undefined;
  #payloadBytes = 0;

  push(chunk: Uint8Array): BridgeFrame[] {
    const frames: BridgeFrame[] = [];
    let offset = 0;

    while (offset < chunk.length) {
      if (!this.#pending) {
        const count = Math.min(BRIDGE_HEADER_SIZE - this.#headerBytes, chunk.length - offset);
        this.#header.set(chunk.subarray(offset, offset + count), this.#headerBytes);
        this.#headerBytes += count;
        offset += count;
        if (this.#headerBytes < BRIDGE_HEADER_SIZE) continue;

        this.#pending = decodeHeader(this.#header);
        this.#headerBytes = 0;
        if (this.#pending.payloadLength === 0) {
          frames.push(this.#complete(Buffer.alloc(0)));
          continue;
        }
        this.#payload = Buffer.allocUnsafe(this.#pending.payloadLength);
      }

      const count = Math.min(this.#pending.payloadLength - this.#payloadBytes, chunk.length - offset);
      this.#payload!.set(chunk.subarray(offset, offset + count), this.#payloadBytes);
      this.#payloadBytes += count;
      offset += count;
      if (this.#payloadBytes === this.#pending.payloadLength) frames.push(this.#complete(this.#payload!));
    }

    return frames;
  }

  complete(): void {
    if (this.#headerBytes !== 0 || this.#pending) {
      throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, 'Stream ended during a frame');
    }
  }

  #complete(payload: Buffer): BridgeFrame {
    const header = this.#pending!;
    if (header.kind === BridgeMessageKind.Error) decodeErrorMessage(payload);
    const { payloadLength: _, ...frame } = header;
    this.#pending = undefined;
    this.#payload = undefined;
    this.#payloadBytes = 0;
    return { ...frame, payload };
  }
}

function decodeHeader(header: Buffer): DecodedHeader {
  if (!header.subarray(0, 4).equals(magic)) {
    throw new BridgeProtocolError(BridgeErrorCode.InvalidMagic, 'Invalid bridge magic');
  }
  const version = header.readUInt16LE(4);
  if (version !== BRIDGE_PROTOCOL_VERSION) {
    throw new BridgeProtocolError(BridgeErrorCode.UnsupportedVersion, `Unsupported bridge version: ${version}`);
  }

  const kind = header.readUInt8(6) as BridgeMessageKind;
  const flags = header.readUInt8(7);
  const opcode = header.readUInt16LE(8) as BridgeOpcode;
  const status = header.readUInt16LE(10) as BridgeErrorCode;
  const requestId = header.readUInt32LE(12);
  const payloadLength = header.readUInt32LE(16);
  const reserved = header.readUInt32LE(20);
  validateHeader(kind, flags, opcode, status, requestId, payloadLength, reserved);
  return { kind, opcode, status, requestId, payloadLength };
}

function validateHeader(
  kind: BridgeMessageKind,
  flags: number,
  opcode: BridgeOpcode,
  status: BridgeErrorCode,
  requestId: number,
  payloadLength: number,
  reserved: number,
): void {
  if (!validKinds.has(kind)) throw new BridgeProtocolError(BridgeErrorCode.InvalidMessageKind, `Unknown message kind: ${kind}`);
  if (flags !== 0) throw new BridgeProtocolError(BridgeErrorCode.InvalidFlags, `Unsupported flags: ${flags}`);
  if (!validOpcodes.has(opcode)) throw new BridgeProtocolError(BridgeErrorCode.UnknownOpcode, `Unknown opcode: ${opcode}`);
  if (reserved !== 0) throw new BridgeProtocolError(BridgeErrorCode.InvalidHeader, 'Reserved header bytes must be zero');
  if (!Number.isInteger(requestId) || requestId < 0 || requestId > 0xffffffff) {
    throw new BridgeProtocolError(BridgeErrorCode.InvalidRequestId, `Invalid request ID: ${requestId}`);
  }
  if (!Number.isInteger(payloadLength) || payloadLength < 0 || payloadLength > BRIDGE_MAX_PAYLOAD_LENGTH) {
    throw new BridgeProtocolError(BridgeErrorCode.PayloadTooLarge, `Invalid payload length: ${payloadLength}`);
  }
  if (kind === BridgeMessageKind.Error) {
    if (!validErrorCodes.has(status)) throw new BridgeProtocolError(BridgeErrorCode.InvalidStatus, `Invalid error status: ${status}`);
  } else if (status !== BridgeErrorCode.None) {
    throw new BridgeProtocolError(BridgeErrorCode.InvalidStatus, `Non-error frame has status: ${status}`);
  }
  if (kind === BridgeMessageKind.Handshake) {
    if (opcode !== BridgeOpcode.Control || requestId !== 0) {
      throw new BridgeProtocolError(BridgeErrorCode.InvalidRequestId, 'Handshake must use control opcode and request ID zero');
    }
  } else {
    if (requestId === 0) throw new BridgeProtocolError(BridgeErrorCode.InvalidRequestId, 'Non-handshake request ID must be nonzero');
    if (kind !== BridgeMessageKind.Error && opcode === BridgeOpcode.Control) {
      throw new BridgeProtocolError(BridgeErrorCode.UnknownOpcode, 'Control opcode is only valid for handshake and error frames');
    }
  }
  if (kind === BridgeMessageKind.Cancel && payloadLength !== 0) {
    throw new BridgeProtocolError(BridgeErrorCode.MalformedPayload, 'Cancellation payload must be empty');
  }
}
