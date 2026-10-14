export type UpdateChannel = 'nightly' | 'stable';

export type PackageChannel = UpdateChannel | 'development';

export interface PackageFileEntry {
  path: string;
  size: number;
  sha256: string;
}

export interface ForgePackageManifest {
  schemaVersion: 1;
  product: 'horizon-forge';
  version: string;
  channel: PackageChannel;
  commit: string;
  sdkRevision: string;
  bridgeProtocol: 1;
  platform: 'linux' | 'windows';
  architecture: 'x64';
  format: 'tar.gz' | 'zip';
  topLevelDirectory: string;
  executable: string;
  minimumUpdaterVersion: string;
  provenance: {
    repository: 'Horizon-Private-Server/horizon-forge-v2';
    workflow: '.github/workflows/ci.yml' | 'local';
  };
  fileCount: number;
  expandedSize: number;
  executablePaths: string[];
  files: PackageFileEntry[];
}

export type InstallLayoutKind =
  | 'managed'
  | 'package-manager'
  | 'read-only'
  | 'development'
  | 'unknown'
  | 'staged';

export interface InstallLayoutAssessment {
  kind: InstallLayoutKind;
  root: string;
  reason: string;
  manifest?: ForgePackageManifest;
}

export interface InstallLayoutEvidence {
  root: string;
  isPackaged: boolean;
  externalReason?: string;
  manifest?: ForgePackageManifest;
  manifestError?: string;
  identityMatches?: boolean;
  safeRoot?: boolean;
  staged?: boolean;
  replaceable?: boolean;
}

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

export interface InstallableReleaseArtifact extends ReleaseArtifact {
  format: 'tar.gz' | 'zip';
  topLevelDirectory: string;
  executable: string;
  expandedSize: number;
  fileCount: number;
  packageManifestSize: number;
  packageManifestSha256: string;
}

export interface SignedReleaseManifest {
  schemaVersion: 2;
  version: string;
  channel: UpdateChannel;
  tag: string;
  commit: string;
  sdkRevision: string;
  bridgeProtocol: 1;
  minimumUpdaterVersion: string;
  provenance: {
    repository: 'Horizon-Private-Server/horizon-forge-v2';
    workflow: '.github/workflows/ci.yml';
    runId: number;
    commit: string;
    tag: string;
  };
  signature: {
    algorithm: 'ed25519';
    keyId: string;
    file: 'release-manifest.json.sig';
  };
  artifacts: InstallableReleaseArtifact[];
}

export interface TrustedUpdateKey {
  id: string;
  publicKeySpkiBase64: string;
}

export interface VerifiedReleaseEnvelope {
  manifest: SignedReleaseManifest;
  artifact: InstallableReleaseArtifact;
  manifestBytes: Uint8Array;
  signatureBytes: Uint8Array;
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

export type UpdateDownloadState = 'idle' | 'downloading' | 'verifying' | 'ready' | 'cancelled' | 'error';

export interface UpdateDownloadSnapshot {
  state: UpdateDownloadState;
  version?: string;
  channel?: UpdateChannel;
  receivedBytes: number;
  totalBytes: number;
  message: string;
}

export type UpdateStageState = 'idle' | 'inspecting' | 'extracting' | 'validating' | 'ready' | 'cancelled' | 'error';

export interface UpdateStageSnapshot {
  state: UpdateStageState;
  version?: string;
  channel?: UpdateChannel;
  completedFiles: number;
  totalFiles: number;
  message: string;
}

export type UpdateActivationState = 'idle' | 'preparing' | 'waiting-for-exit' | 'rolled-back' | 'error';

export interface UpdateActivationSnapshot {
  state: UpdateActivationState;
  version?: string;
  channel?: UpdateChannel;
  message: string;
}

export type UpdateLifecycleState =
  | 'idle'
  | 'checking'
  | 'current'
  | 'available'
  | 'downloading'
  | 'verifying'
  | 'preparing'
  | 'ready'
  | 'deferred'
  | 'failed'
  | 'restarting'
  | 'rolled-back'
  | 'updated';

export type UpdateLifecycleAction = 'download' | 'cancel' | 'retry' | 'defer' | 'install' | 'open' | 'dismiss';

export interface UpdateLifecycleActionItem {
  action: UpdateLifecycleAction;
  label: string;
}

export interface UpdateLifecycleSnapshot {
  state: UpdateLifecycleState;
  title: string;
  message: string;
  severity: 'info' | 'warning' | 'error';
  version?: string;
  channel?: UpdateChannel;
  source?: string;
  completed?: number;
  total?: number;
  progressUnit?: 'bytes' | 'files';
  actions: UpdateLifecycleActionItem[];
}

export interface UpdateCheckResult {
  status: 'available' | 'current' | 'unavailable' | 'busy' | 'error';
  title: string;
  message: string;
}
