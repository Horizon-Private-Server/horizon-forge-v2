import { ipcMain } from 'electron';

import type { AssetPreviewKind } from '../types/ForgeApi.js';
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
  const activePreviewRequests = new Map<string, number>();
  let terrainLoading = false;

  function assertPreview(assetId: unknown, kind?: unknown): asserts assetId is string {
    if (typeof assetId !== 'string' || !/^[0-9a-f]{64}$/.test(assetId))
      throw new TypeError('Asset preview ID is invalid');
    if (kind !== undefined && kind !== 'moby' && kind !== 'tie' && kind !== 'shrub')
      throw new TypeError('Asset preview kind is invalid');
  }

  function assertRequestToken(value: unknown): asserts value is string {
    if (typeof value !== 'string'
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value))
      throw new TypeError('Asset preview request token is invalid');
  }

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
        projectPath: project.projectPath,
        catalogRootPath: settings.paths.assets,
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

  ipcMain.handle('forge:asset-preview', async (
    event, assetId: unknown, kind: unknown, requestToken: unknown,
  ) => {
    assertSender(event.sender.id);
    assertPreview(assetId, kind);
    assertRequestToken(requestToken);
    if (activePreviewRequests.has(requestToken)) throw new Error('Asset preview request token is already active');
    const project = await (await host.getEditorSnapshot()).result;
    if (project.target.game !== 'UYA') throw new Error('Asset previews currently require a UYA project');
    const request = await host.prepareAssetPreview({
      cacheRootPath: settings.paths.renderCache,
      catalogRootPath: settings.paths.assets,
      assetId,
      kind: kind as AssetPreviewKind,
      targetGame: project.target.game,
      viewPreset: 'model-default',
    });
    activePreviewRequests.set(requestToken, request.requestId);
    try {
      return await renderAssets.addPreview(await request.result);
    } finally {
      if (activePreviewRequests.get(requestToken) === request.requestId) activePreviewRequests.delete(requestToken);
    }
  });

  ipcMain.handle('forge:asset-preview-cancel', async (event, requestToken: unknown) => {
    assertSender(event.sender.id);
    assertRequestToken(requestToken);
    const requestId = activePreviewRequests.get(requestToken);
    if (requestId !== undefined) await host.cancel(requestId);
  });
}
