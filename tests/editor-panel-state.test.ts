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
  buildHudBankItems,
  buildFxTextureItems,
  buildReferenceIndex,
  buildAssetFamilies,
  buildSceneEntityGroups,
  buildSkyTreeItems,
  buildTerrainTreeItems,
  entityStateLabel,
  entityTreeKind,
  entityTreeText,
  compatibleReferenceTargets,
  isStaleAssetExplorerCursor,
  retainAssetExplorerPageDepth,
  nextTreeSelection,
  nextViewportSelection,
  referencePage,
  referenceNavigationTarget,
  formatHudSpriteId,
  hudThumbnailDimensions,
  nextHudSpriteId,
  validateHudPng,
  validateFxPng,
  virtualGridWindow,
} from '../src/renderer/editor/EditorPanelState.ts';
import type { AssetExplorerItem } from '../src/types/AssetExplorer.js';
import type { EditorEntity, EditorFx, EditorHud, EditorInstancedCollisionCandidate, EditorReference } from '../src/types/EditorRuntime.js';
import { createAssetPlacementCommand, createSkyShellAddCommand } from '../src/utils/AssetPlacement.ts';
import { BUILD_LAYERS } from '../src/utils/BuildLayers.ts';
import {
  collisionTypeId, collisionTypeIdOptions, defaultCollisionType, formatCollisionType,
  formatUyaCollisionTypeId, packCollisionType, soundTypeId,
} from '../src/utils/CollisionFormat.ts';
import { parseSplinePointId, removeSplinePoints, splinePointId } from '../src/utils/SplinePoints.ts';
import { applyTextureChannel, formatTextureDimensions } from '../src/utils/TexturePreview.ts';
import { recommendInstancedCollisionCandidate } from '../src/utils/InstancedCollision.ts';

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

test('shared build-layer allowlist includes FX textures', () => {
  assert.ok(BUILD_LAYERS.includes('Fx'));
});

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

test('HUD bank state exposes native placement, change, issue, and search metadata', () => {
  const sourceId = 'a'.repeat(64);
  const replacementId = 'b'.repeat(64);
  const hud: EditorHud = {
    canRead: true,
    isDirty: false,
    canReplace: true,
    canAppend: true,
    physicalBankCount: 5,
    minimumAppendBank: 3,
    minimumAppendSpriteId: 0xe000,
    maximumAppendSpriteId: 0xefff,
    maximumIconCount: 1_024,
    sourceIcons: [{
      sourceIconIndex: 4,
      spriteId: 0x1234,
      frames: [{
        sourceFrameIndex: 7,
        sourcePaletteIndex: 8,
        sourceTextureIndex: 9,
        paletteBankIndex: 1,
        textureBankIndex: 2,
        width: 32,
        height: 64,
        sourceTexture: { id: sourceId, kind: 'Texture' },
        effectiveTexture: { id: replacementId, kind: 'Texture' },
      }, {
        sourceFrameIndex: 10,
        sourcePaletteIndex: -1,
        sourceTextureIndex: 11,
        paletteBankIndex: -1,
        textureBankIndex: 2,
        width: 0,
        height: 0,
        diagnostic: 'Palette is missing.',
      }],
    }],
    additions: [{ spriteId: 0xe001, bankIndex: 4, width: 16, height: 16,
      texture: { id: 'c'.repeat(64), kind: 'Texture' } }],
  };
  assert.equal(buildHudBankItems(hud, '', 'all').length, 3);
  assert.equal(buildHudBankItems(hud, '1234', 'all')[0].state, 'override');
  assert.equal(buildHudBankItems(hud, '', 'changed').length, 2);
  assert.equal(buildHudBankItems(hud, 'palette', 'issues')[0].state, 'invalid');
  assert.equal(nextHudSpriteId(hud, 0xe000, 0xefff), 0xe000);
  assert.equal(formatHudSpriteId(0xe001), 'E001');
  assert.deepEqual(hudThumbnailDimensions(16, 16), { width: 32, height: 32 });
  assert.deepEqual(hudThumbnailDimensions(32, 32), { width: 64, height: 64 });
  assert.deepEqual(hudThumbnailDimensions(64, 64), { width: 128, height: 128 });
  assert.deepEqual(hudThumbnailDimensions(256, 64), { width: 300, height: 75 });

  const large = virtualGridWindow(1_024, 1_200, 650, 80_000, 280, 284);
  assert.ok(large.endIndex - large.startIndex <= 32);
});

test('HUD PNG preflight rejects decode limits before a project command', () => {
  const png = new Uint8Array(24);
  png.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  new DataView(png.buffer).setUint32(16, 64);
  new DataView(png.buffer).setUint32(20, 32);
  assert.deepEqual(validateHudPng(png), { valid: true, width: 64, height: 32 });
  new DataView(png.buffer).setUint32(16, 63);
  assert.match(validateHudPng(png).diagnostic ?? '', /powers of two/);
  assert.match(validateHudPng(new Uint8Array([1, 2, 3])).diagnostic ?? '', /valid PNG/);
});

test('FX texture state preserves source indexes and filters changes and issues', () => {
  const fx: EditorFx = {
    canRead: true,
    isDirty: true,
    canReplace: true,
    canAppend: true,
    maximumTextureCount: 4_096,
    sourceTextures: [{
      sourceIndex: 0,
      label: 'FX_LAME_SHADOW',
      width: 64,
      height: 32,
      paletteOffset: 0,
      pixelOffset: 0x400,
      isSwizzled: false,
      sourceTexture: { id: 'a'.repeat(64), kind: 'Texture' },
      effectiveTexture: { id: 'b'.repeat(64), kind: 'Texture' },
    }, {
      sourceIndex: 1,
      label: 'FX_CLOUDY_CIRCLE_1',
      width: 0,
      height: 0,
      paletteOffset: -1,
      pixelOffset: -1,
      isSwizzled: false,
      diagnostic: 'FX texture 1 is invalid.',
    }],
    additions: [{ width: 16, height: 16, texture: { id: 'c'.repeat(64), kind: 'Texture' } }],
  };
  assert.deepEqual(buildFxTextureItems(fx, '', 'all').map((value) => value.index), [0, 1, 2]);
  assert.equal(buildFxTextureItems(fx, '', 'changed').length, 2);
  assert.equal(buildFxTextureItems(fx, 'cloudy', 'issues')[0].index, 1);
  assert.equal(buildFxTextureItems(fx, 'FX_TEXTURE_2', 'all')[0].kind, 'addition');
});

test('FX PNG preflight accepts the native limit and rejects non-power-of-two dimensions', () => {
  const png = new Uint8Array(24);
  png.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const view = new DataView(png.buffer);
  view.setUint32(16, 4_096);
  view.setUint32(20, 2);
  assert.deepEqual(validateFxPng(png), { valid: true, width: 4_096, height: 2 });
  view.setUint32(16, 3);
  assert.match(validateFxPng(png).diagnostic ?? '', /powers of two/);
});

test('reference indexes and pages keep large graph work bounded', () => {
  const references: EditorReference[] = Array.from({ length: 25_000 }, (_, index) => ({
    ownerEntityId: `owner-${index % 5}`,
    domain: 'entity',
    fieldKey: 'geometry.area.splines',
    nullable: true,
    targetKind: 'spline',
    targetEntityId: `target-${index}`,
    sourceValue: index,
    missing: false,
  }));
  const index = buildReferenceIndex(references);
  assert.equal(index.outgoing.get('owner-0')?.length, 5_000);
  assert.equal(index.incoming.get('target-24999')?.[0].ownerEntityId, 'owner-4');
  const last = referencePage(index.outgoing.get('owner-0') ?? [], 49);
  assert.equal(last.values.length, 100);
  assert.equal(last.pageCount, 50);
});

test('reference target filtering uses typed kinds and collision source rules', () => {
  const entities = [
    { ...entity(1), asset: { id: 'tie', kind: 'Tie' } },
    { ...entity(2), asset: { id: 'shrub', kind: 'Shrub' } },
    { ...entity(3), asset: undefined, geometry: { kind: 'spline', points: [] } },
  ] satisfies EditorEntity[];
  assert.deepEqual(compatibleReferenceTargets({
    ownerEntityId: 'area', domain: 'entity', fieldKey: 'geometry.area.splines', nullable: true,
    targetKind: 'spline', sourceValue: 0, missing: false,
  }, entities).map((value) => value.id), ['3']);
  assert.deepEqual(compatibleReferenceTargets({
    ownerEntityId: 'collision', domain: 'entity', fieldKey: 'collision.attachment', nullable: false,
    targetKind: 'entity', missing: false,
  }, entities).map((value) => value.id), ['1', '2']);
});

test('reference navigation routes entities to the scene and assets to preview', () => {
  const entityReference: EditorReference = {
    ownerEntityId: 'owner', domain: 'entity', fieldKey: 'geometry.area.splines', nullable: true,
    targetKind: 'spline', targetEntityId: 'target', sourceValue: 7, missing: false,
  };
  const assetReference: EditorReference = {
    ownerEntityId: 'owner', domain: 'asset', fieldKey: 'entity.asset', nullable: true,
    targetKind: 'Moby', targetAssetId: 'asset', missing: false,
  };
  assert.deepEqual(referenceNavigationTarget(entityReference, 'outgoing'),
    { domain: 'entity', id: 'target', kind: 'spline' });
  assert.deepEqual(referenceNavigationTarget(entityReference, 'incoming'),
    { domain: 'entity', id: 'owner' });
  assert.deepEqual(referenceNavigationTarget(assetReference, 'outgoing'),
    { domain: 'asset', id: 'asset', kind: 'Moby' });
});

test('texture preview channels expose opaque color and alpha values', () => {
  const rgb = new Uint8ClampedArray([10, 20, 30, 40]);
  applyTextureChannel(rgb, 'rgb');
  assert.deepEqual([...rgb], [10, 20, 30, 255]);
  const alpha = new Uint8ClampedArray([10, 20, 30, 40]);
  applyTextureChannel(alpha, 'alpha');
  assert.deepEqual([...alpha], [40, 40, 40, 255]);
  assert.equal(formatTextureDimensions(64, 64, { width: 32, height: 32 }), '64 × 64 → 32 × 32');
  assert.equal(formatTextureDimensions(64, 64, { width: 64, height: 64 }), '64 × 64');
});

test('instanced collision recommendation respects hard failures and measured deviation', () => {
  const candidate = (
    token: string,
    worstBytes: number,
    maximumDeviation: number,
    hardViolationCount = 0,
  ): EditorInstancedCollisionCandidate => ({
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
  assert.equal(recommendInstancedCollisionCandidate(candidates, 4)?.token, 'best');
  assert.equal(recommendInstancedCollisionCandidate(candidates, 1), undefined);
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
