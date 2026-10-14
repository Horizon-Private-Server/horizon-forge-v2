import { createHash } from 'node:crypto';
import { open, rename, rm } from 'node:fs/promises';
import path from 'node:path';

import type {
  AvailableUpdate,
  ReleaseArtifact,
  TrustedUpdateKey,
  UpdateChannel,
  VerifiedReleaseEnvelope,
} from '../types/Updates.js';
import {
  compareForgeVersions,
  validateReleaseManifest,
  validateSignedReleaseEnvelope,
} from '../utils/UpdateValidation.js';
import { isTrustedGitHubUpdateUrl } from './UpdateDownloadManager.js';

export const releaseRepository = 'Horizon-Private-Server/horizon-forge-v2';
export const releasesApi = `https://api.github.com/repos/${releaseRepository}/releases`;
export const trustedReleaseSource = `GitHub · ${releaseRepository}`;

interface GitHubAsset {
  name: string;
  browser_download_url: string;
}

export interface GitHubRelease {
  tag_name: string;
  body: string | null;
  html_url: string;
  draft: boolean;
  prerelease: boolean;
  assets: GitHubAsset[];
}

export interface ResolvedUpdate extends AvailableUpdate {
  envelope?: VerifiedReleaseEnvelope;
}

export function parseTrustedUpdateKeys(value: unknown): TrustedUpdateKey[] {
  if (value === undefined) return [];
  if (!Array.isArray(value) || value.length > 8) throw new TypeError('Forge update trust configuration is invalid.');
  const keys = value.map((entry) => {
    if (!isRecord(entry)
      || Object.keys(entry).some((key) => key !== 'id' && key !== 'publicKeySpkiBase64')
      || typeof entry.id !== 'string'
      || !/^[A-Za-z0-9._-]{1,64}$/.test(entry.id)
      || typeof entry.publicKeySpkiBase64 !== 'string'
      || entry.publicKeySpkiBase64.length > 256
      || !/^[A-Za-z0-9+/]+={0,2}$/.test(entry.publicKeySpkiBase64)) {
      throw new TypeError('Forge update trust configuration is invalid.');
    }
    return { id: entry.id, publicKeySpkiBase64: entry.publicKeySpkiBase64 };
  });
  if (new Set(keys.map((key) => key.id)).size !== keys.length) {
    throw new TypeError('Forge update trust configuration contains duplicate key IDs.');
  }
  return keys;
}

interface FindUpdateOptions {
  channel: UpdateChannel;
  installedChannel?: UpdateChannel;
  currentVersion: string;
  trustedKeys: readonly TrustedUpdateKey[];
  platform: NodeJS.Platform;
  architecture: string;
}

export async function findGitHubUpdate(options: FindUpdateOptions): Promise<ResolvedUpdate | undefined> {
  const response = await fetchGithubJson(options.channel === 'stable'
    ? `${releasesApi}/latest`
    : `${releasesApi}?per_page=20`);
  const values = options.channel === 'stable' ? [response] : response;
  if (!Array.isArray(values)) throw new TypeError('GitHub returned invalid release metadata.');
  const releases = values.map(parseGitHubRelease).filter((release) => release
    && !release.draft
    && release.prerelease === (options.channel === 'nightly')
    && isChannelTag(release.tag_name, options.channel)
    && (options.channel !== options.installedChannel
      || compareForgeVersions(release.tag_name.slice(1), options.currentVersion) > 0)) as GitHubRelease[];
  releases.sort((left, right) => compareForgeVersions(right.tag_name.slice(1), left.tag_name.slice(1)));
  const release = releases[0];
  if (!release) return undefined;

  assertTrustedReleaseUrl(release.html_url);
  const manifestAsset = release.assets.find((asset) => asset.name === 'release-manifest.json');
  if (!manifestAsset) throw new Error(`${release.tag_name} has no release manifest.`);
  assertTrustedReleaseUrl(manifestAsset.browser_download_url);
  if (options.platform !== 'win32' && options.platform !== 'linux') {
    throw new Error(`Updates are not supported on ${options.platform} yet.`);
  }
  if (options.architecture !== 'x64') throw new Error(`Updates are not supported on ${options.architecture} yet.`);
  const platform: ReleaseArtifact['platform'] = options.platform === 'win32' ? 'windows' : 'linux';
  const manifestBytes = await fetchReleaseAsset(manifestAsset.browser_download_url, 2 * 1024 * 1024);
  let metadata: unknown;
  try {
    metadata = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(manifestBytes));
  } catch {
    throw new TypeError('Release manifest is not valid UTF-8 JSON.');
  }
  let envelope: VerifiedReleaseEnvelope | undefined;
  let manifest;
  let artifact;
  if (isRecord(metadata) && metadata.schemaVersion === 2) {
    const signatureAsset = release.assets.find((asset) => asset.name === 'release-manifest.json.sig');
    if (!signatureAsset) throw new Error(`${release.tag_name} has no release manifest signature.`);
    assertTrustedReleaseUrl(signatureAsset.browser_download_url);
    envelope = validateSignedReleaseEnvelope(
      manifestBytes,
      await fetchReleaseAsset(signatureAsset.browser_download_url, 64),
      options.trustedKeys,
      { channel: options.channel, tag: release.tag_name, platform, currentVersion: options.currentVersion },
    );
    ({ manifest, artifact } = envelope);
  } else {
    ({ manifest, artifact } = validateReleaseManifest(metadata, options.channel, release.tag_name, platform));
  }
  if (options.channel === options.installedChannel
    && compareForgeVersions(manifest.version, options.currentVersion) <= 0) return undefined;
  const packageAsset = release.assets.find((asset) => asset.name === artifact.file);
  if (!packageAsset) throw new Error(`${release.tag_name} is missing ${artifact.file}.`);
  assertTrustedReleaseUrl(packageAsset.browser_download_url);
  return {
    version: manifest.version,
    channel: options.channel,
    tag: manifest.tag,
    notes: release.body?.trim() || 'No release notes provided.',
    releaseUrl: release.html_url,
    downloadUrl: packageAsset.browser_download_url,
    artifact,
    envelope,
  };
}

export async function fetchGithubJson(url: string): Promise<unknown> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15_000);
  try {
    const response = await fetch(url, {
      headers: { Accept: 'application/vnd.github+json', 'User-Agent': 'Horizon-Forge' },
      redirect: 'error',
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(`Trusted update source returned HTTP ${response.status}.`);
    const text = await response.text();
    if (Buffer.byteLength(text) > 2 * 1024 * 1024) throw new Error('Release metadata is too large.');
    return JSON.parse(text);
  } finally {
    clearTimeout(timeout);
  }
}

export async function fetchReleaseAsset(url: string, maximumBytes: number): Promise<Uint8Array> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15_000);
  try {
    const response = await fetchTrustedRelease(url, controller.signal);
    if (!response.ok || !response.body) throw new Error(`Trusted update source returned HTTP ${response.status}.`);
    const reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let total = 0;
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > maximumBytes) throw new Error('Release asset is larger than allowed.');
      chunks.push(value);
    }
    const bytes = new Uint8Array(total);
    let offset = 0;
    for (const chunk of chunks) {
      bytes.set(chunk, offset);
      offset += chunk.byteLength;
    }
    return bytes;
  } finally {
    clearTimeout(timeout);
  }
}

export async function downloadVerifiedPackage(
  url: string,
  destination: string,
  artifact: ReleaseArtifact,
): Promise<void> {
  const temporary = path.join(path.dirname(destination), `.${path.basename(destination)}.${process.pid}.${Date.now()}.download`);
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10 * 60_000);
  const handle = await open(temporary, 'wx');
  try {
    const response = await fetchTrustedRelease(url, controller.signal);
    if (!response.ok || !response.body) throw new Error(`Package download returned HTTP ${response.status}.`);
    const hash = createHash('sha256');
    let size = 0;
    const reader = response.body.getReader();
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      const bytes = Buffer.from(value);
      size += bytes.length;
      if (size > artifact.size) throw new Error('Package download exceeds its declared size.');
      hash.update(bytes);
      await handle.write(bytes);
    }
    await handle.sync();
    if (size !== artifact.size || hash.digest('hex') !== artifact.sha256.toLowerCase()) {
      throw new Error('Package size or SHA-256 does not match the trusted release manifest.');
    }
    await handle.close();
    await rename(temporary, destination);
  } catch (error) {
    await handle.close().catch(() => undefined);
    await rm(temporary, { force: true }).catch(() => undefined);
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

async function fetchTrustedRelease(url: string, signal: AbortSignal): Promise<Response> {
  let current = new URL(url);
  if (!isTrustedGitHubUpdateUrl(current, false)) throw new Error('Release asset URL is not trusted.');
  for (let redirects = 0; redirects <= 5; redirects += 1) {
    const response = await fetch(current.href, { signal, redirect: 'manual' });
    if (![301, 302, 303, 307, 308].includes(response.status)) return response;
    const location = response.headers.get('location');
    if (!location) throw new Error('Release asset redirect has no destination.');
    current = new URL(location, current);
    if (!isTrustedGitHubUpdateUrl(current, true)) throw new Error('Release asset redirect is not trusted.');
  }
  throw new Error('Release asset followed too many redirects.');
}

export function parseGitHubRelease(value: unknown): GitHubRelease | undefined {
  if (!isRecord(value)
    || typeof value.tag_name !== 'string'
    || typeof value.html_url !== 'string'
    || typeof value.draft !== 'boolean'
    || typeof value.prerelease !== 'boolean'
    || !Array.isArray(value.assets)) return undefined;
  const assets = value.assets.filter((asset): asset is GitHubAsset => isRecord(asset)
    && typeof asset.name === 'string' && typeof asset.browser_download_url === 'string');
  return { ...value, body: typeof value.body === 'string' ? value.body : null, assets } as GitHubRelease;
}

export function assertTrustedReleaseUrl(value: string): void {
  const url = new URL(value);
  if (url.protocol !== 'https:' || url.hostname !== 'github.com'
    || !url.pathname.startsWith(`/${releaseRepository}/releases/`)) {
    throw new Error('Release metadata contains an untrusted URL.');
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isChannelTag(tag: string, channel: UpdateChannel): boolean {
  return channel === 'stable'
    ? /^v\d+\.\d+\.\d+$/.test(tag)
    : /^v\d+\.\d+\.\d+-nightly\.[1-9]\d*\.[0-9a-f]{8}$/.test(tag);
}
