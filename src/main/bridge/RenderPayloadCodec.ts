import { PayloadReader, PayloadWriter } from './PayloadIO.ts';
import type { UyaRenderPackageRequest, UyaRenderPackageResult } from '../../types/BridgePayloads.js';

export function encodeUyaRenderPackageRequest(value: UyaRenderPackageRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.sourceIsoPath);
  writer.writeString(value.cacheRootPath);
  writer.writeString(value.fingerprint);
  writer.writeUInt32(value.level);
  return writer.toBuffer();
}

export function decodeUyaRenderPackageRequest(payload: Uint8Array): UyaRenderPackageRequest {
  const reader = new PayloadReader(payload);
  const value = {
    sourceIsoPath: reader.readString(),
    cacheRootPath: reader.readString(),
    fingerprint: reader.readString(),
    level: reader.readUInt32(),
  };
  reader.complete();
  return value;
}

export function encodeUyaRenderPackageResult(value: UyaRenderPackageResult): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.rootPath);
  writer.writeString(value.cacheKey);
  writer.writeStrings(value.terrainPaths);
  writer.writeBoolean(value.cacheHit);
  return writer.toBuffer();
}

export function decodeUyaRenderPackageResult(payload: Uint8Array): UyaRenderPackageResult {
  const reader = new PayloadReader(payload);
  const value = {
    rootPath: reader.readString(),
    cacheKey: reader.readString(),
    terrainPaths: reader.readStrings(),
    cacheHit: reader.readBoolean(),
  };
  reader.complete();
  return value;
}
