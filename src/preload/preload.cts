import { contextBridge, ipcRenderer } from 'electron';
import type {
  BuildPatchProgress,
  BuildLayerId,
  AssetExplorerQuery,
  AssetPreviewKind,
  EditorCommand,
  EditorInstancedCollisionGenerationSettings,
  ForgeAction,
  ForgeApi,
  ForgeWindowAction,
  ForgeNotification,
  Progress,
  SetupProgress,
} from '../types/ForgeApi.js';

const forgeApi = Object.freeze({
  getHostStatus: () => ipcRenderer.invoke('forge:host-status'),
  echo: (message: string) => ipcRenderer.invoke('forge:echo', message),
  getSettings: () => ipcRenderer.invoke('forge:settings-get'),
  setSetting: (key: string, value: unknown) => ipcRenderer.invoke('forge:settings-set', key, value),
  resetSettings: (key?: string) => ipcRenderer.invoke('forge:settings-reset', key),
  exportSettings: (includeMachinePaths: boolean) => ipcRenderer.invoke('forge:settings-export', includeMachinePaths),
  clearRenderCache: () => ipcRenderer.invoke('forge:render-cache-clear'),
  reloadMobyDexDataset: () => ipcRenderer.invoke('forge:mobydex-reload'),
  checkForUpdates: () => ipcRenderer.invoke('forge:updates-check'),
  getNotifications: () => ipcRenderer.invoke('forge:notifications-get'),
  dismissNotification: (id: string) => ipcRenderer.invoke('forge:notifications-dismiss', id),
  runNotificationAction: (id: string) => ipcRenderer.invoke('forge:notifications-action', id),
  getSetupState: () => ipcRenderer.invoke('forge:setup-state'),
  chooseSetupDirectory: (kind: 'projects' | 'developmentIsos') => ipcRenderer.invoke('forge:setup-choose-directory', kind),
  chooseUyaSource: () => ipcRenderer.invoke('forge:setup-choose-source'),
  createDevelopmentIso: () => ipcRenderer.invoke('forge:setup-create-development-iso'),
  importUyaAssets: (force = false) => ipcRenderer.invoke('forge:setup-import-uya-assets', force),
  getProjectHub: () => ipcRenderer.invoke('forge:projects-hub'),
  preflightUyaProject: (level: number) => ipcRenderer.invoke('forge:projects-preflight-uya', level),
  createUyaProject: (name: string, level: number, allowPartial: boolean) =>
    ipcRenderer.invoke('forge:projects-create-uya', name, level, allowPartial),
  openForgeProject: () => ipcRenderer.invoke('forge:projects-open'),
  openRecentProject: (path: string) => ipcRenderer.invoke('forge:projects-open-recent', path),
  renameForgeProject: (path: string, name: string) => ipcRenderer.invoke('forge:projects-rename', path, name),
  restoreForgeProject: (path: string, recoveryId: string) => ipcRenderer.invoke('forge:projects-restore', path, recoveryId),
  repairForgeProject: (path: string) => ipcRenderer.invoke('forge:projects-repair', path),
  repairForgeProjectAssets: (path: string) => ipcRenderer.invoke('forge:projects-repair-assets', path),
  previewCatalogGarbageCollection: () => ipcRenderer.invoke('forge:catalog-gc-preview'),
  collectCatalogGarbage: (confirmationToken: string) => ipcRenderer.invoke('forge:catalog-gc-collect', confirmationToken),
  queryAssetExplorer: (query: AssetExplorerQuery) => ipcRenderer.invoke('forge:asset-explorer-query', query),
  cancelAssetExplorerQuery: () => ipcRenderer.invoke('forge:asset-explorer-cancel'),
  getAssetPreview: (assetId: string, kind: AssetPreviewKind, requestToken: string, shellIndex?: number) =>
    ipcRenderer.invoke('forge:asset-preview', assetId, kind, requestToken, shellIndex),
  cancelAssetPreview: (requestToken: string) => ipcRenderer.invoke('forge:asset-preview-cancel', requestToken),
  getAssetThumbnail: (targetGame: string, assetId: string, kind: AssetPreviewKind, shellIndex?: number) =>
    ipcRenderer.invoke('forge:asset-thumbnail', targetGame, assetId, kind, shellIndex),
  storeAssetThumbnail: (targetGame: string, assetId: string, kind: AssetPreviewKind, bytes: Uint8Array, shellIndex?: number) =>
    ipcRenderer.invoke('forge:asset-thumbnail-store', targetGame, assetId, kind, bytes, shellIndex),
  removeRecentProject: (path: string) => ipcRenderer.invoke('forge:projects-remove-recent', path),
  revealForgeProject: (path: string) => ipcRenderer.invoke('forge:projects-reveal', path),
  revealLogs: () => ipcRenderer.invoke('forge:logs-reveal'),
  openEditorProject: (path: string) => ipcRenderer.invoke('forge:editor-open', path),
  closeEditorProject: () => ipcRenderer.invoke('forge:editor-close'),
  getEditorSnapshot: () => ipcRenderer.invoke('forge:editor-query'),
  executeEditorCommand: (command: EditorCommand) => ipcRenderer.invoke('forge:editor-execute', command),
  inspectInstancedCollisionSource: (entityId: string) =>
    ipcRenderer.invoke('forge:editor-instanced-collision-inspect', entityId),
  previewInstancedCollision: (entityId: string, settings?: EditorInstancedCollisionGenerationSettings) =>
    ipcRenderer.invoke('forge:editor-instanced-collision-preview', entityId, settings),
  cancelInstancedCollisionPreview: () => ipcRenderer.invoke('forge:editor-instanced-collision-cancel'),
  applyInstancedCollisionPreview: (commandId: string, token: string) =>
    ipcRenderer.invoke('forge:editor-instanced-collision-apply', commandId, token),
  getInstancedCollisionPreviewModel: (token: string, requestToken: string) =>
    ipcRenderer.invoke('forge:editor-instanced-collision-model', token, requestToken),
  getAppliedInstancedCollisionModel: (proxyAssetId: string, requestToken: string) =>
    ipcRenderer.invoke('forge:editor-instanced-collision-applied-model', proxyAssetId, requestToken),
  saveEditorProject: () => ipcRenderer.invoke('forge:editor-save'),
  getBuildPlan: () => ipcRenderer.invoke('forge:editor-build-plan'),
  buildAndPatchProject: (includedLayers: BuildLayerId[]) =>
    ipcRenderer.invoke('forge:editor-build-patch', includedLayers),
  cancelBuildAndPatch: () => ipcRenderer.invoke('forge:editor-build-cancel'),
  runWindowAction: (action: ForgeWindowAction) => ipcRenderer.send('forge:window-action', action),
  setEditorTextInputActive: (active: boolean) => ipcRenderer.send('forge:editor-text-input', active),
  readEditorEvents: (afterSequence: number, limit = 100) =>
    ipcRenderer.invoke('forge:editor-events', afterSequence, limit),
  getEditorTerrain: () => ipcRenderer.invoke('forge:editor-terrain'),
  cancelEditorTerrain: () => ipcRenderer.invoke('forge:editor-terrain-cancel'),
  cancelSetupOperation: () => ipcRenderer.invoke('forge:setup-cancel'),
  onSetupProgress: (listener: (progress: SetupProgress) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, progress: SetupProgress) => listener(progress);
    ipcRenderer.on('forge:setup-progress', handler);
    return () => ipcRenderer.removeListener('forge:setup-progress', handler);
  },
  onEditorTerrainProgress: (listener: (progress: Progress) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, progress: Progress) => listener(progress);
    ipcRenderer.on('forge:editor-terrain-progress', handler);
    return () => ipcRenderer.removeListener('forge:editor-terrain-progress', handler);
  },
  onBuildPatchProgress: (listener: (progress: BuildPatchProgress) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, progress: BuildPatchProgress) => listener(progress);
    ipcRenderer.on('forge:editor-build-progress', handler);
    return () => ipcRenderer.removeListener('forge:editor-build-progress', handler);
  },
  onForgeAction: (listener: (action: ForgeAction) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, action: ForgeAction) => listener(action);
    ipcRenderer.on('forge:action', handler);
    return () => ipcRenderer.removeListener('forge:action', handler);
  },
  onNotificationsChanged: (listener: (notifications: ForgeNotification[]) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, notifications: ForgeNotification[]) => listener(notifications);
    ipcRenderer.on('forge:notifications-changed', handler);
    return () => ipcRenderer.removeListener('forge:notifications-changed', handler);
  },
}) satisfies ForgeApi;

contextBridge.exposeInMainWorld('forge', forgeApi);
