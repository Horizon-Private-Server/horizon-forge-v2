import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { createServer } from 'node:http';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { UpdateDownloadManager } from '../dist-electron/main/UpdateDownloadManager.js';
import { validateSignedReleaseEnvelope } from '../dist-electron/utils/UpdateValidation.js';

const packageBytes = Buffer.alloc(64 * 1024, 0x5a);
const packageHash = createHash('sha256').update(packageBytes).digest('hex');
const { privateKey, publicKey } = generateKeyPairSync('ed25519');
const trustedKeys = [{
  id: 'fixture-2026',
  publicKeySpkiBase64: publicKey.export({ format: 'der', type: 'spki' }).toString('base64'),
}];

function createEnvelope() {
  const manifest = {
    schemaVersion: 2,
    version: '2.0.0-nightly.42.01234567',
    channel: 'nightly',
    tag: 'v2.0.0-nightly.42.01234567',
    commit: '0123456789abcdef0123456789abcdef01234567',
    sdkRevision: 'v0.5.0',
    bridgeProtocol: 1,
    minimumUpdaterVersion: '2.0.0',
    provenance: {
      repository: 'Horizon-Private-Server/horizon-forge-v2',
      workflow: '.github/workflows/ci.yml',
      runId: 42,
      commit: '0123456789abcdef0123456789abcdef01234567',
      tag: 'v2.0.0-nightly.42.01234567',
    },
    signature: { algorithm: 'ed25519', keyId: 'fixture-2026', file: 'release-manifest.json.sig' },
    artifacts: ['linux', 'windows'].map((platform) => ({
      platform,
      architecture: 'x64',
      file: `horizon-forge-2.0.0-nightly.42.01234567-${platform}-x64.${platform === 'windows' ? 'zip' : 'tar.gz'}`,
      size: packageBytes.length,
      sha256: packageHash,
      format: platform === 'windows' ? 'zip' : 'tar.gz',
      topLevelDirectory: `horizon-forge-${platform}-x64`,
      executable: platform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge',
      expandedSize: packageBytes.length * 2,
      fileCount: 4,
      packageManifestSize: 1024,
      packageManifestSha256: 'a'.repeat(64),
    })),
  };
  const manifestBytes = Buffer.from(`${JSON.stringify(manifest)}\n`);
  const signatureBytes = sign(null, manifestBytes, privateKey);
  return validateSignedReleaseEnvelope(manifestBytes, signatureBytes, trustedKeys, {
    channel: 'nightly',
    tag: manifest.tag,
    platform: 'linux',
    currentVersion: '2.0.0-nightly.41.ffffffff',
  });
}

function createManager(root, serverUrl, changes = [], timeoutMilliseconds = 2_000) {
  return new UpdateDownloadManager({
    root,
    channel: 'nightly',
    currentVersion: '2.0.0-nightly.41.ffffffff',
    platform: 'linux',
    trustedKeys,
    fetch,
    isTrustedUrl: (url) => url.origin === serverUrl,
    onChange: (snapshot) => changes.push(snapshot),
    timeoutMilliseconds,
  });
}

async function fixtureServer() {
  const server = createServer((request, response) => {
    if (request.url === '/package') {
      response.writeHead(200, { 'content-length': packageBytes.length });
      response.end(packageBytes);
    } else if (request.url === '/slow') {
      response.writeHead(200, { 'content-length': packageBytes.length });
      let offset = 0;
      const timer = setInterval(() => {
        if (response.destroyed || offset >= packageBytes.length) {
          clearInterval(timer);
          if (!response.destroyed) response.end();
          return;
        }
        response.write(packageBytes.subarray(offset, offset += 1024));
      }, 5);
    } else if (request.url === '/redirect') {
      response.writeHead(302, { location: 'https://example.com/untrusted' });
      response.end();
    } else if (request.url === '/trusted-redirect') {
      response.writeHead(302, { location: '/package' });
      response.end();
    } else if (request.url === '/oversize') {
      response.writeHead(200, { 'transfer-encoding': 'chunked' });
      response.write(packageBytes);
      response.end('extra');
    } else if (request.url === '/corrupt') {
      response.writeHead(200, { 'content-length': packageBytes.length });
      response.end(Buffer.alloc(packageBytes.length, 0x33));
    } else if (request.url === '/hang') {
      response.writeHead(200, { 'transfer-encoding': 'chunked' });
      response.write(packageBytes.subarray(0, 1));
    } else {
      response.writeHead(404);
      response.end();
    }
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  return {
    url: `http://127.0.0.1:${address.port}`,
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

test('verified downloads commit atomically and restart revalidates the complete package', async (context) => {
  const root = await mkdtemp(path.join(tmpdir(), 'forge-update-'));
  const server = await fixtureServer();
  context.after(async () => { await server.close(); await rm(root, { recursive: true }); });
  const changes = [];
  const envelope = createEnvelope();
  const manager = createManager(root, server.url, changes);
  assert.equal((await manager.start({ envelope, downloadUrl: `${server.url}/package` })).state, 'ready');
  assert.deepEqual([...new Set(changes.map(({ state }) => state))], ['downloading', 'verifying', 'ready']);
  assert.equal((await createManager(root, server.url).recover()).state, 'ready');

  await manager.cleanupReady();
  assert.equal((await manager.start({ envelope, downloadUrl: `${server.url}/trusted-redirect` })).state, 'ready');

  const ready = JSON.parse(await readFile(path.join(root, 'ready.json'), 'utf8'));
  await writeFile(path.join(root, ready.file), Buffer.alloc(packageBytes.length, 0x11));
  assert.equal((await createManager(root, server.url).recover()).state, 'error');
});

test('downloads reject concurrency, cancellation, redirects, corruption, oversize, timeout, and disk errors', async (context) => {
  const parent = await mkdtemp(path.join(tmpdir(), 'forge-update-errors-'));
  const server = await fixtureServer();
  context.after(async () => { await server.close(); await rm(parent, { recursive: true }); });
  const envelope = createEnvelope();

  const cancelRoot = path.join(parent, 'cancel');
  const cancelling = createManager(cancelRoot, server.url);
  const active = cancelling.start({ envelope, downloadUrl: `${server.url}/slow` });
  await assert.rejects(cancelling.start({ envelope, downloadUrl: `${server.url}/package` }), /already active/);
  assert.equal(cancelling.cancel(), true);
  assert.equal((await active).state, 'cancelled');
  assert.ok((await readdir(cancelRoot)).every((name) => !name.endsWith('.download')));

  for (const [name, expected] of [
    ['redirect', /not trusted/],
    ['corrupt', /SHA-256/],
    ['oversize', /exceeds/],
  ]) {
    const snapshot = await createManager(path.join(parent, name), server.url)
      .start({ envelope, downloadUrl: `${server.url}/${name}` });
    assert.equal(snapshot.state, 'error');
    assert.match(snapshot.message, expected);
  }

  const timeout = await createManager(path.join(parent, 'timeout'), server.url, [], 30)
    .start({ envelope, downloadUrl: `${server.url}/hang` });
  assert.equal(timeout.state, 'error');
  assert.match(timeout.message, /timed out/);

  const fileRoot = path.join(parent, 'not-a-directory');
  await writeFile(fileRoot, 'occupied');
  const disk = await createManager(fileRoot, server.url).start({ envelope, downloadUrl: `${server.url}/package` });
  assert.equal(disk.state, 'error');
});
