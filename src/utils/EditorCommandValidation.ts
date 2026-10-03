import type { EditorCommand, EditorLevelSettings } from '../types/EditorRuntime.js';

const commandKinds = new Set([
  'setSelection', 'renameProject', 'updateTransform', 'updateTransforms',
  'renameEntity', 'setEntityLayer', 'setEntityState', 'undo', 'redo',
  'deleteEntities', 'duplicateEntities', 'copyEntities', 'pasteEntities',
  'updateLevelSettings', 'updateSplinePoints',
  'createEntityFromAsset',
  'addSkyShellFromAsset', 'updateSkyShell', 'reorderSkyShell',
  'removeTieCollisionProxy', 'setTieCollisionEnabled', 'setTieCollisionRawType',
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
  if (command.kind === 'removeTieCollisionProxy') return command.entityIds.length === 1;
  if (command.kind === 'setTieCollisionEnabled')
    return command.entityIds.length > 0 && typeof command.enabled === 'boolean';
  if (command.kind === 'setTieCollisionRawType')
    return command.entityIds.length === 1 && Number.isInteger(command.rawType)
      && Number(command.rawType) >= 0 && Number(command.rawType) <= 0xff;
  return true;
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
