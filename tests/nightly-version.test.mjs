import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { createNightlyVersion } from '../scripts/nightly-version.mjs';
import { createReleaseManifest } from '../scripts/release-manifest.mjs';
import { validateStableVersion } from '../scripts/stable-version.mjs';

test('nightly versions are ordered and commit-specific', () => {
  assert.equal(createNightlyVersion('2.0.0', '123', 'A1B2C3D4E5F6'), '2.0.0-nightly.123.a1b2c3d4');
  assert.throws(() => createNightlyVersion('2.0', '123', 'a1b2c3d4'), /Base version/);
  assert.throws(() => createNightlyVersion('2.0.0', '0', 'a1b2c3d4'), /Run number/);
});

test('stable tags exactly match package versions', () => {
  assert.equal(validateStableVersion('2.0.0', 'v2.0.0'), '2.0.0');
  assert.throws(() => validateStableVersion('2.0.0', 'v2.0.1'), /does not match/);
  assert.throws(() => validateStableVersion('2.0.0-beta.1', 'v2.0.0-beta.1'), /Package version/);
});

test('stable release notes compare against the previous stable release', async () => {
  const workflow = await readFile('.github/workflows/ci.yml', 'utf8');
  const configuration = await readFile('.github/release.yml', 'utf8');
  assert.match(workflow, /--exclude-pre-releases/);
  assert.match(workflow, /--notes-start-tag "\$PREVIOUS_STABLE_TAG"/);
  assert.match(configuration, /title: Features/);
  assert.match(configuration, /title: Fixes/);
  assert.match(configuration, /labels:\n\s+- '\*'/);
});

test('release manifests retain channel, provenance, and package hashes', async (context) => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-release-'));
  context.after(() => rm(directory, { recursive: true }));
  await writeFile(path.join(directory, 'horizon-forge-2.0.0-linux-x64.tar.gz'), 'linux');
  await writeFile(path.join(directory, 'horizon-forge-2.0.0-windows-x64.zip'), 'windows');
  const manifest = await createReleaseManifest(
    directory, '2.0.0', 'stable', 'a'.repeat(40), 'v0.5.0', 'v2.0.0');
  assert.equal(manifest.channel, 'stable');
  assert.equal(manifest.commit, 'a'.repeat(40));
  assert.deepEqual(manifest.artifacts.map(({ platform, architecture, size }) => ({ platform, architecture, size })), [
    { platform: 'linux', architecture: 'x64', size: 5 },
    { platform: 'windows', architecture: 'x64', size: 7 },
  ]);
  assert.ok(manifest.artifacts.every(({ sha256 }) => /^[0-9a-f]{64}$/.test(sha256)));
  await assert.rejects(
    createReleaseManifest(directory, '2.0.0', 'nightly', 'a'.repeat(40), 'v0.5.0', 'v2.0.0'),
    /does not match nightly/,
  );
});
