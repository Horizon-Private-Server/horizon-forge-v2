import { createHash } from 'node:crypto';
import { readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export async function createReleaseManifest(directory, version, channel, commit, sdkRevision, tag) {
  if (!['nightly', 'stable'].includes(channel)) throw new Error(`Unsupported release channel ${channel}`);
  const versionPattern = channel === 'stable'
    ? /^\d+\.\d+\.\d+$/
    : /^\d+\.\d+\.\d+-nightly\.[1-9]\d*\.[0-9a-f]{8}$/;
  if (!versionPattern.test(version) || tag !== `v${version}`)
    throw new Error(`Release identity ${tag} does not match ${channel} version ${version}`);
  if (!/^[0-9a-f]{40}$/i.test(commit)) throw new Error('Release commit must be a full SHA');
  if (!/^(?:[0-9a-f]{40}|v\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.-]+)?)$/i.test(sdkRevision))
    throw new Error('SDK revision must be a full SHA or pinned release tag');
  const names = (await readdir(directory))
    .filter((name) => name.endsWith('.tar.gz') || name.endsWith('.zip'))
    .sort();
  if (names.length !== 2) throw new Error(`Expected two release packages, found ${names.length}`);
  const artifacts = await Promise.all(names.map(async (name) => {
    if (!name.startsWith(`horizon-forge-${version}-`))
      throw new Error(`Release package ${name} does not match version ${version}`);
    const bytes = await readFile(path.join(directory, name));
    const platform = name.includes('-linux-x64.') ? 'linux' : name.includes('-windows-x64.') ? 'windows' : undefined;
    if (!platform) throw new Error(`Unknown release package ${name}`);
    return {
      platform,
      architecture: 'x64',
      file: name,
      size: bytes.length,
      sha256: createHash('sha256').update(bytes).digest('hex'),
    };
  }));
  return {
    schemaVersion: 1,
    version,
    channel,
    tag,
    commit: commit.toLowerCase(),
    sdkRevision,
    bridgeProtocol: 1,
    artifacts,
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [directory, version, channel, commit, sdkRevision, tag, output] = process.argv.slice(2);
  if (!output) throw new Error('Usage: release-manifest.mjs <directory> <version> <channel> <commit> <sdk> <tag> <output>');
  await writeFile(output, `${JSON.stringify(
    await createReleaseManifest(directory, version, channel, commit, sdkRevision, tag), null, 2)}\n`);
}
