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
  decodeUyaAssetImportRequest,
  decodeUyaAssetImportResult,
  decodeUyaIsoValidation,
  decodeForgeProjectDescriptor,
  decodeProjectInspectRequest,
  decodeProjectRecoveryRequest,
  decodeProjectRenameRequest,
  decodeUyaProjectCreationRequest,
  decodeUyaProjectOptions,
  decodeUyaProjectPreflight,
  decodeUyaProjectPreflightRequest,
  encodeDevelopmentIso,
  encodeDevelopmentIsoRequest,
  encodeEchoRequest,
  encodeHandshake,
  encodeProgress,
  encodeText,
  encodeUyaAssetImportRequest,
  encodeUyaAssetImportResult,
  encodeUyaIsoValidation,
  encodeForgeProjectDescriptor,
  encodeProjectInspectRequest,
  encodeProjectRecoveryRequest,
  encodeProjectRenameRequest,
  encodeUyaProjectCreationRequest,
  encodeUyaProjectOptions,
  encodeUyaProjectPreflight,
  encodeUyaProjectPreflightRequest,
} from '../src/main/bridge/PayloadCodec.ts';
import {
  decodeCatalogCollectionRequest,
  decodeCatalogMaintenance,
  decodeCatalogMaintenanceRequest,
  decodeProjectAssetRepairRequest,
  encodeCatalogCollectionRequest,
  encodeCatalogMaintenance,
  encodeCatalogMaintenanceRequest,
  encodeProjectAssetRepairRequest,
} from '../src/main/bridge/MaintenancePayloadCodec.ts';
import {
  decodeUyaRenderPackageRequest,
  decodeUyaRenderPackageResult,
  encodeUyaRenderPackageRequest,
  encodeUyaRenderPackageResult,
} from '../src/main/bridge/RenderPayloadCodec.ts';

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
  const importRequest = {
    sourceIsoPath: copyRequest.sourcePath,
    catalogRootPath: '/assets',
    fingerprint: iso.fingerprint,
    revision: iso.revision,
    force: true,
  };
  assert.deepEqual(decodeUyaAssetImportRequest(encodeUyaAssetImportRequest(importRequest)), importRequest);
  const importResult = {
    completedLevels: 40, totalLevels: 40, assetAppearances: 900, uniqueAssets: 500, failedAssets: 2, resumed: true,
  };
  assert.deepEqual(decodeUyaAssetImportResult(encodeUyaAssetImportResult(importResult)), importResult);
  const projectOptions = { levels: [1, 3, 5], warnings: ['partial'] };
  assert.deepEqual(decodeUyaProjectOptions(encodeUyaProjectOptions(projectOptions)), projectOptions);
  const createProject = {
    sourceIsoPath: '/clean.iso', catalogRootPath: '/assets', projectPath: '/project', name: 'Test',
    fingerprint: iso.fingerprint, revision: '1.00', level: 3, allowPartial: true,
  };
  assert.deepEqual(decodeUyaProjectCreationRequest(encodeUyaProjectCreationRequest(createProject)), createProject);
  const preflightRequest = { sourceIsoPath: '/clean.iso', catalogRootPath: '/assets', level: 3 };
  assert.deepEqual(decodeUyaProjectPreflightRequest(encodeUyaProjectPreflightRequest(preflightRequest)), preflightRequest);
  const preflight = {
    level: 3, sourceInstanceCount: 422, renderableInstanceCount: 378, modelLessInstanceCount: 44,
    missingAssetInstanceCount: 0, missingClassCount: 0, warnings: ['partial'],
  };
  assert.deepEqual(decodeUyaProjectPreflight(encodeUyaProjectPreflight(preflight)), preflight);
  const inspectProject = { projectPath: '/project', catalogRootPath: '/assets' };
  assert.deepEqual(decodeProjectInspectRequest(encodeProjectInspectRequest(inspectProject)), inspectProject);
  const renameProject = { ...inspectProject, name: 'Renamed' };
  assert.deepEqual(decodeProjectRenameRequest(encodeProjectRenameRequest(renameProject)), renameProject);
  const recoveryRequest = { ...inspectProject, recoveryId: '1000-0123456789abcdef0123456789abcdef' };
  assert.deepEqual(decodeProjectRecoveryRequest(encodeProjectRecoveryRequest(recoveryRequest)), recoveryRequest);
  const repairRequest = { ...inspectProject, sourceIsoPath: '/clean.iso' };
  assert.deepEqual(decodeProjectAssetRepairRequest(encodeProjectAssetRepairRequest(repairRequest)), repairRequest);
  const maintenanceRequest = { catalogRootPath: '/assets', projectRoots: ['/projects', '/external'] };
  assert.deepEqual(
    decodeCatalogMaintenanceRequest(encodeCatalogMaintenanceRequest(maintenanceRequest)), maintenanceRequest,
  );
  const collectionRequest = { ...maintenanceRequest, confirmationToken: 'c'.repeat(64) };
  assert.deepEqual(decodeCatalogCollectionRequest(encodeCatalogCollectionRequest(collectionRequest)), collectionRequest);
  const maintenance = {
    projectCount: 2, catalogAssetCount: 500, protectedAssetCount: 450, candidateCount: 50,
    catalogCandidateCount: 48, candidateBytes: 123_456, confirmationToken: collectionRequest.confirmationToken,
    candidateKinds: ['Moby: 30', 'Tie: 20'], blockers: [],
  };
  assert.deepEqual(decodeCatalogMaintenance(encodeCatalogMaintenance(maintenance)), maintenance);
  const descriptor = {
    path: '/project', name: 'Test', targetGame: 'UYA', targetRegion: 'NTSC-U', targetRevision: '1.00',
    bakeProfile: 'uya-ntsc-u', baseLevel: 3, modifiedUnixMilliseconds: 1000,
    entityCount: 20, missingAssetCount: 0, isDirty: false, migrationPending: false, warnings: ['partial'],
    recoveries: [{
      id: recoveryRequest.recoveryId, createdUnixMilliseconds: 1000, name: 'Recovered',
      entityCount: 20, fingerprint: 'a'.repeat(64), size: 4096,
    }],
    missingAssets: [{
      id: 'b'.repeat(64), kind: 'Moby', entityCount: 2, repairable: true, provenance: ['UYA level 3'],
    }],
  };
  assert.deepEqual(decodeForgeProjectDescriptor(encodeForgeProjectDescriptor(descriptor)), descriptor);
  const renderRequest = {
    sourceIsoPath: '/clean.iso', cacheRootPath: '/cache', fingerprint: iso.fingerprint, level: 3,
  };
  assert.deepEqual(decodeUyaRenderPackageRequest(encodeUyaRenderPackageRequest(renderRequest)), renderRequest);
  const renderResult = {
    rootPath: '/cache/key', cacheKey: 'key',
    terrainPaths: ['tfrag/tfrag.gltf', 'tfrag/chunks/chunk1/tfrag.gltf'], cacheHit: true,
  };
  assert.deepEqual(decodeUyaRenderPackageResult(encodeUyaRenderPackageResult(renderResult)), renderResult);

  expectProtocolError(BridgeErrorCode.MalformedPayload, () => decodeText(Buffer.concat([encodeText('x'), Buffer.of(0)])));
});
