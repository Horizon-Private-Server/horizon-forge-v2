import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { registerHooks } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

const electronUrl = `data:text/javascript,${encodeURIComponent(`
  export const handlers = new Map();
  export const ipcMain = { handle: (channel, handler) => handlers.set(channel, handler), on() {} };
  export const app = { on() {} };
  export const dialog = { showOpenDialog: async () => ({ canceled: false, filePaths: [globalThis.setupSourcePath] }) };
  export const shell = {};
  export const BrowserWindow = {};
  export const Menu = {};
`)}`;
const hooks = registerHooks({ resolve(specifier, context, next) {
  return specifier === 'electron' ? { url: electronUrl, shortCircuit: true } : next(specifier, context);
} });
let registerIpcHandlers;
let handlers;
try {
  ({ handlers } = await import(electronUrl));
  ({ registerIpcHandlers } = await import('../dist-electron/main/IpcHandlers.js'));
} finally { hooks.deregister(); }

test('setup keeps DL source/import state separate and publishes completion only after success', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'forge-setup-ipc-'));
  try {
    const source = path.join(root, 'source.iso');
    await writeFile(source, 'synthetic ISO');
    globalThis.setupSourcePath = source;
    const values = {
      'paths.developmentIsos': root, 'paths.projects': root,
      'sources.uya.iso': source, 'sources.uya.game': 'UYA', 'sources.uya.region': 'NTSC-U',
      'sources.uya.fingerprint': 'a'.repeat(32), 'targets.uya.developmentIso': source,
      'imports.uya.enabled': true, 'imports.uya.completedFingerprint': 'a'.repeat(32), 'imports.uya.completedVersion': 2,
      'imports.dl.enabled': false,
    };
    const progress = [];
    const settings = { paths: { assets: path.join(root, 'assets') },
      getSnapshot: async () => ({ entries: Object.entries(values).map(([key, value]) => ({ key, value })) }),
      setMany: async update => Object.assign(values, update),
    };
    let failImport = false;
    const host = {
      validateSourceIso: async (game, file, report) => {
        assert.equal(game, 'DL'); assert.equal(file, source); report({ completed: 1, total: 1 });
        return { requestId: 11, result: Promise.resolve({ isSupported: true, game, region: 'NTSC-U', revision: '1.00',
          serial: 'SCUS-97465', size: 123, fingerprint: 'b'.repeat(32), diagnostic: 'verified' }) };
      },
      importAssets: async (game, file, catalog, fingerprint, revision, report, force) => {
        assert.deepEqual([game, file, catalog, fingerprint, revision, force],
          ['DL', source, settings.paths.assets, 'b'.repeat(32), '1.00', false]);
        report({ completed: 1, total: 1 });
        return { requestId: 12, result: failImport ? Promise.reject(new Error('cancelled'))
          : Promise.resolve({ completedLevels: 2, totalLevels: 2, assetAppearances: 10, uniqueAssets: 5, failedAssets: 0, resumed: false }) };
      },
    };
    registerIpcHandlers({ host, settings, notifications: {}, recentProjects: {}, updates: {}, renderAssets: {},
      getMainWindow: () => ({ webContents: { id: 7 } }) });
    const event = { sender: { id: 7, send: (channel, value) => progress.push({ channel, ...value }) } };
    const invoke = (channel, ...args) => handlers.get(channel)(event, ...args);
    assert.equal((await invoke('forge:setup-state')).required, false);
    await assert.rejects(() => invoke('forge:setup-choose-source', 'RC1'), /Unsupported/);
    await assert.rejects(() => handlers.get('forge:setup-choose-source')({ sender: { id: 8 } }, 'DL'), /Untrusted/);
    await invoke('forge:setup-choose-source', 'DL');
    assert.equal(values['targets.uya.developmentIso'], source);
    assert.equal(values['imports.uya.completedVersion'], 2);
    assert.equal((await invoke('forge:setup-state')).required, false);
    values['imports.dl.enabled'] = true;
    assert.equal((await invoke('forge:setup-state')).required, false, 'optional DL imports do not block UYA setup');
    failImport = true;
    await assert.rejects(() => invoke('forge:setup-import-assets', 'DL', false), /cancelled/);
    assert.equal(values['imports.dl.completedFingerprint'], '');
    assert.equal((await invoke('forge:setup-state')).sources.DL.importComplete, false);
    assert.equal((await invoke('forge:setup-state')).required, false, 'cancelled optional imports keep Forge usable');
    failImport = false;
    await invoke('forge:setup-import-assets', 'DL', false);
    const complete = await invoke('forge:setup-state');
    assert.equal(complete.sources.DL.importComplete, true);
    assert.equal(complete.required, false);
    assert.equal(complete.sources.UYA.importComplete, true);
    assert.ok(progress.some(value => value.operation === 'import' && value.game === 'DL'));
    values['sources.dl.iso'] = path.join(root, 'unavailable-DL.iso');
    assert.equal((await invoke('forge:setup-state')).required, false, 'an unavailable DL source is optional');
    values['imports.uya.completedFingerprint'] = '';
    assert.equal((await invoke('forge:setup-state')).required, true, 'enabled UYA imports remain required');
    values['imports.uya.completedFingerprint'] = 'a'.repeat(32);
    await rm(source);
    assert.equal((await invoke('forge:setup-state')).required, true, 'UYA still requires its source and development ISO');
  } finally { delete globalThis.setupSourcePath; await rm(root, { recursive: true, force: true }); }
});
