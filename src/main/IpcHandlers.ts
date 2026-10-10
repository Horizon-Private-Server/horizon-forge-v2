import { app, dialog, ipcMain, shell } from 'electron';
import type { BrowserWindow } from 'electron';
import { mkdir, open } from 'node:fs/promises';
import path from 'node:path';

import type { AssetExplorerQuery, ProjectHubState } from '../types/ForgeApi.js';
import { safeProjectDirectoryName } from '../utils/ApplicationPaths.js';
import { showOpenDialog, showSaveDialog } from '../utils/ElectronDialogs.js';
import { errorMessage } from '../utils/Errors.js';
import { availableBytes, fileExists, writeJsonSafely } from '../utils/FileSystem.js';
import { isTrustedSender } from '../utils/Security.js';
import type { RecentProjects } from './RecentProjects.js';
import type { NotificationCenter } from './NotificationCenter.js';
import { registerNotificationIpcHandlers } from './NotificationIpcHandlers.js';
import { registerBuildIpcHandlers } from './BuildIpcHandlers.js';
import { registerEditorIpcHandlers } from './EditorIpcHandlers.js';
import type { RenderAssetProtocol } from './RenderAssetProtocol.js';
import { registerRenderIpcHandlers } from './RenderIpcHandlers.js';
import { clearRenderCache, type SettingsStore } from './Settings.js';
import type { UpdateService } from './UpdateService.js';
import { registerUpdateIpcHandlers } from './UpdateIpcHandlers.js';
import { registerWindowIpcHandlers } from './WindowIpcHandlers.js';
import type { HostClient } from './bridge/HostClient.js';

interface IpcHandlersOptions {
  host: HostClient;
  notifications: NotificationCenter;
  recentProjects: RecentProjects;
  settings: SettingsStore;
  updates: UpdateService;
  renderAssets: RenderAssetProtocol;
  getMainWindow: () => BrowserWindow | undefined;
}

const uyaImportVersion = 2;
const maximumMobyDexDatasetBytes = 16 * 1024 * 1024;

export function registerIpcHandlers(options: IpcHandlersOptions): void {
  const { host, notifications, recentProjects, settings, updates, renderAssets, getMainWindow } = options;
  let activeSetupRequestId: number | undefined;
  let activeAssetExplorerRequestId: number | undefined;

  function assertSender(senderId: number): void {
    if (!isTrustedSender(senderId, getMainWindow()?.webContents.id)) throw new Error('Untrusted IPC sender');
  }

  function assertAssetExplorerQuery(value: unknown): asserts value is AssetExplorerQuery {
    if (!value || typeof value !== 'object') throw new TypeError('Asset explorer query is invalid');
    const query = value as Record<string, unknown>;
    if (!['ties', 'shrubs', 'mobys', 'skyShells', 'textures'].includes(String(query.category)))
      throw new TypeError('Asset explorer category is invalid');
    for (const field of ['search', 'game', 'level', 'region', 'revision']) {
      const text = query[field];
      if (text !== undefined && (typeof text !== 'string' || text.length > 4_096))
        throw new TypeError(`Asset explorer ${field} is invalid`);
    }
    if (query.cursor !== undefined && (typeof query.cursor !== 'string' || query.cursor.length > 512))
      throw new TypeError('Asset explorer cursor is invalid');
    if (query.tags !== undefined && (!Array.isArray(query.tags) || query.tags.length > 64
      || query.tags.some((tag) => typeof tag !== 'string' || !tag.trim() || tag.length > 4_096)))
      throw new TypeError('Asset explorer tags are invalid');
    if (query.limit !== undefined && (!Number.isInteger(query.limit) || Number(query.limit) < 1
      || Number(query.limit) > 128)) throw new TypeError('Asset explorer batch size is invalid');
  }

  const getActiveEditorSnapshot = registerEditorIpcHandlers({
    host,
    settings,
    assertSender,
    getSettingValues,
    resolveProjectPath: assertRecentProject,
    cancelAssetExplorer: async () => {
      if (activeAssetExplorerRequestId !== undefined) await host.cancel(activeAssetExplorerRequestId);
    },
  });
  registerRenderIpcHandlers({
    host,
    settings,
    renderAssets,
    getEditorTargetGame: () => getActiveEditorSnapshot()?.target.game,
    getEditorProjectPath: () => getActiveEditorSnapshot()?.projectPath,
    assertSender,
  });
  registerBuildIpcHandlers({ host, settings, getMainWindow, assertSender });
  registerWindowIpcHandlers({ getMainWindow, assertSender });
  registerNotificationIpcHandlers(notifications, assertSender);
  registerUpdateIpcHandlers(updates, settings, assertSender);

  async function getSettingValues(): Promise<Record<string, string | number | boolean>> {
    const snapshot = await settings.getSnapshot();
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
    const importUyaAssets = Boolean(values['imports.uya.enabled']);
    const assetImportComplete = sourceVerified
      && values['imports.uya.completedFingerprint'] === values['sources.uya.fingerprint']
      && values['imports.uya.completedVersion'] === uyaImportVersion;
    return {
      required: !sourceVerified || !targetExists || (importUyaAssets && !assetImportComplete),
      projectsDirectory: String(values['paths.projects'] ?? ''),
      developmentIsoDirectory,
      sourceIso,
      developmentIso,
      developmentIsoReady: targetExists,
      importUyaAssets,
      assetImportComplete,
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

  async function getProjectHubState(): Promise<ProjectHubState> {
    const values = await getSettingValues();
    const sourceIsoPath = String(values['sources.uya.iso'] ?? '');
    const projectsDirectory = String(values['paths.projects'] ?? '');
    let diagnostic: string | undefined;
    let creation: ProjectHubState['creation'];
    if (!sourceIsoPath || values['imports.uya.completedFingerprint'] !== values['sources.uya.fingerprint']) {
      diagnostic = 'Complete the UYA source and global asset import in Setup before creating a project.';
    } else {
      try {
        creation = await (await host.listUyaProjectLevels(sourceIsoPath)).result;
      } catch (error) {
        diagnostic = errorMessage(error);
      }
    }

    let paths: string[] = [];
    try {
      paths = await recentProjects.list();
    } catch (error) {
      diagnostic = errorMessage(error);
    }
    const recent = await Promise.all(paths.map(async (projectPath) => {
      try {
        const project = await (await host.inspectForgeProjectSummary(projectPath)).result;
        return { path: projectPath, project };
      } catch (error) {
        return { path: projectPath, error: errorMessage(error) };
      }
    }));
    return { projectsDirectory, creation, recentProjects: recent, diagnostic };
  }

  async function openProject(projectPath: string) {
    const project = await (await host.inspectForgeProject(projectPath, settings.paths.assets)).result;
    await recentProjects.add(project.path);
    return project;
  }

  async function assertRecentProject(value: unknown): Promise<string> {
    if (typeof value !== 'string' || !path.isAbsolute(value)) throw new TypeError('Project path is invalid');
    const resolved = path.resolve(value);
    if (!(await recentProjects.list()).includes(resolved)) throw new Error('Project is not in the recent-project list.');
    return resolved;
  }

  async function getKnownProjectRoots(): Promise<string[]> {
    const values = await getSettingValues();
    return [...new Set([
      String(values['paths.projects'] ?? ''),
      ...await recentProjects.list(),
    ].filter(Boolean).map((value) => path.resolve(value)))];
  }

  ipcMain.handle('forge:host-status', async (event) => {
    assertSender(event.sender.id);
    return host.start();
  });
  ipcMain.handle('forge:echo', async (event, message: unknown) => {
    assertSender(event.sender.id);
    if (typeof message !== 'string') throw new TypeError('Echo message must be a string');
    const request = await host.echo(message);
    return request.result;
  });
  ipcMain.handle('forge:settings-get', (event) => {
    assertSender(event.sender.id);
    return settings.getSnapshot();
  });
  ipcMain.handle('forge:settings-set', (event, key: unknown, value: unknown) => {
    assertSender(event.sender.id);
    if (typeof key !== 'string') throw new TypeError('Setting key must be a string');
    return settings.set(key, value);
  });
  ipcMain.handle('forge:settings-reset', (event, key?: unknown) => {
    assertSender(event.sender.id);
    if (key !== undefined && typeof key !== 'string') throw new TypeError('Setting key must be a string');
    return settings.reset(key);
  });
  ipcMain.handle('forge:settings-export', async (event, includeMachinePaths: unknown) => {
    assertSender(event.sender.id);
    if (typeof includeMachinePaths !== 'boolean') throw new TypeError('Export option must be a boolean');
    const selection = await showSaveDialog(getMainWindow(), {
      title: 'Export Forge settings',
      defaultPath: path.join(app.getPath('documents'), 'horizon-forge-settings.json'),
      filters: [{ name: 'JSON', extensions: ['json'] }],
    });
    if (selection.canceled || !selection.filePath) return false;
    await writeJsonSafely(selection.filePath, await settings.export(includeMachinePaths));
    return true;
  });
  ipcMain.handle('forge:render-cache-clear', async (event) => {
    assertSender(event.sender.id);
    const window = getMainWindow();
    const confirmation = window ? await dialog.showMessageBox(window, {
      type: 'warning',
      buttons: ['Cancel', 'Clear render cache'],
      defaultId: 0,
      cancelId: 0,
      title: 'Clear render cache?',
      message: 'Clear generated previews and thumbnails?',
      detail: 'Imported assets and projects will not be removed. Forge will regenerate previews as needed.',
    }) : { response: 0 };
    if (confirmation.response !== 1) return false;
    await clearRenderCache(settings.paths);
    return true;
  });
  ipcMain.handle('forge:mobydex-reload', async (event) => {
    assertSender(event.sender.id);
    const selection = await showOpenDialog(getMainWindow(), {
      title: 'Reload MobyDex dataset',
      properties: ['openFile'],
      filters: [{ name: 'MobyDex dataset', extensions: ['json'] }],
    });
    if (selection.canceled || !selection.filePaths[0]) return undefined;
    const bytes = await readBoundedFile(selection.filePaths[0], maximumMobyDexDatasetBytes);
    const message = await (await host.reloadMobyDexDataset(bytes)).result;
    notifications.publish({
      id: 'mobydex-reloaded',
      title: 'MobyDex reloaded',
      message,
      severity: 'info',
      createdUnixMilliseconds: Date.now(),
    });
    return message;
  });
  ipcMain.handle('forge:setup-state', async (event) => {
    assertSender(event.sender.id);
    return getSetupState();
  });
  ipcMain.handle('forge:setup-choose-directory', async (event, kind: unknown) => {
    assertSender(event.sender.id);
    if (activeSetupRequestId !== undefined) throw new Error('Another setup operation is already running');
    if (kind !== 'projects' && kind !== 'developmentIsos') throw new TypeError('Invalid setup directory');
    const selection = await showOpenDialog(getMainWindow(), { properties: ['openDirectory', 'createDirectory'] });
    if (selection.canceled || !selection.filePaths[0]) return getSetupState();
    await settings.set(kind === 'projects' ? 'paths.projects' : 'paths.developmentIsos', selection.filePaths[0]);
    return getSetupState();
  });
  ipcMain.handle('forge:setup-choose-source', async (event) => {
    assertSender(event.sender.id);
    if (activeSetupRequestId !== undefined) throw new Error('Another setup operation is already running');
    const selection = await showOpenDialog(getMainWindow(), {
      properties: ['openFile'],
      filters: [{ name: 'PlayStation 2 ISO', extensions: ['iso'] }],
    });
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
        const values = await getSettingValues();
        const sameSource = String(values['sources.uya.fingerprint'] ?? '') === identity.fingerprint;
        await settings.setMany({
          'sources.uya.iso': sourcePath,
          'sources.uya.game': identity.game,
          'sources.uya.region': identity.region,
          'sources.uya.revision': identity.revision,
          'sources.uya.serial': identity.serial,
          'sources.uya.size': identity.size,
          'sources.uya.fingerprint': identity.fingerprint,
          ...sameSource ? {} : {
            'targets.uya.developmentIso': '',
            'imports.uya.completedFingerprint': '',
            'imports.uya.completedVersion': 0,
          },
        });
      }
      return { path: sourcePath, identity };
    } finally {
      activeSetupRequestId = undefined;
    }
  });
  ipcMain.handle('forge:setup-create-development-iso', async (event) => {
    assertSender(event.sender.id);
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
      const window = getMainWindow();
      const confirmation = window
        ? await dialog.showMessageBox(window, {
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
      await settings.setMany({ 'targets.uya.developmentIso': result.path });
      return result;
    } finally {
      activeSetupRequestId = undefined;
    }
  });
  ipcMain.handle('forge:setup-import-uya-assets', async (event, force: unknown) => {
    assertSender(event.sender.id);
    if (typeof force !== 'boolean') throw new TypeError('Asset re-import option is invalid');
    if (activeSetupRequestId !== undefined) throw new Error('Another setup operation is already running');
    const values = await getSettingValues();
    const sourceIsoPath = String(values['sources.uya.iso'] ?? '');
    const fingerprint = String(values['sources.uya.fingerprint'] ?? '');
    const revision = String(values['sources.uya.revision'] ?? '');
    if (!sourceIsoPath || !fingerprint || !revision) throw new Error('Select and validate the source ISO first.');

    activeSetupRequestId = 0;
    try {
      const request = await host.importUyaAssets(
        sourceIsoPath,
        settings.paths.assets,
        fingerprint,
        revision,
        (progress) => event.sender.send('forge:setup-progress', { operation: 'import', ...progress }),
        force,
      );
      activeSetupRequestId = request.requestId;
      const result = await request.result;
      await settings.setMany({
        'imports.uya.completedFingerprint': fingerprint,
        'imports.uya.completedVersion': uyaImportVersion,
      });
      return result;
    } finally {
      activeSetupRequestId = undefined;
    }
  });
  ipcMain.handle('forge:setup-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activeSetupRequestId !== undefined) await host.cancel(activeSetupRequestId);
  });
  ipcMain.handle('forge:projects-hub', async (event) => {
    assertSender(event.sender.id);
    return getProjectHubState();
  });
  ipcMain.handle('forge:projects-preflight-uya', async (event, level: unknown) => {
    assertSender(event.sender.id);
    if (!Number.isInteger(level) || Number(level) < 0 || Number(level) > 99) throw new TypeError('Base level is invalid');
    const values = await getSettingValues();
    const sourceIsoPath = String(values['sources.uya.iso'] ?? '');
    if (!sourceIsoPath || values['imports.uya.completedFingerprint'] !== values['sources.uya.fingerprint']) {
      throw new Error('Complete the UYA global asset import before creating a project.');
    }
    return (await host.preflightUyaProject(sourceIsoPath, settings.paths.assets, Number(level))).result;
  });
  ipcMain.handle('forge:projects-create-uya', async (event, name: unknown, level: unknown, allowPartial: unknown) => {
    assertSender(event.sender.id);
    if (typeof name !== 'string' || !name.trim() || name.length > 256) throw new TypeError('Project name is invalid');
    if (!Number.isInteger(level) || Number(level) < 0 || Number(level) > 99) throw new TypeError('Base level is invalid');
    if (typeof allowPartial !== 'boolean') throw new TypeError('Partial-content confirmation is invalid');
    const values = await getSettingValues();
    const projectsDirectory = String(values['paths.projects'] ?? '');
    const sourceIsoPath = String(values['sources.uya.iso'] ?? '');
    const fingerprint = String(values['sources.uya.fingerprint'] ?? '');
    const revision = String(values['sources.uya.revision'] ?? '');
    if (!projectsDirectory || !sourceIsoPath || !fingerprint || !revision) throw new Error('Complete UYA setup before creating a project.');
    if (values['imports.uya.completedFingerprint'] !== fingerprint) throw new Error('Complete the UYA global asset import before creating a project.');
    await mkdir(projectsDirectory, { recursive: true });
    const selection = await showSaveDialog(getMainWindow(), {
      title: 'Create Forge project',
      buttonLabel: 'Create project',
      defaultPath: path.join(projectsDirectory, safeProjectDirectoryName(name)),
    });
    if (selection.canceled || !selection.filePath) return undefined;
    const request = await host.createUyaProject({
      sourceIsoPath,
      catalogRootPath: settings.paths.assets,
      projectPath: selection.filePath,
      name: name.trim(),
      fingerprint,
      revision,
      level: Number(level),
      allowPartial,
    });
    const project = await request.result;
    await recentProjects.add(project.path);
    return project;
  });
  ipcMain.handle('forge:projects-open', async (event) => {
    assertSender(event.sender.id);
    const selection = await showOpenDialog(getMainWindow(), {
      title: 'Open Forge project',
      properties: ['openFile'],
      filters: [{ name: 'Forge project', extensions: ['json'] }],
    });
    if (selection.canceled || !selection.filePaths[0]) return undefined;
    if (path.basename(selection.filePaths[0]) !== 'forge-project.json') throw new Error('Select a forge-project.json manifest.');
    return openProject(path.dirname(selection.filePaths[0]));
  });
  ipcMain.handle('forge:projects-open-recent', async (event, projectPath: unknown) => {
    assertSender(event.sender.id);
    return openProject(await assertRecentProject(projectPath));
  });
  ipcMain.handle('forge:projects-rename', async (event, projectPath: unknown, name: unknown) => {
    assertSender(event.sender.id);
    const resolved = await assertRecentProject(projectPath);
    if (typeof name !== 'string' || !name.trim() || name.length > 256) throw new TypeError('Project name is invalid');
    const request = await host.renameForgeProject(resolved, settings.paths.assets, name.trim());
    const project = await request.result;
    await recentProjects.add(project.path);
    return project;
  });
  ipcMain.handle('forge:projects-restore', async (event, projectPath: unknown, recoveryId: unknown) => {
    assertSender(event.sender.id);
    const resolved = await assertRecentProject(projectPath);
    if (typeof recoveryId !== 'string') throw new TypeError('Recovery ID is invalid');
    const request = await host.restoreForgeProject(resolved, settings.paths.assets, recoveryId);
    const project = await request.result;
    await recentProjects.add(project.path);
    return project;
  });
  ipcMain.handle('forge:projects-repair', async (event, projectPath: unknown) => {
    assertSender(event.sender.id);
    const resolved = await assertRecentProject(projectPath);
    const values = await getSettingValues();
    const sourceIsoPath = String(values['sources.uya.iso'] ?? '');
    if (!sourceIsoPath || !await fileExists(sourceIsoPath))
      throw new Error('The clean UYA ISO is unavailable. Repair its path in Forge → Setup first.');
    const request = await host.repairForgeProject(resolved, settings.paths.assets, sourceIsoPath);
    const project = await request.result;
    await recentProjects.add(project.path);
    return project;
  });
  ipcMain.handle('forge:projects-repair-assets', async (event, projectPath: unknown) => {
    assertSender(event.sender.id);
    if (activeSetupRequestId !== undefined) throw new Error('Another setup or repair operation is already running');
    const resolved = await assertRecentProject(projectPath);
    const values = await getSettingValues();
    const sourceIsoPath = String(values['sources.uya.iso'] ?? '');
    if (!sourceIsoPath || !await fileExists(sourceIsoPath))
      throw new Error('The clean UYA ISO is unavailable. Repair its path in Forge → Setup first.');
    activeSetupRequestId = 0;
    try {
      const request = await host.repairForgeProjectAssets(
        resolved,
        settings.paths.assets,
        sourceIsoPath,
        (progress) => event.sender.send('forge:setup-progress', { operation: 'import', ...progress }),
      );
      activeSetupRequestId = request.requestId;
      return await request.result;
    } finally {
      activeSetupRequestId = undefined;
    }
  });
  ipcMain.handle('forge:catalog-gc-preview', async (event) => {
    assertSender(event.sender.id);
    return (await host.previewCatalogGarbageCollection(
      settings.paths.assets, await getKnownProjectRoots())).result;
  });
  ipcMain.handle('forge:catalog-gc-collect', async (event, confirmationToken: unknown) => {
    assertSender(event.sender.id);
    if (typeof confirmationToken !== 'string' || confirmationToken.length !== 64)
      throw new TypeError('Catalog cleanup confirmation is invalid');
    const result = await (await host.collectCatalogGarbage(
      settings.paths.assets, await getKnownProjectRoots(), confirmationToken)).result;
    if (result.catalogCandidateCount > 0) {
      await settings.setMany({
        'imports.uya.completedFingerprint': '',
        'imports.uya.completedVersion': 0,
      });
    }
    return result;
  });
  ipcMain.handle('forge:asset-explorer-query', async (event, query: unknown) => {
    assertSender(event.sender.id);
    assertAssetExplorerQuery(query);
    const activeEditorSnapshot = getActiveEditorSnapshot();
    if (!activeEditorSnapshot) throw new Error('Open a project before browsing the asset catalog.');
    if (activeAssetExplorerRequestId !== undefined) await host.cancel(activeAssetExplorerRequestId);
    const request = await host.queryAssetExplorer({
      ...query,
      catalogRootPath: settings.paths.assets,
      limit: query.limit ?? 64,
      targetGame: activeEditorSnapshot.target.game,
      targetRegion: activeEditorSnapshot.target.region,
      targetRevision: activeEditorSnapshot.target.revision,
      currentSkyShellCount: activeEditorSnapshot.entities.filter((entity) => entity.skyShell !== undefined).length,
    });
    activeAssetExplorerRequestId = request.requestId;
    try {
      return await request.result;
    } finally {
      if (activeAssetExplorerRequestId === request.requestId) activeAssetExplorerRequestId = undefined;
    }
  });
  ipcMain.handle('forge:asset-explorer-cancel', async (event) => {
    assertSender(event.sender.id);
    if (activeAssetExplorerRequestId !== undefined) await host.cancel(activeAssetExplorerRequestId);
  });
  ipcMain.handle('forge:projects-remove-recent', async (event, projectPath: unknown) => {
    assertSender(event.sender.id);
    await recentProjects.remove(await assertRecentProject(projectPath));
    return getProjectHubState();
  });
  ipcMain.handle('forge:projects-reveal', async (event, projectPath: unknown) => {
    assertSender(event.sender.id);
    const resolved = await assertRecentProject(projectPath);
    shell.showItemInFolder(path.join(resolved, 'forge-project.json'));
  });
  ipcMain.handle('forge:logs-reveal', async (event) => {
    assertSender(event.sender.id);
    await mkdir(settings.paths.logs, { recursive: true });
    const message = await shell.openPath(settings.paths.logs);
    if (message) throw new Error(message);
  });
}

async function readBoundedFile(filePath: string, maximumBytes: number): Promise<Buffer> {
  const file = await open(filePath, 'r');
  try {
    const buffer = Buffer.allocUnsafe(maximumBytes + 1);
    let length = 0;
    while (length < buffer.length) {
      const result = await file.read(buffer, length, buffer.length - length, null);
      if (result.bytesRead === 0) break;
      length += result.bytesRead;
    }
    if (length > maximumBytes) throw new TypeError(`MobyDex dataset exceeds ${maximumBytes} bytes`);
    return buffer.subarray(0, length);
  } finally {
    await file.close();
  }
}
