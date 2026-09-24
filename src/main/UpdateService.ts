import { createHash } from 'node:crypto';
import { open, readFile, rename, rm } from 'node:fs/promises';
import path from 'node:path';
import { app, dialog, net, shell } from 'electron';
import type { BrowserWindow } from 'electron';

import type { AvailableUpdate, ReleaseArtifact, UpdateChannel } from '../types/Updates.js';
import { formatBytes } from '../utils/Format.js';
import { compareForgeVersions, validateReleaseManifest } from '../utils/UpdateValidation.js';
import type { NotificationCenter } from './NotificationCenter.js';

const repository = 'Horizon-Private-Server/horizon-forge-v2';
const releasesApi = `https://api.github.com/repos/${repository}/releases`;
const trustedSource = `GitHub · ${repository}`;

interface GitHubAsset {
  name: string;
  browser_download_url: string;
}

interface GitHubRelease {
  tag_name: string;
  body: string | null;
  html_url: string;
  draft: boolean;
  prerelease: boolean;
  assets: GitHubAsset[];
}

interface UpdateServiceOptions {
  version: string;
  channel?: UpdateChannel;
  notifications: NotificationCenter;
  getMainWindow(): BrowserWindow | undefined;
  isProjectDirty(): boolean;
}

export class UpdateService {
  private checking = false;
  private readonly promptedTags = new Set<string>();

  private constructor(private readonly options: UpdateServiceOptions) {}

  get installedChannel(): UpdateChannel | undefined {
    return this.options.channel;
  }

  static async create(options: Omit<UpdateServiceOptions, 'channel'>, packageJsonPath: string): Promise<UpdateService> {
    let channel: UpdateChannel | undefined;
    try {
      const value: unknown = JSON.parse(await readFile(packageJsonPath, 'utf8'));
      if (isRecord(value) && isRecord(value.forge)
        && (value.forge.channel === 'stable' || value.forge.channel === 'nightly')) channel = value.forge.channel;
    } catch {
      // Development builds intentionally have no update channel.
    }
    return new UpdateService({ ...options, channel });
  }

  async check(manual: boolean, channel: UpdateChannel): Promise<void> {
    if (this.checking) return;
    if (!this.options.channel) {
      if (manual) this.options.notifications.publish({
        id: 'update:unavailable',
        title: 'Update checks are unavailable',
        message: 'Update checks are not available in local development or standalone builds.',
        severity: 'info',
        createdUnixMilliseconds: Date.now(),
      });
      return;
    }

    this.checking = true;
    try {
      const update = await this.findUpdate(channel);
      if (!update) {
        if (manual) this.options.notifications.publish({
          id: `update:current:${channel}`,
          title: 'Horizon Forge is up to date',
          message: `Channel: ${channel}\nVersion: ${this.options.version}\nTrusted source: ${trustedSource}`,
          severity: 'info',
          createdUnixMilliseconds: Date.now(),
        });
        return;
      }
      if (!manual && this.promptedTags.has(update.tag)) return;
      this.promptedTags.add(update.tag);
      this.publishUpdate(update);
    } catch (error) {
      if (!manual) {
        console.warn('Forge update check failed', error);
        return;
      }
      this.options.notifications.publish({
        id: `update:error:${channel}`,
        title: 'Forge could not securely check for updates',
        message: error instanceof Error ? error.message : String(error),
        severity: 'error',
        createdUnixMilliseconds: Date.now(),
      });
    } finally {
      this.checking = false;
    }
  }

  private async findUpdate(channel: UpdateChannel): Promise<AvailableUpdate | undefined> {
    const response = await fetchJson(channel === 'stable' ? `${releasesApi}/latest` : `${releasesApi}?per_page=20`);
    const releases = channel === 'stable' ? [response] : response;
    if (!Array.isArray(releases)) throw new TypeError('GitHub returned invalid release metadata.');
    const candidates = releases.map(parseRelease).filter((release) => release
      && !release.draft
      && release.prerelease === (channel === 'nightly')
      && isChannelTag(release.tag_name, channel)
      && (channel !== this.options.channel
        || compareForgeVersions(release.tag_name.slice(1), this.options.version) > 0)) as GitHubRelease[];
    candidates.sort((left, right) => compareForgeVersions(right.tag_name.slice(1), left.tag_name.slice(1)));
    const release = candidates[0];
    if (!release) return undefined;

    assertTrustedReleaseUrl(release.html_url);
    const manifestAsset = release.assets.find((asset) => asset.name === 'release-manifest.json');
    if (!manifestAsset) throw new Error(`${release.tag_name} has no release manifest.`);
    assertTrustedReleaseUrl(manifestAsset.browser_download_url);
    if (process.platform !== 'win32' && process.platform !== 'linux') {
      throw new Error(`Updates are not supported on ${process.platform} yet.`);
    }
    if (process.arch !== 'x64') throw new Error(`Updates are not supported on ${process.arch} yet.`);
    const platform: ReleaseArtifact['platform'] = process.platform === 'win32' ? 'windows' : 'linux';
    const { manifest, artifact } = validateReleaseManifest(
      await fetchJson(manifestAsset.browser_download_url), channel, release.tag_name, platform,
    );
    if (channel === this.options.channel && compareForgeVersions(manifest.version, this.options.version) <= 0) return undefined;
    const packageAsset = release.assets.find((asset) => asset.name === artifact.file);
    if (!packageAsset) throw new Error(`${release.tag_name} is missing ${artifact.file}.`);
    assertTrustedReleaseUrl(packageAsset.browser_download_url);
    return {
      version: manifest.version, channel, tag: manifest.tag, notes: release.body?.trim() || 'No release notes provided.',
      releaseUrl: release.html_url, downloadUrl: packageAsset.browser_download_url, artifact,
    };
  }

  private publishUpdate(update: AvailableUpdate): void {
    const windows = process.platform === 'win32';
    const dirtyWarning = this.options.isProjectDirty()
      ? '\n\nUnsaved project changes are open. Forge will not close or restart; save them before replacing the application.'
      : '';
    this.options.notifications.publish({
      id: `update:${update.channel}`,
      title: `Horizon Forge ${update.version} is available`,
      message: `Trusted source: ${trustedSource}\nChannel: ${update.channel}\nDownload: ${formatBytes(update.artifact.size)}`
        + `${dirtyWarning}\n\n${update.notes.slice(0, 3000)}`,
      severity: 'info',
      createdUnixMilliseconds: Date.now(),
      actionLabel: windows ? 'Download verified package' : 'Open GitHub release',
    }, () => this.handoff(update));
  }

  private async handoff(update: AvailableUpdate): Promise<boolean> {
    const windows = process.platform === 'win32';
    if (!windows) {
      await shell.openExternal(update.releaseUrl);
      return true;
    }
    return this.downloadWindowsPackage(update);
  }

  private async downloadWindowsPackage(update: AvailableUpdate): Promise<boolean> {
    const selection = this.options.getMainWindow()
      ? await dialog.showSaveDialog(this.options.getMainWindow()!, {
        defaultPath: path.join(app.getPath('downloads'), update.artifact.file),
      })
      : await dialog.showSaveDialog({ defaultPath: path.join(app.getPath('downloads'), update.artifact.file) });
    if (selection.canceled || !selection.filePath) return false;
    await downloadVerified(update.downloadUrl, selection.filePath, update.artifact);
    shell.showItemInFolder(selection.filePath);
    this.options.notifications.publish({
      id: `update-downloaded:${update.version}`,
      title: 'Verified Forge package downloaded',
      message: 'Save your work, close Forge, then replace this installation with the downloaded package.',
      severity: 'info',
      createdUnixMilliseconds: Date.now(),
    });
    return true;
  }

}

async function fetchJson(url: string): Promise<unknown> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15_000);
  try {
    const response = await net.fetch(url, {
      headers: { Accept: 'application/vnd.github+json', 'User-Agent': 'Horizon-Forge' },
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

async function downloadVerified(url: string, destination: string, artifact: ReleaseArtifact): Promise<void> {
  const temporary = path.join(path.dirname(destination), `.${path.basename(destination)}.${process.pid}.${Date.now()}.download`);
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10 * 60_000);
  const handle = await open(temporary, 'wx');
  try {
    const response = await net.fetch(url, { signal: controller.signal });
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

function parseRelease(value: unknown): GitHubRelease | undefined {
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

function isChannelTag(tag: string, channel: UpdateChannel): boolean {
  return channel === 'stable' ? /^v\d+\.\d+\.\d+$/.test(tag) : /^v\d+\.\d+\.\d+-nightly\.[1-9]\d*\.[0-9a-f]{8}$/.test(tag);
}

function assertTrustedReleaseUrl(value: string): void {
  const url = new URL(value);
  if (url.protocol !== 'https:' || url.hostname !== 'github.com'
    || !url.pathname.startsWith(`/${repository}/releases/`)) throw new Error('Release metadata contains an untrusted URL.');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
