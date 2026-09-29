import type { AssetPlacementDragData } from '../types/AssetExplorer.js';
import type { EditorCommand, ProjectVector3 } from '../types/EditorRuntime.js';

export const ASSET_PLACEMENT_MIME = 'application/x-horizon-forge-asset';

export function readAssetPlacementDrag(data: DataTransfer): AssetPlacementDragData | undefined {
  try {
    const value = JSON.parse(data.getData(ASSET_PLACEMENT_MIME)) as Partial<AssetPlacementDragData>;
    return typeof value.assetId === 'string' && /^[0-9a-f]{64}$/.test(value.assetId)
      && ['tie', 'shrub', 'moby'].includes(String(value.kind))
      && Number.isInteger(value.classId) && Number(value.classId) >= 0 && Number(value.classId) <= 0xffff
      ? value as AssetPlacementDragData
      : undefined;
  } catch {
    return undefined;
  }
}

export function createAssetPlacementCommand(asset: AssetPlacementDragData, position: ProjectVector3): EditorCommand {
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
