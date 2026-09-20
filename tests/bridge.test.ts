import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import {
  BRIDGE_HEADER_SIZE,
  BRIDGE_MAX_PAYLOAD_LENGTH,
  BridgeFrameDecoder,
  decodeErrorMessage,
  encodeFrame,
} from '../src/main/bridge/FrameCodec.ts';
import {
  BridgeErrorCode,
  BridgeMessageKind,
  BridgeProtocolError,
  type BridgeFrame,
} from '../src/main/bridge/BridgeProtocol.ts';
import {
  decodeEchoRequest,
  decodeDevelopmentIso,
  decodeDevelopmentIsoRequest,
  decodeHandshake,
  decodeProgress,
  decodeText,
  decodeUyaIsoValidation,
  encodeDevelopmentIso,
  encodeDevelopmentIsoRequest,
  encodeEchoRequest,
  encodeHandshake,
  encodeProgress,
  encodeText,
  encodeUyaIsoValidation,
} from '../src/main/bridge/PayloadCodec.ts';

interface GoldenFrame {
  name: string;
  kind: BridgeFrame['kind'];
  opcode: BridgeFrame['opcode'];
  status: BridgeFrame['status'];
  requestId: number;
  payloadHex: string;
  frameHex: string;
}

const vectors = JSON.parse(
  readFileSync(new URL('./fixtures/bridge-v1.json', import.meta.url), 'utf8'),
) as GoldenFrame[];

function frameFrom(vector: GoldenFrame): BridgeFrame {
  return {
    kind: vector.kind,
    opcode: vector.opcode,
    status: vector.status,
    requestId: vector.requestId,
    payload: Buffer.from(vector.payloadHex, 'hex'),
  };
}

function assertFrame(actual: BridgeFrame, expected: GoldenFrame): void {
  assert.equal(actual.kind, expected.kind);
  assert.equal(actual.opcode, expected.opcode);
  assert.equal(actual.status, expected.status);
  assert.equal(actual.requestId, expected.requestId);
  assert.equal(Buffer.from(actual.payload).toString('hex'), expected.payloadHex);
}

function expectProtocolError(code: number, action: () => unknown): void {
  assert.throws(action, (error) => error instanceof BridgeProtocolError && error.code === code);
}

test('shared golden vectors encode and decode exactly', () => {
  for (const vector of vectors) {
    assert.equal(encodeFrame(frameFrom(vector)).toString('hex'), vector.frameHex, vector.name);
    const decoder = new BridgeFrameDecoder();
    const [decoded] = decoder.push(Buffer.from(vector.frameHex, 'hex'));
    decoder.complete();
    assertFrame(decoded!, vector);
  }
  assert.equal(decodeErrorMessage(frameFrom(vectors.at(-1)!).payload), 'bad');
});

test('fragmented and coalesced reads retain frame boundaries', () => {
  const stream = Buffer.concat(vectors.map((vector) => Buffer.from(vector.frameHex, 'hex')));

  const fragmented = new BridgeFrameDecoder();
  const fragmentedFrames: BridgeFrame[] = [];
  for (const byte of stream) fragmentedFrames.push(...fragmented.push(Uint8Array.of(byte)));
  fragmented.complete();
  assert.equal(fragmentedFrames.length, vectors.length);
  fragmentedFrames.forEach((frame, index) => assertFrame(frame, vectors[index]!));

  const coalesced = new BridgeFrameDecoder();
  const coalescedFrames = coalesced.push(stream);
  coalesced.complete();
  assert.equal(coalescedFrames.length, vectors.length);
});

test('invalid headers fail before payload allocation', () => {
  const valid = Buffer.from(vectors[1]!.frameHex, 'hex').subarray(0, BRIDGE_HEADER_SIZE);

  const badMagic = Buffer.from(valid);
  badMagic[0] ^= 0xff;
  expectProtocolError(BridgeErrorCode.InvalidMagic, () => new BridgeFrameDecoder().push(badMagic));

  const badVersion = Buffer.from(valid);
  badVersion.writeUInt16LE(2, 4);
  expectProtocolError(BridgeErrorCode.UnsupportedVersion, () => new BridgeFrameDecoder().push(badVersion));

  const badOpcode = Buffer.from(valid);
  badOpcode.writeUInt16LE(0xffff, 8);
  expectProtocolError(BridgeErrorCode.UnknownOpcode, () => new BridgeFrameDecoder().push(badOpcode));

  const oversized = Buffer.from(valid);
  oversized.writeUInt32LE(BRIDGE_MAX_PAYLOAD_LENGTH + 1, 16);
  expectProtocolError(BridgeErrorCode.PayloadTooLarge, () => new BridgeFrameDecoder().push(oversized));
});

test('truncated and malformed payloads fail safely', () => {
  const truncated = new BridgeFrameDecoder();
  truncated.push(Buffer.from(vectors[1]!.frameHex, 'hex').subarray(0, BRIDGE_HEADER_SIZE + 1));
  expectProtocolError(BridgeErrorCode.MalformedPayload, () => truncated.complete());

  const malformedError = Buffer.from(vectors.at(-1)!.frameHex, 'hex');
  malformedError.writeUInt32LE(4, BRIDGE_HEADER_SIZE);
  expectProtocolError(BridgeErrorCode.MalformedPayload, () => new BridgeFrameDecoder().push(malformedError));

  expectProtocolError(BridgeErrorCode.MalformedPayload, () => encodeFrame({
    ...frameFrom(vectors[4]!),
    kind: BridgeMessageKind.Cancel,
    payload: Uint8Array.of(1),
  }));
});

test('operation payloads round trip and reject trailing data', () => {
  const handshake = {
    hostVersion: '0.1.0',
    sdkRevision: 'abc123',
    supportedGames: ['UYA'],
    capabilities: ['bridge.echo'],
  };
  assert.deepEqual(decodeHandshake(encodeHandshake(handshake)), handshake);
  assert.deepEqual(decodeEchoRequest(encodeEchoRequest('hello', 50)), { message: 'hello', delayMs: 50 });
  assert.equal(decodeText(encodeText('result')), 'result');
  assert.deepEqual(decodeProgress(encodeProgress(2, 10)), { completed: 2, total: 10 });
  const iso = {
    isSupported: true,
    game: 'UYA',
    region: 'NTSC-U',
    revision: '1.00',
    serial: 'SCUS-97353',
    size: 4_379_377_664,
    fingerprint: 'ba9f2b38c7346e7b6e5b8e87717d5893',
    diagnostic: 'verified',
  };
  assert.deepEqual(decodeUyaIsoValidation(encodeUyaIsoValidation(iso)), iso);
  const copyRequest = {
    sourcePath: '/clean.iso', targetPath: '/development.iso', fingerprint: iso.fingerprint, overwrite: true,
  };
  assert.deepEqual(decodeDevelopmentIsoRequest(encodeDevelopmentIsoRequest(copyRequest)), copyRequest);
  const copyResult = { path: copyRequest.targetPath, size: iso.size, fingerprint: iso.fingerprint };
  assert.deepEqual(decodeDevelopmentIso(encodeDevelopmentIso(copyResult)), copyResult);

  expectProtocolError(BridgeErrorCode.MalformedPayload, () => decodeText(Buffer.concat([encodeText('x'), Buffer.of(0)])));
});
