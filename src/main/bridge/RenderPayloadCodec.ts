import { PayloadReader, PayloadWriter } from './PayloadIO.ts';
import type { UyaRenderPackageRequest, UyaRenderPackageResult } from '../../types/BridgePayloads.js';

export function encodeUyaRenderPackageRequest(value: UyaRenderPackageRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.sourceIsoPath);
  writer.writeString(value.cacheRootPath);
  writer.writeString(value.fingerprint);
  writer.writeUInt32(value.level);
  writer.writeString(value.projectPath);
  writer.writeString(value.catalogRootPath);
  return writer.toBuffer();
}

export function decodeUyaRenderPackageRequest(payload: Uint8Array): UyaRenderPackageRequest {
  const reader = new PayloadReader(payload);
  const value = {
    sourceIsoPath: reader.readString(),
    cacheRootPath: reader.readString(),
    fingerprint: reader.readString(),
    level: reader.readUInt32(),
    projectPath: reader.readString(),
    catalogRootPath: reader.readString(),
  };
  reader.complete();
  return value;
}

export function encodeUyaRenderPackageResult(value: UyaRenderPackageResult): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.rootPath);
  writer.writeString(value.cacheKey);
  writer.writeStrings(value.terrainPaths);
  writer.writeUInt32(value.assets.length);
  value.assets.forEach((asset) => {
    writer.writeString(asset.assetId);
    writer.writeBoolean(asset.path !== undefined);
    if (asset.path !== undefined) writer.writeString(asset.path);
    writer.writeBoolean(asset.error !== undefined);
    if (asset.error !== undefined) writer.writeString(asset.error);
  });
  writer.writeBoolean(value.cacheHit);
  return writer.toBuffer();
}

export function decodeUyaRenderPackageResult(payload: Uint8Array): UyaRenderPackageResult {
  const reader = new PayloadReader(payload);
  const rootPath = reader.readString();
  const cacheKey = reader.readString();
  const terrainPaths = reader.readStrings();
  const assetCount = reader.readUInt32();
  if (assetCount > 100_000) throw new Error('Render asset list exceeds item limit');
  const assets = Array.from({ length: assetCount }, () => {
    const asset: UyaRenderPackageResult['assets'][number] = { assetId: reader.readString() };
    if (reader.readBoolean()) asset.path = reader.readString();
    if (reader.readBoolean()) asset.error = reader.readString();
    return asset;
  });
  const value = { rootPath, cacheKey, terrainPaths, assets, cacheHit: reader.readBoolean() };
  reader.complete();
  return value;
}
