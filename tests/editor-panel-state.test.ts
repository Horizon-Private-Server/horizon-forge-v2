import assert from 'node:assert/strict';
import test from 'node:test';

import {
  buildSceneEntityGroups,
  buildTerrainTreeItems,
  entityStateLabel,
  nextTreeSelection,
  nextViewportSelection,
} from '../src/renderer/editor/EditorPanelState.ts';
import type { EditorEntity } from '../src/types/EditorRuntime.js';

function entity(index: number): EditorEntity {
  return {
    id: String(index),
    name: `Moby ${index}`,
    layer: index % 2 ? 'mobys' : 'gameplay',
    transform: {
      position: { x: 0, y: 0, z: 0 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
    asset: { id: 'asset', kind: 'moby' },
    state: {
      dirty: index === 42,
      hidden: false,
      disabled: false,
      locked: index === 42,
      invalid: false,
      missingAsset: index === 42,
    },
  };
}

test('scene tree grouping filters, labels states, and caps large results', () => {
  const entities = Array.from({ length: 25_000 }, (_, index) => entity(index));
  const started = performance.now();
  const all = buildSceneEntityGroups(entities, '');
  const elapsed = performance.now() - started;
  assert.equal(all.matched, 25_000);
  assert.equal(all.shown, 1_000);
  assert.equal(all.groups.length, 2);
  assert.ok(elapsed < 1_000, `large tree model took ${elapsed.toFixed(1)} ms`);

  const filtered = buildSceneEntityGroups(entities, 'moby 42');
  assert.ok(filtered.matched > 0);
  assert.ok(filtered.groups.flatMap((group) => group.entities).every((value) => value.name.includes('42')));
  assert.equal(entityStateLabel(entity(42)), 'Moby 42 [dirty, locked, missing asset]');
});

test('terrain package sections become filterable scene-tree items', () => {
  const urls = [
    'forge-asset://key/assets/tfrag/tfrag.gltf',
    'forge-asset://key/assets/tfrag/chunks/chunk2/tfrag.gltf',
  ];
  assert.deepEqual(buildTerrainTreeItems(urls, ''), [
    { value: urls[0], label: 'Primary tfrag' },
    { value: urls[1], label: 'Chunk 2 tfrag' },
  ]);
  assert.deepEqual(buildTerrainTreeItems(urls, 'chunk 2'), [
    { value: urls[1], label: 'Chunk 2 tfrag' },
  ]);
});

test('tree selection follows desktop replace, toggle, and range conventions', () => {
  const values = ['a', 'b', 'c', 'd'];
  let state = nextTreeSelection(['a'], 'b', values, 'a', { toggle: false, range: false });
  assert.deepEqual(state, { selected: ['b'], anchor: 'b' });
  state = nextTreeSelection(state.selected, 'd', values, state.anchor, { toggle: true, range: false });
  assert.deepEqual(state, { selected: ['b', 'd'], anchor: 'd' });
  state = nextTreeSelection(state.selected, 'b', values, state.anchor, { toggle: true, range: false });
  assert.deepEqual(state, { selected: ['d'], anchor: 'b' });
  state = nextTreeSelection(state.selected, 'd', values, state.anchor, { toggle: false, range: true });
  assert.deepEqual(state, { selected: ['b', 'c', 'd'], anchor: 'b' });
});

test('viewport selection replaces, adds, toggles, and clears predictably', () => {
  assert.deepEqual(nextViewportSelection(['a'], 'b', { toggle: false, add: false }), ['b']);
  assert.deepEqual(nextViewportSelection(['a'], 'b', { toggle: false, add: true }), ['a', 'b']);
  assert.deepEqual(nextViewportSelection(['a', 'b'], 'a', { toggle: true, add: false }), ['b']);
  assert.deepEqual(nextViewportSelection(['a'], undefined, { toggle: false, add: false }), []);
  assert.deepEqual(nextViewportSelection(['a'], undefined, { toggle: true, add: false }), ['a']);
});
