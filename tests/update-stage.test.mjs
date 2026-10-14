import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { chmod, cp, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { gzipSync } from 'node:zlib';
import test from 'node:test';

import { inspectUpdateArchive, validateArchiveLayout } from '../dist-electron/main/UpdateArchiveInspector.js';
import { UpdatePackageStager } from '../dist-electron/main/UpdatePackageStager.js';
import { validateSignedReleaseEnvelope } from '../dist-electron/utils/UpdateValidation.js';

const run = promisify(execFile);
const { privateKey, publicKey } = generateKeyPairSync('ed25519');
const trustedKeys = [{
  id: 'stage-fixture',
  publicKeySpkiBase64: publicKey.export({ format: 'der', type: 'spki' }).toString('base64'),
}];

test('Linux and Windows packages stage completely and restart rejects a changed stage', async (context) => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-stage-'));
  context.after(() => rm(directory, { recursive: true }));

  if (process.platform === 'linux') {
    const linux = await createFixture(directory, 'linux');
    const linuxStaging = path.join(directory, 'linux-staging');
    const linuxChanges = [];
    const linuxStager = new UpdatePackageStager({
      root: linuxStaging,
      targetRoot: linux.packageRoot,
      platform: 'linux',
      onChange: (snapshot) => linuxChanges.push(snapshot),
    });
    assert.equal((await linuxStager.stage(linux.download)).state, 'ready');
    assert.deepEqual([...new Set(linuxChanges.map(({ state }) => state))], [
      'inspecting', 'extracting', 'validating', 'ready',
    ]);
    assert.equal((await new UpdatePackageStager({ root: linuxStaging, targetRoot: linux.packageRoot, platform: 'linux' })
      .recover(linux.download)).state, 'ready');
    await writeFile(path.join(linux.packageRoot, 'horizon-forge'), 'changed');
    assert.equal((await new UpdatePackageStager({ root: linuxStaging, targetRoot: linux.packageRoot, platform: 'linux' })
      .recover(linux.download)).state, 'error');
    await writeFile(path.join(linux.packageRoot, 'horizon-forge'), 'app');
    await chmod(path.join(linux.packageRoot, 'horizon-forge'), 0o755);
    assert.equal((await linuxStager.stage(linux.download)).state, 'ready');
    const linuxRecord = JSON.parse(await readFile(path.join(linuxStaging, 'ready.json'), 'utf8'));
    await writeFile(path.join(linuxStaging, linuxRecord.directory, linux.rootName, 'horizon-forge'), 'changed');
    assert.equal((await new UpdatePackageStager({ root: linuxStaging, targetRoot: linux.packageRoot, platform: 'linux' })
      .recover(linux.download)).state, 'error');
  }

  const windows = await createFixture(directory, 'windows');
  const physicalStaging = path.join(directory, 'windows-staging-root');
  const indirectStaging = path.join(directory, 'windows-staging-link');
  await mkdir(physicalStaging);
  await symlink(physicalStaging, indirectStaging, process.platform === 'win32' ? 'junction' : 'dir');
  const windowsStager = new UpdatePackageStager({
    root: path.join(indirectStaging, 'staging'),
    targetRoot: windows.packageRoot,
    platform: 'windows',
    extract: async (_archive, destination) => {
      await cp(windows.packageRoot, path.join(destination, windows.rootName), { recursive: true });
    },
  });
  const windowsState = await windowsStager.stage(windows.download);
  assert.equal(windowsState.state, 'ready', windowsState.message);
});

test('archive preflight rejects unsafe paths, collisions, links, and cancellation cleans partial stages', async (context) => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-stage-invalid-'));
  context.after(() => rm(directory, { recursive: true }));
  const fixture = await createFixture(directory, 'linux');
  const artifact = fixture.download.envelope.artifact;
  const validEntries = [
    { path: `${fixture.rootName}/forge-package.json`, kind: 'file', size: artifact.packageManifestSize },
    ...fixture.files.map((file) => ({ path: `${fixture.rootName}/${file.path}`, kind: 'file', size: file.bytes.length })),
  ];
  assert.throws(() => validateArchiveLayout([
    ...validEntries.slice(0, -1), { ...validEntries.at(-1), path: '../escape' },
  ], artifact), /unsafe path|unexpected root/);
  assert.throws(() => validateArchiveLayout([
    ...validEntries, { ...validEntries[0], path: validEntries[0].path.toUpperCase() },
  ], { ...artifact, fileCount: artifact.fileCount + 1, expandedSize: artifact.expandedSize + artifact.packageManifestSize }),
  /duplicate or case-colliding/);

  for (const type of ['1', '2', '3', '4', '6']) {
    const specialTar = path.join(directory, `special-${type}.tar.gz`);
    await writeFile(specialTar, gzipSync(Buffer.concat([
      tarHeader(`${fixture.rootName}/escape`, 0, type), Buffer.alloc(1024),
    ])));
    await assert.rejects(inspectUpdateArchive(specialTar, artifact), /unsupported entry type/);
  }

  const linkedZipBytes = createStoredZip([{
    name: `${fixture.rootName}/escape`, bytes: Buffer.alloc(0), mode: 0o120777,
  }]);
  const linkedZip = path.join(directory, 'linked.zip');
  await writeFile(linkedZip, linkedZipBytes);
  await assert.rejects(inspectUpdateArchive(linkedZip, {
    ...artifact,
    platform: 'windows',
    format: 'zip',
    size: linkedZipBytes.length,
    topLevelDirectory: 'horizon-forge-windows-x64',
    executable: 'horizon-forge.exe',
  }), /linked, special/);

  const staging = path.join(directory, 'cancel-staging');
  let extracting;
  const enteredExtraction = new Promise((resolve) => { extracting = resolve; });
  const stager = new UpdatePackageStager({
    root: staging,
    targetRoot: fixture.packageRoot,
    platform: 'linux',
    extract: async (_archive, _destination, signal) => {
      extracting();
      await new Promise((resolve, reject) => {
        signal.addEventListener('abort', () => reject(signal.reason), { once: true });
      });
    },
  });
  const operation = stager.stage(fixture.download);
  await enteredExtraction;
  assert.equal(stager.cancel(), true);
  assert.equal((await operation).state, 'cancelled');
  assert.ok((await readdir(staging)).every((name) => !name.startsWith('.partial-')));
});

async function createFixture(directory, platform) {
  const rootName = `horizon-forge-${platform}-x64`;
  const packageRoot = path.join(directory, `${platform}-package`, rootName);
  const files = platform === 'linux' ? [
    { path: 'horizon-forge', bytes: Buffer.from('app'), executable: true },
    { path: 'resources/app/package.json', bytes: Buffer.from('{}') },
    { path: 'resources/host/Forge.Host', bytes: Buffer.from('host') },
  ] : [
    { path: 'horizon-forge.exe', bytes: Buffer.from('app') },
    { path: 'resources/app/package.json', bytes: Buffer.from('{}') },
    { path: 'resources/host/Forge.Host.exe', bytes: Buffer.from('host') },
  ];
  for (const file of files) {
    const absolute = path.join(packageRoot, ...file.path.split('/'));
    await mkdir(path.dirname(absolute), { recursive: true });
    await writeFile(absolute, file.bytes);
    if (file.executable) await chmod(absolute, 0o755);
  }
  const manifest = {
    schemaVersion: 1,
    product: 'horizon-forge',
    version: '2.0.0-nightly.42.01234567',
    channel: 'nightly',
    commit: '0123456789abcdef0123456789abcdef01234567',
    sdkRevision: 'v0.5.0',
    bridgeProtocol: 1,
    platform,
    architecture: 'x64',
    format: platform === 'windows' ? 'zip' : 'tar.gz',
    topLevelDirectory: rootName,
    executable: platform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge',
    minimumUpdaterVersion: '2.0.0',
    provenance: {
      repository: 'Horizon-Private-Server/horizon-forge-v2',
      workflow: '.github/workflows/ci.yml',
    },
    fileCount: files.length,
    expandedSize: files.reduce((total, file) => total + file.bytes.length, 0),
    executablePaths: files.filter((file) => file.executable).map((file) => file.path),
    files: files.map((file) => ({
      path: file.path,
      size: file.bytes.length,
      sha256: createHash('sha256').update(file.bytes).digest('hex'),
    })),
  };
  const manifestBytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
  await writeFile(path.join(packageRoot, 'forge-package.json'), manifestBytes);
  const archivePath = path.join(directory, `${platform}-package.${platform === 'windows' ? 'zip' : 'tar.gz'}`);
  if (platform === 'linux') {
    await run('tar', ['--format=ustar', '-czf', path.basename(archivePath), '-C', `${platform}-package`, rootName], {
      cwd: directory,
    });
  } else {
    const entries = [
      { name: `${rootName}/forge-package.json`, bytes: manifestBytes },
      ...files.map((file) => ({ name: `${rootName}/${file.path}`, bytes: file.bytes })),
    ];
    await writeFile(archivePath, createStoredZip(entries));
  }
  const archiveBytes = await readFile(archivePath);
  const artifact = {
    platform,
    architecture: 'x64',
    file: `horizon-forge-2.0.0-nightly.42.01234567-${platform}-x64.${platform === 'windows' ? 'zip' : 'tar.gz'}`,
    size: archiveBytes.length,
    sha256: createHash('sha256').update(archiveBytes).digest('hex'),
    format: manifest.format,
    topLevelDirectory: rootName,
    executable: manifest.executable,
    expandedSize: manifest.expandedSize,
    fileCount: manifest.fileCount,
    packageManifestSize: manifestBytes.length,
    packageManifestSha256: createHash('sha256').update(manifestBytes).digest('hex'),
  };
  const otherPlatform = platform === 'windows' ? 'linux' : 'windows';
  const release = {
    schemaVersion: 2,
    version: manifest.version,
    channel: manifest.channel,
    tag: `v${manifest.version}`,
    commit: manifest.commit,
    sdkRevision: manifest.sdkRevision,
    bridgeProtocol: 1,
    minimumUpdaterVersion: manifest.minimumUpdaterVersion,
    provenance: {
      repository: manifest.provenance.repository,
      workflow: manifest.provenance.workflow,
      runId: 42,
      commit: manifest.commit,
      tag: `v${manifest.version}`,
    },
    signature: { algorithm: 'ed25519', keyId: 'stage-fixture', file: 'release-manifest.json.sig' },
    artifacts: [artifact, {
      ...artifact,
      platform: otherPlatform,
      file: `horizon-forge-${manifest.version}-${otherPlatform}-x64.${otherPlatform === 'windows' ? 'zip' : 'tar.gz'}`,
      format: otherPlatform === 'windows' ? 'zip' : 'tar.gz',
      topLevelDirectory: `horizon-forge-${otherPlatform}-x64`,
      executable: otherPlatform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge',
    }],
  };
  const releaseBytes = Buffer.from(`${JSON.stringify(release)}\n`);
  const envelope = validateSignedReleaseEnvelope(releaseBytes, sign(null, releaseBytes, privateKey), trustedKeys, {
    channel: 'nightly',
    tag: release.tag,
    platform,
    currentVersion: '2.0.0-nightly.41.ffffffff',
  });
  return {
    rootName,
    packageRoot,
    files,
    download: {
      envelope,
      packagePath: archivePath,
      sourceUrl: `https://github.com/Horizon-Private-Server/horizon-forge-v2/releases/download/${release.tag}/${artifact.file}`,
    },
  };
}

function createStoredZip(entries) {
  const localParts = [];
  const centralParts = [];
  let offset = 0;
  for (const entry of entries) {
    const name = Buffer.from(entry.name);
    const crc = crc32(entry.bytes);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x800, 6);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(entry.bytes.length, 18);
    local.writeUInt32LE(entry.bytes.length, 22);
    local.writeUInt16LE(name.length, 26);
    localParts.push(local, name, entry.bytes);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(0x0314, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x800, 8);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(entry.bytes.length, 20);
    central.writeUInt32LE(entry.bytes.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(((entry.mode ?? 0o100644) * 65_536) >>> 0, 38);
    central.writeUInt32LE(offset, 42);
    centralParts.push(central, name);
    offset += local.length + name.length + entry.bytes.length;
  }
  const centralBytes = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBytes.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...localParts, centralBytes, end]);
}

function tarHeader(name, size, type) {
  const header = Buffer.alloc(512);
  header.write(name, 0, 100, 'utf8');
  writeOctal(header, 100, 8, 0o644);
  writeOctal(header, 108, 8, 0);
  writeOctal(header, 116, 8, 0);
  writeOctal(header, 124, 12, size);
  writeOctal(header, 136, 12, 0);
  header.fill(0x20, 148, 156);
  header.write(type, 156, 1, 'ascii');
  header.write('ustar\0', 257, 6, 'ascii');
  header.write('00', 263, 2, 'ascii');
  writeOctal(header, 148, 8, header.reduce((sum, byte) => sum + byte, 0));
  return header;
}

function writeOctal(buffer, offset, length, value) {
  const text = value.toString(8).padStart(length - 1, '0');
  buffer.write(text, offset, length - 1, 'ascii');
  buffer[offset + length - 1] = 0;
}

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}
