import { ipcMain } from 'electron';

import type { RenderAssetProtocol } from './RenderAssetProtocol.js';
import type { SettingsStore } from './Settings.js';
import type { HostClient } from './bridge/HostClient.js';

interface RenderIpcHandlersOptions {
  host: HostClient;
  settings: SettingsStore;
  renderAssets: RenderAssetProtocol;
  assertSender(senderId: number): void;
}

export function registerRenderIpcHandlers(options: RenderIpcHandlersOptions): void {
  const { host, settings, renderAssets, assertSender } = options;
  let activeRequestId: number | undefined;
  let terrainLoading = false;

  ipcMain.handle('forge:editor-terrain', async (event) => {
    assertSender(event.sender.id);
    if (terrainLoading) throw new Error('Terrain is already loading');
    terrainLoading = true;
    try {
      const project = await (await host.getEditorSnapshot()).result;
      if (project.target.game !== 'UYA') throw new Error('The active project does not target UYA');
      const values = Object.fromEntries((await settings.getSnapshot()).entries.map((entry) => [entry.key, entry.value]));
      const fingerprint = project.baseLevel.sourceFingerprint;
      const sourceIsoPath = values['sources.uya.fingerprint'] === fingerprint
        ? String(values['sources.uya.iso'] ?? '')
        : '';
      const request = await host.prepareUyaRenderPackage({
        sourceIsoPath,
        cacheRootPath: settings.paths.renderCache,
        fingerprint,
        level: project.baseLevel.level,
      }, (progress) => event.sender.send('forge:editor-terrain-progress', progress));
      activeRequestId = request.requestId;
      return await renderAssets.addPackage(await request.result);
    } finally {
      activeRequestId = undefined;
      terrainLoading = false;
    }
  });

  ipcMain.handle('forge:editor-terrain-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activeRequestId !== undefined) await host.cancel(activeRequestId);
  });
}
