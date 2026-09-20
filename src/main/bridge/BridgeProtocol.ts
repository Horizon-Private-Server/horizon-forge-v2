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
  ValidateUyaIso: 2,
  CreateDevelopmentIso: 3,
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
  InvalidInput: 13,
  Conflict: 14,
  InsufficientSpace: 15,
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

export interface DecodedHeader extends Omit<BridgeFrame, 'payload'> {
  payloadLength: number;
}

export interface HostRequest<T> {
  requestId: number;
  result: Promise<T>;
}

export class BridgeProtocolError extends Error {
  readonly code: BridgeErrorCode;

  constructor(code: BridgeErrorCode, message: string) {
    super(message);
    this.name = 'BridgeProtocolError';
    this.code = code;
  }
}
