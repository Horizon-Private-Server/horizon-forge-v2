import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { lstat, opendir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const manifestName = 'forge-package.json';
const maximumFiles = 10_000;
const maximumExpandedSize = 8 * 1024 ** 3;
const minimumUpdaterVersion = '2.0.0';

export async function createPackageManifest(directory, identity) {
  const target = parseTarget(identity.target);
  if (!['development', 'nightly', 'stable'].includes(identity.channel)) {
    throw new Error(`Invalid package channel ${identity.channel}`);
  }
  const versionPattern = identity.channel === 'nightly'
    ? /^\d+\.\d+\.\d+-nightly\.[1-9]\d*\.[0-9a-f]{8}$/
    : /^\d+\.\d+\.\d+$/;
  if (!versionPattern.test(identity.version)) throw new Error(`Invalid package version ${identity.version}`);
  if (!/^[0-9a-f]{40}$/i.test(identity.commit)) throw new Error('Package commit must be a full SHA.');
  if (!/^(?:[0-9a-f]{40}|v\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.-]+)?)$/i.test(identity.sdkRevision)) {
    throw new Error('Package SDK revision must be a full SHA or pinned release tag.');
  }
  if (identity.executable !== target.executable) {
    throw new Error(`Package executable must be ${target.executable}.`);
  }

  const files = [];
  const executablePaths = [];
  const limits = { expandedSize: 0 };
  await collectFiles(directory, directory, target.platform, files, executablePaths, limits);
  files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
  executablePaths.sort();
  assertPaths(files.map((entry) => entry.path));
  const expandedSize = files.reduce((total, entry) => total + entry.size, 0);
  if (files.length === 0 || files.length > maximumFiles || expandedSize > maximumExpandedSize) {
    throw new Error('Package exceeds its file-count or expanded-size limit.');
  }
  const required = [
    identity.executable,
    'resources/app/package.json',
    `resources/host/Forge.Host${target.platform === 'windows' ? '.exe' : ''}`,
  ];
  if (required.some((requiredPath) => !files.some((entry) => entry.path === requiredPath))) {
    throw new Error('Package is missing a required application file.');
  }

  return {
    schemaVersion: 1,
    product: 'horizon-forge',
    version: identity.version,
    channel: identity.channel,
    commit: identity.commit.toLowerCase(),
    sdkRevision: identity.sdkRevision,
    bridgeProtocol: 1,
    platform: target.platform,
    architecture: 'x64',
    format: target.format,
    topLevelDirectory: `horizon-forge-${identity.target}`,
    executable: identity.executable,
    minimumUpdaterVersion,
    provenance: {
      repository: 'Horizon-Private-Server/horizon-forge-v2',
      workflow: process.env.GITHUB_ACTIONS === 'true' ? '.github/workflows/ci.yml' : 'local',
    },
    fileCount: files.length,
    expandedSize,
    executablePaths,
    files,
  };
}

async function collectFiles(root, directory, platform, files, executablePaths, limits) {
  const entries = [];
  const handle = await opendir(directory);
  for await (const entry of handle) entries.push(entry);
  entries.sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
  for (const entry of entries) {
    const absolute = path.join(directory, entry.name);
    const relative = path.relative(root, absolute).split(path.sep).join('/');
    if (relative === manifestName) continue;
    if (relative.length > 512) throw new Error(`Package path is too long: ${relative}`);
    if (entry.isSymbolicLink()) throw new Error(`Package cannot contain a symbolic link: ${relative}`);
    if (entry.isDirectory()) {
      await collectFiles(root, absolute, platform, files, executablePaths, limits);
      continue;
    }
    if (!entry.isFile()) throw new Error(`Package contains a special file: ${relative}`);
    if (files.length >= maximumFiles) throw new Error('Package contains too many files.');
    const metadata = await lstat(absolute);
    if (!Number.isSafeInteger(metadata.size) || metadata.size < 0) throw new Error(`Invalid file size: ${relative}`);
    limits.expandedSize += metadata.size;
    if (limits.expandedSize > maximumExpandedSize) throw new Error('Package expanded size is too large.');
    files.push({ path: relative, size: metadata.size, sha256: await hashFile(absolute) });
    if (platform === 'linux' && (metadata.mode & 0o111) !== 0) executablePaths.push(relative);
  }
}

async function hashFile(filePath) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

function assertPaths(paths) {
  const all = new Set(paths.map((value) => value.toLowerCase()));
  if (all.size !== paths.length) throw new Error('Package contains case-colliding paths.');
  for (const value of paths) {
    if (!value || value.length > 512 || !/^[\x20-\x7e]+$/.test(value)
      || value.startsWith('/') || value.endsWith('/') || value.includes('\\') || value.includes(':')
      || value.split('/').some((segment) => !segment || segment === '.' || segment === '..')) {
      throw new Error(`Package contains an unsafe path: ${value}`);
    }
    const lower = value.toLowerCase();
    const segments = lower.split('/');
    for (let length = 1; length < segments.length; length += 1) {
      if (all.has(segments.slice(0, length).join('/'))) throw new Error(`Package path collides with a file: ${value}`);
    }
  }
}

function parseTarget(value) {
  if (value === 'linux-x64') return { platform: 'linux', format: 'tar.gz', executable: 'horizon-forge' };
  if (value === 'windows-x64') return { platform: 'windows', format: 'zip', executable: 'horizon-forge.exe' };
  throw new Error(`Unsupported package target ${value}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [directory, version, channel, commit, sdkRevision, target, executable] = process.argv.slice(2);
  if (!executable) {
    throw new Error('Usage: package-manifest.mjs <directory> <version> <channel> <commit> <sdk> <target> <executable>');
  }
  const manifest = await createPackageManifest(directory, {
    version, channel, commit, sdkRevision, target, executable,
  });
  await writeFile(path.join(directory, manifestName), `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
}
