import { ipcMain } from 'electron';

import type {
  EditorCommand, EditorSnapshot, EditorTieCollisionGenerationSettings,
} from '../types/ForgeApi.js';
import { isEditorCommand } from '../utils/EditorCommandValidation.js';
import { setEditorMenuState, setEditorTextInputActive } from './ApplicationMenu.js';
import type { SettingsStore } from './Settings.js';
import type { HostClient } from './bridge/HostClient.js';

interface EditorIpcHandlersOptions {
  host: HostClient;
  settings: SettingsStore;
  assertSender(senderId: number): void;
  getSettingValues(): Promise<Record<string, string | number | boolean>>;
  resolveProjectPath(value: unknown): Promise<string>;
  cancelAssetExplorer(): Promise<void>;
}

export function registerEditorIpcHandlers(
  options: EditorIpcHandlersOptions,
): () => EditorSnapshot | undefined {
  const {
    host, settings, assertSender, getSettingValues, resolveProjectPath, cancelAssetExplorer,
  } = options;
  let activePreviewRequestId: number | undefined;
  let activeSnapshot: EditorSnapshot | undefined;

  const updateSnapshot = (value: EditorSnapshot): EditorSnapshot => {
    activeSnapshot = value;
    setEditorMenuState(value);
    return value;
  };

  ipcMain.on('forge:editor-text-input', (event, active: unknown) => {
    assertSender(event.sender.id);
    if (typeof active !== 'boolean') throw new TypeError('Editor input context is invalid');
    setEditorTextInputActive(active);
  });
  ipcMain.handle('forge:editor-open', async (event, projectPath: unknown) => {
    assertSender(event.sender.id);
    const values = await getSettingValues();
    return updateSnapshot(await (await host.openEditorProject(
      await resolveProjectPath(projectPath),
      settings.paths.assets,
      Number(values['editor.autosaveSeconds']),
    )).result);
  });
  ipcMain.handle('forge:editor-close', async (event) => {
    assertSender(event.sender.id);
    await cancelAssetExplorer();
    if (activePreviewRequestId !== undefined) await host.cancel(activePreviewRequestId);
    await (await host.closeEditorProject()).result;
    activeSnapshot = undefined;
    setEditorMenuState();
  });
  ipcMain.handle('forge:editor-query', async (event) => {
    assertSender(event.sender.id);
    return updateSnapshot(await (await host.getEditorSnapshot()).result);
  });
  ipcMain.handle('forge:editor-execute', async (event, command: unknown) => {
    assertSender(event.sender.id);
    assertEditorCommand(command);
    return updateSnapshot(await (await host.executeEditorCommand(command)).result);
  });
  ipcMain.handle('forge:editor-tie-collision-inspect', async (event, entityId: unknown) => {
    assertSender(event.sender.id);
    if (typeof entityId !== 'string' || !entityId)
      throw new TypeError('TIE collision entity ID is invalid');
    return await (await host.inspectTieCollisionSource(entityId)).result;
  });
  ipcMain.handle('forge:editor-tie-collision-preview', async (
    event,
    entityId: unknown,
    generationSettings: unknown,
  ) => {
    assertSender(event.sender.id);
    if (typeof entityId !== 'string' || !entityId) throw new TypeError('TIE collision entity ID is invalid');
    assertTieCollisionSettings(generationSettings);
    if (activePreviewRequestId !== undefined) await host.cancel(activePreviewRequestId);
    const request = await host.previewTieCollision(entityId, generationSettings);
    activePreviewRequestId = request.requestId;
    try { return await request.result; }
    finally {
      if (activePreviewRequestId === request.requestId) activePreviewRequestId = undefined;
    }
  });
  ipcMain.handle('forge:editor-tie-collision-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activePreviewRequestId !== undefined) await host.cancel(activePreviewRequestId);
  });
  ipcMain.handle('forge:editor-tie-collision-apply', async (event, commandId: unknown, token: unknown) => {
    assertSender(event.sender.id);
    if (typeof commandId !== 'string' || !commandId || typeof token !== 'string' || !token)
      throw new TypeError('TIE collision apply request is invalid');
    return updateSnapshot(await (await host.applyTieCollisionPreview(commandId, token)).result);
  });
  ipcMain.handle('forge:editor-save', async (event) => {
    assertSender(event.sender.id);
    return updateSnapshot(await (await host.saveEditorProject()).result);
  });
  ipcMain.handle('forge:editor-events', async (event, afterSequence: unknown, limit: unknown) => {
    assertSender(event.sender.id);
    if (!Number.isSafeInteger(afterSequence) || Number(afterSequence) < 0
      || !Number.isInteger(limit) || Number(limit) < 1 || Number(limit) > 1_024) {
      throw new TypeError('Editor event range is invalid');
    }
    return (await host.readEditorEvents(Number(afterSequence), Number(limit))).result;
  });

  return () => activeSnapshot;
}

function assertEditorCommand(value: unknown): asserts value is EditorCommand {
  if (!isEditorCommand(value)) throw new TypeError('Editor command is invalid');
}

function assertTieCollisionSettings(
  value: unknown,
): asserts value is EditorTieCollisionGenerationSettings | undefined {
  if (value === undefined) return;
  if (!value || typeof value !== 'object') throw new TypeError('TIE collision settings are invalid');
  const settings = value as Record<string, unknown>;
  if (typeof settings.rawType !== 'number' || !Number.isInteger(settings.rawType)
    || settings.rawType < 0 || settings.rawType > 0xff
    || typeof settings.profileSections !== 'number' || !Number.isInteger(settings.profileSections)
    || settings.profileSections < 1 || settings.profileSections > 16
    || typeof settings.surfaceLodIndex !== 'number' || !Number.isInteger(settings.surfaceLodIndex)
    || settings.surfaceLodIndex < -1 || settings.surfaceLodIndex > 2
    || typeof settings.useHull !== 'boolean')
    throw new TypeError('TIE collision settings are invalid');
}
