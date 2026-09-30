import type {
  AssetModelPlacementDragData, AssetPlacementDragData, AssetSkyShellDragData,
} from '../types/AssetExplorer.js';
import type { EditorCommand, ProjectVector3 } from '../types/EditorRuntime.js';

export const ASSET_PLACEMENT_MIME = 'application/x-horizon-forge-asset';
export const SKY_SHELL_PLACEMENT_MIME = 'application/x-horizon-forge-sky-shell';

export function readAssetPlacementDrag(data: DataTransfer): AssetPlacementDragData | undefined {
  try {
    const value = JSON.parse(data.getData(ASSET_PLACEMENT_MIME)) as Record<string, unknown>;
    if (typeof value.assetId !== 'string' || !/^[0-9a-f]{64}$/.test(value.assetId)) return undefined;
    if (value.kind === 'sky') return Number.isInteger(value.shellIndex) && Number(value.shellIndex) >= 0
      ? value as unknown as AssetSkyShellDragData : undefined;
    return ['tie', 'shrub', 'moby'].includes(String(value.kind))
      && Number.isInteger(value.classId) && Number(value.classId) >= 0 && Number(value.classId) <= 0xffff
      ? value as unknown as AssetModelPlacementDragData : undefined;
  } catch {
    return undefined;
  }
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
