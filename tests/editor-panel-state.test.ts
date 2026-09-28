import assert from 'node:assert/strict';
import test from 'node:test';

import {
  assetGridWindow,
  buildAssetFamilies,
  buildSceneEntityGroups,
  buildSkyTreeItems,
  buildTerrainTreeItems,
  entityStateLabel,
  entityTreeKind,
  entityTreeText,
  nextTreeSelection,
  nextViewportSelection,
} from '../src/renderer/editor/EditorPanelState.ts';
import type { AssetExplorerItem } from '../src/types/AssetExplorer.js';
import type { EditorEntity } from '../src/types/EditorRuntime.js';
import { parseSplinePointId, removeSplinePoints, splinePointId } from '../src/utils/SplinePoints.ts';

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
    transformModes: ['translate', 'rotate', 'scale'],
    state: {
      dirty: index === 42,
      hidden: false,
      disabled: false,
      locked: index === 42,
      readOnly: false,
      invalid: false,
      missingAsset: index === 42,
    },
  };
}

test('asset grid keeps DOM work bounded around visible rows', () => {
  const first = assetGridWindow(10_000, 680, 440, 0);
  assert.equal(first.columns, 4);
  assert.equal(first.startIndex, 0);
  assert.ok(first.endIndex <= 20);
  const middle = assetGridWindow(10_000, 680, 440, 100_000);
  assert.ok(middle.startIndex > 0);
  assert.ok(middle.endIndex - middle.startIndex <= 28);
  assert.equal(middle.totalHeight, first.totalHeight);
});

test('asset explorer groups exact variants by target class and prefers the base-level source', () => {
  const variant = (assetId: string, classIds: number[], level: string, revision = '1.00'): AssetExplorerItem => ({
    assetId, category: 'mobys', displayLabel: 'moby:0x000B', canonicalFormatVersion: 1, byteSize: 100,
    aliases: ['moby:11', 'moby:0x000B'], tags: ['vanilla'], classIds, previewState: 'notCached', canPlace: true,
    sources: [{ game: 'UYA', region: 'NTSC-U', revision, level, archive: 'assets.bin', sourceIndex: 1 }],
  });
  const families = buildAssetFamilies([
    variant('b'.repeat(64), [11], 'level01'),
    variant('a'.repeat(64), [11], 'level03'),
    variant('c'.repeat(64), [12, 13], 'level03'),
    variant('d'.repeat(64), [11], 'level03', '2.00'),
  ], { game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 3 });

  assert.deepEqual(families.map((family) => family.classId), [11, 11, 12, 13]);
  assert.equal(families[0].displayLabel, 'moby:0x000B');
  assert.equal(families[0].representativeAssetId, 'a'.repeat(64));
  assert.deepEqual(families[0].variants.map((item) => item.assetId), ['a'.repeat(64), 'b'.repeat(64)]);
  assert.equal(families[1].variants[0].sources[0].revision, '2.00');
});

test('scene tree grouping filters, labels states, and returns every result', () => {
  const entities = Array.from({ length: 25_000 }, (_, index) => entity(index));
  const started = performance.now();
  const all = buildSceneEntityGroups(entities, '');
  const elapsed = performance.now() - started;
  assert.equal(all.matched, 25_000);
  assert.equal(all.groups.reduce((total, group) => total + group.entities.length, 0), 25_000);
  assert.equal(all.groups.length, 2);
  assert.ok(elapsed < 1_000, `large tree model took ${elapsed.toFixed(1)} ms`);

  const filtered = buildSceneEntityGroups(entities, 'moby 42');
  assert.ok(filtered.matched > 0);
  assert.ok(filtered.groups.flatMap((group) => group.entities).every((value) => value.name.includes('42')));
  assert.equal(entityStateLabel(entity(42)), 'Moby 42 [dirty, locked, missing asset]');
  assert.equal(entityTreeKind(entity(42)), 'moby');
  assert.equal(entityTreeKind({ ...entity(42), asset: undefined, geometry: { kind: 'cuboid', points: [] } }), 'cuboid');
  assert.equal(entityTreeText({ ...entity(42), name: 'Moby 0x002A #42' }),
    '0x002A #42 [dirty, locked, missing asset]');
  assert.equal(entityTreeText(entity(42)), 'Moby 42 [dirty, locked, missing asset]');
  assert.equal(entityTreeText({ ...entity(42), name: 'Custom name' }), 'Custom name [dirty, locked, missing asset]');
});

test('scene tree keeps every populated layer visible', () => {
  const entities = ['mobys', 'ties', 'shrubs'].flatMap((layer, group) =>
    Array.from({ length: 1_000 }, (_, index) => ({ ...entity(group * 1_000 + index), layer })));
  const model = buildSceneEntityGroups(entities, '');

  assert.deepEqual(model.groups.map((group) => group.layer), ['mobys', 'shrubs', 'ties']);
  assert.equal(model.groups.reduce((total, group) => total + group.entities.length, 0), 3_000);
  assert.ok(model.groups.every((group) => group.entities.length > 0));
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

test('sky meshes become filterable scene-tree items', () => {
  assert.deepEqual(buildSkyTreeItems(['skybox_shell_00', 'clouds'], ''), [
    { value: 'render:sky:0', label: 'Sky shell 0' },
    { value: 'render:sky:1', label: 'clouds' },
  ]);
  assert.deepEqual(buildSkyTreeItems(['skybox_shell_00', 'clouds'], 'shell'), [
    { value: 'render:sky:0', label: 'Sky shell 0' },
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

test('spline point tree IDs round-trip without constraining entity IDs', () => {
  const value = splinePointId('entity:with:colons', 42);
  assert.deepEqual(parseSplinePointId(value), { entityId: 'entity:with:colons', index: 42 });
  assert.equal(parseSplinePointId('entity'), undefined);
  assert.deepEqual(removeSplinePoints([
    { x: 0, y: 0, z: 0, w: 0 },
    { x: 1, y: 1, z: 1, w: 1 },
    { x: 2, y: 2, z: 2, w: 2 },
  ], new Set([0, 2])), [{ x: 1, y: 1, z: 1, w: 1 }]);
});
