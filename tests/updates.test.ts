import assert from 'node:assert/strict';
import { generateKeyPairSync, sign } from 'node:crypto';
import test from 'node:test';

import {
  classifyInstallLayout,
  compareForgeVersions,
  validateForgePackageManifest,
  validateReleaseManifest,
  validateSignedReleaseEnvelope,
} from '../src/utils/UpdateValidation.ts';
import { applicationTitle } from '../src/utils/ApplicationTitle.ts';

const manifest = {
  schemaVersion: 1,
  version: '2.0.0-nightly.42.01234567',
  channel: 'nightly',
  tag: 'v2.0.0-nightly.42.01234567',
  commit: '0123456789abcdef0123456789abcdef01234567',
  sdkRevision: 'v0.5.0',
  bridgeProtocol: 1,
  artifacts: [
    {
      platform: 'linux', architecture: 'x64',
      file: 'horizon-forge-2.0.0-nightly.42.01234567-linux-x64.tar.gz',
      size: 1234, sha256: 'a'.repeat(64),
    },
    {
      platform: 'windows', architecture: 'x64',
      file: 'horizon-forge-2.0.0-nightly.42.01234567-windows-x64.zip',
      size: 2345, sha256: 'b'.repeat(64),
    },
  ],
};

test('updates are ordered and release manifests stay within their trusted channel', () => {
  assert.equal(applicationTitle(false, '2.0.0'), 'Horizon Forge - Local Dev');
  assert.equal(applicationTitle(true, '2.0.0'), 'Horizon Forge - Standalone');
  assert.equal(applicationTitle(true, '2.0.0', 'stable'), 'Horizon Forge v2.0.0');
  assert.equal(compareForgeVersions('2.0.0-nightly.43.89abcdef', manifest.version), 1);
  assert.equal(compareForgeVersions('2.0.0', manifest.version), 1);
  assert.equal(compareForgeVersions(manifest.version, manifest.version), 0);
  assert.equal(validateReleaseManifest(manifest, 'nightly', manifest.tag, 'windows').artifact.size, 2345);
  assert.throws(() => validateReleaseManifest({ ...manifest, sdkRevision: 'main' },
    'nightly', manifest.tag, 'windows'), /SDK revision/);
  assert.throws(() => validateReleaseManifest(manifest, 'stable', manifest.tag, 'windows'), /identity/);
  assert.throws(() => validateReleaseManifest({ ...manifest, artifacts: [
    { ...manifest.artifacts[0], file: '../forge.tar.gz' },
  ] }, 'nightly', manifest.tag, 'linux'), /invalid package/);
  assert.throws(() => validateReleaseManifest({ ...manifest, artifacts: [
    { ...manifest.artifacts[0], sha256: 'unsigned' },
  ] }, 'nightly', manifest.tag, 'linux'), /invalid package/);
  assert.throws(() => validateReleaseManifest({ ...manifest, artifacts: [
    { ...manifest.artifacts[0], file: 'horizon-forge-9.9.9-linux-x64.tar.gz' }, manifest.artifacts[1],
  ] }, 'nightly', manifest.tag, 'linux'), /package identity/);
});

test('schema 2 release envelopes require exact signed identity and a pinned Ed25519 key', () => {
  const { privateKey, publicKey } = generateKeyPairSync('ed25519');
  const signedManifest = {
    ...manifest,
    schemaVersion: 2,
    minimumUpdaterVersion: '2.0.0',
    provenance: {
      repository: 'Horizon-Private-Server/horizon-forge-v2',
      workflow: '.github/workflows/ci.yml',
      runId: 42,
      commit: manifest.commit,
      tag: manifest.tag,
    },
    signature: { algorithm: 'ed25519', keyId: 'test-2026', file: 'release-manifest.json.sig' },
    artifacts: manifest.artifacts.map((artifact) => ({
      ...artifact,
      format: artifact.platform === 'windows' ? 'zip' : 'tar.gz',
      topLevelDirectory: `horizon-forge-${artifact.platform}-x64`,
      executable: artifact.platform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge',
      expandedSize: 4096,
      fileCount: 4,
      packageManifestSize: 1024,
      packageManifestSha256: 'c'.repeat(64),
    })),
  };
  const bytes = Buffer.from(`${JSON.stringify(signedManifest)}\n`);
  const signature = sign(null, bytes, privateKey);
  const keys = [{
    id: 'test-2026',
    publicKeySpkiBase64: publicKey.export({ format: 'der', type: 'spki' }).toString('base64'),
  }];
  const expected = {
    channel: 'nightly' as const,
    tag: manifest.tag,
    platform: 'linux' as const,
    currentVersion: '2.0.0-nightly.41.ffffffff',
  };
  assert.equal(validateSignedReleaseEnvelope(bytes, signature, keys, expected).artifact.platform, 'linux');
  assert.throws(() => validateSignedReleaseEnvelope(Buffer.from(bytes.toString().replace('4096', '4097')),
    signature, keys, expected), /signature/);
  assert.throws(() => validateSignedReleaseEnvelope(bytes, signature, [], expected), /not trusted/);
  assert.throws(() => validateSignedReleaseEnvelope(bytes, signature, keys, {
    ...expected, channel: 'stable',
  }), /identity/);
  assert.throws(() => validateSignedReleaseEnvelope(bytes, signature, keys, {
    ...expected, currentVersion: manifest.version,
  }), /not a newer release/);
  assert.throws(() => validateSignedReleaseEnvelope(Buffer.from(JSON.stringify({
    ...signedManifest, minimumUpdaterVersion: '3.0.0',
  })), sign(null, Buffer.from(JSON.stringify({
    ...signedManifest, minimumUpdaterVersion: '3.0.0',
  })), privateKey), keys, expected), /or newer/);
});

const packageManifest = {
  schemaVersion: 1,
  product: 'horizon-forge',
  version: '2.0.0-nightly.42.01234567',
  channel: 'nightly',
  commit: '0123456789abcdef0123456789abcdef01234567',
  sdkRevision: 'v0.5.0',
  bridgeProtocol: 1,
  platform: 'linux',
  architecture: 'x64',
  format: 'tar.gz',
  topLevelDirectory: 'horizon-forge-linux-x64',
  executable: 'horizon-forge',
  minimumUpdaterVersion: '2.0.0',
  provenance: {
    repository: 'Horizon-Private-Server/horizon-forge-v2',
    workflow: '.github/workflows/ci.yml',
  },
  fileCount: 3,
  expandedSize: 6,
  executablePaths: ['horizon-forge', 'resources/host/Forge.Host'],
  files: [
    { path: 'horizon-forge', size: 1, sha256: 'a'.repeat(64) },
    { path: 'resources/app/package.json', size: 2, sha256: 'b'.repeat(64) },
    { path: 'resources/host/Forge.Host', size: 3, sha256: 'c'.repeat(64) },
  ],
};

test('portable package manifests are complete, bounded, and path-safe', () => {
  assert.equal(validateForgePackageManifest(packageManifest).expandedSize, 6);
  assert.throws(() => validateForgePackageManifest({
    ...packageManifest,
    files: [...packageManifest.files, { path: '../escape', size: 1, sha256: 'd'.repeat(64) }],
    fileCount: 4,
    expandedSize: 7,
  }), /file entry/);
  assert.throws(() => validateForgePackageManifest({
    ...packageManifest,
    files: packageManifest.files.map((entry, index) => index === 1 ? { ...entry, path: 'HORIZON-FORGE' } : entry),
  }), /paths/);
  assert.throws(() => validateForgePackageManifest({ ...packageManifest, expandedSize: 7 }), /expanded size/);
  assert.throws(() => validateForgePackageManifest({ ...packageManifest, extra: true }), /schema 1/);
});

test('install classification separates managed, staged, read-only, external, and unknown layouts', () => {
  const base = {
    root: '/apps/horizon-forge-linux-x64',
    isPackaged: true,
    manifest: validateForgePackageManifest(packageManifest),
    identityMatches: true,
    safeRoot: true,
    staged: false,
    replaceable: true,
  };
  assert.equal(classifyInstallLayout(base).kind, 'managed');
  assert.equal(classifyInstallLayout({ ...base, replaceable: false }).kind, 'read-only');
  assert.equal(classifyInstallLayout({ ...base, staged: true }).kind, 'staged');
  assert.equal(classifyInstallLayout({ ...base, externalReason: 'Updated by package manager.' }).kind, 'package-manager');
  assert.equal(classifyInstallLayout({
    ...base,
    manifest: validateForgePackageManifest({
      ...packageManifest, provenance: { ...packageManifest.provenance, workflow: 'local' },
    }),
  }).kind, 'unknown');
  assert.equal(classifyInstallLayout({ ...base, manifest: undefined, manifestError: 'Missing.' }).kind, 'unknown');
  assert.equal(classifyInstallLayout({ ...base, isPackaged: false }).kind, 'development');
});
