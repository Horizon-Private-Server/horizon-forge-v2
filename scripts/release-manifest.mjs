import { createHash, createPrivateKey, sign } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, readdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export async function createReleaseManifest(directory, version, channel, commit, sdkRevision, tag, signedIdentity) {
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
    const archivePath = path.join(directory, name);
    const metadata = await stat(archivePath);
    const platform = name.includes('-linux-x64.') ? 'linux' : name.includes('-windows-x64.') ? 'windows' : undefined;
    if (!platform) throw new Error(`Unknown release package ${name}`);
    const artifact = {
      platform,
      architecture: 'x64',
      file: name,
      size: metadata.size,
      sha256: await hashFile(archivePath),
    };
    if (!signedIdentity) return artifact;
    const packageManifestPath = `${archivePath}.package.json`;
    const packageManifestBytes = await readFile(packageManifestPath);
    const packageManifest = JSON.parse(packageManifestBytes.toString('utf8'));
    validatePackageIdentity(packageManifest, { version, channel, commit, sdkRevision, platform });
    return {
      ...artifact,
      format: packageManifest.format,
      topLevelDirectory: packageManifest.topLevelDirectory,
      executable: packageManifest.executable,
      expandedSize: packageManifest.expandedSize,
      fileCount: packageManifest.fileCount,
      packageManifestSize: packageManifestBytes.length,
      packageManifestSha256: createHash('sha256').update(packageManifestBytes).digest('hex'),
    };
  }));
  const common = {
    version,
    channel,
    tag,
    commit: commit.toLowerCase(),
    sdkRevision,
    bridgeProtocol: 1,
  };
  if (!signedIdentity) return {
    schemaVersion: 1,
    ...common,
    artifacts,
  };
  if (!Number.isSafeInteger(signedIdentity.runId) || signedIdentity.runId <= 0
    || !/^[A-Za-z0-9._-]{1,64}$/.test(signedIdentity.keyId)) {
    throw new Error('Signed release identity is invalid.');
  }
  return {
    schemaVersion: 2,
    ...common,
    minimumUpdaterVersion: '2.0.0',
    provenance: {
      repository: 'Horizon-Private-Server/horizon-forge-v2',
      workflow: '.github/workflows/ci.yml',
      runId: signedIdentity.runId,
      commit: commit.toLowerCase(),
      tag,
    },
    signature: {
      algorithm: 'ed25519',
      keyId: signedIdentity.keyId,
      file: 'release-manifest.json.sig',
    },
    artifacts,
  };
}

export function signReleaseManifest(manifestBytes, privateKeyPem) {
  const privateKey = createPrivateKey(privateKeyPem);
  if (privateKey.asymmetricKeyType !== 'ed25519') throw new Error('Update signing key must be Ed25519.');
  return sign(null, manifestBytes, privateKey);
}

async function hashFile(filePath) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

function validatePackageIdentity(value, identity) {
  if (!value || typeof value !== 'object'
    || value.schemaVersion !== 1
    || value.product !== 'horizon-forge'
    || value.version !== identity.version
    || value.channel !== identity.channel
    || value.commit !== identity.commit.toLowerCase()
    || value.sdkRevision !== identity.sdkRevision
    || value.bridgeProtocol !== 1
    || value.platform !== identity.platform
    || value.architecture !== 'x64'
    || value.format !== (identity.platform === 'windows' ? 'zip' : 'tar.gz')
    || value.topLevelDirectory !== `horizon-forge-${identity.platform}-x64`
    || value.executable !== (identity.platform === 'windows' ? 'horizon-forge.exe' : 'horizon-forge')
    || !Number.isSafeInteger(value.expandedSize) || value.expandedSize <= 0 || value.expandedSize > 8 * 1024 ** 3
    || !Number.isSafeInteger(value.fileCount) || value.fileCount <= 0 || value.fileCount > 10_000) {
    throw new Error(`Package manifest for ${identity.platform} does not match the release identity.`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [directory, version, channel, commit, sdkRevision, tag, output] = process.argv.slice(2);
  if (!output) throw new Error('Usage: release-manifest.mjs <directory> <version> <channel> <commit> <sdk> <tag> <output>');
  const privateKey = process.env.FORGE_UPDATE_SIGNING_KEY_PEM;
  const keyId = process.env.FORGE_UPDATE_KEY_ID;
  const publicKey = process.env.FORGE_UPDATE_PUBLIC_KEY_SPKI;
  const runId = process.env.GITHUB_RUN_ID;
  const configured = [privateKey, keyId, publicKey].filter(Boolean).length;
  if (configured !== 0 && configured !== 3) {
    throw new Error('Signed releases require the private key, key ID, pinned public key, and workflow run ID together.');
  }
  if (configured === 3 && !runId) throw new Error('Signed releases require a workflow run ID.');
  const manifest = await createReleaseManifest(directory, version, channel, commit, sdkRevision, tag,
    configured === 3 ? { keyId, runId: Number(runId) } : undefined);
  const bytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
  await writeFile(output, bytes);
  if (privateKey) await writeFile(`${output}.sig`, signReleaseManifest(bytes, privateKey));
}
