import { app, BrowserWindow, protocol, session } from 'electron';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { createApplicationPaths } from '../utils/ApplicationPaths.js';
import { isAllowedNavigation } from '../utils/Security.js';
import { installApplicationMenu } from './ApplicationMenu.js';
import { registerIpcHandlers } from './IpcHandlers.js';
import { RecentProjects } from './RecentProjects.js';
import { RenderAssetProtocol } from './RenderAssetProtocol.js';
import { SettingsStore } from './Settings.js';
import { HostClient } from './bridge/HostClient.js';

const currentDirectory = path.dirname(fileURLToPath(import.meta.url));
const applicationPath = path.join(currentDirectory, '../../dist/index.html');
const applicationUrl = pathToFileURL(applicationPath).href;
const developmentHostPath = path.join(app.getAppPath(), 'src/Forge.Host/bin/Release/net10.0/Forge.Host.dll');
const hostPath = app.isPackaged
  ? path.join(process.resourcesPath, 'host/Forge.Host')
  : process.env.FORGE_HOST_PATH ?? developmentHostPath;
const host = app.isPackaged ? new HostClient(hostPath, []) : new HostClient('dotnet', [hostPath]);
let mainWindow: BrowserWindow | undefined;
let quitting = false;

protocol.registerSchemesAsPrivileged([{
  scheme: 'forge-asset',
  privileges: { secure: true, standard: true, supportFetchAPI: true, corsEnabled: true },
}]);

function createWindow(): void {
  const window = new BrowserWindow({
    width: 1280,
    height: 800,
    minWidth: 800,
    minHeight: 600,
    backgroundColor: '#11141a',
    webPreferences: {
      preload: path.join(currentDirectory, '../preload/preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  mainWindow = window;

  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, url) => {
    if (!isAllowedNavigation(url, applicationUrl)) event.preventDefault();
  });
  window.on('closed', () => {
    if (mainWindow === window) mainWindow = undefined;
  });
  void window.loadFile(applicationPath);
}

app.whenReady().then(async () => {
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
  const settings = new SettingsStore(createApplicationPaths(
    app.getPath('userData'),
    app.getPath('documents'),
    app.getPath('logs'),
  ));
  await settings.ensureFile().catch((error) => console.error('Could not initialize Forge settings', error));
  const recentProjects = new RecentProjects(path.join(settings.paths.data, 'recent-projects.json'));
  const renderAssets = new RenderAssetProtocol(settings.paths.renderCache);
  renderAssets.register();
  registerIpcHandlers({
    host,
    recentProjects,
    renderAssets,
    settings,
    getMainWindow: () => mainWindow,
  });
  installApplicationMenu((action) => mainWindow?.webContents.send('forge:action', action));
  createWindow();
  void host.start().catch((error) => console.error('Forge host failed to start', error));

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('before-quit', (event) => {
  if (quitting) return;
  event.preventDefault();
  quitting = true;
  void host.stop().finally(() => app.quit());
});
