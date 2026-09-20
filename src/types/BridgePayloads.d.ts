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
