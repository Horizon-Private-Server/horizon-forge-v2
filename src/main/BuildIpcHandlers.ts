import { app, dialog, ipcMain } from 'electron';
import type { BrowserWindow } from 'electron';
import { appendFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

import type { BuildLayerId } from '../types/ForgeApi.js';
import { BUILD_LAYERS } from '../utils/BuildLayers.js';
import { errorMessage } from '../utils/Errors.js';
import type { SettingsStore } from './Settings.js';
import type { HostClient } from './bridge/HostClient.js';
import { loadOcclusionVisibility, previewOcclusionSamples, rebuildOcclusion } from './OcclusionBakeService.js';

interface BuildIpcHandlersOptions {
  host: HostClient;
  settings: SettingsStore;
  getMainWindow: () => BrowserWindow | undefined;
  assertSender(senderId: number): void;
}

const BUILD_LAYER_SET = new Set<BuildLayerId>(BUILD_LAYERS);

export function registerBuildIpcHandlers(options: BuildIpcHandlersOptions): void {
  const { host, settings, getMainWindow, assertSender } = options;
  let activeRequestId: number | undefined;
  let running = false;
  let occlusion: AbortController | undefined;
  app.on('before-quit', () => occlusion?.abort());

  for (const [channel, action] of [
    ['forge:editor-rebuild-occlusion', rebuildOcclusion],
    ['forge:editor-preview-occlusion-samples', previewOcclusionSamples],
    ['forge:editor-occlusion-visibility', loadOcclusionVisibility],
  ] as const) ipcMain.handle(channel, async (event, samplePullIn: unknown = 2, pixelEdgeAllowance: unknown = false) => {
    assertSender(event.sender.id);
    if (typeof samplePullIn !== 'number' || !Number.isInteger(samplePullIn) || samplePullIn < 0 || samplePullIn > 5)
      throw new TypeError('Occlusion sample pull-in must be a whole number from 0 to 5.');
    if (typeof pixelEdgeAllowance !== 'boolean') throw new TypeError('Occlusion pixel edge allowance must be a boolean.');
    if (running) throw new Error('A build is already running.');
    running = true;
    const controller = new AbortController();
    occlusion = controller;
    const cancel = () => controller.abort();
    event.sender.once('destroyed', cancel);
    try {
      const project = await (await (channel === 'forge:editor-occlusion-visibility'
        ? host.getEditorSnapshot() : host.saveEditorProject())).result;
      if (project.target.game !== 'UYA') throw new Error('Occlusion scene export currently supports UYA projects.');
      const values = Object.fromEntries((await settings.getSnapshot()).entries.map((entry) => [entry.key, entry.value]));
      const iso = String(values['sources.uya.iso'] ?? '');
      if (!iso) throw new Error('Configure the clean source ISO before rebuilding occlusion.');
      if (String(values['sources.uya.fingerprint'] ?? '').toLowerCase() !== project.baseLevel.sourceFingerprint.toLowerCase())
        throw new Error('The configured clean source ISO does not match this project.');
      return await action(project.projectPath, settings.paths.assets, iso, controller.signal,
        (value) => { if (!event.sender.isDestroyed()) event.sender.send('forge:editor-build-progress', value); }, samplePullIn, pixelEdgeAllowance);
    } catch (error) {
      await retainBuildFailure(settings.paths.logs, error);
      throw error;
    } finally {
      event.sender.removeListener('destroyed', cancel);
      occlusion = undefined;
      running = false;
    }
  });

  ipcMain.handle('forge:editor-build-plan', async (event) => {
    assertSender(event.sender.id);
    const project = await (await host.getEditorSnapshot()).result;
    return (await host.getUyaBuildPlan({
      projectRoot: project.projectPath,
      catalogRoot: settings.paths.assets,
    })).result;
  });

  ipcMain.handle('forge:editor-build-patch', async (event, includedLayers: unknown) => {
    assertSender(event.sender.id);
    if (!Array.isArray(includedLayers)
      || includedLayers.some((value) => typeof value !== 'string' || !BUILD_LAYER_SET.has(value as BuildLayerId))
      || new Set(includedLayers).size !== includedLayers.length)
      throw new TypeError('Build layers are invalid.');
    if (running) throw new Error('A build is already running.');
    running = true;
    try {
      const project = await (await host.saveEditorProject()).result;
      if (project.target.game !== 'UYA') throw new Error('Build and patch currently supports only UYA projects.');
      const values = Object.fromEntries(
        (await settings.getSnapshot()).entries.map((entry) => [entry.key, entry.value]));
      const cleanSourceIso = String(values['sources.uya.iso'] ?? '');
      const developmentIso = String(values['targets.uya.developmentIso'] ?? '');
      const sourceFingerprint = String(values['sources.uya.fingerprint'] ?? '');
      if (!cleanSourceIso || !developmentIso || !sourceFingerprint)
        throw new Error('Complete UYA source and development ISO setup before building.');

      const run = async (acknowledgedWarnings: string[]) => {
        const request = await host.buildAndPatchUyaProject({
          projectRoot: project.projectPath,
          catalogRoot: settings.paths.assets,
          cleanSourceIso,
          developmentIso,
          sourceFingerprint,
          acknowledgedWarnings,
          forceFullImage: false,
          includedLayers: includedLayers as BuildLayerId[],
          forceInPlace: values['build.uya.forceOversizedInPlace'] === true,
        }, (progress) => event.sender.send('forge:editor-build-progress', progress));
        activeRequestId = request.requestId;
        return request.result;
      };

      try {
        let result = await run([]);
        if (result.requiresWarningAcknowledgement) {
          activeRequestId = undefined;
          const window = getMainWindow();
          const confirmation = window ? await dialog.showMessageBox(window, {
            type: 'warning',
            title: 'Build warnings',
            message: 'Review these warnings before building and patching.',
            detail: result.diagnostics.join('\n\n'),
            buttons: ['Cancel', 'Continue'],
            defaultId: 0,
            cancelId: 0,
          }) : { response: 0 };
          if (confirmation.response !== 1) return result;
          result = await run(result.warningCodes);
        }
        if (!result.succeeded) {
          await retainBuildFailure(
            settings.paths.logs,
            new Error([result.message, ...result.diagnostics, result.nextAction].filter(Boolean).join('\n')),
          );
        }
        return result;
      } catch (error) {
        await retainBuildFailure(settings.paths.logs, error);
        throw error;
      } finally {
        activeRequestId = undefined;
      }
    } finally { running = false; }
  });

  ipcMain.handle('forge:editor-build-cancel', async (event) => {
    assertSender(event.sender.id);
    occlusion?.abort();
    if (activeRequestId !== undefined) await host.cancel(activeRequestId);
  });
}

async function retainBuildFailure(logDirectory: string, error: unknown): Promise<void> {
  try {
    await mkdir(logDirectory, { recursive: true });
    const detail = error instanceof Error ? error.stack ?? error.message : String(error);
    await appendFile(
      path.join(logDirectory, 'build-patch.log'),
      `[${new Date().toISOString()}] ${detail}\n\n`,
      'utf8');
  } catch (logError) {
    console.error(`Could not retain build failure log: ${errorMessage(logError)}`);
  }
}
