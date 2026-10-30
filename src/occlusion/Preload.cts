import { contextBridge, ipcRenderer } from 'electron';
import type { OcclusionWorkerApi } from '../types/OcclusionWorker.js';
let cancelled = false;
ipcRenderer.on('occlusion:cancel', () => { cancelled = true; });
const api: OcclusionWorkerApi = {
    cancelled: () => cancelled,
    progress: value => ipcRenderer.send('occlusion:progress', value),
    finish: async value => { await ipcRenderer.invoke('occlusion:finish', value); },
    fail: value => ipcRenderer.send('occlusion:fail', String(value).slice(0, 8192)),
};
contextBridge.exposeInMainWorld('occlusion', api);
