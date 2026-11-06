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
  export const Menu = { getApplicationMenu: () => undefined };
`)}`;
const hooks = registerHooks({ resolve(specifier, context, next) {
  return specifier === 'electron' ? { url: electronUrl, shortCircuit: true } : next(specifier, context);
} });
let registerIpcHandlers;
let registerEditorIpcHandlers;
let handlers;
try {
  ({ handlers } = await import(electronUrl));
  ({ registerIpcHandlers } = await import('../dist-electron/main/IpcHandlers.js'));
  ({ registerEditorIpcHandlers } = await import('../dist-electron/main/EditorIpcHandlers.js'));
} finally { hooks.deregister(); }

test('content import options use the selected game source and reject invalid games and senders', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'forge-content-options-'));
  try {
    const values = {};
    for (const game of ['RC1', 'GC', 'UYA', 'DL']) {
      const source = path.join(root, `${game}.iso`);
      await writeFile(source, 'synthetic ISO');
      values[`sources.${game.toLowerCase()}.iso`] = source;
      values[`sources.${game.toLowerCase()}.fingerprint`] = 'a'.repeat(32);
    }
    const calls = [];
    const previews = [];
    const imports = [];
    registerEditorIpcHandlers({
      host: {
        openEditorProject: async () => ({ result: { projectPath: root, target: { game: 'UYA' }, entities: [] } }),
        listSourceLevels: async (game, source) => {
          calls.push([game, source]);
          return { result: [1, ...(game === 'GC' ? [30] : [])] };
        },
        planContentImport: async request => { previews.push(request); return { requestId: 99, result: { game: request.sourceGame } }; },
        applyContentImport: async request => {
          imports.push(request);
          return { requestId: 100, result: { projectPath: root, target: { game: 'UYA' }, entities: [] } };
        },
      },
      settings: { paths: { assets: root } },
      getSettingValues: async () => values,
      assertSender: id => { if (id !== 7) throw new Error('Untrusted sender'); },
      resolveProjectPath: async value => value,
      cancelBuild: async () => {},
    });
    const event = { sender: { id: 7 } };
    const invoke = (channel, ...args) => handlers.get(channel)(event, ...args);
    await assert.rejects(invoke('forge:content-import-options', 'GC'), /Open a project/);
    await invoke('forge:editor-open', root);
    for (const [game, name] of [['RC1', 'Novalis'], ['GC', 'Oozla'], ['UYA', 'Veldin'], ['DL', 'DreadZone Station']]) {
      const options = await invoke('forge:content-import-options', game);
      assert.equal(options.levels[0].label, `01 - ${name}`);
      assert.deepEqual(calls.at(-1), [game, path.join(root, `${game}.iso`)]);
      if (game === 'GC') assert.deepEqual(options.levels[1], { value: 30, label: '30 - Insomniac Museum' });
    }
    await assert.rejects(invoke('forge:content-import-options', '../gc'), /Unsupported/);
    assert.equal((await invoke('forge:content-import-plan', 30, [], 'GC')).game, 'GC');
    assert.equal(previews[0].sourceIsoPath, path.join(root, 'GC.iso'));
    assert.equal(previews[0].sourceFingerprint, values['sources.gc.fingerprint']);
    assert.equal(previews[0].sourceGame, 'GC');
    assert.equal((await invoke('forge:content-import-plan', 1, [], 'RC1')).game, 'RC1');
    assert.equal(previews[1].sourceIsoPath, path.join(root, 'RC1.iso'));
    assert.equal(previews[1].sourceFingerprint, values['sources.rc1.fingerprint']);
    assert.equal((await invoke('forge:content-import-plan', 1, [], 'DL')).game, 'DL');
    assert.equal(previews[2].sourceIsoPath, path.join(root, 'DL.iso'));
    assert.equal(previews[2].sourceFingerprint, values['sources.dl.fingerprint']);
    await assert.rejects(invoke('forge:content-import-plan', -1, [], 'GC'), /Donor level/);
    await invoke('forge:content-import-apply', 30, 'import-fixture', [{ family: 'ties', mode: 'replace' }], [], 'GC');
    assert.equal(imports[0].sourceGame, 'GC');
    assert.equal(imports[0].sourceIsoPath, path.join(root, 'GC.iso'));
    await assert.rejects(invoke('forge:content-import-apply', 30, 'import-fixture', [{ family: 'ties', mode: 'add' }], [], 'GC'), /replacement/);
    await assert.rejects(invoke('forge:content-import-apply', 42, 'import-fixture', [{ family: 'ties', mode: 'add' }], [], 'DL'), /replacement/);
    await invoke('forge:content-import-apply', 1, 'import-fixture', [{ family: 'ties', mode: 'replace' }], [], 'RC1');
    assert.equal(imports[1].sourceGame, 'RC1');
    assert.equal(imports[1].sourceIsoPath, path.join(root, 'RC1.iso'));
    await invoke('forge:content-import-apply', 42, 'import-fixture', [{ family: 'ties', mode: 'replace' }], [], 'DL');
    assert.equal(imports[2].sourceGame, 'DL');
    assert.equal(imports[2].sourceIsoPath, path.join(root, 'DL.iso'));
    await assert.rejects(invoke('forge:content-import-apply', 1, 'import-fixture', [{ family: 'ties', mode: 'add' }], [], 'RC1'), /replacement/);
    assert.equal(imports.length, 3);
    await assert.rejects(handlers.get('forge:content-import-options')({ sender: { id: 8 } }, 'GC'), /Untrusted/);
    values['sources.gc.iso'] = path.join(root, 'missing.iso');
    await assert.rejects(invoke('forge:content-import-options', 'GC'), /GC source ISO/);
    assert.equal(calls.length, 4);
  } finally { await rm(root, { recursive: true, force: true }); }
});

for (const game of ['DL', 'GC', 'RC1']) {
  const revision = game === 'GC' ? '1.01' : '1.00';
  test(`setup keeps ${game} source/import state separate and publishes completion only after success`, async () => {
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
        [`imports.${game.toLowerCase()}.enabled`]: false,
      };
      const progress = [];
      const settings = { paths: { assets: path.join(root, 'assets') },
        getSnapshot: async () => ({ entries: Object.entries(values).map(([key, value]) => ({ key, value })) }),
        setMany: async update => Object.assign(values, update),
      };
      let failImport = false;
      const host = {
        validateSourceIso: async (game, file, report) => {
          assert.equal(game, game); assert.equal(file, source); report({ completed: 1, total: 1 });
          return { requestId: 11, result: Promise.resolve({ isSupported: true, game, region: 'NTSC-U', revision,
            serial: game === 'GC' ? 'SCUS-97268' : game === 'RC1' ? 'SCUS-97199' : 'SCUS-97465', size: 123, fingerprint: 'b'.repeat(32), diagnostic: 'verified' }) };
        },
        importAssets: async (game, file, catalog, fingerprint, revision, report, force) => {
          assert.deepEqual([game, file, catalog, fingerprint, revision, force],
            [game, source, settings.paths.assets, 'b'.repeat(32), revision, false]);
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
      await assert.rejects(() => invoke('forge:setup-choose-source', 'Unknown'), /Unsupported/);
      await assert.rejects(() => handlers.get('forge:setup-choose-source')({ sender: { id: 8 } }, game), /Untrusted/);
      await invoke('forge:setup-choose-source', game);
      assert.equal(values['targets.uya.developmentIso'], source);
      assert.equal(values['imports.uya.completedVersion'], 2);
      assert.equal((await invoke('forge:setup-state')).required, false);
      values[`imports.${game.toLowerCase()}.enabled`] = true;
      assert.equal((await invoke('forge:setup-state')).required, false, `optional ${game} imports do not block UYA setup`);
      failImport = true;
      await assert.rejects(() => invoke('forge:setup-import-assets', game, false), /cancelled/);
      assert.equal(values[`imports.${game.toLowerCase()}.completedFingerprint`], '');
      assert.equal((await invoke('forge:setup-state')).sources[game].importComplete, false);
      assert.equal((await invoke('forge:setup-state')).required, false, 'cancelled optional imports keep Forge usable');
      failImport = false;
      await invoke('forge:setup-import-assets', game, false);
      const complete = await invoke('forge:setup-state');
      assert.equal(complete.sources[game].importComplete, true);
      assert.equal(complete.required, false);
      assert.equal(complete.sources.UYA.importComplete, true);
      if (game === 'RC1') {
        assert.equal(values['imports.rc1.completedVersion'], 2);
        values['imports.rc1.completedVersion'] = 1;
        assert.equal((await invoke('forge:setup-state')).sources.RC1.importComplete, false,
          'RC1 imports before the authored mipmap fix require reimport');
      }
      assert.ok(progress.some(value => value.operation === 'import' && value.game === game));
      values[`sources.${game.toLowerCase()}.iso`] = path.join(root, `unavailable-${game}.iso`);
      assert.equal((await invoke('forge:setup-state')).required, false, `an unavailable ${game} source is optional`);
      values['imports.uya.completedFingerprint'] = '';
      assert.equal((await invoke('forge:setup-state')).required, true, 'enabled UYA imports remain required');
      values['imports.uya.completedFingerprint'] = 'a'.repeat(32);
      await rm(source);
      assert.equal((await invoke('forge:setup-state')).required, true, 'UYA still requires its source and development ISO');
    } finally { delete globalThis.setupSourcePath; await rm(root, { recursive: true, force: true }); }
  });

}
