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
  assets: { assetId: string; path?: string; error?: string }[];
  cacheHit: boolean;
}
