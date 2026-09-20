import { contextBridge, ipcRenderer } from 'electron';

contextBridge.exposeInMainWorld('forge', Object.freeze({
  getHostStatus: () => ipcRenderer.invoke('forge:host-status'),
  getTfragUrl: () => ipcRenderer.invoke('forge:tfrag-url'),
  echo: (message: string) => ipcRenderer.invoke('forge:echo', message),
}));
