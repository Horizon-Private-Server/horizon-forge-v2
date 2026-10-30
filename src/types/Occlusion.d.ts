export interface OcclusionBakeSummary {
  octants: number;
  origins: number;
  wallMs: number;
  gpuMs: number | null;
}

export interface OcclusionSamplePreview {
  profile: string;
  origins: [number, number, number][];
  cells: { x: number; y: number; z: number; samples: number[]; inwardCorners?: number }[];
}

export interface OcclusionVisibilityData {
  schemaVersion: 1;
  source: 'vanilla' | 'generated';
  gridHash: string;
  octants: { x: number; y: number; z: number; maskIndex: number }[];
  masks: Uint8Array;
  entities: { id: string; groups: number[] }[];
}

export interface OcclusionViewportState {
  octant?: { id: number; x: number; y: number; z: number };
  hiddenEntities: number;
}
