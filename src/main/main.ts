import { app, BrowserWindow, ipcMain, net, protocol, session } from 'electron';
import { realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { HostClient } from './bridge/HostClient.js';
import { isAllowedNavigation, isPathInside, isTrustedSender } from './Security.js';

protocol.registerSchemesAsPrivileged([{
  scheme: 'forge-asset',
  privileges: { secure: true, standard: true, supportFetchAPI: true, corsEnabled: true },
}]);

const currentDirectory = path.dirname(fileURLToPath(import.meta.url));
const applicationPath = path.join(currentDirectory, '../../dist/index.html');
const applicationUrl = pathToFileURL(applicationPath).href;
const developmentHostPath = path.join(app.getAppPath(), 'src/Forge.Host/bin/Release/net10.0/Forge.Host.dll');
const hostPath = app.isPackaged
  ? path.join(process.resourcesPath, 'host/Forge.Host')
  : process.env.FORGE_HOST_PATH ?? developmentHostPath;
const host = app.isPackaged ? new HostClient(hostPath, []) : new HostClient('dotnet', [hostPath]);
const developmentTfragPath = path.resolve(
  app.getAppPath(),
  '../ratchet-ps2-cli/test-assets/tfrags/_viewer/UYA/level3/terrain/terrain.gltf',
);
const tfragPath = process.env.FORGE_TFRAG_PATH ?? (app.isPackaged ? undefined : developmentTfragPath);
let mainWindow: BrowserWindow | undefined;
let tfragUrl: string | undefined;

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

app.whenReady().then(() => {
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
  registerHostHandlers();
  void registerTfragProtocol().finally(createWindow);
  void host.start().catch((error) => console.error('Forge host failed to start', error));

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('before-quit', () => {
  void host.stop();
});

function registerHostHandlers(): void {
  ipcMain.handle('forge:host-status', async (event) => {
    assertTrustedSender(event.sender.id);
    return host.start();
  });
  ipcMain.handle('forge:echo', async (event, message: unknown) => {
    assertTrustedSender(event.sender.id);
    if (typeof message !== 'string') throw new TypeError('Echo message must be a string');
    const request = await host.echo(message);
    return request.result;
  });
  ipcMain.handle('forge:tfrag-url', (event) => {
    assertTrustedSender(event.sender.id);
    return tfragUrl;
  });
}

async function registerTfragProtocol(): Promise<void> {
  if (!tfragPath) return;

  try {
    const modelPath = await realpath(tfragPath);
    const root = await realpath(path.dirname(modelPath));
    tfragUrl = `forge-asset://local/${encodeURIComponent(path.basename(modelPath))}`;

    protocol.handle('forge-asset', async (request) => {
      if (request.method !== 'GET') return new Response(null, { status: 405 });

      try {
        const url = new URL(request.url);
        if (url.hostname !== 'local') return new Response(null, { status: 404 });
        const candidate = await realpath(path.resolve(root, decodeURIComponent(url.pathname).replace(/^\/+/, '')));
        if (!isPathInside(root, candidate)) return new Response(null, { status: 404 });
        return net.fetch(pathToFileURL(candidate).href);
      } catch (error) {
        console.warn(`Could not serve ${request.url}`, error);
        return new Response(null, { status: 404 });
      }
    });
  } catch (error) {
    console.warn(`Tfrag fixture is unavailable at ${tfragPath}`, error);
  }
}

function assertTrustedSender(senderId: number): void {
  if (!isTrustedSender(senderId, mainWindow?.webContents.id)) throw new Error('Untrusted IPC sender');
}
