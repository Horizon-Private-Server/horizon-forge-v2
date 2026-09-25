import type { BuildLayerId } from './ForgeApi.js';

export interface EchoRequest {
  message: string;
  delayMs: number;
}

export interface DevelopmentIsoRequest {
  sourcePath: string;
  targetPath: string;
  fingerprint: string;
  overwrite: boolean;
}

export interface UyaAssetImportRequest {
  sourceIsoPath: string;
  catalogRootPath: string;
  fingerprint: string;
  revision: string;
  force: boolean;
}

export interface UyaProjectCreationRequest {
  sourceIsoPath: string;
  catalogRootPath: string;
  projectPath: string;
  name: string;
  fingerprint: string;
  revision: string;
  level: number;
  allowPartial: boolean;
}

export interface UyaProjectPreflightRequest {
  sourceIsoPath: string;
  catalogRootPath: string;
  level: number;
}

export interface ProjectInspectRequest {
  projectPath: string;
  catalogRootPath: string;
}

export interface ProjectRenameRequest extends ProjectInspectRequest {
  name: string;
}

export interface ProjectRecoveryRequest extends ProjectInspectRequest {
  recoveryId: string;
}

export interface ProjectAssetRepairRequest extends ProjectInspectRequest {
  sourceIsoPath: string;
}

export interface CatalogMaintenanceRequest {
  catalogRootPath: string;
  projectRoots: string[];
}

export interface CatalogCollectionRequest extends CatalogMaintenanceRequest {
  confirmationToken: string;
}

export interface UyaRenderPackageRequest {
  sourceIsoPath: string;
  cacheRootPath: string;
  fingerprint: string;
  level: number;
  projectPath: string;
  catalogRootPath: string;
}

export interface UyaRenderPackageResult {
  rootPath: string;
  cacheKey: string;
  terrainPaths: string[];
  skyPath?: string;
  environment?: {
    backgroundColor: [number, number, number];
    fogColor: [number, number, number];
    fogNearDistance: number;
    fogFarDistance: number;
    fogNearIntensity: number;
    fogFarIntensity: number;
    deathHeight: number;
    isSphericalWorld: boolean;
    sphereCenter: [number, number, number];
    shipPosition: [number, number, number];
    shipRotationZ: number;
    shipPath: number;
    shipCameraCuboidStart: number;
    shipCameraCuboidEnd: number;
    chunkPlaneCount: number;
    coreSoundsCount: number;
  };
  occlusionOctants: { x: number; y: number; z: number; maskIndex: number }[];
  assets: { assetId: string; kind: 'moby' | 'tie' | 'shrub'; path?: string; error?: string }[];
  cacheHit: boolean;
}

export interface UyaBuildPatchRequest {
  projectRoot: string;
  catalogRoot: string;
  cleanSourceIso: string;
  developmentIso: string;
  sourceFingerprint: string;
  acknowledgedWarnings: string[];
  forceFullImage: boolean;
  includedLayers: BuildLayerId[];
}

export interface UyaBuildPlanRequest {
  projectRoot: string;
  catalogRoot: string;
}
