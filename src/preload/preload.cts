import { contextBridge, ipcRenderer } from 'electron';
import type { ForgeApi, ForgeDialog, SetupProgress } from '../types/ForgeApi.js';

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
  cancelSetupOperation: () => ipcRenderer.invoke('forge:setup-cancel'),
  onSetupProgress: (listener: (progress: SetupProgress) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, progress: SetupProgress) => listener(progress);
    ipcRenderer.on('forge:setup-progress', handler);
    return () => ipcRenderer.removeListener('forge:setup-progress', handler);
  },
  onOpenDialog: (listener: (dialog: ForgeDialog) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, dialog: ForgeDialog) => listener(dialog);
    ipcRenderer.on('forge:open-dialog', handler);
    return () => ipcRenderer.removeListener('forge:open-dialog', handler);
  },
}) satisfies ForgeApi;

contextBridge.exposeInMainWorld('forge', forgeApi);
