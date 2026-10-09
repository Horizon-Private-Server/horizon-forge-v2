import type { EditorCommand, EditorLevelSettings } from '../types/EditorRuntime.js';

const commandKinds = new Set([
  'setSelection', 'renameProject', 'updateTransform', 'updateTransforms',
  'renameEntity', 'setEntityLayer', 'setEntityState', 'undo', 'redo',
  'deleteEntities', 'duplicateEntities', 'copyEntities', 'pasteEntities',
  'updateLevelSettings', 'updateSplinePoints',
  'createEntityFromAsset',
  'addSkyShellFromAsset', 'updateSkyShell', 'reorderSkyShell',
  'removeInstancedCollisionProxy', 'setInstancedCollisionEnabled', 'setInstancedCollisionRawType',
  'setInstancedCollisionFaceTypes',
  'setEntityReference',
  'updatePaletteOptimization',
  'replaceHudTexture', 'removeHudTextureOverride', 'addHudIcon', 'removeHudIcon',
  'replaceFxTexture', 'removeFxTextureOverride', 'addFxTexture', 'removeFxTexture',
]);

export function isEditorCommand(value: unknown): value is EditorCommand {
  if (!value || typeof value !== 'object') return false;
  const command = value as Record<string, unknown>;
  if (typeof command.id !== 'string'
    || !commandKinds.has(String(command.kind))
    || !Array.isArray(command.entityIds)
    || command.entityIds.some((id) => typeof id !== 'string')) return false;
  if (command.kind === 'updateLevelSettings')
    return command.entityIds.length === 0 && isLevelSettings(command.levelSettings);
  if (command.kind === 'updatePaletteOptimization') {
    if (!command.paletteOptimization || typeof command.paletteOptimization !== 'object') return false;
    const profile = command.paletteOptimization as Record<string, unknown>;
    return command.entityIds.length === 0
      && profile.mappingVersion === 'paletteOptimization.v1'
      && Number.isInteger(profile.strength)
      && Number(profile.strength) >= 0 && Number(profile.strength) <= 100;
  }
  if (command.kind === 'updateSplinePoints')
    return command.entityIds.length === 1 && Array.isArray(command.points)
      && command.points.length <= 100_000 && command.points.every(isPoint);
  if (command.kind === 'createEntityFromAsset')
    return command.entityIds.length === 0 && isAssetPlacement(command.placement);
  if (command.kind === 'addSkyShellFromAsset')
    return command.entityIds.length === 0 && isSkyShellSource(command.source);
  if (command.kind === 'updateSkyShell')
    return command.entityIds.length === 1 && isSkyShellUpdate(command.update);
  if (command.kind === 'reorderSkyShell')
    return command.entityIds.length === 1 && Number.isInteger(command.destinationOrder)
      && Number(command.destinationOrder) >= 0 && Number(command.destinationOrder) <= 100_000;
  if (command.kind === 'removeInstancedCollisionProxy') return command.entityIds.length === 1;
  if (command.kind === 'setInstancedCollisionEnabled')
    return command.entityIds.length > 0
      && (command.enabled === null || typeof command.enabled === 'boolean');
  if (command.kind === 'setInstancedCollisionRawType')
    return command.entityIds.length === 1 && Number.isInteger(command.rawType)
      && Number(command.rawType) >= 0 && Number(command.rawType) <= 0xff;
  if (command.kind === 'setInstancedCollisionFaceTypes')
    return command.entityIds.length === 1
      && typeof command.expectedProxyAssetId === 'string'
      && /^[0-9a-f]{64}$/.test(command.expectedProxyAssetId)
      && Array.isArray(command.faceTypes) && command.faceTypes.length > 0
      && command.faceTypes.length <= 100_000
      && command.faceTypes.every(isCollisionFaceType)
      && new Set(command.faceTypes.map((value) => value.faceIndex)).size === command.faceTypes.length;
  if (command.kind === 'setEntityReference')
    return command.entityIds.length === 1 && isReferenceUpdate(command.reference);
  if (command.kind === 'replaceHudTexture')
    return command.entityIds.length === 0 && isAssetId(command.sourceAssetId)
      && isHudImage(command.imageFormat, command.imageBytes);
  if (command.kind === 'removeHudTextureOverride')
    return command.entityIds.length === 0 && isAssetId(command.sourceAssetId);
  if (command.kind === 'addHudIcon')
    return command.entityIds.length === 0
      && Number.isInteger(command.spriteId) && Number(command.spriteId) >= 0
      && Number(command.spriteId) <= 0xffff
      && Number.isInteger(command.bankIndex) && Number(command.bankIndex) >= 0
      && Number(command.bankIndex) < 5
      && isHudImage(command.imageFormat, command.imageBytes);
  if (command.kind === 'removeHudIcon')
    return command.entityIds.length === 0
      && Number.isInteger(command.spriteId) && Number(command.spriteId) >= 0
      && Number(command.spriteId) <= 0xffff;
  if (command.kind === 'replaceFxTexture')
    return command.entityIds.length === 0 && isAssetId(command.sourceAssetId)
      && isHudImage(command.imageFormat, command.imageBytes);
  if (command.kind === 'removeFxTextureOverride')
    return command.entityIds.length === 0 && isAssetId(command.sourceAssetId);
  if (command.kind === 'addFxTexture')
    return command.entityIds.length === 0 && isHudImage(command.imageFormat, command.imageBytes);
  if (command.kind === 'removeFxTexture')
    return command.entityIds.length === 0 && Number.isInteger(command.index)
      && Number(command.index) >= 0 && Number(command.index) < 4_096;
  return true;
}

function isAssetId(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
}

function isHudImage(format: unknown, bytes: unknown): boolean {
  return (format === 'png' || format === 'pif') && bytes instanceof Uint8Array
    && bytes.byteLength > 0 && bytes.byteLength <= 16 * 1024 * 1024;
}

function isReferenceUpdate(value: unknown): boolean {
  if (!value || typeof value !== 'object') return false;
  const reference = value as Record<string, unknown>;
  return typeof reference.fieldKey === 'string'
    && reference.fieldKey.length > 0 && reference.fieldKey.length <= 128
    && /^[A-Za-z0-9.\[\]-]+$/.test(reference.fieldKey)
    && (reference.sourceValue === undefined || Number.isInteger(reference.sourceValue)
      && Number(reference.sourceValue) >= 0 && Number(reference.sourceValue) <= 0x7fff_ffff)
    && (reference.targetEntityId === undefined || typeof reference.targetEntityId === 'string');
}

function isCollisionFaceType(value: unknown): value is { faceIndex: number; rawType: number } {
  if (!value || typeof value !== 'object') return false;
  const faceType = value as Record<string, unknown>;
  return Number.isInteger(faceType.faceIndex) && Number(faceType.faceIndex) >= 0
    && Number(faceType.faceIndex) <= 0x7fff_ffff
    && Number.isInteger(faceType.rawType) && Number(faceType.rawType) >= 0
    && Number(faceType.rawType) <= 0xff;
}

function isSkyShellSource(value: unknown): boolean {
  if (!value || typeof value !== 'object') return false;
  const source = value as Record<string, unknown>;
  return typeof source.assetId === 'string' && /^[0-9a-f]{64}$/.test(source.assetId)
    && Number.isInteger(source.shellIndex) && Number(source.shellIndex) >= 0 && Number(source.shellIndex) <= 0xffff;
}

function isSkyShellUpdate(value: unknown): boolean {
  if (!value || typeof value !== 'object') return false;
  const update = value as Record<string, unknown>;
  return (update.initialRotationRadians !== undefined || update.angularVelocityRadiansPerSecond !== undefined)
    && (update.initialRotationRadians === undefined || isVector(update.initialRotationRadians))
    && (update.angularVelocityRadiansPerSecond === undefined || isVector(update.angularVelocityRadiansPerSecond));
}

function isAssetPlacement(value: unknown): boolean {
  if (!value || typeof value !== 'object') return false;
  const placement = value as Record<string, unknown>;
  return typeof placement.assetId === 'string' && /^[0-9a-f]{64}$/.test(placement.assetId)
    && ['Tie', 'Shrub', 'Moby'].includes(String(placement.kind))
    && Number.isInteger(placement.classId) && Number(placement.classId) >= 0
    && Number(placement.classId) <= 0xffff
    && isTransform(placement.transform);
}

function isTransform(value: unknown): boolean {
  if (!value || typeof value !== 'object') return false;
  const transform = value as Record<string, unknown>;
  return isVector(transform.position) && isVector(transform.scale)
    && !!transform.rotation && typeof transform.rotation === 'object'
    && ['x', 'y', 'z', 'w'].every((key) => isFiniteNumber((transform.rotation as Record<string, unknown>)[key], Number.NEGATIVE_INFINITY));
}

function isVector(value: unknown): boolean {
  return !!value && typeof value === 'object'
    && ['x', 'y', 'z'].every((key) => isFiniteNumber((value as Record<string, unknown>)[key], Number.NEGATIVE_INFINITY));
}

function isPoint(value: unknown): boolean {
  if (!value || typeof value !== 'object') return false;
  const point = value as Record<string, unknown>;
  return isFiniteNumber(point.x, Number.NEGATIVE_INFINITY)
    && isFiniteNumber(point.y, Number.NEGATIVE_INFINITY)
    && isFiniteNumber(point.z, Number.NEGATIVE_INFINITY)
    && isFiniteNumber(point.w, Number.NEGATIVE_INFINITY);
}

function isLevelSettings(value: unknown): value is EditorLevelSettings {
  if (!value || typeof value !== 'object') return false;
  const settings = value as Record<string, unknown>;
  return isColor(settings.backgroundColor)
    && isColor(settings.fogColor)
    && isFiniteNumber(settings.fogNearDistance, 0)
    && isFiniteNumber(settings.fogFarDistance, 0)
    && isFiniteNumber(settings.fogNearIntensity, 0, 255)
    && isFiniteNumber(settings.fogFarIntensity, 0, 255);
}

function isColor(value: unknown): boolean {
  return Array.isArray(value) && value.length === 3
    && value.every((channel) => Number.isInteger(channel) && channel >= 0 && channel <= 255);
}

function isFiniteNumber(value: unknown, minimum: number, maximum = Number.POSITIVE_INFINITY): boolean {
  return typeof value === 'number' && Number.isFinite(value) && value >= minimum && value <= maximum;
}
