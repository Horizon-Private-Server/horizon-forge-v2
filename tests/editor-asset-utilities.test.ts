import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { AssetThumbnailCache } from '../src/main/AssetThumbnailCache.ts';
import type { AssetExplorerItem } from '../src/types/AssetExplorer.js';
import type { EditorInstancedCollisionCandidate } from '../src/types/EditorRuntime.js';
import { createAssetPlacementCommand, createSkyShellAddCommand } from '../src/utils/AssetPlacement.ts';
import {
  assetExplorerFilterCount, assetExplorerQueryKey, buildAssetFamilies,
  isStaleAssetExplorerCursor, retainAssetExplorerPageDepth,
} from '../src/utils/AssetExplorer.ts';
import { recommendInstancedCollisionCandidate } from '../src/utils/InstancedCollision.ts';
import { applyTextureChannel, formatTextureDimensions } from '../src/utils/TexturePreview.ts';

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

