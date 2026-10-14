import { createHash, randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { mkdir, open, readFile, readdir, rename, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

import type {
  TrustedUpdateKey,
  UpdateChannel,
  UpdateDownloadSnapshot,
  VerifiedReleaseEnvelope,
} from '../types/Updates.js';
import { validateSignedReleaseEnvelope } from '../utils/UpdateValidation.js';

const readyRecordName = 'ready.json';
const maximumReadyRecordBytes = 3 * 1024 ** 2;
const defaultTimeoutMilliseconds = 10 * 60_000;
const redirectStatuses = new Set([301, 302, 303, 307, 308]);

interface UpdateDownloadRequest {
  envelope: VerifiedReleaseEnvelope;
  downloadUrl: string;
}

interface UpdateDownloadManagerOptions {
  root: string;
  channel: UpdateChannel;
  currentVersion: string;
  platform: 'linux' | 'windows';
  trustedKeys: readonly TrustedUpdateKey[];
  fetch: (url: string, init: RequestInit) => Promise<Response>;
  isTrustedUrl: (url: URL, redirected: boolean) => boolean;
  onChange?: (snapshot: UpdateDownloadSnapshot) => void;
  timeoutMilliseconds?: number;
}

interface ReadyDownloadRecord {
  schemaVersion: 1;
  sourceUrl: string;
  file: string;
  manifestBase64: string;
  signatureBase64: string;
}

export interface VerifiedUpdateDownload {
  envelope: VerifiedReleaseEnvelope;
  packagePath: string;
  sourceUrl: string;
}

export class UpdateDownloadManager {
  private snapshot: UpdateDownloadSnapshot = idleSnapshot();
  private controller?: AbortController;

  constructor(private readonly options: UpdateDownloadManagerOptions) {}

  get state(): UpdateDownloadSnapshot {
    return { ...this.snapshot };
  }

  cancel(): boolean {
    if (!this.controller) return false;
    this.controller.abort();
    return true;
  }

  async getReadyDownload(): Promise<VerifiedUpdateDownload> {
    if (this.controller) throw new Error('An update download is still active.');
    return this.readReadyDownload();
  }

  async cleanupReady(): Promise<void> {
    await mkdir(this.options.root, { recursive: true });
    const recordPath = path.join(this.options.root, readyRecordName);
    try {
      const record = parseReadyRecord(JSON.parse(await readFile(recordPath, 'utf8')));
      await rm(this.resolveOwnedFile(record.file), { force: true });
    } catch (error) {
      if (!isMissingFile(error)) console.warn('Could not read completed update download during cleanup', error);
    }
    await rm(recordPath, { force: true });
    await this.removePartialDownloads();
    this.update(idleSnapshot());
  }

  async recover(): Promise<UpdateDownloadSnapshot> {
    if (this.controller) throw new Error('An update download is already active.');
    const recordPath = path.join(this.options.root, readyRecordName);
    try {
      await mkdir(this.options.root, { recursive: true });
      await this.removePartialDownloads();
    } catch (error) {
      return this.update({
        state: 'error', receivedBytes: 0, totalBytes: 0,
        message: `Update download storage is unavailable: ${errorMessage(error)}`,
      });
    }
    let metadata;
    try {
      metadata = await stat(recordPath);
    } catch (error) {
      if (isMissingFile(error)) return this.update(idleSnapshot());
      return this.update({
        state: 'error', receivedBytes: 0, totalBytes: 0,
        message: `Saved update metadata is unavailable: ${errorMessage(error)}`,
      });
    }
    try {
      if (metadata.size <= 0 || metadata.size > maximumReadyRecordBytes) throw new Error('Saved update metadata is invalid.');
      const { envelope } = await this.readReadyDownload();
      return this.update({
        state: 'ready', version: envelope.manifest.version, channel: envelope.manifest.channel,
        receivedBytes: envelope.artifact.size, totalBytes: envelope.artifact.size,
        message: 'Verified update download is ready.',
      });
    } catch (error) {
      await rm(recordPath, { force: true }).catch(() => undefined);
      return this.update({
        state: 'error', receivedBytes: 0, totalBytes: 0,
        message: `Saved update was rejected: ${errorMessage(error)}`,
      });
    }
  }

  private async readReadyDownload(): Promise<VerifiedUpdateDownload> {
    const record = parseReadyRecord(JSON.parse(await readFile(path.join(this.options.root, readyRecordName), 'utf8')));
    const sourceUrl = new URL(record.sourceUrl);
    if (!this.options.isTrustedUrl(sourceUrl, false)) throw new Error('Saved update source URL is not trusted.');
    const manifestBytes = Buffer.from(record.manifestBase64, 'base64');
    const signatureBytes = Buffer.from(record.signatureBase64, 'base64');
    const identity = readSignedIdentity(manifestBytes);
    const envelope = validateSignedReleaseEnvelope(manifestBytes, signatureBytes, this.options.trustedKeys, {
      channel: this.options.channel,
      tag: identity.tag,
      platform: this.options.platform,
      currentVersion: this.options.currentVersion,
    });
    const packagePath = this.resolveOwnedFile(record.file);
    await verifyFile(packagePath, envelope.artifact.size, envelope.artifact.sha256);
    return { envelope, packagePath, sourceUrl: sourceUrl.href };
  }

  async start(request: UpdateDownloadRequest): Promise<UpdateDownloadSnapshot> {
    if (this.controller) throw new Error('An update download is already active.');
    const identity = readSignedIdentity(request.envelope.manifestBytes);
    const envelope = validateSignedReleaseEnvelope(
      request.envelope.manifestBytes,
      request.envelope.signatureBytes,
      this.options.trustedKeys,
      {
        channel: this.options.channel,
        tag: identity.tag,
        platform: this.options.platform,
        currentVersion: this.options.currentVersion,
      },
    );
    const { artifact } = envelope;
    const initialUrl = new URL(request.downloadUrl);
    if (!this.options.isTrustedUrl(initialUrl, false)) throw new Error('Update package URL is not trusted.');
    const controller = new AbortController();
    this.controller = controller;
    const timeout = setTimeout(() => controller.abort(new Error('Update download timed out.')),
      this.options.timeoutMilliseconds ?? defaultTimeoutMilliseconds);
    let handle;
    let received = 0;
    let temporaryPath: string | undefined;
    try {
      await mkdir(this.options.root, { recursive: true });
      const temporaryName = `.${artifact.sha256}.${randomUUID()}.download`;
      temporaryPath = this.resolveOwnedFile(temporaryName);
      const finalName = `${artifact.sha256}.${artifact.platform === 'windows' ? 'zip' : 'tar.gz'}`;
      const finalPath = this.resolveOwnedFile(finalName);
      handle = await open(temporaryPath, 'wx');
      this.update({
        state: 'downloading', version: envelope.manifest.version,
        channel: envelope.manifest.channel, receivedBytes: 0, totalBytes: artifact.size,
        message: 'Downloading verified update package…',
      });
      const response = await this.fetchFollowingTrustedRedirects(initialUrl, controller.signal);
      if (!response.ok || !response.body) throw new Error(`Update package returned HTTP ${response.status}.`);
      const declaredLength = response.headers.get('content-length');
      if (declaredLength !== null && Number(declaredLength) !== artifact.size) {
        throw new Error('Update package length does not match signed metadata.');
      }
      const hash = createHash('sha256');
      const reader = response.body.getReader();
      let lastProgressAt = 0;
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        const bytes = Buffer.from(value);
        received += bytes.length;
        if (received > artifact.size) throw new Error('Update package exceeds its signed size.');
        hash.update(bytes);
        await handle.write(bytes);
        const now = Date.now();
        if (now - lastProgressAt >= 100 || received === artifact.size) {
          lastProgressAt = now;
          this.update({ ...this.snapshot, receivedBytes: received });
        }
      }
      await handle.sync();
      await handle.close();
      handle = undefined;
      this.update({ ...this.snapshot, state: 'verifying', receivedBytes: received, message: 'Verifying update package…' });
      if (received !== artifact.size || hash.digest('hex') !== artifact.sha256) {
        throw new Error('Update package size or SHA-256 does not match its signed metadata.');
      }
      await rm(finalPath, { force: true });
      await rename(temporaryPath, finalPath);
      await this.writeReadyRecord({
        schemaVersion: 1,
        sourceUrl: initialUrl.href,
        file: finalName,
        manifestBase64: Buffer.from(envelope.manifestBytes).toString('base64'),
        signatureBase64: Buffer.from(envelope.signatureBytes).toString('base64'),
      });
      return this.update({
        state: 'ready', version: envelope.manifest.version, channel: envelope.manifest.channel,
        receivedBytes: received, totalBytes: artifact.size, message: 'Verified update download is ready.',
      });
    } catch (error) {
      await handle?.close().catch(() => undefined);
      if (temporaryPath) await rm(temporaryPath, { force: true }).catch(() => undefined);
      const cancelled = controller.signal.aborted && controller.signal.reason?.message !== 'Update download timed out.';
      return this.update({
        state: cancelled ? 'cancelled' : 'error', version: envelope.manifest.version,
        channel: envelope.manifest.channel, receivedBytes: received, totalBytes: artifact.size,
        message: cancelled ? 'Update download cancelled.' : errorMessage(controller.signal.reason ?? error),
      });
    } finally {
      clearTimeout(timeout);
      this.controller = undefined;
    }
  }

  private async fetchFollowingTrustedRedirects(initialUrl: URL, signal: AbortSignal): Promise<Response> {
    let url = initialUrl;
    for (let redirects = 0; redirects <= 5; redirects += 1) {
      const response = await this.options.fetch(url.href, { signal, redirect: 'manual' });
      if (!redirectStatuses.has(response.status)) return response;
      const location = response.headers.get('location');
      if (!location) throw new Error('Update redirect has no destination.');
      const next = new URL(location, url);
      if (!this.options.isTrustedUrl(next, true)) throw new Error('Update redirect destination is not trusted.');
      url = next;
    }
    throw new Error('Update package followed too many redirects.');
  }

  private async writeReadyRecord(record: ReadyDownloadRecord): Promise<void> {
    const temporary = path.join(this.options.root, `.${readyRecordName}.${randomUUID()}.tmp`);
    await writeFile(temporary, `${JSON.stringify(record)}\n`, { flag: 'wx', mode: 0o600 });
    try {
      await rename(temporary, path.join(this.options.root, readyRecordName));
    } catch (error) {
      await rm(temporary, { force: true }).catch(() => undefined);
      throw error;
    }
  }

  private async removePartialDownloads(): Promise<void> {
    for (const entry of await readdir(this.options.root, { withFileTypes: true })) {
      if (entry.isFile() && entry.name.startsWith('.') && entry.name.endsWith('.download')) {
        await rm(this.resolveOwnedFile(entry.name), { force: true });
      }
    }
  }

  private resolveOwnedFile(name: string): string {
    if (name !== path.basename(name) || name.length > 200) throw new Error('Update download filename is invalid.');
    const resolved = path.resolve(this.options.root, name);
    if (path.dirname(resolved) !== path.resolve(this.options.root)) throw new Error('Update download path escapes its root.');
    return resolved;
  }

  private update(snapshot: UpdateDownloadSnapshot): UpdateDownloadSnapshot {
    const totalBytes = Math.max(0, snapshot.totalBytes);
    this.snapshot = {
      ...snapshot,
      receivedBytes: Math.max(0, Math.min(snapshot.receivedBytes, totalBytes)),
      totalBytes,
    };
    this.options.onChange?.({ ...this.snapshot });
    return { ...this.snapshot };
  }
}

export function isTrustedGitHubUpdateUrl(url: URL, redirected: boolean): boolean {
  if (url.protocol !== 'https:' || url.username || url.password || url.hash) return false;
  if (!redirected) {
    return url.hostname === 'github.com'
      && url.pathname.startsWith('/Horizon-Private-Server/horizon-forge-v2/releases/download/');
  }
  return ['release-assets.githubusercontent.com', 'objects.githubusercontent.com'].includes(url.hostname);
}

async function verifyFile(filePath: string, expectedSize: number, expectedHash: string): Promise<void> {
  const metadata = await stat(filePath);
  if (!metadata.isFile() || metadata.size !== expectedSize) throw new Error('Saved update package size changed.');
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  if (hash.digest('hex') !== expectedHash) throw new Error('Saved update package hash changed.');
}

function parseReadyRecord(value: unknown): ReadyDownloadRecord {
  if (!isRecord(value)
    || value.schemaVersion !== 1
    || typeof value.sourceUrl !== 'string'
    || typeof value.file !== 'string'
    || typeof value.manifestBase64 !== 'string'
    || typeof value.signatureBase64 !== 'string'
    || Object.keys(value).some((key) => !['schemaVersion', 'sourceUrl', 'file', 'manifestBase64', 'signatureBase64'].includes(key))) {
    throw new Error('Saved update metadata is invalid.');
  }
  return value as unknown as ReadyDownloadRecord;
}

function readSignedIdentity(bytes: Uint8Array): { tag: string } {
  const value: unknown = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  if (!isRecord(value) || typeof value.tag !== 'string') throw new Error('Saved update identity is invalid.');
  return { tag: value.tag };
}

function idleSnapshot(): UpdateDownloadSnapshot {
  return { state: 'idle', receivedBytes: 0, totalBytes: 0, message: 'No update download is active.' };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isMissingFile(error: unknown): boolean {
  return isRecord(error) && error.code === 'ENOENT';
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
