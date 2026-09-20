import { Buffer } from 'node:buffer';

import type {
  DevelopmentIsoResult,
  ForgeHostStatus,
  ForgeProjectDescriptor,
  Progress,
  UyaAssetImportResult,
  UyaIsoIdentity,
  UyaProjectOptions,
  UyaProjectPreflight,
} from '../../types/ForgeApi.js';
import { MAX_LIST_ITEMS, malformed, PayloadReader, PayloadWriter } from './PayloadIO.ts';
import type {
  DevelopmentIsoRequest,
  EchoRequest,
  ProjectInspectRequest,
  ProjectRecoveryRequest,
  ProjectRenameRequest,
  UyaAssetImportRequest,
  UyaProjectCreationRequest,
  UyaProjectPreflightRequest,
} from '../../types/BridgePayloads.js';

export const MAX_ECHO_DELAY_MS = 60_000;

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

export function encodeUyaAssetImportRequest(value: UyaAssetImportRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.sourceIsoPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.fingerprint);
  writer.writeString(value.revision);
  writer.writeBoolean(value.force);
  return writer.toBuffer();
}

export function decodeUyaAssetImportRequest(payload: Uint8Array): UyaAssetImportRequest {
  const reader = new PayloadReader(payload);
  const value = {
    sourceIsoPath: reader.readString(),
    catalogRootPath: reader.readString(),
    fingerprint: reader.readString(),
    revision: reader.readString(),
    force: reader.readBoolean(),
  };
  reader.complete();
  return value;
}

export function encodeUyaAssetImportResult(value: UyaAssetImportResult): Buffer {
  const writer = new PayloadWriter();
  writer.writeUInt32(value.completedLevels);
  writer.writeUInt32(value.totalLevels);
  writer.writeUInt32(value.assetAppearances);
  writer.writeUInt32(value.uniqueAssets);
  writer.writeUInt32(value.failedAssets);
  writer.writeBoolean(value.resumed);
  return writer.toBuffer();
}

export function decodeUyaAssetImportResult(payload: Uint8Array): UyaAssetImportResult {
  const reader = new PayloadReader(payload);
  const value = {
    completedLevels: reader.readUInt32(),
    totalLevels: reader.readUInt32(),
    assetAppearances: reader.readUInt32(),
    uniqueAssets: reader.readUInt32(),
    failedAssets: reader.readUInt32(),
    resumed: reader.readBoolean(),
  };
  reader.complete();
  return value;
}

export function encodeUyaProjectOptions(value: UyaProjectOptions): Buffer {
  const writer = new PayloadWriter();
  writer.writeUInt32s(value.levels);
  writer.writeStrings(value.warnings);
  return writer.toBuffer();
}

export function decodeUyaProjectOptions(payload: Uint8Array): UyaProjectOptions {
  const reader = new PayloadReader(payload);
  const value = { levels: reader.readUInt32s(), warnings: reader.readStrings() };
  reader.complete();
  return value;
}

export function encodeUyaProjectCreationRequest(value: UyaProjectCreationRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.sourceIsoPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.projectPath);
  writer.writeString(value.name);
  writer.writeString(value.fingerprint);
  writer.writeString(value.revision);
  writer.writeUInt32(value.level);
  writer.writeBoolean(value.allowPartial);
  return writer.toBuffer();
}

export function decodeUyaProjectCreationRequest(payload: Uint8Array): UyaProjectCreationRequest {
  const reader = new PayloadReader(payload);
  const value = {
    sourceIsoPath: reader.readString(),
    catalogRootPath: reader.readString(),
    projectPath: reader.readString(),
    name: reader.readString(),
    fingerprint: reader.readString(),
    revision: reader.readString(),
    level: reader.readUInt32(),
    allowPartial: reader.readBoolean(),
  };
  reader.complete();
  return value;
}

export function encodeUyaProjectPreflightRequest(value: UyaProjectPreflightRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.sourceIsoPath);
  writer.writeString(value.catalogRootPath);
  writer.writeUInt32(value.level);
  return writer.toBuffer();
}

export function decodeUyaProjectPreflightRequest(payload: Uint8Array): UyaProjectPreflightRequest {
  const reader = new PayloadReader(payload);
  const value = { sourceIsoPath: reader.readString(), catalogRootPath: reader.readString(), level: reader.readUInt32() };
  reader.complete();
  return value;
}

export function encodeUyaProjectPreflight(value: UyaProjectPreflight): Buffer {
  const writer = new PayloadWriter();
  writer.writeUInt32(value.level);
  writer.writeUInt32(value.sourceInstanceCount);
  writer.writeUInt32(value.renderableInstanceCount);
  writer.writeUInt32(value.modelLessInstanceCount);
  writer.writeUInt32(value.missingAssetInstanceCount);
  writer.writeUInt32(value.missingClassCount);
  writer.writeStrings(value.warnings);
  return writer.toBuffer();
}

export function decodeUyaProjectPreflight(payload: Uint8Array): UyaProjectPreflight {
  const reader = new PayloadReader(payload);
  const value = {
    level: reader.readUInt32(),
    sourceInstanceCount: reader.readUInt32(),
    renderableInstanceCount: reader.readUInt32(),
    modelLessInstanceCount: reader.readUInt32(),
    missingAssetInstanceCount: reader.readUInt32(),
    missingClassCount: reader.readUInt32(),
    warnings: reader.readStrings(),
  };
  reader.complete();
  return value;
}

export function encodeProjectInspectRequest(value: ProjectInspectRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.projectPath);
  writer.writeString(value.catalogRootPath);
  return writer.toBuffer();
}

export function decodeProjectInspectRequest(payload: Uint8Array): ProjectInspectRequest {
  const reader = new PayloadReader(payload);
  const value = { projectPath: reader.readString(), catalogRootPath: reader.readString() };
  reader.complete();
  return value;
}

export function encodeProjectRenameRequest(value: ProjectRenameRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.projectPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.name);
  return writer.toBuffer();
}

export function decodeProjectRenameRequest(payload: Uint8Array): ProjectRenameRequest {
  const reader = new PayloadReader(payload);
  const value = { projectPath: reader.readString(), catalogRootPath: reader.readString(), name: reader.readString() };
  reader.complete();
  return value;
}

export function encodeProjectRecoveryRequest(value: ProjectRecoveryRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.projectPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.recoveryId);
  return writer.toBuffer();
}

export function decodeProjectRecoveryRequest(payload: Uint8Array): ProjectRecoveryRequest {
  const reader = new PayloadReader(payload);
  const value = { projectPath: reader.readString(), catalogRootPath: reader.readString(), recoveryId: reader.readString() };
  reader.complete();
  return value;
}

export function encodeForgeProjectDescriptor(value: ForgeProjectDescriptor): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.path);
  writer.writeString(value.name);
  writer.writeString(value.targetGame);
  writer.writeString(value.targetRegion);
  writer.writeString(value.targetRevision);
  writer.writeString(value.bakeProfile);
  writer.writeUInt32(value.baseLevel);
  writer.writeUInt64(value.modifiedUnixMilliseconds);
  writer.writeUInt32(value.entityCount);
  writer.writeUInt32(value.missingAssetCount);
  writer.writeBoolean(value.isDirty);
  writer.writeBoolean(value.migrationPending);
  writer.writeStrings(value.warnings);
  if (value.recoveries.length > MAX_LIST_ITEMS) malformed('List exceeds item limit');
  writer.writeUInt32(value.recoveries.length);
  value.recoveries.forEach((recovery) => {
    writer.writeString(recovery.id);
    writer.writeUInt64(recovery.createdUnixMilliseconds);
    writer.writeString(recovery.name);
    writer.writeUInt32(recovery.entityCount);
    writer.writeString(recovery.fingerprint);
    writer.writeUInt64(recovery.size);
  });
  if (value.missingAssets.length > 4_096) malformed('Missing-asset list exceeds item limit');
  writer.writeUInt32(value.missingAssets.length);
  value.missingAssets.forEach((missing) => {
    writer.writeString(missing.id);
    writer.writeString(missing.kind);
    writer.writeUInt32(missing.entityCount);
    writer.writeBoolean(missing.repairable);
    writer.writeStrings(missing.provenance);
  });
  return writer.toBuffer();
}

export function decodeForgeProjectDescriptor(payload: Uint8Array): ForgeProjectDescriptor {
  const reader = new PayloadReader(payload);
  const value: ForgeProjectDescriptor = {
    path: reader.readString(),
    name: reader.readString(),
    targetGame: reader.readString(),
    targetRegion: reader.readString(),
    targetRevision: reader.readString(),
    bakeProfile: reader.readString(),
    baseLevel: reader.readUInt32(),
    modifiedUnixMilliseconds: reader.readUInt64(),
    entityCount: reader.readUInt32(),
    missingAssetCount: reader.readUInt32(),
    isDirty: reader.readBoolean(),
    migrationPending: reader.readBoolean(),
    warnings: reader.readStrings(),
    recoveries: [],
    missingAssets: [],
  };
  const recoveryCount = reader.readUInt32();
  if (recoveryCount > MAX_LIST_ITEMS) malformed('List exceeds item limit');
  value.recoveries = Array.from({ length: recoveryCount }, () => ({
    id: reader.readString(),
    createdUnixMilliseconds: reader.readUInt64(),
    name: reader.readString(),
    entityCount: reader.readUInt32(),
    fingerprint: reader.readString(),
    size: reader.readUInt64(),
  }));
  const missingCount = reader.readUInt32();
  if (missingCount > 4_096) malformed('Missing-asset list exceeds item limit');
  value.missingAssets = Array.from({ length: missingCount }, () => ({
    id: reader.readString(),
    kind: reader.readString(),
    entityCount: reader.readUInt32(),
    repairable: reader.readBoolean(),
    provenance: reader.readStrings(),
  }));
  reader.complete();
  return value;
}
