import { malformed, PayloadReader, PayloadWriter } from './PayloadIO.ts';
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
  writer.writeBoolean(value.skyPath !== undefined);
  if (value.skyPath !== undefined) writer.writeString(value.skyPath);
  writer.writeBoolean(value.environment !== undefined);
  if (value.environment) {
    value.environment.backgroundColor.forEach((channel) => writer.writeUInt32(channel));
    value.environment.fogColor.forEach((channel) => writer.writeUInt32(channel));
    writer.writeFloat32(value.environment.fogNearDistance);
    writer.writeFloat32(value.environment.fogFarDistance);
    writer.writeFloat32(value.environment.fogNearIntensity);
    writer.writeFloat32(value.environment.fogFarIntensity);
  }
  writer.writeUInt32(value.assets.length);
  value.assets.forEach((asset) => {
    writer.writeString(asset.assetId);
    writer.writeString(asset.kind);
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
  const skyPath = reader.readBoolean() ? reader.readString() : undefined;
  const environment = reader.readBoolean() ? {
    backgroundColor: [reader.readUInt32(), reader.readUInt32(), reader.readUInt32()] as [number, number, number],
    fogColor: [reader.readUInt32(), reader.readUInt32(), reader.readUInt32()] as [number, number, number],
    fogNearDistance: reader.readFloat32(),
    fogFarDistance: reader.readFloat32(),
    fogNearIntensity: reader.readFloat32(),
    fogFarIntensity: reader.readFloat32(),
  } : undefined;
  const assetCount = reader.readUInt32();
  if (assetCount > 100_000) throw new Error('Render asset list exceeds item limit');
  const assets = Array.from({ length: assetCount }, () => {
    const assetId = reader.readString();
    const kind = reader.readString();
    if (kind !== 'moby' && kind !== 'tie' && kind !== 'shrub') malformed('Render asset kind is invalid');
    const asset: UyaRenderPackageResult['assets'][number] = { assetId, kind };
    if (reader.readBoolean()) asset.path = reader.readString();
    if (reader.readBoolean()) asset.error = reader.readString();
    return asset;
  });
  const value = { rootPath, cacheKey, terrainPaths, skyPath, environment, assets, cacheHit: reader.readBoolean() };
  reader.complete();
  return value;
}
