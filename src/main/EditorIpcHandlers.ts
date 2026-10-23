import { ipcMain } from 'electron';

import type {
  EditorCommand, EditorSnapshot, EditorInstancedCollisionGenerationSettings,
} from '../types/ForgeApi.js';
import type { ContentImportSelection } from '../types/ContentImport.js';
import {
  isEditorCommand, isGroupTemplateRequest, isUuid,
} from '../utils/EditorCommandValidation.js';
import { applyEditorCommandResult } from '../utils/EditorCommandResult.js';
import { fileExists } from '../utils/FileSystem.js';
import { formatUyaLevelLabel } from '../utils/UyaLevels.js';
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
  let activeGroupTemplateRequestId: number | undefined;
  let activeSoundPreviewRequestId: number | undefined;
  let activeContentImportRequestId: number | undefined;
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
    if (activeGroupTemplateRequestId !== undefined) await host.cancel(activeGroupTemplateRequestId);
    if (activeSoundPreviewRequestId !== undefined) await host.cancel(activeSoundPreviewRequestId);
    if (activeContentImportRequestId !== undefined) await host.cancel(activeContentImportRequestId);
    await (await host.closeEditorProject()).result;
    activeSnapshot = undefined;
    setEditorMenuState();
  });
  ipcMain.handle('forge:editor-query', async (event) => {
    assertSender(event.sender.id);
    return updateSnapshot(await (await host.getEditorSnapshot()).result);
  });
  ipcMain.handle('forge:content-import-options', async (event) => {
    assertSender(event.sender.id);
    requireContentImportSnapshot(activeSnapshot);
    const { sourceIsoPath } = await contentImportSource(getSettingValues);
    const options = await (await host.listUyaProjectLevels(sourceIsoPath)).result;
    return {
      levels: options.levels.map((level) => ({ value: level, label: formatUyaLevelLabel(level) })),
      warnings: options.warnings,
    };
  });
  ipcMain.handle('forge:content-import-plan', async (event, sourceLevel: unknown, selections: unknown) => {
    assertSender(event.sender.id);
    const snapshot = requireContentImportSnapshot(activeSnapshot);
    assertContentImportLevel(sourceLevel);
    assertContentImportSelections(selections);
    const source = await contentImportSource(getSettingValues);
    if (activeContentImportRequestId !== undefined) await host.cancel(activeContentImportRequestId);
    const request = await host.planContentImport({
      projectPath: snapshot.projectPath,
      catalogRootPath: settings.paths.assets,
      ...source,
      sourceLevel: Number(sourceLevel),
      selections,
    }, (progress) => event.sender.send('forge:content-import-progress', progress));
    activeContentImportRequestId = request.requestId;
    try { return await request.result; }
    finally {
      if (activeContentImportRequestId === request.requestId) activeContentImportRequestId = undefined;
    }
  });
  ipcMain.handle('forge:content-import-apply', async (
    event,
    sourceLevel: unknown,
    batchId: unknown,
    selections: unknown,
    referenceChoices: unknown,
  ) => {
    assertSender(event.sender.id);
    const snapshot = requireContentImportSnapshot(activeSnapshot);
    assertContentImportLevel(sourceLevel);
    if (typeof batchId !== 'string' || !batchId || batchId.length > 256)
      throw new TypeError('Import batch ID is invalid.');
    assertContentImportSelections(selections);
    assertContentImportReferenceChoices(referenceChoices);
    const source = await contentImportSource(getSettingValues);
    if (activeContentImportRequestId !== undefined) await host.cancel(activeContentImportRequestId);
    const request = await host.applyContentImport({
      projectPath: snapshot.projectPath,
      catalogRootPath: settings.paths.assets,
      ...source,
      sourceLevel: Number(sourceLevel),
      batchId,
      selections,
      referenceChoices,
    }, (progress) => event.sender.send('forge:content-import-progress', progress));
    activeContentImportRequestId = request.requestId;
    try { return updateSnapshot(await request.result); }
    finally {
      if (activeContentImportRequestId === request.requestId) activeContentImportRequestId = undefined;
    }
  });
  ipcMain.handle('forge:content-import-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activeContentImportRequestId !== undefined) await host.cancel(activeContentImportRequestId);
  });
  ipcMain.handle('forge:content-import-removal-plan', async (event, batchId: unknown) => {
    assertSender(event.sender.id);
    const snapshot = requireContentImportSnapshot(activeSnapshot);
    assertImportBatchId(batchId);
    return await (await host.planContentImportRemoval(snapshot.projectPath, batchId)).result;
  });
  ipcMain.handle('forge:content-import-removal-apply', async (
    event,
    batchId: unknown,
    targetFingerprint: unknown,
  ) => {
    assertSender(event.sender.id);
    const snapshot = requireContentImportSnapshot(activeSnapshot);
    assertImportBatchId(batchId);
    if (typeof targetFingerprint !== 'string' || !/^[0-9a-f]{64}$/.test(targetFingerprint))
      throw new TypeError('Import-removal target fingerprint is invalid.');
    return updateSnapshot(await (await host.applyContentImportRemoval(
      snapshot.projectPath, batchId, targetFingerprint)).result);
  });
  ipcMain.handle('forge:sound-workspace', async (event) => {
    assertSender(event.sender.id);
    if (!activeSnapshot) throw new TypeError('No editor project is open');
    return await (await host.getSoundWorkspace()).result;
  });
  ipcMain.handle('forge:sound-preview', async (
    event,
    nativeIndex: unknown,
    preferAudible: unknown,
    catalogSource: unknown,
  ) => {
    assertSender(event.sender.id);
    if (!activeSnapshot || !Number.isInteger(nativeIndex) || Number(nativeIndex) < 0
      || typeof preferAudible !== 'boolean')
      throw new TypeError('Sound preview request is invalid');
    const source = parseCatalogSoundSource(catalogSource, settings.paths.assets);
    if (activeSoundPreviewRequestId !== undefined) await host.cancel(activeSoundPreviewRequestId);
    const request = await host.renderSoundPreview(Number(nativeIndex), preferAudible, source);
    activeSoundPreviewRequestId = request.requestId;
    try { return await request.result; }
    finally {
      if (activeSoundPreviewRequestId === request.requestId) activeSoundPreviewRequestId = undefined;
    }
  });
  ipcMain.handle('forge:sound-preview-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activeSoundPreviewRequestId !== undefined) await host.cancel(activeSoundPreviewRequestId);
  });
  ipcMain.handle('forge:editor-execute', async (event, command: unknown) => {
    assertSender(event.sender.id);
    assertEditorCommand(command);
    if (!activeSnapshot) throw new TypeError('No editor project is open');
    const result = await (await host.executeEditorCommand(command)).result;
    updateSnapshot(applyEditorCommandResult(activeSnapshot, result));
    return result;
  });
  ipcMain.handle('forge:editor-instanced-collision-inspect', async (event, entityId: unknown) => {
    assertSender(event.sender.id);
    if (typeof entityId !== 'string' || !entityId)
      throw new TypeError('instanced collision entity ID is invalid');
    return await (await host.inspectInstancedCollisionSource(entityId)).result;
  });
  ipcMain.handle('forge:editor-instanced-collision-preview', async (
    event,
    entityId: unknown,
    generationSettings: unknown,
  ) => {
    assertSender(event.sender.id);
    if (typeof entityId !== 'string' || !entityId) throw new TypeError('instanced collision entity ID is invalid');
    assertInstancedCollisionSettings(generationSettings);
    if (activePreviewRequestId !== undefined) await host.cancel(activePreviewRequestId);
    const request = await host.previewInstancedCollision(entityId, generationSettings);
    activePreviewRequestId = request.requestId;
    try { return await request.result; }
    finally {
      if (activePreviewRequestId === request.requestId) activePreviewRequestId = undefined;
    }
  });
  ipcMain.handle('forge:editor-instanced-collision-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activePreviewRequestId !== undefined) await host.cancel(activePreviewRequestId);
  });
  ipcMain.handle('forge:editor-instanced-collision-apply', async (event, commandId: unknown, token: unknown) => {
    assertSender(event.sender.id);
    if (typeof commandId !== 'string' || !commandId || typeof token !== 'string' || !token)
      throw new TypeError('instanced collision apply request is invalid');
    return updateSnapshot(await (await host.applyInstancedCollisionPreview(commandId, token)).result);
  });
  ipcMain.handle('forge:editor-group-template-preview', async (event, request: unknown) => {
    assertSender(event.sender.id);
    if (!isGroupTemplateRequest(request)) throw new TypeError('group template request is invalid');
    if (activeGroupTemplateRequestId !== undefined) await host.cancel(activeGroupTemplateRequestId);
    const pending = await host.previewGroupTemplate(request);
    activeGroupTemplateRequestId = pending.requestId;
    try { return await pending.result; }
    finally {
      if (activeGroupTemplateRequestId === pending.requestId) activeGroupTemplateRequestId = undefined;
    }
  });
  ipcMain.handle('forge:editor-group-template-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activeGroupTemplateRequestId !== undefined) await host.cancel(activeGroupTemplateRequestId);
  });
  ipcMain.handle('forge:editor-group-template-apply', async (event, commandId: unknown, token: unknown) => {
    assertSender(event.sender.id);
    if (!isUuid(commandId) || !isUuid(token))
      throw new TypeError('group template apply request is invalid');
    return updateSnapshot(await (await host.applyGroupTemplatePreview(commandId, token)).result);
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

function parseCatalogSoundSource(
  value: unknown,
  catalogRootPath: string,
): { catalogRootPath: string; bankAssetId: string; game: string } | undefined {
  if (value === undefined) return undefined;
  if (!value || typeof value !== 'object') throw new TypeError('Catalog sound preview source is invalid');
  const source = value as Record<string, unknown>;
  if (typeof source.bankAssetId !== 'string' || !/^[0-9a-f]{64}$/.test(source.bankAssetId)
    || typeof source.game !== 'string' || !source.game)
    throw new TypeError('Catalog sound preview source is invalid');
  return { catalogRootPath, bankAssetId: source.bankAssetId, game: source.game };
}

function requireContentImportSnapshot(value?: EditorSnapshot): EditorSnapshot {
  if (!value) throw new Error('Open a project before importing map content.');
  if (value.target.game !== 'UYA') throw new Error('Map content import is currently available for UYA projects.');
  return value;
}

function assertContentImportLevel(value: unknown): asserts value is number {
  if (!Number.isInteger(value) || Number(value) < 0 || Number(value) > 99)
    throw new TypeError('Donor level is invalid.');
}

function assertImportBatchId(value: unknown): asserts value is string {
  if (typeof value !== 'string' || !/^import-[0-9a-f]{32}$/.test(value))
    throw new TypeError('Import batch ID is invalid.');
}

function assertContentImportSelections(value: unknown): asserts value is ContentImportSelection[] {
  if (!Array.isArray(value) || value.length > 32) throw new TypeError('Import selections are invalid.');
  const families = new Set<string>();
  for (const entry of value) {
    if (!entry || typeof entry !== 'object') throw new TypeError('Import selection is invalid.');
    const selection = entry as Record<string, unknown>;
    if (typeof selection.family !== 'string' || !selection.family || selection.family.length > 128
      || (selection.mode !== 'add' && selection.mode !== 'replace') || families.has(selection.family))
      throw new TypeError('Import selection is invalid.');
    families.add(selection.family);
  }
}

function assertContentImportReferenceChoices(
  value: unknown,
): asserts value is import('../types/ContentImport.js').ContentImportReferenceChoice[] {
  if (!Array.isArray(value) || value.length > 100_000)
    throw new TypeError('Import reference choices are invalid.');
  const keys = new Set<string>();
  for (const entry of value) {
    if (!entry || typeof entry !== 'object') throw new TypeError('Import reference choice is invalid.');
    const choice = entry as Record<string, unknown>;
    const key = JSON.stringify([choice.ownerSourceIndex, choice.fieldKey, choice.sourceValue]);
    if (!Number.isInteger(choice.ownerSourceIndex) || !Number.isInteger(choice.sourceValue)
      || typeof choice.fieldKey !== 'string' || !choice.fieldKey || choice.fieldKey.length > 512
      || (choice.action !== 'map' && choice.action !== 'clear') || keys.has(key)
      || (choice.action === 'map' && (typeof choice.targetEntityId !== 'string' || !choice.targetEntityId))
      || (choice.action === 'clear' && choice.targetEntityId !== undefined))
      throw new TypeError('Import reference choice is invalid.');
    keys.add(key);
  }
}

async function contentImportSource(
  getSettingValues: () => Promise<Record<string, string | number | boolean>>,
): Promise<{ sourceIsoPath: string; sourceFingerprint: string }> {
  const values = await getSettingValues();
  const sourceIsoPath = String(values['sources.uya.iso'] ?? '');
  const sourceFingerprint = String(values['sources.uya.fingerprint'] ?? '');
  if (!sourceIsoPath || !sourceFingerprint || !await fileExists(sourceIsoPath))
    throw new Error('Select and validate the UYA source ISO in Setup first.');
  return { sourceIsoPath, sourceFingerprint };
}

function assertEditorCommand(value: unknown): asserts value is EditorCommand {
  if (!isEditorCommand(value)) throw new TypeError('Editor command is invalid');
}

function assertInstancedCollisionSettings(
  value: unknown,
): asserts value is EditorInstancedCollisionGenerationSettings | undefined {
  if (value === undefined) return;
  if (!value || typeof value !== 'object') throw new TypeError('instanced collision settings are invalid');
  const settings = value as Record<string, unknown>;
  if (typeof settings.rawType !== 'number' || !Number.isInteger(settings.rawType)
    || settings.rawType < 0 || settings.rawType > 0xff
    || typeof settings.profileSections !== 'number' || !Number.isInteger(settings.profileSections)
    || settings.profileSections < 1 || settings.profileSections > 16
    || typeof settings.surfaceLodIndex !== 'number' || !Number.isInteger(settings.surfaceLodIndex)
    || settings.surfaceLodIndex < -1 || settings.surfaceLodIndex > 2
    || typeof settings.useHull !== 'boolean'
    || typeof settings.targetFaceRatio !== 'number' || !Number.isFinite(settings.targetFaceRatio)
    || settings.targetFaceRatio < 0.1 || settings.targetFaceRatio > 1)
    throw new TypeError('instanced collision settings are invalid');
}
