export type AssetExplorerCategory = 'ties' | 'shrubs' | 'mobys' | 'tfrags' | 'skyShells' | 'textures';
export type AssetModelKind = 'tie' | 'shrub' | 'moby';
export type AssetPreviewKind = AssetModelKind | 'tfrag' | 'texture' | 'sky';
export type CustomModelPreviewView = 'source' | 'target' | 'thumbnail';

export interface AssetPreviewSource {
  url: string;
  cacheHit: boolean;
  detailsUrl?: string;
  diagnosticGlbUrl?: string;
}

export interface CustomModelPreviewDetails {
  schemaVersion: 1;
  authoritative: false;
  sourceName: string;
  targetClassId: number;
  vertexCount: number;
  triangleCount: number;
  packetCount: number;
  materialCount: number;
  textureCount: number;
  paletteCount: number;
  bounds: {
    minimumX: number;
    minimumY: number;
    minimumZ: number;
    maximumX: number;
    maximumY: number;
    maximumZ: number;
  };
  lodPolicy: string;
  primitives: {
    sourceMeshIndex: number;
    sourcePrimitiveIndex: number;
    nativeMaterialIndex: number;
    firstTriangle: number;
    triangleCount: number;
    firstPacket: number;
    packetCount: number;
  }[];
  diagnostics: string[];
}

export interface AssetExplorerQuery {
  category: AssetExplorerCategory;
  search?: string;
  game?: string;
  level?: string;
  region?: string;
  revision?: string;
  tags?: string[];
  cursor?: string;
  limit?: number;
}

export interface AssetExplorerSource {
  game: string;
  region: string;
  revision: string;
  level: string;
  archive: string;
  sourceIndex: number;
  textureUse?: {
    ownerKind: AssetModelKind;
    ownerClassId: number;
    role: string;
    slot: number;
    restorable: boolean;
  };
  mobySounds?: {
    bankAssetId: string;
    slots: {
      slot: number;
      nativeProgramIndex: number;
      soundAssetId?: string;
      previewSupported: boolean;
      previewBlocker?: string;
    }[];
  };
}

export interface AssetExplorerItem {
  assetId: string;
  category: AssetExplorerCategory;
  displayLabel: string;
  canonicalFormatVersion: number;
  byteSize: number;
  aliases: string[];
  tags: string[];
  sources: AssetExplorerSource[];
  classIds: number[];
  previewState: 'notCached' | 'missingBlob';
  canPlace: boolean;
  placementDisabledReason?: string;
  shellIndex?: number;
  previewRevision?: string;
}

export interface AssetExplorerFamily {
  familyId: string;
  category: AssetExplorerCategory;
  displayLabel: string;
  classId?: number;
  variants: AssetExplorerItem[];
  representativeAssetId: string;
}

export interface AssetModelPlacementDragData {
  assetId: string;
  kind: AssetModelKind;
  classId: number;
}

export interface AssetSkyShellDragData {
  assetId: string;
  kind: 'sky';
  shellIndex: number;
  dropError?: string;
}

export interface AssetTfragPlacementDragData {
  assetId: string;
  kind: 'tfrag';
  importId: string;
}

export type AssetPlacementDragData = AssetModelPlacementDragData | AssetTfragPlacementDragData | AssetSkyShellDragData;

export interface AssetExplorerFacets {
  games: string[];
  levels: string[];
  regions: string[];
  revisions: string[];
  tags: string[];
}

export interface AssetExplorerPage {
  items: AssetExplorerItem[];
  facets: AssetExplorerFacets;
  nextCursor?: string;
}
