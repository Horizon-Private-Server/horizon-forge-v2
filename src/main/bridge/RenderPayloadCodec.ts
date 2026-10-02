import { malformed, PayloadReader, PayloadWriter } from './PayloadIO.ts';
import type {
  AssetPreviewRequest,
  AssetPreviewResult,
  UyaRenderPackageRequest,
  UyaRenderPackageResult,
} from '../../types/BridgePayloads.js';

export function encodeAssetPreviewRequest(value: AssetPreviewRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.cacheRootPath);
  writer.writeString(value.catalogRootPath);
  writer.writeString(value.assetId);
  writer.writeString(value.kind);
  writer.writeString(value.targetGame);
  writer.writeString(value.viewPreset);
  writer.writeBoolean(value.shellIndex !== undefined);
  if (value.shellIndex !== undefined) writer.writeUInt32(value.shellIndex);
  return writer.toBuffer();
}

export function decodeAssetPreviewRequest(payload: Uint8Array): AssetPreviewRequest {
  const reader = new PayloadReader(payload);
  const cacheRootPath = reader.readString();
  const catalogRootPath = reader.readString();
  const assetId = reader.readString();
  const kind = reader.readString();
  if (kind !== 'moby' && kind !== 'tie' && kind !== 'shrub' && kind !== 'texture' && kind !== 'sky')
    malformed('Asset preview kind is invalid');
  const value: AssetPreviewRequest = {
    cacheRootPath,
    catalogRootPath,
    assetId,
    kind,
    targetGame: reader.readString(),
    viewPreset: reader.readString(),
  };
  if (reader.readBoolean()) value.shellIndex = reader.readUInt32();
  reader.complete();
  return value;
}

export function encodeAssetPreviewResult(value: AssetPreviewResult): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.rootPath);
  writer.writeString(value.cacheKey);
  writer.writeString(value.modelPath);
  writer.writeBoolean(value.cacheHit);
  return writer.toBuffer();
}

export function decodeAssetPreviewResult(payload: Uint8Array): AssetPreviewResult {
  const reader = new PayloadReader(payload);
  const value = {
    rootPath: reader.readString(),
    cacheKey: reader.readString(),
    modelPath: reader.readString(),
    cacheHit: reader.readBoolean(),
  };
  reader.complete();
  return value;
}

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
    writer.writeFloat32(value.environment.deathHeight);
    writer.writeBoolean(value.environment.isSphericalWorld);
    value.environment.sphereCenter.forEach((coordinate) => writer.writeFloat32(coordinate));
    value.environment.shipPosition.forEach((coordinate) => writer.writeFloat32(coordinate));
    writer.writeFloat32(value.environment.shipRotationZ);
    writer.writeInt32(value.environment.shipPath);
    writer.writeInt32(value.environment.shipCameraCuboidStart);
    writer.writeInt32(value.environment.shipCameraCuboidEnd);
    writer.writeInt32(value.environment.chunkPlaneCount);
    writer.writeInt32(value.environment.coreSoundsCount);
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
  writer.writeUInt32(value.occlusionOctants.length);
  value.occlusionOctants.forEach((octant) => {
    writer.writeInt32(octant.x);
    writer.writeInt32(octant.y);
    writer.writeInt32(octant.z);
    writer.writeInt32(octant.maskIndex);
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
    deathHeight: reader.readFloat32(),
    isSphericalWorld: reader.readBoolean(),
    sphereCenter: [reader.readFloat32(), reader.readFloat32(), reader.readFloat32()] as [number, number, number],
    shipPosition: [reader.readFloat32(), reader.readFloat32(), reader.readFloat32()] as [number, number, number],
    shipRotationZ: reader.readFloat32(),
    shipPath: reader.readInt32(),
    shipCameraCuboidStart: reader.readInt32(),
    shipCameraCuboidEnd: reader.readInt32(),
    chunkPlaneCount: reader.readInt32(),
    coreSoundsCount: reader.readInt32(),
  } : undefined;
  const assetCount = reader.readUInt32();
  if (assetCount > 100_000) throw new Error('Render asset list exceeds item limit');
  const assets = Array.from({ length: assetCount }, () => {
    const assetId = reader.readString();
    const kind = reader.readString();
    if (kind !== 'moby' && kind !== 'tie' && kind !== 'shrub' && kind !== 'collision')
      malformed('Render asset kind is invalid');
    const asset: UyaRenderPackageResult['assets'][number] = { assetId, kind };
    if (reader.readBoolean()) asset.path = reader.readString();
    if (reader.readBoolean()) asset.error = reader.readString();
    return asset;
  });
  const octantCount = reader.readUInt32();
  if (octantCount > 1_000_000) malformed('Occlusion octant list exceeds item limit');
  const occlusionOctants = Array.from({ length: octantCount }, () => ({
    x: reader.readInt32(), y: reader.readInt32(), z: reader.readInt32(), maskIndex: reader.readInt32(),
  }));
  const value = {
    rootPath, cacheKey, terrainPaths, skyPath, environment, assets, occlusionOctants,
    cacheHit: reader.readBoolean(),
  };
  reader.complete();
  return value;
}
