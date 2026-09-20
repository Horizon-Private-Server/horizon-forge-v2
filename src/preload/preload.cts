import { contextBridge, ipcRenderer } from 'electron';
import type { ForgeAction, ForgeApi, SetupProgress } from '../types/ForgeApi.js';

const forgeApi = Object.freeze({
  getHostStatus: () => ipcRenderer.invoke('forge:host-status'),
  getTfragUrl: () => ipcRenderer.invoke('forge:tfrag-url'),
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
  removeRecentProject: (path: string) => ipcRenderer.invoke('forge:projects-remove-recent', path),
  revealForgeProject: (path: string) => ipcRenderer.invoke('forge:projects-reveal', path),
  cancelSetupOperation: () => ipcRenderer.invoke('forge:setup-cancel'),
  onSetupProgress: (listener: (progress: SetupProgress) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, progress: SetupProgress) => listener(progress);
    ipcRenderer.on('forge:setup-progress', handler);
    return () => ipcRenderer.removeListener('forge:setup-progress', handler);
  },
  onForgeAction: (listener: (action: ForgeAction) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, action: ForgeAction) => listener(action);
    ipcRenderer.on('forge:action', handler);
    return () => ipcRenderer.removeListener('forge:action', handler);
  },
}) satisfies ForgeApi;

contextBridge.exposeInMainWorld('forge', forgeApi);
