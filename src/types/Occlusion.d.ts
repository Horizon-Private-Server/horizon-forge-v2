export interface OcclusionBakeSummary {
  octants: number;
  origins: number;
  wallMs: number;
  gpuMs: number | null;
  nativeGridBytes?: number;
}

export interface OcclusionPreparation {
  gridBytes: number;
  treeBytes: number;
  maskSlots: number;
  unsharedMaskBytes: number;
  alwaysVisibleGroups: number;
  cellLimit: number;
  sampleLimit: number;
}

export interface OcclusionSamplePreview {
  profile: string;
  origins: [number, number, number][];
  cells: { x: number; y: number; z: number; samples: number[]; inwardCorners?: number }[];
  preparation?: OcclusionPreparation;
}

export interface OcclusionVisibilityData {
  schemaVersion: 1;
  source: 'vanilla' | 'generated';
  gridHash: string;
  coverageRecipeFingerprint?: string;
  /** Normalized project identity, matched against coverage.visibilityFingerprint. */
  projectFingerprint?: string;
  octants: { x: number; y: number; z: number; maskIndex: number }[];
  masks: Uint8Array;
  entities: { id: string; groups: number[] }[];
}

export type OcclusionVisibilityResult = OcclusionVisibilityData | { schemaVersion: 1; status: 'needs-build' };

export interface OcclusionViewportState {
  octant?: { id: number; x: number; y: number; z: number };
  hiddenEntities: number;
}
