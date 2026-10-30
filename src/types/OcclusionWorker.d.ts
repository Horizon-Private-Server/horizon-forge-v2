export type OcclusionVector = [number, number, number];

export interface OcclusionWorkerCell {
  x: number;
  y: number;
  z: number;
  reservedVisible: boolean;
  samples: number[];
  inwardCorners?: number;
}

export interface OcclusionWorkerCheck {
  origin: OcclusionVector;
  direction: OcclusionVector;
  geometry: string;
  mobys: string;
}

export interface OcclusionWorkerScene {
  schemaVersion: 1;
  profile: string;
  inputHash: string;
  triangles: number;
  trianglesHash: string;
  texturesHash: string;
  alphaHash: string;
  origins: OcclusionVector[];
  cells: OcclusionWorkerCell[];
  spheres: { center: OcclusionVector; radius: number; group: number }[];
  checks: OcclusionWorkerCheck[];
  fallbackMask: string;
  samplePullIn: number;
  barrierHeights?: number[] | null;
  pixelEdgeAllowance?: boolean;
}

export interface OcclusionWorkerResult {
  schemaVersion: 2;
  profile: string;
  inputHash: string;
  sceneHash: string;
  origins: number;
  octants: number;
  batches: number;
  wallMs: number;
  gpuMs: number | null;
  sampleMasks: string;
  adapter?: { vendor: string; description: string };
}

export interface OcclusionWorkerApi {
  cancelled(): boolean;
  progress(value: { completed: number; total: number; message: string }): void;
  finish(value: OcclusionWorkerResult): Promise<void>;
  fail(value: unknown): void;
}

export interface OcclusionCameraView {
  origin: OcclusionVector;
  sample: number;
  face: number;
  resolution: number;
  basis: [OcclusionVector, OcclusionVector, OcclusionVector];
  check?: boolean;
}

export type OcclusionGpuBufferFactory = (size: number, usage: GPUBufferUsageFlags, data?: AllowSharedBufferSource) => GPUBuffer;
