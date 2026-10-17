import type { CustomModelPreviewDetails } from '../types/AssetExplorer.js';

export function parseCustomModelPreviewDetails(value: unknown): CustomModelPreviewDetails {
  if (!isRecord(value) || value.schemaVersion !== 1 || value.authoritative !== false
    || typeof value.sourceName !== 'string' || !integer(value.targetClassId)
    || !count(value.vertexCount) || !count(value.triangleCount) || !count(value.packetCount)
    || !count(value.materialCount) || !count(value.textureCount) || !count(value.paletteCount)
    || typeof value.lodPolicy !== 'string' || !bounds(value.bounds)
    || !Array.isArray(value.diagnostics) || !value.diagnostics.every((item) => typeof item === 'string')
    || !Array.isArray(value.primitives) || !value.primitives.every(primitive))
    throw new Error('Custom model preview metadata is invalid.');
  return value as unknown as CustomModelPreviewDetails;
}

function primitive(value: unknown): boolean {
  return isRecord(value)
    && count(value.sourceMeshIndex) && count(value.sourcePrimitiveIndex)
    && count(value.nativeMaterialIndex) && count(value.firstTriangle)
    && count(value.triangleCount) && count(value.firstPacket) && count(value.packetCount);
}

function bounds(value: unknown): boolean {
  return isRecord(value) && finite(value.minimumX) && finite(value.minimumY) && finite(value.minimumZ)
    && finite(value.maximumX) && finite(value.maximumY) && finite(value.maximumZ);
}

function count(value: unknown): value is number {
  return integer(value) && value >= 0;
}

function integer(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value);
}

function finite(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
