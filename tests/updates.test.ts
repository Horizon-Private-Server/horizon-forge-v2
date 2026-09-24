import assert from 'node:assert/strict';
import test from 'node:test';

import { compareForgeVersions, validateReleaseManifest } from '../src/utils/UpdateValidation.ts';
import { applicationTitle } from '../src/utils/ApplicationTitle.ts';

const manifest = {
  schemaVersion: 1,
  version: '2.0.0-nightly.42.01234567',
  channel: 'nightly',
  tag: 'v2.0.0-nightly.42.01234567',
  commit: '0123456789abcdef0123456789abcdef01234567',
  sdkRevision: '89abcdef0123456789abcdef0123456789abcdef',
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
