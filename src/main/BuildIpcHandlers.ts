import { dialog, ipcMain } from 'electron';
import type { BrowserWindow } from 'electron';
import { appendFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

import type { BuildLayerId } from '../types/ForgeApi.js';
import { errorMessage } from '../utils/Errors.js';
import type { SettingsStore } from './Settings.js';
import type { HostClient } from './bridge/HostClient.js';

interface BuildIpcHandlersOptions {
  host: HostClient;
  settings: SettingsStore;
  getMainWindow: () => BrowserWindow | undefined;
  assertSender(senderId: number): void;
}

const BUILD_LAYERS = new Set<BuildLayerId>([
  'World', 'Sky', 'Tfrags', 'Collision', 'Ties', 'Shrubs', 'Mobys', 'Gameplay', 'Lighting', 'Opaque',
]);

export function registerBuildIpcHandlers(options: BuildIpcHandlersOptions): void {
  const { host, settings, getMainWindow, assertSender } = options;
  let activeRequestId: number | undefined;

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
      || includedLayers.some((value) => typeof value !== 'string' || !BUILD_LAYERS.has(value as BuildLayerId))
      || new Set(includedLayers).size !== includedLayers.length)
      throw new TypeError('Build layers are invalid.');
    if (activeRequestId !== undefined) throw new Error('A build is already running.');
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
  });

  ipcMain.handle('forge:editor-build-cancel', async (event) => {
    assertSender(event.sender.id);
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
