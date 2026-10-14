import path from 'node:path';
import { createPublicKey, verify } from 'node:crypto';

import type {
  ForgePackageManifest,
  InstallLayoutAssessment,
  InstallLayoutEvidence,
  InstallableReleaseArtifact,
  PackageFileEntry,
  ReleaseArtifact,
  ReleaseManifest,
  SignedReleaseManifest,
  TrustedUpdateKey,
  UpdateChannel,
  VerifiedReleaseEnvelope,
} from '../types/Updates.js';

const stableVersion = /^\d+\.\d+\.\d+$/;
const nightlyVersion = /^\d+\.\d+\.\d+-nightly\.[1-9]\d*\.[0-9a-f]{8}$/;
const maximumPackageFiles = 10_000;
const maximumExpandedPackageSize = 8 * 1024 ** 3;
const maximumPackageManifestSize = 8 * 1024 ** 2;
const releaseRepository = 'Horizon-Private-Server/horizon-forge-v2';
const releaseWorkflow = '.github/workflows/ci.yml';

export function classifyInstallLayout(evidence: InstallLayoutEvidence): InstallLayoutAssessment {
  const { root, manifest } = evidence;
  if (!evidence.isPackaged) return {
    kind: 'development', root, reason: 'Local development builds cannot replace themselves.',
  };
  if (evidence.externalReason) return { kind: 'package-manager', root, reason: evidence.externalReason };
  if (!manifest) return {
    kind: 'unknown', root, reason: evidence.manifestError ?? 'The installation has no valid Forge package manifest.',
  };
  if (!evidence.identityMatches) return {
    kind: 'unknown', root, reason: 'The package identity does not match the running executable.', manifest,
  };
  if (!evidence.safeRoot) return {
    kind: 'unknown', root, reason: 'The installation root is indirect or cannot be resolved safely.', manifest,
  };
  if (manifest.channel !== 'development' && manifest.provenance.workflow !== '.github/workflows/ci.yml') return {
    kind: 'unknown', root, reason: 'A locally produced package is not a release-managed installation.', manifest,
  };
  if (evidence.staged) return manifest.channel === 'development'
    ? { kind: 'unknown', root, reason: 'A development package cannot be an update stage.', manifest }
    : { kind: 'staged', root, reason: 'Forge is running from an app-owned update staging directory.', manifest };
  if (manifest.channel === 'development') return {
    kind: 'development', root, reason: 'Packaged development builds are not release-managed installs.', manifest,
  };
  if (!evidence.replaceable) return {
    kind: 'read-only', root, reason: 'The installation directory or its parent is not replaceable.', manifest,
  };
  return {
    kind: 'managed', root, reason: 'This portable Forge installation can use full-package replacement.', manifest,
  };
}

export function validateForgePackageManifest(value: unknown): ForgePackageManifest {
  if (!isRecord(value) || !hasOnlyKeys(value, [
    'schemaVersion', 'product', 'version', 'channel', 'commit', 'sdkRevision', 'bridgeProtocol',
    'platform', 'architecture', 'format', 'topLevelDirectory', 'executable', 'minimumUpdaterVersion',
    'provenance', 'fileCount', 'expandedSize', 'executablePaths', 'files',
  ])) throw new TypeError('Forge package manifest must be a schema 1 object.');
  if (value.schemaVersion !== 1
    || value.product !== 'horizon-forge'
    || (value.channel !== 'stable' && value.channel !== 'nightly' && value.channel !== 'development')
    || typeof value.version !== 'string'
    || !(value.channel === 'nightly' ? nightlyVersion : stableVersion).test(value.version)
    || typeof value.commit !== 'string'
    || !/^[0-9a-f]{40}$/i.test(value.commit)
    || typeof value.sdkRevision !== 'string'
    || !/^(?:[0-9a-f]{40}|v\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.-]+)?)$/i.test(value.sdkRevision)
    || value.bridgeProtocol !== 1
    || (value.platform !== 'linux' && value.platform !== 'windows')
    || value.architecture !== 'x64'
    || value.format !== (value.platform === 'windows' ? 'zip' : 'tar.gz')
    || value.topLevelDirectory !== `horizon-forge-${value.platform}-x64`
    || value.executable !== (value.platform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge')
    || typeof value.minimumUpdaterVersion !== 'string'
    || !stableVersion.test(value.minimumUpdaterVersion)) {
    throw new TypeError('Forge package manifest identity is invalid.');
  }
  if (!isRecord(value.provenance)
    || !hasOnlyKeys(value.provenance, ['repository', 'workflow'])
    || value.provenance.repository !== 'Horizon-Private-Server/horizon-forge-v2'
    || (value.provenance.workflow !== '.github/workflows/ci.yml' && value.provenance.workflow !== 'local')) {
    throw new TypeError('Forge package provenance is invalid.');
  }
  try {
    parseVersion(value.version as string);
    parseVersion(value.minimumUpdaterVersion as string);
  } catch {
    throw new TypeError('Forge package manifest identity is invalid.');
  }
  if (!Array.isArray(value.files)
    || !Number.isSafeInteger(value.fileCount)
    || value.fileCount !== value.files.length
    || value.files.length === 0
    || value.files.length > maximumPackageFiles
    || !Number.isSafeInteger(value.expandedSize)
    || Number(value.expandedSize) <= 0
    || Number(value.expandedSize) > maximumExpandedPackageSize) {
    throw new TypeError('Forge package file limits are invalid.');
  }

  const files = value.files.map(validatePackageFile);
  const paths = files.map((entry) => entry.path);
  if (!isStrictlySorted(paths) || hasCaseOrPrefixCollision(paths) || paths.includes('forge-package.json')) {
    throw new TypeError('Forge package file paths are invalid.');
  }
  const expandedSize = files.reduce((total, entry) => total + entry.size, 0);
  if (!Number.isSafeInteger(expandedSize) || expandedSize !== value.expandedSize) {
    throw new TypeError('Forge package expanded size is invalid.');
  }
  const executable = value.executable as string;
  const platform = value.platform as ForgePackageManifest['platform'];
  const required = [
    executable,
    'resources/app/package.json',
    `resources/host/Forge.Host${platform === 'windows' ? '.exe' : ''}`,
  ];
  if (required.some((requiredPath) => !paths.includes(requiredPath))) {
    throw new TypeError('Forge package is missing a required file.');
  }
  if (!Array.isArray(value.executablePaths)
    || value.executablePaths.some((entry) => typeof entry !== 'string')
    || !isStrictlySorted(value.executablePaths as string[])
    || new Set(value.executablePaths).size !== value.executablePaths.length
    || value.executablePaths.some((entry) => !paths.includes(entry as string))
    || (platform === 'linux' && !value.executablePaths.includes(executable))) {
    throw new TypeError('Forge package executable paths are invalid.');
  }
  return {
    ...value,
    provenance: value.provenance,
    executablePaths: [...value.executablePaths],
    files,
  } as ForgePackageManifest;
}

export function compareForgeVersions(left: string, right: string): number {
  const leftParts = parseVersion(left);
  const rightParts = parseVersion(right);
  for (let index = 0; index < leftParts.length; index += 1) {
    const difference = leftParts[index] - rightParts[index];
    if (difference !== 0) return Math.sign(difference);
  }
  return 0;
}

export function validateReleaseManifest(
  value: unknown,
  channel: UpdateChannel,
  tag: string,
  platform: ReleaseArtifact['platform'],
): { manifest: ReleaseManifest; artifact: ReleaseArtifact } {
  if (!isRecord(value)) throw new TypeError('Release manifest must be an object.');
  const versionPattern = channel === 'stable' ? stableVersion : nightlyVersion;
  if (typeof value.sdkRevision !== 'string'
    || !/^(?:[0-9a-f]{40}|v\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.-]+)?)$/i.test(value.sdkRevision)) {
    throw new TypeError('Release manifest SDK revision is invalid.');
  }
  if (value.schemaVersion !== 1
    || value.channel !== channel
    || typeof value.version !== 'string'
    || !versionPattern.test(value.version)
    || value.tag !== tag
    || tag !== `v${value.version}`
    || typeof value.commit !== 'string'
    || !/^[0-9a-f]{40}$/i.test(value.commit)
    || value.bridgeProtocol !== 1
    || !Array.isArray(value.artifacts)) {
    throw new TypeError('Release manifest identity is invalid.');
  }

  const artifacts = value.artifacts.map(validateArtifact);
  if (artifacts.length !== 2
    || new Set(artifacts.map((artifact) => artifact.platform)).size !== 2
    || artifacts.some((artifact) => artifact.file !== expectedFile(value.version as string, artifact.platform))) {
    throw new TypeError('Release manifest package identity is invalid.');
  }
  const artifact = artifacts.find((candidate) => candidate.platform === platform && candidate.architecture === 'x64');
  if (!artifact) throw new TypeError(`Release manifest has no ${platform}-x64 package.`);
  return { manifest: { ...value, artifacts } as ReleaseManifest, artifact };
}

export function validateSignedReleaseEnvelope(
  manifestBytes: Uint8Array,
  signatureBytes: Uint8Array,
  trustedKeys: readonly TrustedUpdateKey[],
  expected: {
    channel: UpdateChannel;
    tag: string;
    platform: ReleaseArtifact['platform'];
    currentVersion: string;
  },
): VerifiedReleaseEnvelope {
  if (manifestBytes.byteLength === 0 || manifestBytes.byteLength > 2 * 1024 ** 2) {
    throw new TypeError('Signed release manifest size is invalid.');
  }
  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(manifestBytes));
  } catch {
    throw new TypeError('Signed release manifest is not valid UTF-8 JSON.');
  }
  const { manifest, artifact } = validateSignedReleaseManifest(value, expected);
  const key = trustedKeys.find((candidate) => candidate.id === manifest.signature.keyId);
  if (!key) throw new TypeError(`Release signature key ${manifest.signature.keyId} is not trusted.`);
  if (signatureBytes.byteLength !== 64) throw new TypeError('Release signature length is invalid.');
  let publicKey;
  try {
    publicKey = createPublicKey({
      key: Buffer.from(key.publicKeySpkiBase64, 'base64'),
      format: 'der',
      type: 'spki',
    });
  } catch {
    throw new TypeError(`Release signature key ${key.id} is invalid.`);
  }
  if (publicKey.asymmetricKeyType !== 'ed25519'
    || !verify(null, manifestBytes, publicKey, signatureBytes)) {
    throw new TypeError('Release manifest signature is invalid.');
  }
  return {
    manifest,
    artifact,
    manifestBytes: Uint8Array.from(manifestBytes),
    signatureBytes: Uint8Array.from(signatureBytes),
  };
}

function validateSignedReleaseManifest(
  value: unknown,
  expected: {
    channel: UpdateChannel;
    tag: string;
    platform: ReleaseArtifact['platform'];
    currentVersion: string;
  },
): { manifest: SignedReleaseManifest; artifact: InstallableReleaseArtifact } {
  if (!isRecord(value) || !hasOnlyKeys(value, [
    'schemaVersion', 'version', 'channel', 'tag', 'commit', 'sdkRevision', 'bridgeProtocol',
    'minimumUpdaterVersion', 'provenance', 'signature', 'artifacts',
  ])) throw new TypeError('Signed release manifest must be a schema 2 object.');
  const versionPattern = expected.channel === 'stable' ? stableVersion : nightlyVersion;
  if (value.schemaVersion !== 2
    || value.channel !== expected.channel
    || typeof value.version !== 'string'
    || !versionPattern.test(value.version)
    || value.tag !== expected.tag
    || expected.tag !== `v${value.version}`
    || typeof value.commit !== 'string'
    || !/^[0-9a-f]{40}$/i.test(value.commit)
    || typeof value.sdkRevision !== 'string'
    || !/^(?:[0-9a-f]{40}|v\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.-]+)?)$/i.test(value.sdkRevision)
    || value.bridgeProtocol !== 1
    || typeof value.minimumUpdaterVersion !== 'string'
    || !stableVersion.test(value.minimumUpdaterVersion)
    || !Array.isArray(value.artifacts)) {
    throw new TypeError('Signed release manifest identity is invalid.');
  }
  if (compareForgeVersions(value.version, expected.currentVersion) <= 0) {
    throw new TypeError('Signed release manifest is not a newer release.');
  }
  if (compareMinimumVersion(value.minimumUpdaterVersion, expected.currentVersion) > 0) {
    throw new TypeError(`Forge ${value.minimumUpdaterVersion} or newer is required to install this update.`);
  }
  if (!isRecord(value.provenance)
    || !hasOnlyKeys(value.provenance, ['repository', 'workflow', 'runId', 'commit', 'tag'])
    || value.provenance.repository !== releaseRepository
    || value.provenance.workflow !== releaseWorkflow
    || !Number.isSafeInteger(value.provenance.runId)
    || Number(value.provenance.runId) <= 0
    || value.provenance.commit !== value.commit
    || value.provenance.tag !== value.tag) {
    throw new TypeError('Signed release provenance is invalid.');
  }
  if (!isRecord(value.signature)
    || !hasOnlyKeys(value.signature, ['algorithm', 'keyId', 'file'])
    || value.signature.algorithm !== 'ed25519'
    || typeof value.signature.keyId !== 'string'
    || !/^[A-Za-z0-9._-]{1,64}$/.test(value.signature.keyId)
    || value.signature.file !== 'release-manifest.json.sig') {
    throw new TypeError('Signed release signature descriptor is invalid.');
  }

  const artifacts = value.artifacts.map(validateInstallableArtifact);
  if (artifacts.length !== 2
    || new Set(artifacts.map((artifact) => artifact.platform)).size !== 2
    || artifacts.some((artifact) => artifact.file !== expectedFile(value.version as string, artifact.platform))) {
    throw new TypeError('Signed release package identity is invalid.');
  }
  const artifact = artifacts.find((candidate) => candidate.platform === expected.platform);
  if (!artifact) throw new TypeError(`Signed release has no ${expected.platform}-x64 package.`);
  return {
    manifest: { ...value, provenance: value.provenance, signature: value.signature, artifacts } as SignedReleaseManifest,
    artifact,
  };
}

function compareMinimumVersion(required: string, installed: string): number {
  const requiredParts = parseVersion(required);
  const installedParts = parseVersion(installed);
  for (let index = 0; index < 3; index += 1) {
    const difference = requiredParts[index] - installedParts[index];
    if (difference !== 0) return Math.sign(difference);
  }
  return 0;
}

function validateInstallableArtifact(value: unknown): InstallableReleaseArtifact {
  const artifact = validateArtifact(value);
  if (!isRecord(value) || !hasOnlyKeys(value, [
    'platform', 'architecture', 'file', 'size', 'sha256', 'format', 'topLevelDirectory',
    'executable', 'expandedSize', 'fileCount', 'packageManifestSize', 'packageManifestSha256',
  ])
    || value.format !== (artifact.platform === 'windows' ? 'zip' : 'tar.gz')
    || value.topLevelDirectory !== `horizon-forge-${artifact.platform}-x64`
    || value.executable !== (artifact.platform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge')
    || !Number.isSafeInteger(value.expandedSize)
    || Number(value.expandedSize) <= 0
    || Number(value.expandedSize) > maximumExpandedPackageSize
    || !Number.isSafeInteger(value.fileCount)
    || Number(value.fileCount) <= 0
    || Number(value.fileCount) > maximumPackageFiles
    || !Number.isSafeInteger(value.packageManifestSize)
    || Number(value.packageManifestSize) <= 0
    || Number(value.packageManifestSize) > maximumPackageManifestSize
    || typeof value.packageManifestSha256 !== 'string'
    || !/^[0-9a-f]{64}$/.test(value.packageManifestSha256)) {
    throw new TypeError('Signed release manifest contains an invalid installable package.');
  }
  return value as unknown as InstallableReleaseArtifact;
}

function validateArtifact(value: unknown): ReleaseArtifact {
  if (!isRecord(value)
    || (value.platform !== 'linux' && value.platform !== 'windows')
    || value.architecture !== 'x64'
    || typeof value.file !== 'string'
    || value.file !== path.basename(value.file)
    || !/^horizon-forge-.+-(linux|windows)-x64\.(tar\.gz|zip)$/.test(value.file)
    || !Number.isSafeInteger(value.size)
    || Number(value.size) <= 0
    || Number(value.size) > 4 * 1024 ** 3
    || typeof value.sha256 !== 'string'
    || !/^[0-9a-f]{64}$/i.test(value.sha256)) {
    throw new TypeError('Release manifest contains an invalid package.');
  }
  return value as unknown as ReleaseArtifact;
}

function validatePackageFile(value: unknown): PackageFileEntry {
  if (!isRecord(value)
    || !hasOnlyKeys(value, ['path', 'size', 'sha256'])
    || typeof value.path !== 'string'
    || !isSafePackagePath(value.path)
    || !Number.isSafeInteger(value.size)
    || Number(value.size) < 0
    || Number(value.size) > maximumExpandedPackageSize
    || typeof value.sha256 !== 'string'
    || !/^[0-9a-f]{64}$/.test(value.sha256)) {
    throw new TypeError('Forge package contains an invalid file entry.');
  }
  return value as unknown as PackageFileEntry;
}

function isSafePackagePath(value: string): boolean {
  if (value.length === 0 || value.length > 512 || !/^[\x20-\x7e]+$/.test(value)
    || value.includes('\\') || value.includes('\0') || value.includes(':')) {
    return false;
  }
  const segments = value.split('/');
  return !value.startsWith('/')
    && !value.endsWith('/')
    && segments.every((segment) => segment.length > 0 && segment !== '.' && segment !== '..')
    && path.posix.normalize(value) === value;
}

function isStrictlySorted(values: readonly unknown[]): values is string[] {
  return values.every((value, index) => typeof value === 'string' && (index === 0 || (values[index - 1] as string) < value));
}

function hasCaseOrPrefixCollision(paths: readonly string[]): boolean {
  const foldedPaths = paths.map((filePath) => filePath.toLowerCase());
  const folded = new Set(foldedPaths);
  if (folded.size !== paths.length) return true;
  for (const lower of foldedPaths) {
    const segments = lower.split('/');
    for (let length = 1; length < segments.length; length += 1) {
      if (folded.has(segments.slice(0, length).join('/'))) return true;
    }
  }
  return false;
}

function expectedFile(version: string, platform: ReleaseArtifact['platform']): string {
  return `horizon-forge-${version}-${platform}-x64.${platform === 'windows' ? 'zip' : 'tar.gz'}`;
}

function parseVersion(value: string): number[] {
  const match = /^(\d+)\.(\d+)\.(\d+)(?:-nightly\.([1-9]\d*)\.[0-9a-f]{8})?$/.exec(value);
  if (!match) throw new TypeError(`Invalid Forge version: ${value}`);
  const parts = [Number(match[1]), Number(match[2]), Number(match[3]), match[4] ? 0 : 1, Number(match[4] ?? 0)];
  if (parts.some((part) => !Number.isSafeInteger(part))) throw new TypeError(`Invalid Forge version: ${value}`);
  return parts;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function hasOnlyKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const allowed = new Set(keys);
  return Object.keys(value).every((key) => allowed.has(key));
}
