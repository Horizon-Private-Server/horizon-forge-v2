import { app, ipcMain } from 'electron';
import type { BrowserWindow } from 'electron';

import type { ForgeWindowAction } from '../types/ForgeApi.js';

interface WindowIpcHandlersOptions {
  getMainWindow: () => BrowserWindow | undefined;
  assertSender: (senderId: number) => void;
}

const actions = new Set<ForgeWindowAction>([
  'quit', 'minimize', 'toggleMaximize', 'resetZoom', 'zoomIn', 'zoomOut',
  'toggleFullscreen', 'toggleDevTools',
]);

export function registerWindowIpcHandlers(options: WindowIpcHandlersOptions): void {
  ipcMain.on('forge:window-action', (event, value: unknown) => {
    options.assertSender(event.sender.id);
    if (typeof value !== 'string' || !actions.has(value as ForgeWindowAction))
      throw new TypeError('Window action is invalid');
    const window = options.getMainWindow();
    if (!window) return;
    switch (value as ForgeWindowAction) {
      case 'quit': app.quit(); break;
      case 'minimize': window.minimize(); break;
      case 'toggleMaximize': window.isMaximized() ? window.unmaximize() : window.maximize(); break;
      case 'resetZoom': window.webContents.setZoomLevel(0); break;
      case 'zoomIn': window.webContents.setZoomLevel(window.webContents.getZoomLevel() + 0.5); break;
      case 'zoomOut': window.webContents.setZoomLevel(window.webContents.getZoomLevel() - 0.5); break;
      case 'toggleFullscreen': window.setFullScreen(!window.isFullScreen()); break;
      case 'toggleDevTools': if (!app.isPackaged) window.webContents.toggleDevTools(); break;
    }
  });
}
