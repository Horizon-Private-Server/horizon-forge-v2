import assert from 'node:assert/strict';
import test from 'node:test';

import { formatSourceLevelLabel } from '../src/utils/SourceLevels.ts';
import { errorMessage } from '../src/utils/Errors.ts';
import { createRendererHarness, find } from './support/RendererHarness.mjs';

const settle = () => new Promise(setImmediate);
const levels = { RC1: [0, 1], GC: [0, 1, 30], UYA: [1, 40], DL: [1, 42] };
const donor = tree => find(tree, node => node.type === 'Select' && node.props.label === 'Donor level').props;
const ribbon = tree => find(tree, node => node.type === 'SegmentedControl').props;
const previewButton = tree => find(tree, node => node.type === 'Button'
  && ['Preview changes', 'View preview'].includes(node.props.children)).props;

function mount(getContentImportOptions = async game => ({
  levels: levels[game].map(value => ({ value, label: formatSourceLevelLabel(game, value) })), warnings: [],
})) {
  const plans = [];
  const applied = [];
  const runtime = createRendererHarness({
    dependencies: specifier => {
      if (specifier.endsWith('/LogStore.ts')) return { forgeLog: { sync() {}, write() {} } };
      if (specifier.endsWith('/Errors.ts')) return { errorMessage };
      if (specifier.endsWith('/ContentImportQualification.ts'))
        return { createContentImportQualificationRecord: async () => 'record' };
    },
    forge: {
      getContentImportOptions,
      onContentImportProgress: () => () => {},
      planContentImport: async (...args) => {
        plans.push(args);
        const families = args[1].length ? args[1] : [{ family: 'ties', mode: args[2] === 'UYA' ? 'add' : 'replace' }];
        return { families: families.map(family => ({ ...family, selected: true,
          add: [], reuse: [], remap: [], remove: [], protect: [] })),
          referenceRequirements: [], warnings: [], blockers: [] };
      },
      applyContentImport: async (...args) => { applied.push(args); },
    },
  });
  const component = runtime.load('editor/MapContentImportDialog');
  const props = { opened: true, project: { projectId: 'project', projectName: 'Test',
    target: { game: 'UYA' }, entities: [] }, onClose() {}, onApplied() {} };
  const render = () => runtime.render(component, props);
  render();
  return { runtime, render, plans, applied };
}

test('four-game ribbon loads matching level names and clears the previous donor', async () => {
  const ui = mount();
  try {
    await settle();
    let tree = ui.render();
    assert.deepEqual(Array.from(ribbon(tree).data), ['RC1', 'GC', 'UYA', 'DL']);
    assert.equal(ribbon(tree).value, 'UYA');
    donor(tree).onChange('40');
    await settle();
    assert.equal(ui.plans.length, 1, 'UYA donor selection still previews');
    assert.ok(find(ui.render(), node => node.type === 'ContentImportFamilyBoard'));
    for (const game of ['GC', 'RC1', 'DL', 'UYA']) {
      ribbon(ui.render()).onChange(game);
      tree = ui.render();
      assert.equal(donor(tree).value, null);
      assert.equal(donor(tree).disabled, true);
      assert.equal(find(tree, node => node.type === 'ContentImportFamilyBoard'), undefined);
      await settle();
      tree = ui.render();
      assert.deepEqual(Array.from(donor(tree).data, entry => [entry.value, entry.label]),
        levels[game].map(level => [String(level), formatSourceLevelLabel(game, level)]));
      if (game !== 'UYA') {
        donor(tree).onChange('1');
        await settle();
        tree = ui.render();
        assert.equal(previewButton(tree).disabled, false);
        assert.deepEqual(JSON.parse(JSON.stringify(ui.plans.at(-1))), [1, [], game]);

      }
    }
  } finally { ui.runtime.dispose(); }
});

for (const game of ['GC', 'RC1', 'DL']) test(`${game} preview recalculates family choices and applies the approved replacement`, async () => {
  const ui = mount();
  try {
    await settle();
    ribbon(ui.render()).onChange(game);
    ui.render();
    await settle();
    donor(ui.render()).onChange('1');
    await settle();
    let tree = ui.render();
    const board = find(tree, node => node.type === 'ContentImportFamilyBoard');
    board.props.onChange({ ties: 'replace', shrubs: 'replace' });
    tree = ui.render();
    previewButton(tree).onClick();
    await settle();
    tree = ui.render();
    assert.deepEqual(JSON.parse(JSON.stringify(ui.plans.at(-1))), [1, [{ family: 'shrubs', mode: 'replace' }, { family: 'ties', mode: 'replace' }], game]);
    const apply = find(tree, node => node.type === 'Button' && node.props.children === 'Import');
    assert.equal(apply.props.disabled, false);
    apply.props.onClick();
    await settle();
    assert.equal(ui.applied.length, 1);
    assert.equal(ui.applied[0][4], game);
    assert.equal(ui.applied[0][2][0].mode, 'replace');
  } finally { ui.runtime.dispose(); }
});

test('late level lists and failures cannot overwrite a newly selected game', async () => {
  const pending = new Map();
  const ui = mount(game => new Promise((resolve, reject) => pending.set(game, { resolve, reject })));
  try {
    ribbon(ui.render()).onChange('GC');
    ui.render();
    pending.get('GC').resolve({ levels: [{ value: 1, label: '01 - Oozla' }], warnings: [] });
    pending.get('UYA').resolve({ levels: [{ value: 1, label: '01 - Veldin' }], warnings: [] });
    await settle();
    assert.equal(donor(ui.render()).data[0].label, '01 - Oozla');
    ribbon(ui.render()).onChange('RC1');
    ui.render();
    ribbon(ui.render()).onChange('DL');
    ui.render();
    pending.get('RC1').reject(new Error('Stale source error'));
    pending.get('DL').resolve({ levels: [], warnings: [] });
    await settle();
    assert.equal(find(ui.render(), node => node.props?.title === 'Map content import failed'), undefined);
    assert.equal(donor(ui.render()).data.length, 0);
  } finally { ui.runtime.dispose(); }
});

test('source labels preserve game-specific level IDs and unknown levels', () => {
  assert.equal(formatSourceLevelLabel('GC', 30), '30 - Insomniac Museum');
  assert.equal(formatSourceLevelLabel('GC', 0), '00 - Aranos Tutorial');
  assert.equal(formatSourceLevelLabel('RC1', 1), '01 - Novalis');
  assert.equal(formatSourceLevelLabel('UYA', 1), '01 - Veldin');
  assert.equal(formatSourceLevelLabel('DL', 42), '42 - MP: Catacrom Graveyard');
  assert.equal(formatSourceLevelLabel('GC', 99), '99 - Level 99');
});
