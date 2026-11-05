import assert from 'node:assert/strict';
import test from 'node:test';

import * as errors from '../src/utils/Errors.ts';
import * as format from '../src/utils/Format.ts';
import * as keybindings from '../src/utils/Keybindings.ts';
import * as treeColors from '../src/utils/SceneTreeColors.ts';
import * as selection from '../src/utils/SelectionVisualization.ts';
import * as sizes from '../src/utils/UiSize.ts';
import * as collision from '../src/utils/UyaCollisionVisualization.ts';
import { createRendererHarness, find } from './support/RendererHarness.mjs';

const settle = () => new Promise(setImmediate);
const importResult = { completedLevels: 2, totalLevels: 2, assetAppearances: 10, failedAssets: 0 };
const dependencies = specifier => {
  const modules = { Errors: errors, Format: format, Keybindings: keybindings, SceneTreeColors: treeColors,
    SelectionVisualization: selection, UiSize: sizes, UyaCollisionVisualization: collision };
  if (specifier.endsWith('/LogStore.ts')) return { forgeLog: { sync() {}, write() {} } };
  return modules[specifier.match(/\/([^/]+)\.ts$/)?.[1]];
};

function button(tree, label) {
  const found = find(tree, node => node.type === 'Button' && node.props.children === label);
  assert.ok(found, `Missing button: ${label}`);
  return found.props;
}

function setupState() {
  const source = game => ({
    iso: `/${game}.iso`, identity: { game, region: 'NTSC-U', revision: '1.00', serial: game, size: 100, fingerprint: game },
    importEnabled: true, importComplete: true,
  });
  return {
    projectsDirectory: '/projects', developmentIsoDirectory: '/development', developmentIso: '/development/UYA.iso',
    developmentIsoReady: true, availableBytes: 200, sources: { UYA: source('UYA'), DL: source('DL'), GC: source('GC'), RC1: source('RC1') },
  };
}

async function mountSetup(state, forge = {}) {
  let closed = 0;
  const runtime = createRendererHarness({ dependencies, forge: {
    getSetupState: async () => structuredClone({ ...state, required: !state.sources.UYA.identity || !state.developmentIsoReady
      || state.sources.UYA.importEnabled && !state.sources.UYA.importComplete }),
    onSetupProgress: () => () => {},
    ...forge,
  } });
  const component = runtime.load('setup/SetupWizard');
  const render = () => runtime.render(component, { opened: true, onClose: () => closed++ });
  render();
  await settle();
  return { runtime, render, get closed() { return closed; } };
}

for (const game of ['DL', 'GC', 'RC1']) {
  test(`${game} controls select, enable, cancel, resume, and re-import independently of UYA`, async () => {
    const state = setupState();
    state.sources[game].importEnabled = false;
    state.sources[game].importComplete = false;
    const calls = [];
    let pending, report;
    const ui = await mountSetup(state, {
      onSetupProgress: callback => { report = callback; return () => {}; },
      chooseAssetSource: async game => { calls.push(['choose', game]); },
      setSetting: async (key, value) => {
        assert.equal(key, `imports.${game.toLowerCase()}.enabled`); state.sources[game].importEnabled = value;
      },
      importAssets: (game, force) => {
        calls.push(['import', game, force]);
        return new Promise((resolve, reject) => { pending = { resolve, reject }; });
      },
      createDevelopmentIso: async () => { throw new Error(`${game} imports must not create a development ISO`); },
      cancelSetupOperation: async () => pending.reject(new Error('Import cancelled')),
    });
    let tree = ui.render();
    assert.equal(tree.props.withCloseButton, true);
    assert.equal(button(tree, `Import / resume ${game} assets`).disabled, true);
    find(tree, node => node.props?.label === `Clean ${game} NTSC-U ISO`).props.onChoose();
    await settle();
    assert.deepEqual(calls, [['choose', game]]);
    tree = ui.render();
    find(tree, node => node.type === 'Checkbox' && node.props.label === `Enable ${game} asset import`)
      .props.onChange({ currentTarget: { checked: true } });
    await settle();
    tree = ui.render();
    const start = button(tree, `Import / resume ${game} assets`);
    assert.equal(start.disabled, false);
    start.onClick(); start.onClick();
    tree = ui.render();
    assert.equal(calls.filter(call => call[0] === 'import').length, 1, 'rapid clicks start only one import');
    assert.equal(button(tree, 'Re-import UYA assets').disabled, true);
    report({ operation: 'import', game: game, completed: 1, total: 2 });
    tree = ui.render();
    assert.ok(find(tree, node => node.props?.role === 'status' && node.props.children === `Importing reusable ${game} assets…`));
    button(tree, 'Cancel operation').onClick();
    await settle();
    tree = ui.render();
    assert.equal(tree.props.withCloseButton, true, 'cancelled optional imports leave setup closable');
    assert.equal(button(tree, 'Done').disabled, false);
    assert.equal(state.sources.UYA.importComplete, true);
    button(tree, `Import / resume ${game} assets`).onClick();
    state.sources[game].importComplete = true;
    pending.resolve(importResult);
    await settle();
    tree = ui.render();
    button(tree, `Re-import ${game} assets`).onClick();
    pending.resolve(importResult);
    await settle();
    assert.deepEqual(calls.filter(call => call[0] === 'import'), [
      ['import', game, false], ['import', game, false], ['import', game, true],
    ]);
    button(ui.render(), 'Done').onClick();
    assert.equal(ui.closed, 1);
    ui.runtime.dispose();
  });

}

test('finishing UYA setup ignores enabled DL imports without a source', async () => {
  const state = setupState();
  state.developmentIsoReady = false;
  state.sources.UYA.importComplete = false;
  state.sources.DL = { iso: '', importEnabled: true, importComplete: false };
  const calls = [];
  const ui = await mountSetup(state, {
    createDevelopmentIso: async () => { calls.push('copy UYA'); state.developmentIsoReady = true; return {}; },
    importAssets: async (game, force) => {
      calls.push(`import ${game} ${force}`); state.sources[game].importComplete = true; return importResult;
    },
  });
  const tree = ui.render();
  assert.equal(tree.props.withCloseButton, false, 'unfinished UYA setup remains required');
  assert.equal(button(tree, 'Import / resume DL assets').disabled, true);
  assert.equal(button(tree, 'Finish UYA setup').disabled, false);
  button(tree, 'Finish UYA setup').onClick();
  await settle();
  assert.deepEqual(calls, ['copy UYA', 'import UYA false']);
  assert.equal(ui.closed, 1);
  assert.equal(state.sources.DL.importComplete, false);
  ui.runtime.dispose();
});

test('DL asset extraction can run before UYA source or development ISO setup', async () => {
  const state = setupState();
  state.sources.UYA = { iso: '', importEnabled: true, importComplete: false };
  state.sources.DL.importComplete = false;
  state.developmentIsoReady = false;
  state.availableBytes = 0;
  const calls = [];
  const ui = await mountSetup(state, {
    importAssets: async (game, force) => { calls.push([game, force]); return importResult; },
  });
  const tree = ui.render();
  assert.equal(button(tree, 'Finish UYA setup').disabled, true);
  assert.equal(button(tree, 'Import / resume DL assets').disabled, false);
  button(tree, 'Import / resume DL assets').onClick();
  await settle();
  assert.deepEqual(calls, [['DL', false]]);
  ui.runtime.dispose();
});

test('Settings displays all source paths and opens Setup after closing Settings', async () => {
  const calls = [];
  const runtime = createRendererHarness({ dependencies, forge: {
    getSettings: async () => ({ entries: [
      { key: 'sources.uya.iso', value: '/UYA.iso' }, { key: 'sources.dl.iso', value: '/DL.iso' }, { key: 'sources.gc.iso', value: '/GC.iso' }, { key: 'sources.rc1.iso', value: '/RC1.iso' },
    ] }),
  } });
  const component = runtime.load('settings/SettingsModal');
  const noop = () => {};
  const props = {
    opened: true, onClose: () => calls.push('close Settings'), onOpenSetup: () => calls.push('open Setup'),
    onKeybindingsChange: noop, sceneTreeColors: treeColors.DEFAULT_SCENE_TREE_COLORS,
    onSceneTreeColorsChange: noop, selectionColor: '#ffffff', onSelectionColorChange: noop,
    collisionVisualization: collision.DEFAULT_UYA_COLLISION_VISUALIZATION, onCollisionVisualizationChange: noop,
    onViewportStatsChange: noop, uiSize: 'xs', onUiSizeChange: noop,
  };
  runtime.render(component, props);
  await settle();
  const tree = runtime.render(component, props);
  for (const game of ['UYA', 'DL', 'GC', 'RC1']) {
    const input = find(tree, node => node.type === 'TextInput' && node.props.label === `Clean ${game} ISO`);
    assert.equal(input.props.value, `/${game}.iso`);
    assert.equal(input.props.readOnly, true);
  }
  button(tree, 'Open Setup').onClick();
  assert.deepEqual(calls, ['close Settings', 'open Setup']);
  runtime.dispose();
});
