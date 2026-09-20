import { app, BrowserWindow, dialog, ipcMain, net, protocol, session } from 'electron';
import { mkdir, realpath } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { HostClient } from './bridge/HostClient.js';
import { installApplicationMenu } from './ApplicationMenu.js';
import { createApplicationPaths } from '../utils/ApplicationPaths.js';
import { availableBytes, fileExists, writeJsonSafely } from '../utils/FileSystem.js';
import { isAllowedNavigation, isPathInside, isTrustedSender } from '../utils/Security.js';
import { SettingsStore } from './Settings.js';

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
let settings: SettingsStore | undefined;
let activeSetupRequestId: number | undefined;

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
  settings = new SettingsStore(createApplicationPaths(
    app.getPath('userData'),
    app.getPath('documents'),
    app.getPath('logs'),
  ));
  await settings.ensureFile().catch((error) => console.error('Could not initialize Forge settings', error));
  registerHostHandlers();
  installApplicationMenu((dialog) => mainWindow?.webContents.send('forge:open-dialog', dialog));
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
  ipcMain.handle('forge:settings-get', (event) => {
    assertTrustedSender(event.sender.id);
    return getSettings().getSnapshot();
  });
  ipcMain.handle('forge:settings-set', (event, key: unknown, value: unknown) => {
    assertTrustedSender(event.sender.id);
    if (typeof key !== 'string') throw new TypeError('Setting key must be a string');
    return getSettings().set(key, value);
  });
  ipcMain.handle('forge:settings-reset', (event, key?: unknown) => {
    assertTrustedSender(event.sender.id);
    if (key !== undefined && typeof key !== 'string') throw new TypeError('Setting key must be a string');
    return getSettings().reset(key);
  });
  ipcMain.handle('forge:settings-export', async (event, includeMachinePaths: unknown) => {
    assertTrustedSender(event.sender.id);
    if (typeof includeMachinePaths !== 'boolean') throw new TypeError('Export option must be a boolean');
    const options = {
      title: 'Export Forge settings',
      defaultPath: path.join(app.getPath('documents'), 'horizon-forge-settings.json'),
      filters: [{ name: 'JSON', extensions: ['json'] }],
    };
    const selection = mainWindow
      ? await dialog.showSaveDialog(mainWindow, options)
      : await dialog.showSaveDialog(options);
    if (selection.canceled || !selection.filePath) return false;
    await writeJsonSafely(selection.filePath, await getSettings().export(includeMachinePaths));
    return true;
  });
  ipcMain.handle('forge:setup-state', async (event) => {
    assertTrustedSender(event.sender.id);
    return getSetupState();
  });
  ipcMain.handle('forge:setup-choose-directory', async (event, kind: unknown) => {
    assertTrustedSender(event.sender.id);
    if (activeSetupRequestId !== undefined) throw new Error('Another setup operation is already running');
    if (kind !== 'projects' && kind !== 'developmentIsos') throw new TypeError('Invalid setup directory');
    const selection = mainWindow
      ? await dialog.showOpenDialog(mainWindow, { properties: ['openDirectory', 'createDirectory'] })
      : await dialog.showOpenDialog({ properties: ['openDirectory', 'createDirectory'] });
    if (selection.canceled || !selection.filePaths[0]) return getSetupState();
    await getSettings().set(kind === 'projects' ? 'paths.projects' : 'paths.developmentIsos', selection.filePaths[0]);
    return getSetupState();
  });
  ipcMain.handle('forge:setup-choose-source', async (event) => {
    assertTrustedSender(event.sender.id);
    if (activeSetupRequestId !== undefined) throw new Error('Another setup operation is already running');
    const selection = mainWindow
      ? await dialog.showOpenDialog(mainWindow, { properties: ['openFile'], filters: [{ name: 'PlayStation 2 ISO', extensions: ['iso'] }] })
      : await dialog.showOpenDialog({ properties: ['openFile'], filters: [{ name: 'PlayStation 2 ISO', extensions: ['iso'] }] });
    if (selection.canceled || !selection.filePaths[0]) return undefined;
    const sourcePath = selection.filePaths[0];
    activeSetupRequestId = 0;
    try {
      const request = await host.validateUyaIso(sourcePath, (progress) => {
        event.sender.send('forge:setup-progress', { operation: 'validate', ...progress });
      });
      activeSetupRequestId = request.requestId;
      const identity = await request.result;
      if (identity.isSupported) {
        await getSettings().setMany({
          'sources.uya.iso': sourcePath,
          'sources.uya.game': identity.game,
          'sources.uya.region': identity.region,
          'sources.uya.revision': identity.revision,
          'sources.uya.serial': identity.serial,
          'sources.uya.size': identity.size,
          'sources.uya.fingerprint': identity.fingerprint,
          'targets.uya.developmentIso': '',
        });
      }
      return { path: sourcePath, identity };
    } finally {
      activeSetupRequestId = undefined;
    }
  });
  ipcMain.handle('forge:setup-create-development-iso', async (event) => {
    assertTrustedSender(event.sender.id);
    if (activeSetupRequestId !== undefined) throw new Error('Another setup operation is already running');
    const values = await getSettingValues();
    const sourcePath = String(values['sources.uya.iso'] ?? '');
    const fingerprint = String(values['sources.uya.fingerprint'] ?? '');
    const destination = String(values['paths.developmentIsos'] ?? '');
    if (!sourcePath || !fingerprint || !destination) throw new Error('Select and validate the source ISO and destination first.');
    await mkdir(destination, { recursive: true });
    const targetPath = path.join(destination, 'Ratchet & Clank - Up Your Arsenal - Forge Development.iso');
    const overwrite = await fileExists(targetPath);
    if (overwrite) {
      const confirmation = mainWindow
        ? await dialog.showMessageBox(mainWindow, {
          type: 'warning',
          buttons: ['Cancel', 'Replace development ISO'],
          defaultId: 0,
          cancelId: 0,
          title: 'Replace development ISO?',
          message: 'The existing development ISO will be replaced only after the new copy is fully verified.',
          detail: targetPath,
        })
        : { response: 0 };
      if (confirmation.response !== 1) return undefined;
    }
    activeSetupRequestId = 0;
    try {
      const request = await host.createDevelopmentIso(sourcePath, targetPath, fingerprint, overwrite, (progress) => {
        event.sender.send('forge:setup-progress', { operation: 'copy', ...progress });
      });
      activeSetupRequestId = request.requestId;
      const result = await request.result;
      await getSettings().setMany({ 'targets.uya.developmentIso': result.path });
      return result;
    } finally {
      activeSetupRequestId = undefined;
    }
  });
  ipcMain.handle('forge:setup-cancel', async (event) => {
    assertTrustedSender(event.sender.id);
    if (activeSetupRequestId) await host.cancel(activeSetupRequestId);
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

function getSettings(): SettingsStore {
  if (!settings) throw new Error('Settings are not initialized');
  return settings;
}

async function getSettingValues(): Promise<Record<string, string | number | boolean>> {
  const snapshot = await getSettings().getSnapshot();
  return Object.fromEntries(snapshot.entries.map((entry) => [entry.key, entry.value]));
}

async function getSetupState() {
  const values = await getSettingValues();
  const sourceIso = String(values['sources.uya.iso'] ?? '');
  const developmentIso = String(values['targets.uya.developmentIso'] ?? '');
  const developmentIsoDirectory = String(values['paths.developmentIsos'] ?? '');
  const sourceExists = sourceIso ? await fileExists(sourceIso) : false;
  const targetExists = developmentIso ? await fileExists(developmentIso) : false;
  const sourceVerified = sourceExists
    && values['sources.uya.game'] === 'UYA'
    && values['sources.uya.region'] === 'NTSC-U'
    && typeof values['sources.uya.fingerprint'] === 'string'
    && values['sources.uya.fingerprint'].length === 32;
  return {
    required: !sourceVerified || !targetExists,
    projectsDirectory: String(values['paths.projects'] ?? ''),
    developmentIsoDirectory,
    sourceIso,
    developmentIso,
    importUyaAssets: Boolean(values['imports.uya.enabled']),
    source: sourceIso ? {
      game: String(values['sources.uya.game'] ?? ''),
      region: String(values['sources.uya.region'] ?? ''),
      revision: String(values['sources.uya.revision'] ?? ''),
      serial: String(values['sources.uya.serial'] ?? ''),
      size: Number(values['sources.uya.size'] ?? 0),
      fingerprint: String(values['sources.uya.fingerprint'] ?? ''),
    } : undefined,
    availableBytes: await availableBytes(developmentIsoDirectory),
  };
}
