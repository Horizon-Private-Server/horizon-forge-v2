import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { AssetThumbnailCache } from '../src/main/AssetThumbnailCache.ts';
import {
  assetExplorerFilterCount,
  assetExplorerQueryKey,
  assetGridWindow,
  buildAssetFamilies,
  buildSceneEntityGroups,
  buildSkyTreeItems,
  buildTerrainTreeItems,
  entityStateLabel,
  entityTreeKind,
  entityTreeText,
  isStaleAssetExplorerCursor,
  retainAssetExplorerPageDepth,
  nextTreeSelection,
  nextViewportSelection,
} from '../src/renderer/editor/EditorPanelState.ts';
import type { AssetExplorerItem } from '../src/types/AssetExplorer.js';
import type { EditorEntity, EditorTieCollisionCandidate } from '../src/types/EditorRuntime.js';
import { createAssetPlacementCommand, createSkyShellAddCommand } from '../src/utils/AssetPlacement.ts';
import {
  collisionTypeId, collisionTypeIdOptions, defaultCollisionType, formatCollisionType,
  formatUyaCollisionTypeId, packCollisionType, soundTypeId,
} from '../src/utils/CollisionFormat.ts';
import { parseSplinePointId, removeSplinePoints, splinePointId } from '../src/utils/SplinePoints.ts';
import { applyTextureChannel } from '../src/utils/TexturePreview.ts';
import { recommendTieCollisionCandidate } from '../src/utils/TieCollision.ts';

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

test('texture preview channels expose opaque color and alpha values', () => {
  const rgb = new Uint8ClampedArray([10, 20, 30, 40]);
  applyTextureChannel(rgb, 'rgb');
  assert.deepEqual([...rgb], [10, 20, 30, 255]);
  const alpha = new Uint8ClampedArray([10, 20, 30, 40]);
  applyTextureChannel(alpha, 'alpha');
  assert.deepEqual([...alpha], [40, 40, 40, 255]);
});

test('TIE collision recommendation respects hard failures and measured deviation', () => {
  const candidate = (
    token: string,
    worstBytes: number,
    maximumDeviation: number,
    hardViolationCount = 0,
  ): EditorTieCollisionCandidate => ({
    token,
    preset: 'surface',
    label: token,
    recipe: {
      kind: 'surface', generatorVersion: 2, recipeVersion: 1, lodIndex: 0, rawType: 0,
      detailSize: 0, sealOpeningSize: 0, surfaceOffset: 0, openBase: false, profileSections: 0,
    },
    encodedByteCount: worstBytes * 2,
    vertexCount: 3,
    faceCount: 1,
    occupiedOctantCount: 1,
    duplicateFaceCount: 0,
    hardViolationCount,
    maximumDeviation,
    deviationSampleCount: 3,
    octants: [{
      x: 0, y: 0, z: 0, faceCount: 1, vertexCount: 3, quadCount: 0,
      encodedByteCount: worstBytes, violations: [],
    }],
  });
  const combinedUnsafe = candidate('combined-unsafe', 5, 1);
  combinedUnsafe.combinedAnalysis = {
    instanceCount: 2, logicalFaceCount: 2, occupiedOctantCount: 1,
    duplicateFaceCount: 0, hardViolationCount: 1, octants: [],
  };
  const candidates = [
    combinedUnsafe, candidate('unsafe', 10, 1, 1), candidate('too-far', 20, 5), candidate('best', 30, 2),
  ];
  assert.equal(recommendTieCollisionCandidate(candidates, 4)?.token, 'best');
  assert.equal(recommendTieCollisionCandidate(candidates, 1), undefined);
});

test('asset thumbnails persist, reject corruption, and prune least-recently-used rasters', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'forge-thumbnail-cache-'));
  const valid = (bytes: Uint8Array) => bytes[0] === 42;
  try {
    const cache = new AssetThumbnailCache(root, valid, 5);
    const first = await cache.store('UYA', 'a'.repeat(64), 'tie', 'sdk', Uint8Array.of(42, 1, 1));
    assert.equal((await cache.get('UYA', 'a'.repeat(64), 'tie', 'sdk'))?.path, first.path);
    await cache.store('UYA', 'b'.repeat(64), 'shrub', 'sdk', Uint8Array.of(42, 2, 2));
    await assert.rejects(stat(path.join(first.rootPath, first.path)), { code: 'ENOENT' });

    const second = await cache.get('UYA', 'b'.repeat(64), 'shrub', 'sdk');
    assert.ok(second);
    await writeFile(path.join(second.rootPath, second.path), Uint8Array.of(0, 2, 2));
    assert.equal(await cache.get('UYA', 'b'.repeat(64), 'shrub', 'sdk'), undefined);
    await assert.rejects(readFile(path.join(second.rootPath, second.path)), { code: 'ENOENT' });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('sky shell thumbnail cache keys include the shell index', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'forge-thumbnail-shell-cache-'));
  try {
    const cache = new AssetThumbnailCache(root, () => true);
    const first = await cache.store('UYA', 'a'.repeat(64), 'sky', 'sdk', Uint8Array.of(1), 0);
    const second = await cache.store('UYA', 'a'.repeat(64), 'sky', 'sdk', Uint8Array.of(2), 1);
    assert.notEqual(first.path, second.path);
    const otherGame = await cache.store('GC', 'a'.repeat(64), 'sky', 'sdk', Uint8Array.of(3), 0);
    assert.notEqual(first.path, otherGame.path);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('asset explorer counts active facets and recognizes stale cursors', () => {
  assert.equal(assetExplorerFilterCount({
    game: 'UYA', region: 'NTSC-U', tags: ['vanilla', 'structural'],
  }), 4);
  assert.equal(isStaleAssetExplorerCursor(
    new Error('The asset catalog changed; restart the query.')),
  true);
  assert.equal(isStaleAssetExplorerCursor(new Error('Catalog query failed.')), false);
});

test('asset explorer query identity changes only with visible query controls', () => {
  const key = assetExplorerQueryKey('skyShells', '', { tags: [] });
  assert.equal(key, assetExplorerQueryKey('skyShells', '', { tags: [] }));
  assert.notEqual(key, assetExplorerQueryKey('skyShells', 'cloud', { tags: [] }));
  assert.notEqual(key, assetExplorerQueryKey('skyShells', '', { level: 'level51', tags: [] }));
});

test('asset explorer refresh retains its loaded page depth before replacing items', async () => {
  const item = (assetId: string): AssetExplorerItem => ({
    assetId, category: 'skyShells', displayLabel: assetId, canonicalFormatVersion: 1, byteSize: 1,
    aliases: [], tags: [], sources: [], classIds: [], previewState: 'notCached', canPlace: true, shellIndex: 0,
  });
  const pages = new Map([
    ['page-2', { items: [item('b')], facets: { games: [], levels: [], regions: [], revisions: [], tags: [] }, nextCursor: 'page-3' }],
    ['page-3', { items: [item('c')], facets: { games: [], levels: [], regions: [], revisions: [], tags: [] } }],
  ]);
  const page = await retainAssetExplorerPageDepth(
    { items: [item('a')], facets: { games: [], levels: [], regions: [], revisions: [], tags: [] }, nextCursor: 'page-2' },
    3,
    async (cursor) => pages.get(cursor),
  );
  assert.deepEqual(page.items.map((value) => value.assetId), ['a', 'b', 'c']);
  assert.equal(page.nextCursor, undefined);
});

test('asset placement uses one shared identity transform command', () => {
  const command = createAssetPlacementCommand({ assetId: 'a'.repeat(64), kind: 'shrub', classId: 42 }, { x: 1, y: 2, z: 3 });
  assert.equal(command.kind, 'createEntityFromAsset');
  assert.deepEqual(command.placement, {
    assetId: 'a'.repeat(64), kind: 'Shrub', classId: 42,
    transform: {
      position: { x: 1, y: 2, z: 3 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
  });
});

test('sky shell addition uses the source shell without a spatial transform', () => {
  const command = createSkyShellAddCommand({ assetId: 'b'.repeat(64), kind: 'sky', shellIndex: 3 });
  assert.equal(command.kind, 'addSkyShellFromAsset');
  assert.deepEqual(command.source, { assetId: 'b'.repeat(64), shellIndex: 3 });
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

test('asset explorer keeps sky shells under separate cards', () => {
  const shell = (shellIndex: number): AssetExplorerItem => ({
    assetId: 'a'.repeat(64), category: 'skyShells', displayLabel: `Sky shell ${shellIndex}`,
    canonicalFormatVersion: 0, byteSize: 100, aliases: ['base:sky:sky'], tags: ['vanilla'],
    sources: [{ game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 'level03', archive: 'sky.bin', sourceIndex: 0 }],
    classIds: [], previewState: 'notCached', canPlace: false, shellIndex,
  });

  const families = buildAssetFamilies([shell(0), shell(1)], {
    game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 3,
  });
  assert.deepEqual(families.map((family) => family.displayLabel), ['Sky shell 0', 'Sky shell 1']);
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

test('collision type formatting keeps game-specific nibble semantics behind target dispatch', () => {
  assert.equal(formatCollisionType(0xa7, 'UYA'), 'Sound 0xA · Type 0x7 · Grind rail · Raw 0xA7');
  assert.equal(formatCollisionType(0xa7, 'GC'), 'Raw 0xA7');
  assert.deepEqual(
    [2, 4, 5, 6, 7, 8, 9, 10, 12, 14, 15].map(formatUyaCollisionTypeId),
    [
      '0x2 · Magnetic', '0x4 · Grind rail', '0x5 · Normal', '0x6 · Normal',
      '0x7 · Grind rail', '0x8 · Slide off', '0x9 · Normal (No ledge grab)',
      '0xA · Magnetic', '0xC · Slide off (No ledge grab)',
      '0xE · Normal (Water trail)', '0xF · Normal',
    ],
  );
  assert.equal(soundTypeId(0xa7, 'UYA'), 0xa);
  assert.equal(collisionTypeId(0xa7, 'UYA'), 0x7);
  assert.equal(packCollisionType(0x7, 0xa, 'UYA'), 0xa7);
  assert.equal(defaultCollisionType('UYA'), 0x0f);
  assert.equal(collisionTypeIdOptions('UYA').length, 16);
});
