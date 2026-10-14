import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { app, dialog, shell } from 'electron';
import type { BrowserWindow } from 'electron';

import type {
  AvailableUpdate,
  InstallLayoutAssessment,
  TrustedUpdateKey,
  UpdateChannel,
  UpdateCheckResult,
  UpdateActivationSnapshot,
  UpdateDownloadSnapshot,
  UpdateLifecycleAction,
  UpdateLifecycleSnapshot,
  UpdateStageSnapshot,
} from '../types/Updates.js';
import { formatBytes } from '../utils/Format.js';
import { detectInstallLayout } from './InstallLayout.js';
import type { NotificationCenter } from './NotificationCenter.js';
import { UpdateActivationManager } from './UpdateActivationManager.js';
import { isTrustedGitHubUpdateUrl, UpdateDownloadManager } from './UpdateDownloadManager.js';
import { UpdateLifecycleCoordinator } from './UpdateLifecycleCoordinator.js';
import { UpdatePackageStager } from './UpdatePackageStager.js';
import {
  downloadVerifiedPackage,
  findGitHubUpdate,
  parseTrustedUpdateKeys,
  trustedReleaseSource,
} from './UpdateReleaseSource.js';
import type { ResolvedUpdate } from './UpdateReleaseSource.js';
import { showUpdateRestartPrompt } from './UpdateRestartPrompt.js';

interface UpdateServiceOptions {
  version: string;
  channel?: UpdateChannel;
  installLayout: InstallLayoutAssessment;
  trustedKeys: readonly TrustedUpdateKey[];
  notifications: NotificationCenter;
  getMainWindow(): BrowserWindow | undefined;
  isProjectDirty(): boolean;
  saveProject(): Promise<void>;
  requestQuit(): void;
}

export class UpdateService {
  private checking = false;
  private readonly promptedTags = new Set<string>();
  private downloadManager?: UpdateDownloadManager;
  private stageManager?: UpdatePackageStager;
  private activationManager?: UpdateActivationManager;
  private readonly lifecycle: UpdateLifecycleCoordinator;
  private currentUpdate?: ResolvedUpdate;
  private retryPhase?: 'check' | 'download' | 'stage' | 'install';
  private installing = false;
  private handingOff = false;
  private startupRecovery?: Promise<void>;

  private constructor(private readonly options: UpdateServiceOptions) {
    this.lifecycle = new UpdateLifecycleCoordinator({
      notifications: options.notifications,
      getMainWindow: options.getMainWindow,
      runAction: (action) => this.runUpdateAction(action),
    });
  }

  get installedChannel(): UpdateChannel | undefined {
    return this.options.channel;
  }

  get installedLayout(): InstallLayoutAssessment {
    return this.options.installLayout;
  }

  static async create(
    options: Omit<UpdateServiceOptions, 'channel' | 'installLayout' | 'trustedKeys'>,
    packageJsonPath: string,
  ): Promise<UpdateService> {
    let channel: UpdateChannel | undefined;
    let trustedKeys: TrustedUpdateKey[] = [];
    let packageValue: unknown;
    try {
      packageValue = JSON.parse(await readFile(packageJsonPath, 'utf8'));
    } catch {
      // Development builds intentionally have no update channel.
    }
    if (isRecord(packageValue) && isRecord(packageValue.forge)) {
      if (packageValue.forge.channel === 'stable' || packageValue.forge.channel === 'nightly') {
        channel = packageValue.forge.channel;
      }
      trustedKeys = parseTrustedUpdateKeys(packageValue.forge.updateKeys);
    }
    const installLayout = await detectInstallLayout({
      isPackaged: app.isPackaged,
      execPath: process.execPath,
      stagingRoot: path.join(app.getPath('userData'), 'updates', 'staging'),
      platform: process.platform,
    });
    const service = new UpdateService({ ...options, channel, installLayout, trustedKeys });
    if (channel && (process.platform === 'win32' || process.platform === 'linux') && process.arch === 'x64') {
      const platform = process.platform === 'win32' ? 'windows' : 'linux';
      service.stageManager = new UpdatePackageStager({
        root: path.join(app.getPath('userData'), 'updates', 'staging'),
        targetRoot: installLayout.root,
        platform,
        onChange: (snapshot) => service.publishStageState(snapshot),
      });
      service.downloadManager = new UpdateDownloadManager({
        root: path.join(app.getPath('userData'), 'updates', 'downloads'),
        channel,
        currentVersion: options.version,
        platform,
        trustedKeys,
        fetch,
        isTrustedUrl: isTrustedGitHubUpdateUrl,
        onChange: (snapshot) => service.publishDownloadState(snapshot),
      });
      if (installLayout.kind === 'managed' && app.isPackaged) {
        service.activationManager = new UpdateActivationManager({
          root: path.join(app.getPath('userData'), 'updates', 'activation'),
          helperPath: path.join(process.resourcesPath, 'updater',
            platform === 'windows' ? 'Forge.Updater.exe' : 'Forge.Updater'),
          installLayout,
          platform,
          currentVersion: options.version,
          arguments: process.argv,
          onChange: (snapshot) => service.publishActivationState(snapshot),
          requestQuit: options.requestQuit,
        });
        if (await service.activationManager.recover()) return service;
        if (service.activationManager.hasStartupHealthRequest) return service;
      }
    }
    return service;
  }

  startBackgroundRecovery(): void {
    if (this.startupRecovery) return;
    this.startupRecovery = this.recoverDownloadedUpdate().catch((error) => {
      console.error('Forge update recovery failed', error);
    });
  }

  getLifecycleState(): UpdateLifecycleSnapshot {
    return this.lifecycle.state;
  }

  async markStartupHealthy(): Promise<void> {
    if (await this.activationManager?.markStartupHealthy()) {
      await Promise.all([
        this.downloadManager?.cleanupReady(),
        this.stageManager?.cleanupReady(),
      ]);
      this.lifecycle.publish({
        state: 'updated', title: `Horizon Forge ${this.options.version} updated successfully`,
        message: 'Startup checks passed. The previous version remains available for one rollback.',
        severity: 'info', version: this.options.version, channel: this.options.channel,
        actions: [{ action: 'dismiss', label: 'Dismiss' }],
      });
    }
  }

  async check(manual: boolean, channel: UpdateChannel): Promise<UpdateCheckResult> {
    if (this.checking) return {
      status: 'busy', title: 'Update check already running', message: 'Forge is already checking this release channel.',
    };
    if (!this.options.channel) {
      return {
        status: 'unavailable',
        title: 'Update checks are unavailable',
        message: 'Update checks are not available in local development or standalone builds.',
      };
    }

    this.checking = true;
    const showChecking = manual && this.lifecycle.state.state === 'idle';
    if (showChecking) this.lifecycle.publish({
      state: 'checking', title: 'Checking for Forge updates',
      message: `Checking the trusted ${channel} release channel.`, severity: 'info', channel, actions: [],
    }, false);
    try {
      this.startBackgroundRecovery();
      await this.startupRecovery;
      const update = await findGitHubUpdate({
        channel,
        installedChannel: this.options.channel,
        currentVersion: this.options.version,
        trustedKeys: this.options.trustedKeys,
        platform: process.platform,
        architecture: process.arch,
      });
      if (!update) {
        if (showChecking) this.lifecycle.publish({
          state: 'current', title: 'Horizon Forge is up to date',
          message: `Channel: ${channel}\nVersion: ${this.options.version}\nTrusted source: ${trustedReleaseSource}`,
          severity: 'info', version: this.options.version, channel, source: trustedReleaseSource,
          actions: [{ action: 'dismiss', label: 'Dismiss' }],
        });
        return {
          status: 'current',
          title: 'Horizon Forge is up to date',
          message: `Channel: ${channel}\nVersion: ${this.options.version}\nTrusted source: ${trustedReleaseSource}`,
        };
      }
      if (!manual && this.promptedTags.has(update.tag)) return {
        status: 'available', title: 'Update available', message: `Horizon Forge ${update.version} is available.`,
      };
      this.promptedTags.add(update.tag);
      this.publishUpdate(update);
      return {
        status: 'available',
        title: 'Update available',
        message: `Horizon Forge ${update.version} is available on the ${update.channel} channel. Open Notifications for details.`,
      };
    } catch (error) {
      if (!manual) {
        console.warn('Forge update check failed', error);
      } else {
        this.retryPhase = 'check';
        this.lifecycle.publish({
          state: 'failed', title: 'Forge update check failed', message: errorMessage(error), severity: 'error',
          channel, actions: [{ action: 'retry', label: 'Retry' }, { action: 'defer', label: 'Dismiss for now' }],
        });
      }
      return {
        status: 'error',
        title: 'Forge could not securely check for updates',
        message: error instanceof Error ? error.message : String(error),
      };
    } finally {
      this.checking = false;
    }
  }

  private async recoverDownloadedUpdate(): Promise<void> {
    if (!this.downloadManager || !this.stageManager) return;
    const downloadState = await this.downloadManager.recover();
    const download = downloadState.state === 'ready'
      ? await this.downloadManager.getReadyDownload().catch(() => undefined)
      : undefined;
    const stageState = await this.stageManager.recover(download);
    if (download && stageState.state === 'idle') await this.prepareDownloadedUpdate();
  }

  private publishUpdate(update: ResolvedUpdate): void {
    const windows = process.platform === 'win32';
    const managedDownload = Boolean(update.envelope && this.downloadManager
      && this.options.installLayout.kind === 'managed' && update.channel === this.options.channel);
    this.currentUpdate = update;
    this.retryPhase = undefined;
    const dirtyWarning = this.options.isProjectDirty()
      ? managedDownload
        ? '\n\nUnsaved project changes are open. Downloading is safe; Forge will ask whether to save or discard before restart.'
        : '\n\nUnsaved project changes are open. Save them before closing Forge to complete the manual update.'
      : '';
    const guidance = managedDownload ? ''
      : `\nAutomatic installation is unavailable: ${update.channel !== this.options.channel
        ? 'channel switches require manual installation.' : this.options.installLayout.reason}`;
    this.lifecycle.publish({
      state: 'available', title: 'Forge update available',
      message: `Horizon Forge ${update.version}\nTrusted source: ${trustedReleaseSource}\nChannel: ${update.channel}`
        + `\nDownload: ${formatBytes(update.artifact.size)}${guidance}${dirtyWarning}\n\n${update.notes.slice(0, 3000)}`,
      severity: 'info', version: update.version, channel: update.channel,
      source: trustedReleaseSource, completed: 0, total: update.artifact.size, progressUnit: 'bytes',
      actions: [
        { action: managedDownload ? 'download' : 'open',
          label: managedDownload ? 'Download update' : windows ? 'Download verified package' : 'Open GitHub release' },
        { action: 'defer', label: 'Defer' },
      ],
    });
  }

  private async handoff(update: ResolvedUpdate): Promise<boolean> {
    if (update.envelope && this.downloadManager && this.options.installLayout.kind === 'managed'
      && update.channel === this.options.channel) {
      this.retryPhase = 'download';
      const snapshot = await this.downloadManager.start({ envelope: update.envelope, downloadUrl: update.downloadUrl });
      if (snapshot.state === 'ready') await this.prepareDownloadedUpdate();
      return false;
    }
    const windows = process.platform === 'win32';
    if (!windows) {
      await shell.openExternal(update.releaseUrl);
      return true;
    }
    return this.downloadWindowsPackage(update);
  }

  private publishDownloadState(snapshot: UpdateDownloadSnapshot): void {
    if (snapshot.state === 'idle') return;
    const percentage = snapshot.totalBytes > 0
      ? Math.floor((snapshot.receivedBytes / snapshot.totalBytes) * 100)
      : 0;
    const message = snapshot.state === 'downloading'
      ? `${percentage}% · ${formatBytes(snapshot.receivedBytes)} of ${formatBytes(snapshot.totalBytes)}`
      : snapshot.message;
    if (snapshot.state === 'downloading' || snapshot.state === 'verifying') {
      this.lifecycle.publish({
        state: snapshot.state, title: snapshot.state === 'verifying' ? 'Verifying Forge update' : 'Downloading Forge update',
        message, severity: 'info', version: snapshot.version, channel: snapshot.channel,
        source: trustedReleaseSource, completed: snapshot.receivedBytes, total: snapshot.totalBytes,
        progressUnit: 'bytes', actions: [{ action: 'cancel', label: 'Cancel' }],
      });
    } else if (snapshot.state === 'ready') {
      this.lifecycle.publish({
        state: 'preparing', title: `Horizon Forge ${snapshot.version} downloaded`,
        message: 'The verified package is ready to prepare safely.', severity: 'info',
        version: snapshot.version, channel: snapshot.channel, source: trustedReleaseSource, actions: [],
      });
    } else if (snapshot.state === 'cancelled') {
      this.retryPhase = 'download';
      this.lifecycle.publish({
        state: 'deferred', title: 'Forge update download cancelled', message: snapshot.message, severity: 'warning',
        version: snapshot.version, channel: snapshot.channel,
        actions: [{ action: 'retry', label: 'Retry download' }],
      }, false);
    } else if (snapshot.state === 'error') {
      this.retryPhase = this.currentUpdate ? 'download' : 'check';
      this.lifecycle.publish({
        state: 'failed', title: 'Forge update download failed', message, severity: 'error',
        version: snapshot.version, channel: snapshot.channel,
        actions: [{ action: 'retry', label: this.currentUpdate ? 'Retry' : 'Check again' },
          { action: 'defer', label: 'Defer' }],
      });
    }
  }

  private async prepareDownloadedUpdate(): Promise<boolean> {
    if (!this.downloadManager || !this.stageManager) return true;
    this.retryPhase = 'stage';
    const current = this.lifecycle.state;
    this.lifecycle.publish({
      state: 'preparing', title: 'Preparing Forge update', message: 'Revalidating the downloaded package…',
      severity: 'info', version: current.version, channel: current.channel, actions: [],
    });
    try {
      const download = await this.downloadManager.getReadyDownload();
      await this.stageManager.stage(download);
    } catch (error) {
      this.publishStageState({
        state: 'error', completedFiles: 0, totalFiles: 0,
        message: error instanceof Error ? error.message : String(error),
      });
    }
    return false;
  }

  private publishStageState(snapshot: UpdateStageSnapshot): void {
    if (snapshot.state === 'idle') return;
    const active = snapshot.state === 'inspecting' || snapshot.state === 'extracting' || snapshot.state === 'validating';
    const message = snapshot.state === 'validating' && snapshot.totalFiles > 0
      ? `${snapshot.message} ${snapshot.completedFiles} of ${snapshot.totalFiles}`
      : snapshot.message;
    if (active) {
      this.lifecycle.publish({
        state: 'preparing', title: 'Preparing Forge update', message, severity: 'info',
        version: snapshot.version, channel: snapshot.channel,
        completed: snapshot.completedFiles, total: snapshot.totalFiles, progressUnit: 'files',
        actions: [{ action: 'cancel', label: 'Cancel' }],
      });
    } else if (snapshot.state === 'ready') {
      const canActivate = this.activationManager !== undefined;
      this.lifecycle.publish({
        state: 'ready', title: `Horizon Forge ${snapshot.version} is ready to install`,
        message: !canActivate
          ? `The update is verified, but automatic installation is unavailable: ${this.options.installLayout.reason}`
          : this.options.isProjectDirty()
            ? 'The update is verified. Save or explicitly discard your project changes when you choose Install and restart.'
            : 'The update is verified. Forge will close and restart only when you choose Install and restart.',
        severity: 'info', version: snapshot.version, channel: snapshot.channel,
        actions: canActivate
          ? [{ action: 'install', label: 'Install and restart' }, { action: 'defer', label: 'Defer' }]
          : [{ action: 'dismiss', label: 'Dismiss' }],
      });
    } else if (snapshot.state === 'cancelled') {
      this.retryPhase = 'stage';
      this.lifecycle.publish({
        state: 'deferred', title: 'Forge update preparation cancelled', message, severity: 'warning',
        version: snapshot.version, channel: snapshot.channel,
        actions: [{ action: 'retry', label: 'Retry preparation' }],
      }, false);
    } else if (snapshot.state === 'error') {
      this.retryPhase = 'stage';
      this.lifecycle.publish({
        state: 'failed', title: 'Forge update preparation failed', message, severity: 'error',
        version: snapshot.version, channel: snapshot.channel,
        actions: [{ action: 'retry', label: 'Retry' }, { action: 'defer', label: 'Defer' }],
      });
    }
  }

  private async installReadyUpdate(): Promise<boolean> {
    if (this.installing) throw new Error('Update installation is already active.');
    this.installing = true;
    try {
      return await this.performInstallReadyUpdate();
    } finally {
      this.installing = false;
    }
  }

  private async performInstallReadyUpdate(): Promise<boolean> {
    if (!this.downloadManager || !this.stageManager || !this.activationManager) {
      this.publishActivationState({ state: 'error', message: 'Managed update installation is unavailable.' });
      return false;
    }
    this.retryPhase = 'install';
    if (this.options.isProjectDirty()) {
      const choice = await showUpdateRestartPrompt(this.options.getMainWindow());
      if (choice === 'cancel') return false;
      if (choice === 'save') {
        try {
          await this.options.saveProject();
          if (this.options.isProjectDirty()) throw new Error('The project is still unsaved.');
        } catch (error) {
          this.publishActivationState({ state: 'error', message: `Project save failed: ${errorMessage(error)}` });
          return false;
        }
      }
    }
    try {
      const download = await this.downloadManager.getReadyDownload();
      const stage = await this.stageManager.getReadyStage(download);
      await this.activationManager.start(stage);
    } catch (error) {
      this.publishActivationState({ state: 'error', message: errorMessage(error) });
    }
    return false;
  }

  private publishActivationState(snapshot: UpdateActivationSnapshot): void {
    if (snapshot.state === 'idle') return;
    if (snapshot.state === 'error') {
      const current = this.lifecycle.state;
      const retryable = snapshot.version !== undefined || current.version !== undefined;
      this.retryPhase = retryable ? 'install' : undefined;
      this.lifecycle.publish({
        state: 'failed', title: 'Forge update installation failed', message: snapshot.message, severity: 'error',
        version: snapshot.version ?? current.version, channel: snapshot.channel ?? current.channel,
        actions: retryable
          ? [{ action: 'retry', label: 'Retry' }, { action: 'defer', label: 'Defer' }]
          : [{ action: 'dismiss', label: 'Dismiss' }],
      });
    } else if (snapshot.state === 'rolled-back') {
      this.lifecycle.publish({
        state: 'rolled-back', title: 'Forge update rolled back', message: snapshot.message, severity: 'warning',
        channel: this.options.channel, actions: [{ action: 'dismiss', label: 'Dismiss' }],
      });
    } else {
      this.lifecycle.publish({
        state: 'restarting', title: snapshot.state === 'waiting-for-exit'
          ? 'Restarting to install Forge update' : 'Preparing Forge update restart',
        message: snapshot.message, severity: 'info', version: snapshot.version, channel: snapshot.channel,
        actions: [],
      });
    }
  }

  async runUpdateAction(action: UpdateLifecycleAction): Promise<void> {
    if (!this.lifecycle.state.actions.some((candidate) => candidate.action === action)) {
      throw new Error('Update action is unavailable in the current state.');
    }
    if (action === 'defer') {
      this.lifecycle.defer();
      return;
    }
    if (action === 'dismiss') {
      this.lifecycle.clear();
      return;
    }
    if (action === 'cancel') {
      if (!this.downloadManager?.cancel() && !this.stageManager?.cancel()) {
        throw new Error('No update operation is active.');
      }
      return;
    }
    if (action === 'install') {
      await this.installReadyUpdate();
      return;
    }
    if (action === 'download' || action === 'open') {
      if (!this.currentUpdate) throw new Error('Update release details are unavailable.');
      if (action === 'download') {
        this.startDownload(this.currentUpdate);
        return;
      }
      if (this.handingOff) throw new Error('The update handoff is already open.');
      this.handingOff = true;
      try {
        if (await this.handoff(this.currentUpdate)) this.lifecycle.publish({
          ...this.lifecycle.state,
          state: 'deferred', title: 'Manual update handoff opened',
          message: 'Forge remains open. Save your work and complete the update using the trusted package or release page.',
          actions: [{ action: 'dismiss', label: 'Dismiss' }],
        }, false);
      } finally {
        this.handingOff = false;
      }
      return;
    }
    if (this.retryPhase === 'check') {
      await this.check(true, this.options.channel ?? 'stable');
    } else if (this.retryPhase === 'download') {
      if (!this.currentUpdate) throw new Error('Update release details are unavailable.');
      this.startDownload(this.currentUpdate);
    } else if (this.retryPhase === 'stage') {
      void this.prepareDownloadedUpdate();
    } else if (this.retryPhase === 'install') {
      await this.installReadyUpdate();
    } else {
      throw new Error('No failed update operation is available to retry.');
    }
  }

  private startDownload(update: ResolvedUpdate): void {
    const operation = this.handoff(update);
    this.lifecycle.publish({
      state: 'downloading', title: 'Downloading Forge update', message: 'Starting verified package download…',
      severity: 'info', version: update.version, channel: update.channel, source: trustedReleaseSource,
      completed: 0, total: update.artifact.size, progressUnit: 'bytes', actions: [{ action: 'cancel', label: 'Cancel' }],
    });
    void operation.catch((error) => this.publishDownloadState({
      state: 'error', version: update.version, channel: update.channel,
      receivedBytes: 0, totalBytes: update.artifact.size, message: errorMessage(error),
    }));
  }

  private async downloadWindowsPackage(update: AvailableUpdate): Promise<boolean> {
    const selection = this.options.getMainWindow()
      ? await dialog.showSaveDialog(this.options.getMainWindow()!, {
        defaultPath: path.join(app.getPath('downloads'), update.artifact.file),
      })
      : await dialog.showSaveDialog({ defaultPath: path.join(app.getPath('downloads'), update.artifact.file) });
    if (selection.canceled || !selection.filePath) return false;
    await downloadVerifiedPackage(update.downloadUrl, selection.filePath, update.artifact);
    shell.showItemInFolder(selection.filePath);
    return true;
  }

}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
