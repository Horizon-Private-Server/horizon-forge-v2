import { contextBridge, ipcRenderer } from 'electron';
import type { EditorCommand, ForgeAction, ForgeApi, Progress, SetupProgress } from '../types/ForgeApi.js';

const forgeApi = Object.freeze({
  getHostStatus: () => ipcRenderer.invoke('forge:host-status'),
  echo: (message: string) => ipcRenderer.invoke('forge:echo', message),
  getSettings: () => ipcRenderer.invoke('forge:settings-get'),
  setSetting: (key: string, value: unknown) => ipcRenderer.invoke('forge:settings-set', key, value),
  resetSettings: (key?: string) => ipcRenderer.invoke('forge:settings-reset', key),
  exportSettings: (includeMachinePaths: boolean) => ipcRenderer.invoke('forge:settings-export', includeMachinePaths),
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
  migrateForgeProject: (path: string) => ipcRenderer.invoke('forge:projects-migrate', path),
  repairForgeProjectAssets: (path: string) => ipcRenderer.invoke('forge:projects-repair-assets', path),
  previewCatalogGarbageCollection: () => ipcRenderer.invoke('forge:catalog-gc-preview'),
  collectCatalogGarbage: (confirmationToken: string) => ipcRenderer.invoke('forge:catalog-gc-collect', confirmationToken),
  removeRecentProject: (path: string) => ipcRenderer.invoke('forge:projects-remove-recent', path),
  revealForgeProject: (path: string) => ipcRenderer.invoke('forge:projects-reveal', path),
  revealLogs: () => ipcRenderer.invoke('forge:logs-reveal'),
  openEditorProject: (path: string) => ipcRenderer.invoke('forge:editor-open', path),
  closeEditorProject: () => ipcRenderer.invoke('forge:editor-close'),
  getEditorSnapshot: () => ipcRenderer.invoke('forge:editor-query'),
  executeEditorCommand: (command: EditorCommand) => ipcRenderer.invoke('forge:editor-execute', command),
  saveEditorProject: () => ipcRenderer.invoke('forge:editor-save'),
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
  onForgeAction: (listener: (action: ForgeAction) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, action: ForgeAction) => listener(action);
    ipcRenderer.on('forge:action', handler);
    return () => ipcRenderer.removeListener('forge:action', handler);
  },
}) satisfies ForgeApi;

contextBridge.exposeInMainWorld('forge', forgeApi);
