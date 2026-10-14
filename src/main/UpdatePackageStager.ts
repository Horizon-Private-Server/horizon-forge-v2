import { randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import {
  lstat,
  mkdir,
  readFile,
  readdir,
  rename,
  rm,
  stat,
  writeFile,
} from 'node:fs/promises';
import path from 'node:path';

import type { ForgePackageManifest, UpdateStageSnapshot } from '../types/Updates.js';
import { inspectUpdateArchive } from './UpdateArchiveInspector.js';
import type { VerifiedUpdateDownload } from './UpdateDownloadManager.js';
import { fingerprintInstalledPackage, validateStagedPackage } from './UpdatePackageValidation.js';

const readyRecordName = 'ready.json';
const maximumRecordBytes = 4 * 1024;
const extractionTimeoutMilliseconds = 10 * 60_000;
const noncePattern = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

interface UpdatePackageStagerOptions {
  root: string;
  targetRoot: string;
  platform: 'linux' | 'windows';
  onChange?: (snapshot: UpdateStageSnapshot) => void;
  extract?: (archivePath: string, destination: string, signal: AbortSignal) => Promise<void>;
}

interface ReadyStageRecord {
  schemaVersion: 1;
  state: 'complete';
  directory: string;
  sourceUrl: string;
  version: string;
  channel: string;
  tag: string;
  commit: string;
  platform: string;
  architecture: string;
  signatureKeyId: string;
  packageSha256: string;
  packageManifestSha256: string;
  targetPackageManifestSha256: string;
}

export interface VerifiedUpdateStage {
  root: string;
  manifest: ForgePackageManifest;
  download: VerifiedUpdateDownload;
  targetPackageManifestSha256: string;
}

export class UpdatePackageStager {
  private snapshot: UpdateStageSnapshot = idleSnapshot();
  private controller?: AbortController;

  constructor(private readonly options: UpdatePackageStagerOptions) {}

  get state(): UpdateStageSnapshot {
    return { ...this.snapshot };
  }

  cancel(): boolean {
    if (!this.controller) return false;
    this.controller.abort(new Error('Update staging cancelled.'));
    return true;
  }

  async recover(download?: VerifiedUpdateDownload): Promise<UpdateStageSnapshot> {
    if (this.controller) throw new Error('Update staging is already active.');
    try {
      await this.ensureSafeRoot();
    } catch (error) {
      return this.fail(`Update staging storage is unavailable: ${errorMessage(error)}`);
    }
    const recordPath = path.join(this.options.root, readyRecordName);
    let record: ReadyStageRecord;
    try {
      const metadata = await stat(recordPath);
      if (metadata.size <= 0 || metadata.size > maximumRecordBytes) throw new Error('Saved update stage metadata is invalid.');
      record = parseStageRecord(JSON.parse(await readFile(recordPath, 'utf8')));
    } catch (error) {
      await this.cleanupDebris();
      if (isMissingFile(error)) return this.update(idleSnapshot());
      await rm(recordPath, { force: true }).catch(() => undefined);
      return this.fail(`Saved update stage was rejected: ${errorMessage(error)}`);
    }
    if (!download) {
      await rm(recordPath, { force: true }).catch(() => undefined);
      await this.cleanupDebris();
      return this.fail('Saved update stage has no verified source package.');
    }
    try {
      assertRecordMatchesDownload(record, download);
      const stage = await this.validateStage(record.directory, download, record.targetPackageManifestSha256);
      await this.cleanupDebris(record.directory);
      return this.update({
        state: 'ready', version: stage.manifest.version, channel: download.envelope.manifest.channel,
        completedFiles: stage.manifest.fileCount, totalFiles: stage.manifest.fileCount,
        message: 'Verified update stage is ready to install.',
      });
    } catch (error) {
      await rm(recordPath, { force: true }).catch(() => undefined);
      await rm(this.resolveStageDirectory(record.directory), { recursive: true, force: true }).catch(() => undefined);
      await this.cleanupDebris();
      return this.fail(`Saved update stage was rejected: ${errorMessage(error)}`);
    }
  }

  async stage(download: VerifiedUpdateDownload): Promise<UpdateStageSnapshot> {
    if (this.controller) throw new Error('Update staging is already active.');
    const controller = new AbortController();
    this.controller = controller;
    const nonce = randomUUID();
    const partialName = `.partial-${nonce}`;
    const partialDirectory = path.join(this.options.root, partialName);
    const finalDirectory = this.resolveStageDirectory(nonce);
    let committed = false;
    try {
      await this.ensureSafeRoot();
      if (download.envelope.artifact.platform !== this.options.platform) {
        throw new Error('Update package platform does not match this installation.');
      }
      this.update({
        state: 'inspecting', version: download.envelope.manifest.version,
        channel: download.envelope.manifest.channel, completedFiles: 0,
        totalFiles: download.envelope.artifact.fileCount,
        message: 'Inspecting update archive…',
      });
      await inspectUpdateArchive(download.packagePath, download.envelope.artifact, controller.signal);
      throwIfAborted(controller.signal);
      await mkdir(partialDirectory, { recursive: false, mode: 0o700 });
      this.update({ ...this.snapshot, state: 'extracting', message: 'Extracting update into a private stage…' });
      await (this.options.extract ?? extractWithTar)(download.packagePath, partialDirectory, controller.signal);
      throwIfAborted(controller.signal);
      this.update({ ...this.snapshot, state: 'validating', message: 'Validating every staged application file…' });
      await assertSingleArchiveRoot(partialDirectory, download.envelope.artifact.topLevelDirectory);
      const root = path.join(partialDirectory, download.envelope.artifact.topLevelDirectory);
      const manifest = await validateStagedPackage(root, download, this.options.platform, controller.signal, (completed) => {
        this.update({ ...this.snapshot, completedFiles: completed });
      });
      const targetPackageManifestSha256 = await fingerprintInstalledPackage(
        this.options.targetRoot, this.options.platform, controller.signal,
      );
      await rename(partialDirectory, finalDirectory);
      const previous = await this.readRecord().catch(() => undefined);
      await this.writeRecord({
        schemaVersion: 1,
        state: 'complete',
        directory: nonce,
        sourceUrl: download.sourceUrl,
        version: manifest.version,
        channel: download.envelope.manifest.channel,
        tag: download.envelope.manifest.tag,
        commit: download.envelope.manifest.commit,
        platform: download.envelope.artifact.platform,
        architecture: download.envelope.artifact.architecture,
        signatureKeyId: download.envelope.manifest.signature.keyId,
        packageSha256: download.envelope.artifact.sha256,
        packageManifestSha256: download.envelope.artifact.packageManifestSha256,
        targetPackageManifestSha256,
      });
      committed = true;
      if (previous && previous.directory !== nonce) {
        await rm(this.resolveStageDirectory(previous.directory), { recursive: true, force: true }).catch(() => undefined);
      }
      await this.cleanupDebris(nonce);
      return this.update({
        state: 'ready', version: manifest.version, channel: download.envelope.manifest.channel,
        completedFiles: manifest.fileCount, totalFiles: manifest.fileCount,
        message: 'Verified update stage is ready to install.',
      });
    } catch (error) {
      if (!committed) {
        await rm(partialDirectory, { recursive: true, force: true }).catch(() => undefined);
        await rm(finalDirectory, { recursive: true, force: true }).catch(() => undefined);
      }
      const cancelled = controller.signal.aborted;
      return this.update({
        state: cancelled ? 'cancelled' : 'error', version: download.envelope.manifest.version,
        channel: download.envelope.manifest.channel, completedFiles: this.snapshot.completedFiles,
        totalFiles: download.envelope.artifact.fileCount,
        message: cancelled ? 'Update staging cancelled.' : errorMessage(error),
      });
    } finally {
      this.controller = undefined;
    }
  }

  async getReadyStage(download: VerifiedUpdateDownload): Promise<VerifiedUpdateStage> {
    if (this.controller) throw new Error('Update staging is still active.');
    const record = await this.readRecord();
    assertRecordMatchesDownload(record, download);
    return this.validateStage(record.directory, download, record.targetPackageManifestSha256);
  }

  async cleanupReady(): Promise<void> {
    await this.ensureSafeRoot();
    try {
      const record = await this.readRecord();
      await rm(this.resolveStageDirectory(record.directory), { recursive: true, force: true });
    } catch (error) {
      if (!isMissingFile(error)) console.warn('Could not read completed update stage during cleanup', error);
    }
    await rm(path.join(this.options.root, readyRecordName), { force: true });
    await this.cleanupDebris();
    this.update(idleSnapshot());
  }

  private async validateStage(
    directory: string,
    download: VerifiedUpdateDownload,
    targetPackageManifestSha256: string,
  ): Promise<VerifiedUpdateStage> {
    const root = path.join(this.resolveStageDirectory(directory), download.envelope.artifact.topLevelDirectory);
    const manifest = await validateStagedPackage(root, download, this.options.platform);
    if (await fingerprintInstalledPackage(this.options.targetRoot, this.options.platform)
      !== targetPackageManifestSha256) {
      throw new Error('Active installation changed after this update was staged.');
    }
    return { root, manifest, download, targetPackageManifestSha256 };
  }

  private async ensureSafeRoot(): Promise<void> {
    await mkdir(this.options.root, { recursive: true });
    const metadata = await lstat(this.options.root);
    if (!metadata.isDirectory() || metadata.isSymbolicLink()) throw new Error('Update staging root is indirect or invalid.');
  }

  private async readRecord(): Promise<ReadyStageRecord> {
    const recordPath = path.join(this.options.root, readyRecordName);
    const metadata = await stat(recordPath);
    if (metadata.size <= 0 || metadata.size > maximumRecordBytes) throw new Error('Saved update stage metadata is invalid.');
    return parseStageRecord(JSON.parse(await readFile(recordPath, 'utf8')));
  }

  private async writeRecord(record: ReadyStageRecord): Promise<void> {
    const temporary = path.join(this.options.root, `.${readyRecordName}.${randomUUID()}.tmp`);
    await writeFile(temporary, `${JSON.stringify(record)}\n`, { flag: 'wx', mode: 0o600 });
    try {
      await rename(temporary, path.join(this.options.root, readyRecordName));
    } catch (error) {
      await rm(temporary, { force: true }).catch(() => undefined);
      throw error;
    }
  }

  private async cleanupDebris(keep?: string): Promise<void> {
    for (const entry of await readdir(this.options.root, { withFileTypes: true })) {
      const removable = entry.isDirectory() && (entry.name.startsWith('.partial-') || noncePattern.test(entry.name));
      if (removable && entry.name !== keep) {
        await rm(path.join(this.options.root, entry.name), { recursive: true, force: true });
      } else if (entry.isFile() && entry.name.startsWith(`.${readyRecordName}.`) && entry.name.endsWith('.tmp')) {
        await rm(path.join(this.options.root, entry.name), { force: true });
      }
    }
  }

  private resolveStageDirectory(name: string): string {
    if (!noncePattern.test(name)) throw new Error('Update stage identifier is invalid.');
    return path.join(this.options.root, name);
  }

  private fail(message: string): UpdateStageSnapshot {
    return this.update({ state: 'error', completedFiles: 0, totalFiles: 0, message });
  }

  private update(snapshot: UpdateStageSnapshot): UpdateStageSnapshot {
    const totalFiles = Math.max(0, snapshot.totalFiles);
    this.snapshot = {
      ...snapshot,
      completedFiles: Math.max(0, Math.min(snapshot.completedFiles, totalFiles)),
      totalFiles,
    };
    this.options.onChange?.({ ...this.snapshot });
    return { ...this.snapshot };
  }
}

async function assertSingleArchiveRoot(directory: string, expectedName: string): Promise<void> {
  const entries = await readdir(directory, { withFileTypes: true });
  if (entries.length !== 1 || entries[0].name !== expectedName || !entries[0].isDirectory()) {
    throw new Error('Extracted update has an unexpected top-level layout.');
  }
}

function assertRecordMatchesDownload(record: ReadyStageRecord, download: VerifiedUpdateDownload): void {
  if (record.version !== download.envelope.manifest.version
    || record.channel !== download.envelope.manifest.channel
    || record.tag !== download.envelope.manifest.tag
    || record.commit !== download.envelope.manifest.commit
    || record.platform !== download.envelope.artifact.platform
    || record.architecture !== download.envelope.artifact.architecture
    || record.signatureKeyId !== download.envelope.manifest.signature.keyId
    || record.sourceUrl !== download.sourceUrl
    || record.packageSha256 !== download.envelope.artifact.sha256
    || record.packageManifestSha256 !== download.envelope.artifact.packageManifestSha256) {
    throw new Error('Saved update stage does not match the verified source package.');
  }
}

async function extractWithTar(archivePath: string, destination: string, signal: AbortSignal): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn('tar', ['-xf', archivePath, '-C', destination], {
      windowsHide: true,
      stdio: ['ignore', 'ignore', 'pipe'],
    });
    let diagnostics = '';
    const timeout = setTimeout(() => child.kill(), extractionTimeoutMilliseconds);
    const abort = () => child.kill();
    signal.addEventListener('abort', abort, { once: true });
    child.stderr?.on('data', (chunk: Buffer) => {
      if (diagnostics.length < 64 * 1024) diagnostics += chunk.toString('utf8').slice(0, 64 * 1024 - diagnostics.length);
    });
    child.once('error', reject);
    child.once('close', (code) => {
      clearTimeout(timeout);
      signal.removeEventListener('abort', abort);
      if (signal.aborted) reject(signal.reason ?? new Error('Update staging cancelled.'));
      else if (code === 0) resolve();
      else reject(new Error(`Update package extraction failed${diagnostics.trim() ? `: ${diagnostics.trim()}` : '.'}`));
    });
  });
}

function parseStageRecord(value: unknown): ReadyStageRecord {
  if (!isRecord(value)
    || value.schemaVersion !== 1
    || value.state !== 'complete'
    || typeof value.directory !== 'string' || !noncePattern.test(value.directory)
    || typeof value.sourceUrl !== 'string'
    || typeof value.version !== 'string'
    || typeof value.channel !== 'string'
    || typeof value.tag !== 'string'
    || typeof value.commit !== 'string'
    || typeof value.platform !== 'string'
    || typeof value.architecture !== 'string'
    || typeof value.signatureKeyId !== 'string'
    || typeof value.packageSha256 !== 'string' || !/^[0-9a-f]{64}$/.test(value.packageSha256)
    || typeof value.packageManifestSha256 !== 'string' || !/^[0-9a-f]{64}$/.test(value.packageManifestSha256)
    || typeof value.targetPackageManifestSha256 !== 'string' || !/^[0-9a-f]{64}$/.test(value.targetPackageManifestSha256)
    || Object.keys(value).some((key) => ![
      'schemaVersion', 'state', 'directory', 'sourceUrl', 'version', 'channel', 'tag', 'commit', 'platform',
      'architecture', 'signatureKeyId', 'packageSha256', 'packageManifestSha256',
      'targetPackageManifestSha256',
    ].includes(key))) {
    throw new Error('Saved update stage metadata is invalid.');
  }
  return value as unknown as ReadyStageRecord;
}

function idleSnapshot(): UpdateStageSnapshot {
  return { state: 'idle', completedFiles: 0, totalFiles: 0, message: 'No update stage is ready.' };
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw signal.reason ?? new Error('Update staging cancelled.');
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
