import { ipcMain } from 'electron';

import type { AssetPreviewKind } from '../types/ForgeApi.js';
import type { RenderAssetProtocol } from './RenderAssetProtocol.js';
import type { SettingsStore } from './Settings.js';
import type { HostClient } from './bridge/HostClient.js';

interface RenderIpcHandlersOptions {
  host: HostClient;
  settings: SettingsStore;
  renderAssets: RenderAssetProtocol;
  getEditorTargetGame(): string | undefined;
  assertSender(senderId: number): void;
}

export function registerRenderIpcHandlers(options: RenderIpcHandlersOptions): void {
  const { host, settings, renderAssets, getEditorTargetGame, assertSender } = options;
  let activeRequestId: number | undefined;
  const activePreviewRequests = new Map<string, number>();
  let terrainLoading = false;

  function assertPreview(assetId: unknown, kind?: unknown, shellIndex?: unknown): asserts assetId is string {
    if (typeof assetId !== 'string' || !/^[0-9a-f]{64}$/.test(assetId))
      throw new TypeError('Asset preview ID is invalid');
    if (kind !== undefined && kind !== 'moby' && kind !== 'tie' && kind !== 'shrub' && kind !== 'texture' && kind !== 'sky')
      throw new TypeError('Asset preview kind is invalid');
    if ((kind === 'sky' && shellIndex !== undefined
        && (!Number.isInteger(shellIndex) || Number(shellIndex) < 0 || Number(shellIndex) >= 8))
      || (kind !== 'sky' && shellIndex !== undefined))
      throw new TypeError('Asset preview shell index is invalid');
  }

  function assertRequestToken(value: unknown): asserts value is string {
    if (typeof value !== 'string'
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value))
      throw new TypeError('Asset preview request token is invalid');
  }

  function assertTargetGame(value: unknown): asserts value is string {
    if (typeof value !== 'string' || !/^[A-Z0-9]{2,16}$/.test(value))
      throw new TypeError('Asset thumbnail target game is invalid');
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
      return await renderAssets.addUyaPackage(await request.result);
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
    event, assetId: unknown, kind: unknown, requestToken: unknown, shellIndex: unknown,
  ) => {
    assertSender(event.sender.id);
    assertPreview(assetId, kind, shellIndex);
    assertRequestToken(requestToken);
    if (activePreviewRequests.has(requestToken)) throw new Error('Asset preview request token is already active');
    const targetGame = getEditorTargetGame();
    if (targetGame !== 'UYA') throw new Error('Asset previews currently require a UYA project');
    const request = await host.prepareAssetPreview({
      cacheRootPath: settings.paths.renderCache,
      catalogRootPath: settings.paths.assets,
      assetId,
      kind: kind as AssetPreviewKind,
      targetGame,
      viewPreset: kind === 'texture' ? 'texture-default' : kind === 'sky' ? 'sky-default' : 'model-default',
      ...(shellIndex !== undefined ? { shellIndex: shellIndex as number } : {}),
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

  ipcMain.handle('forge:editor-tie-collision-model', async (
    event, token: unknown, requestToken: unknown,
  ) => {
    assertSender(event.sender.id);
    assertRequestToken(token);
    assertRequestToken(requestToken);
    if (activePreviewRequests.has(requestToken)) throw new Error('Preview request token is already active');
    const request = await host.prepareTieCollisionPreview(
      settings.paths.renderCache, settings.paths.assets, token);
    activePreviewRequests.set(requestToken, request.requestId);
    try { return await renderAssets.addPreview(await request.result); }
    finally {
      if (activePreviewRequests.get(requestToken) === request.requestId) activePreviewRequests.delete(requestToken);
    }
  });

  ipcMain.handle('forge:asset-thumbnail', async (
    event, targetGame: unknown, assetId: unknown, kind: unknown, shellIndex: unknown,
  ) => {
    assertSender(event.sender.id);
    assertTargetGame(targetGame);
    assertPreview(assetId, kind, shellIndex);
    const { sdkRevision } = await host.start();
    return renderAssets.getThumbnail(
      targetGame, assetId, kind as AssetPreviewKind, sdkRevision, shellIndex as number | undefined,
    );
  });

  ipcMain.handle('forge:asset-thumbnail-store', async (
    event, targetGame: unknown, assetId: unknown, kind: unknown, bytes: unknown, shellIndex: unknown,
  ) => {
    assertSender(event.sender.id);
    assertTargetGame(targetGame);
    assertPreview(assetId, kind, shellIndex);
    if (!(bytes instanceof Uint8Array) || bytes.byteLength <= 0 || bytes.byteLength > 2 * 1024 * 1024)
      throw new TypeError('Asset thumbnail bytes are invalid');
    const { sdkRevision } = await host.start();
    return renderAssets.storeThumbnail(
      targetGame, assetId, kind as AssetPreviewKind, sdkRevision, bytes, shellIndex as number | undefined,
    );
  });
}
