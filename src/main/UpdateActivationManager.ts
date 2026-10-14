import { createHash, randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { chmod, copyFile, lstat, mkdir, readFile, readdir, rename, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

import type { InstallLayoutAssessment, UpdateActivationSnapshot } from '../types/Updates.js';
import type { VerifiedUpdateStage } from './UpdatePackageStager.js';

const noncePattern = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const handoffName = 'handoff.json';
const maximumRecordBytes = 16 * 1024;

interface UpdateActivationManagerOptions {
  root: string;
  helperPath: string;
  installLayout: InstallLayoutAssessment;
  platform: 'linux' | 'windows';
  currentVersion: string;
  arguments: readonly string[];
  onChange?: (snapshot: UpdateActivationSnapshot) => void;
  requestQuit(): void;
  spawnHelper?: typeof spawnDetachedHelper;
}

interface ActivationHandoff {
  schemaVersion: 1;
  nonce: string;
  targetRoot: string;
  stageRoot: string;
  expectedTargetPackageManifestSha256: string;
  expectedPackageManifestSha256: string;
  expectedVersion: string;
  executable: string;
  platform: 'linux' | 'windows';
  healthTimeoutSeconds: number;
}

interface StartupHealthRequest {
  nonce: string;
  healthPath: string;
}

export class UpdateActivationManager {
  private snapshot: UpdateActivationSnapshot = idleSnapshot();
  private active = false;
  private readonly startupHealth: StartupHealthRequest | undefined;

  constructor(private readonly options: UpdateActivationManagerOptions) {
    this.startupHealth = parseStartupHealthRequest(options.arguments);
  }

  get state(): UpdateActivationSnapshot {
    return { ...this.snapshot };
  }

  get hasStartupHealthRequest(): boolean {
    return this.startupHealth !== undefined;
  }

  async recover(): Promise<boolean> {
    await this.ensureSafeRoot();
    if (this.startupHealth) return false;
    const entries = await readdir(this.options.root, { withFileTypes: true });
    const pending: { name: string; modified: number }[] = [];
    const rolledBack: { name: string; modified: number }[] = [];
    for (const entry of entries) {
      if (!entry.isDirectory() || !noncePattern.test(entry.name)) continue;
      const directory = path.join(this.options.root, entry.name);
      try {
        const checkpoint = await readCheckpoint(path.join(directory, 'state.json'));
        if (checkpoint === 'healthy') continue;
        if (checkpoint === 'rolled-back') {
          try {
            await stat(path.join(directory, 'reported'));
          } catch (error) {
            if (!isRecord(error) || error.code !== 'ENOENT') throw error;
            rolledBack.push({ name: entry.name, modified: (await stat(path.join(directory, 'state.json'))).mtimeMs });
          }
          continue;
        }
        if (checkpoint === 'recovery-required') {
          this.fail('An interrupted update requires manual recovery. See its activation record for details.');
          continue;
        }
        const modified = (await stat(path.join(directory, handoffName))).mtimeMs;
        pending.push({ name: entry.name, modified });
      } catch (error) {
        if (isRecord(error) && error.code === 'ENOENT') {
          await rm(directory, { recursive: true, force: true });
        } else {
          this.fail(`Saved update activation was rejected: ${errorMessage(error)}`);
        }
      }
    }
    pending.sort((left, right) => right.modified - left.modified);
    if (!pending[0]) {
      rolledBack.sort((left, right) => right.modified - left.modified);
      if (rolledBack[0]) {
        const directory = path.join(this.options.root, rolledBack[0].name);
        this.update({
          state: 'rolled-back',
          message: 'The update did not start successfully. Forge restored and launched the previous version.',
        });
        await writeFile(path.join(directory, 'reported'), '\n', { flag: 'wx', mode: 0o600 });
      }
      return false;
    }
    try {
      await this.launchExisting(pending[0].name);
      return true;
    } catch (error) {
      this.fail(`Interrupted update could not resume: ${errorMessage(error)}`);
      return false;
    }
  }

  async start(stage: VerifiedUpdateStage): Promise<UpdateActivationSnapshot> {
    if (this.active || this.snapshot.state === 'waiting-for-exit') {
      throw new Error('Update activation is already active.');
    }
    if (this.options.installLayout.kind !== 'managed' || !this.options.installLayout.manifest) {
      return this.fail('This installation cannot be replaced automatically.');
    }
    this.active = true;
    const nonce = randomUUID();
    const directory = path.join(this.options.root, nonce);
    try {
      await this.ensureSafeRoot();
      if (path.resolve(stage.root) === path.resolve(this.options.installLayout.root)) {
        throw new Error('The staged and active installations must be separate.');
      }
      this.update({
        state: 'preparing', version: stage.manifest.version, channel: stage.download.envelope.manifest.channel,
        message: 'Preparing the external update handoff…',
      });
      await mkdir(directory, { recursive: false, mode: 0o700 });
      const helperName = this.options.platform === 'windows' ? 'Forge.Updater.exe' : 'Forge.Updater';
      const helperPath = path.join(directory, helperName);
      await copyRegularFile(this.options.helperPath, helperPath);
      const handoff: ActivationHandoff = {
        schemaVersion: 1,
        nonce,
        targetRoot: path.resolve(this.options.installLayout.root),
        stageRoot: path.resolve(stage.root),
        expectedTargetPackageManifestSha256: stage.targetPackageManifestSha256,
        expectedPackageManifestSha256: stage.download.envelope.artifact.packageManifestSha256,
        expectedVersion: stage.manifest.version,
        executable: stage.manifest.executable,
        platform: this.options.platform,
        healthTimeoutSeconds: 90,
      };
      const bytes = Buffer.from(`${JSON.stringify(handoff)}\n`, 'utf8');
      const handoffPath = path.join(directory, handoffName);
      await writeFile(handoffPath, bytes, { flag: 'wx', mode: 0o600 });
      const digest = createHash('sha256').update(bytes).digest('hex');
      await (this.options.spawnHelper ?? spawnDetachedHelper)(helperPath, handoffPath, nonce, digest, process.pid);
      const result = this.update({
        state: 'waiting-for-exit', version: stage.manifest.version,
        channel: stage.download.envelope.manifest.channel,
        message: 'Forge will close, install the verified update, and restart.',
      });
      this.options.requestQuit();
      return result;
    } catch (error) {
      await rm(directory, { recursive: true, force: true }).catch(() => undefined);
      return this.fail(errorMessage(error));
    } finally {
      this.active = false;
    }
  }

  async markStartupHealthy(): Promise<boolean> {
    if (!this.startupHealth) return false;
    await this.ensureSafeRoot();
    const { nonce, healthPath } = this.startupHealth;
    const directory = path.join(this.options.root, nonce);
    if (!samePath(healthPath, path.join(directory, 'health.json'), this.options.platform)) {
      throw new Error('Update startup health path is invalid.');
    }
    const handoff = await readHandoff(path.join(directory, handoffName));
    const checkpoint = await readCheckpoint(path.join(directory, 'state.json'));
    if (handoff.nonce !== nonce
      || !samePath(handoff.targetRoot, this.options.installLayout.root, this.options.platform)
      || handoff.expectedVersion !== this.options.currentVersion
      || checkpoint !== 'launched') {
      throw new Error('Update startup health request does not match this installation.');
    }
    const temporary = path.join(directory, `.health.${randomUUID()}.tmp`);
    await writeFile(temporary, `${JSON.stringify({
      schemaVersion: 1, nonce, version: this.options.currentVersion, processId: process.pid,
      healthyUnixMilliseconds: Date.now(),
    })}\n`, { flag: 'wx', mode: 0o600 });
    await rm(healthPath, { force: true });
    await rename(temporary, healthPath);
    return true;
  }

  private async launchExisting(nonce: string): Promise<void> {
    const directory = path.join(this.options.root, nonce);
    const handoffPath = path.join(directory, handoffName);
    const bytes = await readBounded(handoffPath);
    const handoff = parseHandoff(JSON.parse(bytes.toString('utf8')));
    if (handoff.nonce !== nonce
      || !samePath(handoff.targetRoot, this.options.installLayout.root, this.options.platform)) {
      throw new Error('Pending update handoff does not match this installation.');
    }
    const helperPath = path.join(directory, this.options.platform === 'windows' ? 'Forge.Updater.exe' : 'Forge.Updater');
    const digest = createHash('sha256').update(bytes).digest('hex');
    await (this.options.spawnHelper ?? spawnDetachedHelper)(helperPath, handoffPath, nonce, digest, process.pid);
    this.update({ state: 'waiting-for-exit', version: handoff.expectedVersion,
      message: 'Resuming the interrupted update after Forge closes.' });
    this.options.requestQuit();
  }

  private async ensureSafeRoot(): Promise<void> {
    await mkdir(this.options.root, { recursive: true, mode: 0o700 });
    const metadata = await lstat(this.options.root);
    if (!metadata.isDirectory() || metadata.isSymbolicLink()) throw new Error('Update activation storage is invalid.');
  }

  private fail(message: string): UpdateActivationSnapshot {
    return this.update({ state: 'error', message });
  }

  private update(snapshot: UpdateActivationSnapshot): UpdateActivationSnapshot {
    this.snapshot = snapshot;
    this.options.onChange?.({ ...snapshot });
    return { ...snapshot };
  }
}

async function spawnDetachedHelper(
  helperPath: string, handoffPath: string, nonce: string, digest: string, sourcePid: number,
): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn(helperPath, [handoffPath, nonce, digest, String(sourcePid)], {
      detached: true, stdio: 'ignore', windowsHide: true,
    });
    child.once('error', reject);
    child.once('spawn', () => { child.unref(); resolve(); });
  });
}

async function copyRegularFile(source: string, destination: string): Promise<void> {
  const metadata = await lstat(source);
  if (!metadata.isFile() || metadata.isSymbolicLink()) throw new Error('Packaged update helper is missing or invalid.');
  await copyFile(source, destination);
  if (process.platform !== 'win32') await chmod(destination, 0o700);
}

async function readHandoff(filePath: string): Promise<ActivationHandoff> {
  return parseHandoff(JSON.parse((await readBounded(filePath)).toString('utf8')));
}

async function readBounded(filePath: string): Promise<Buffer> {
  const metadata = await stat(filePath);
  if (!metadata.isFile() || metadata.size <= 0 || metadata.size > maximumRecordBytes) {
    throw new Error('Update activation record is invalid.');
  }
  return readFile(filePath);
}

function parseHandoff(value: unknown): ActivationHandoff {
  if (!isRecord(value) || value.schemaVersion !== 1 || typeof value.nonce !== 'string'
    || !noncePattern.test(value.nonce) || typeof value.targetRoot !== 'string' || !path.isAbsolute(value.targetRoot)
    || typeof value.stageRoot !== 'string' || !path.isAbsolute(value.stageRoot)
    || typeof value.expectedTargetPackageManifestSha256 !== 'string'
    || typeof value.expectedPackageManifestSha256 !== 'string'
    || !/^[0-9a-f]{64}$/.test(value.expectedTargetPackageManifestSha256)
    || !/^[0-9a-f]{64}$/.test(value.expectedPackageManifestSha256)
    || typeof value.expectedVersion !== 'string' || typeof value.executable !== 'string'
    || (value.platform !== 'linux' && value.platform !== 'windows')
    || !Number.isInteger(value.healthTimeoutSeconds) || Number(value.healthTimeoutSeconds) < 10
    || Object.keys(value).some((key) => ![
      'schemaVersion', 'nonce', 'targetRoot', 'stageRoot', 'expectedTargetPackageManifestSha256',
      'expectedPackageManifestSha256', 'expectedVersion', 'executable', 'platform', 'healthTimeoutSeconds',
    ].includes(key))) throw new Error('Update activation record is invalid.');
  return value as unknown as ActivationHandoff;
}

async function readCheckpoint(filePath: string): Promise<string | undefined> {
  try {
    const value: unknown = JSON.parse((await readBounded(filePath)).toString('utf8'));
    return isRecord(value) && typeof value.checkpoint === 'string' ? value.checkpoint : undefined;
  } catch (error) {
    if (isRecord(error) && error.code === 'ENOENT') return undefined;
    throw error;
  }
}

function parseStartupHealthRequest(arguments_: readonly string[]): StartupHealthRequest | undefined {
  const healthIndex = arguments_.lastIndexOf('--forge-update-health');
  const nonceIndex = arguments_.lastIndexOf('--forge-update-nonce');
  if (healthIndex < 0 && nonceIndex < 0) return undefined;
  const healthPath = arguments_[healthIndex + 1];
  const nonce = arguments_[nonceIndex + 1];
  if (!healthPath || !path.isAbsolute(healthPath) || !nonce || !noncePattern.test(nonce)) return undefined;
  return { healthPath: path.resolve(healthPath), nonce };
}

function samePath(left: string, right: string, platform: 'linux' | 'windows'): boolean {
  const normalizedLeft = path.resolve(left);
  const normalizedRight = path.resolve(right);
  return platform === 'windows'
    ? normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
    : normalizedLeft === normalizedRight;
}

function idleSnapshot(): UpdateActivationSnapshot {
  return { state: 'idle', message: 'No update activation is pending.' };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
