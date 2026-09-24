import { app, BrowserWindow, protocol, session } from 'electron';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { createApplicationPaths } from '../utils/ApplicationPaths.js';
import { applicationTitle } from '../utils/ApplicationTitle.js';
import { isAllowedNavigation } from '../utils/Security.js';
import type { UpdateChannel } from '../types/Updates.js';
import { installApplicationMenu, isEditorDirty } from './ApplicationMenu.js';
import { registerIpcHandlers } from './IpcHandlers.js';
import { NotificationCenter } from './NotificationCenter.js';
import { RecentProjects } from './RecentProjects.js';
import { RenderAssetProtocol } from './RenderAssetProtocol.js';
import { SettingsStore } from './Settings.js';
import { UpdateService } from './UpdateService.js';
import { HostClient } from './bridge/HostClient.js';

const currentDirectory = path.dirname(fileURLToPath(import.meta.url));
const applicationPath = path.join(currentDirectory, '../../dist/index.html');
const applicationUrl = pathToFileURL(applicationPath).href;
const developmentHostPath = path.join(app.getAppPath(), 'src/Forge.Host/bin/Release/net10.0/Forge.Host.dll');
const packagedHostName = process.platform === 'win32' ? 'Forge.Host.exe' : 'Forge.Host';
const hostPath = app.isPackaged
  ? path.join(process.resourcesPath, 'host', packagedHostName)
  : process.env.FORGE_HOST_PATH ?? developmentHostPath;
const host = app.isPackaged ? new HostClient(hostPath, []) : new HostClient('dotnet', [hostPath]);
let mainWindow: BrowserWindow | undefined;
let quitting = false;

protocol.registerSchemesAsPrivileged([{
  scheme: 'forge-asset',
  privileges: { secure: true, standard: true, supportFetchAPI: true, corsEnabled: true },
}]);

function createWindow(installedChannel?: UpdateChannel): void {
  const window = new BrowserWindow({
    title: applicationTitle(app.isPackaged, app.getVersion(), installedChannel),
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
  window.maximize();
  window.setMenuBarVisibility(false);

  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.on('page-title-updated', (event) => event.preventDefault());
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
  const notifications = new NotificationCenter(() => mainWindow);
  const updates = await UpdateService.create({
    version: app.getVersion(),
    getMainWindow: () => mainWindow,
    isProjectDirty: isEditorDirty,
    notifications,
  }, path.join(app.getAppPath(), 'package.json'));
  const settings = new SettingsStore(createApplicationPaths(
    app.getPath('userData'),
    app.getPath('documents'),
    app.getPath('logs'),
  ), updates.installedChannel ?? 'stable');
  await settings.ensureFile().catch((error) => console.error('Could not initialize Forge settings', error));
  const recentProjects = new RecentProjects(path.join(settings.paths.data, 'recent-projects.json'));
  const renderAssets = new RenderAssetProtocol(settings.paths.renderCache);
  renderAssets.register();
  registerIpcHandlers({
    host,
    notifications,
    recentProjects,
    renderAssets,
    settings,
    updates,
    getMainWindow: () => mainWindow,
  });
  installApplicationMenu((action) => mainWindow?.webContents.send('forge:action', action));
  createWindow(updates.installedChannel);
  void host.start().catch((error) => console.error('Forge host failed to start', error));
  if (app.isPackaged) {
    const checkForUpdates = async () => {
      const snapshot = await settings.getSnapshot();
      const enabled = snapshot.entries.find((entry) => entry.key === 'updates.automaticChecks')?.value === true;
      const channel = snapshot.entries.find((entry) => entry.key === 'updates.channel')?.value;
      if (enabled && (channel === 'stable' || channel === 'nightly')) await updates.checkAndPrompt(false, channel);
    };
    const runUpdateCheck = () => void checkForUpdates().catch((error) => console.warn('Could not read update settings', error));
    setTimeout(runUpdateCheck, 30_000).unref();
    setInterval(runUpdateCheck, 6 * 60 * 60_000).unref();
  }

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow(updates.installedChannel);
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
