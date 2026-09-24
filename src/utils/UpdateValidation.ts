import path from 'node:path';

import type { ReleaseArtifact, ReleaseManifest, UpdateChannel } from '../types/Updates.js';

const stableVersion = /^\d+\.\d+\.\d+$/;
const nightlyVersion = /^\d+\.\d+\.\d+-nightly\.[1-9]\d*\.[0-9a-f]{8}$/;

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
  if (value.schemaVersion !== 1
    || value.channel !== channel
    || typeof value.version !== 'string'
    || !versionPattern.test(value.version)
    || value.tag !== tag
    || tag !== `v${value.version}`
    || typeof value.commit !== 'string'
    || !/^[0-9a-f]{40}$/i.test(value.commit)
    || typeof value.sdkRevision !== 'string'
    || !/^[0-9a-f]{40}$/i.test(value.sdkRevision)
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
