import type {
  AssetModelPlacementDragData, AssetPlacementDragData, AssetSkyShellDragData, AssetTfragPlacementDragData,
} from '../types/AssetExplorer.js';
import type { EditorCommand, EditorEntity, ProjectVector3 } from '../types/EditorRuntime.js';

export const ASSET_PLACEMENT_MIME = 'application/x-horizon-forge-asset';
export const SKY_SHELL_PLACEMENT_MIME = 'application/x-horizon-forge-sky-shell';

export function readAssetPlacementDrag(data: DataTransfer): AssetPlacementDragData | undefined {
  try {
    const value = JSON.parse(data.getData(ASSET_PLACEMENT_MIME)) as Record<string, unknown>;
    if (typeof value.assetId !== 'string' || !/^[0-9a-f]{64}$/.test(value.assetId)) return undefined;
    if (value.kind === 'sky') {
      if (!Number.isInteger(value.shellIndex) || Number(value.shellIndex) < 0
        || value.dropError !== undefined && (typeof value.dropError !== 'string' || value.dropError.length > 512))
        return undefined;
      return {
        assetId: value.assetId,
        kind: 'sky',
        shellIndex: Number(value.shellIndex),
        ...(value.dropError ? { dropError: value.dropError as string } : {}),
      };
    }
    if (value.kind === 'tfrag')
      return typeof value.importId === 'string' && /^[0-9a-f]{64}$/.test(value.importId)
        ? value as unknown as AssetTfragPlacementDragData : undefined;
    return ['tie', 'shrub', 'moby'].includes(String(value.kind))
      && Number.isInteger(value.classId) && Number(value.classId) >= 0 && Number(value.classId) <= 0xffff
      ? value as unknown as AssetModelPlacementDragData : undefined;
  } catch {
    return undefined;
  }
}

export function createTfragPlacementCommand(
  asset: AssetTfragPlacementDragData,
  position: ProjectVector3,
  entities: EditorEntity[],
): EditorCommand | undefined {
  const pieces = entities.filter((entity) => entity.tfrag?.customImportId === asset.importId
    && entity.tfrag.sourcePosition);
  const anchor = pieces[0];
  if (!anchor?.tfrag?.sourcePosition) return undefined;
  const currentOffset = subtract(anchor.transform.position, anchor.tfrag.sourcePosition);
  const delta = subtract(position, currentOffset);
  return {
    id: crypto.randomUUID(),
    kind: 'updateTransforms',
    entityIds: pieces.map((entity) => entity.id),
    transforms: pieces.map((entity) => ({
      entityId: entity.id,
      transform: {
        ...entity.transform,
        position: add(entity.transform.position, delta),
      },
    })),
  };
}

export function createAssetPlacementCommand(asset: AssetModelPlacementDragData, position: ProjectVector3): EditorCommand {
  return {
    id: crypto.randomUUID(),
    kind: 'createEntityFromAsset',
    entityIds: [],
    placement: {
      assetId: asset.assetId,
      kind: asset.kind === 'tie' ? 'Tie' : asset.kind === 'shrub' ? 'Shrub' : 'Moby',
      classId: asset.classId,
      transform: {
        position,
        rotation: { x: 0, y: 0, z: 0, w: 1 },
        scale: { x: 1, y: 1, z: 1 },
      },
    },
  };
}

export function createSkyShellAddCommand(asset: AssetSkyShellDragData): EditorCommand {
  return {
    id: crypto.randomUUID(),
    kind: 'addSkyShellFromAsset',
    entityIds: [],
    source: { assetId: asset.assetId, shellIndex: asset.shellIndex },
  };
}

function add(left: ProjectVector3, right: ProjectVector3): ProjectVector3 {
  return { x: left.x + right.x, y: left.y + right.y, z: left.z + right.z };
}

function subtract(left: ProjectVector3, right: ProjectVector3): ProjectVector3 {
  return { x: left.x - right.x, y: left.y - right.y, z: left.z - right.z };
}
