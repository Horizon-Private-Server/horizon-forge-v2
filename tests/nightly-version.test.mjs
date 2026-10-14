import assert from 'node:assert/strict';
import { generateKeyPairSync, verify } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { createNightlyVersion } from '../scripts/nightly-version.mjs';
import { createPackageManifest } from '../scripts/package-manifest.mjs';
import { createReleaseManifest, signReleaseManifest } from '../scripts/release-manifest.mjs';
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
  assert.match(configuration, /labels:\r?\n\s+- '\*'/);
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
  await assert.rejects(
    createReleaseManifest(directory, '2.0.0', 'stable', 'a'.repeat(40), 'main', 'v2.0.0'),
    /SDK revision/,
  );
});

test('signed release manifests bind archive and embedded package identities', async (context) => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-signed-release-'));
  context.after(() => rm(directory, { recursive: true }));
  const commit = 'a'.repeat(40);
  for (const [platform, contents] of [['linux', 'linux'], ['windows', 'windows']]) {
    const extension = platform === 'windows' ? 'zip' : 'tar.gz';
    const archive = path.join(directory, `horizon-forge-2.0.0-${platform}-x64.${extension}`);
    await writeFile(archive, contents);
    await writeFile(`${archive}.package.json`, `${JSON.stringify({
      schemaVersion: 1,
      product: 'horizon-forge',
      version: '2.0.0',
      channel: 'stable',
      commit,
      sdkRevision: 'v0.5.0',
      bridgeProtocol: 1,
      platform,
      architecture: 'x64',
      format: extension,
      topLevelDirectory: `horizon-forge-${platform}-x64`,
      executable: platform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge',
      expandedSize: 100,
      fileCount: 3,
    })}\n`);
  }
  const manifest = await createReleaseManifest(
    directory, '2.0.0', 'stable', commit, 'v0.5.0', 'v2.0.0', { runId: 123, keyId: 'test-2026' });
  assert.equal(manifest.schemaVersion, 2);
  assert.equal(manifest.provenance.runId, 123);
  assert.ok(manifest.artifacts.every((artifact) => artifact.packageManifestSize > 0));
  const bytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
  const { privateKey, publicKey } = generateKeyPairSync('ed25519');
  assert.equal(verify(null, bytes, publicKey, signReleaseManifest(bytes, privateKey.export({
    format: 'pem', type: 'pkcs8',
  }))), true);
});

test('package manifests hash a bounded portable layout without loading files into memory', async (context) => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-package-'));
  context.after(() => rm(directory, { recursive: true }));
  await mkdir(path.join(directory, 'resources', 'app'), { recursive: true });
  await mkdir(path.join(directory, 'resources', 'host'), { recursive: true });
  await writeFile(path.join(directory, 'horizon-forge.exe'), 'app');
  await writeFile(path.join(directory, 'resources', 'app', 'package.json'), '{}');
  await writeFile(path.join(directory, 'resources', 'host', 'Forge.Host.exe'), 'host');
  await writeFile(path.join(directory, 'asset.bin'), Buffer.alloc(1024 * 1024, 7));

  const manifest = await createPackageManifest(directory, {
    version: '2.0.0',
    channel: 'development',
    commit: 'a'.repeat(40),
    sdkRevision: 'v0.5.0',
    target: 'windows-x64',
    executable: 'horizon-forge.exe',
  });
  assert.equal(manifest.topLevelDirectory, 'horizon-forge-windows-x64');
  assert.equal(manifest.fileCount, 4);
  assert.equal(manifest.expandedSize, 1024 * 1024 + 9);
  assert.deepEqual(manifest.files.map((entry) => entry.path), [
    'asset.bin',
    'horizon-forge.exe',
    'resources/app/package.json',
    'resources/host/Forge.Host.exe',
  ]);
  assert.ok(manifest.files.every((entry) => /^[0-9a-f]{64}$/.test(entry.sha256)));
});
