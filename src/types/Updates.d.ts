export type UpdateChannel = 'nightly' | 'stable';

export interface ReleaseArtifact {
  platform: 'linux' | 'windows';
  architecture: 'x64';
  file: string;
  size: number;
  sha256: string;
}

export interface ReleaseManifest {
  schemaVersion: 1;
  version: string;
  channel: UpdateChannel;
  tag: string;
  commit: string;
  sdkRevision: string;
  bridgeProtocol: 1;
  artifacts: ReleaseArtifact[];
}

export interface AvailableUpdate {
  version: string;
  channel: UpdateChannel;
  tag: string;
  notes: string;
  releaseUrl: string;
  downloadUrl: string;
  artifact: ReleaseArtifact;
}
