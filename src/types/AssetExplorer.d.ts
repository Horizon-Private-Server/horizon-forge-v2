export type AssetExplorerCategory = 'ties' | 'shrubs' | 'mobys' | 'skyShells' | 'textures';
export type AssetPreviewKind = 'tie' | 'shrub' | 'moby';

export interface AssetPreviewSource {
  url: string;
  cacheHit: boolean;
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
}

export interface AssetExplorerFamily {
  familyId: string;
  category: AssetExplorerCategory;
  displayLabel: string;
  classId?: number;
  variants: AssetExplorerItem[];
  representativeAssetId: string;
}

export interface AssetPlacementDragData {
  assetId: string;
  kind: AssetPreviewKind;
  classId: number;
}

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
