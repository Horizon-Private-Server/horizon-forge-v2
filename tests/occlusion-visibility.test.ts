import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import type { OcclusionVisibilityData } from '../src/types/Occlusion.js';
import type { EditorEntity } from '../src/types/EditorRuntime.js';
import { ViewportOcclusion } from '../src/renderer/editor/ViewportOcclusion.ts';
import { assetTemplateKey, SceneProjection } from '../src/renderer/editor/SceneProjection.ts';

function fixture(): OcclusionVisibilityData {
  const masks = new Uint8Array(256); masks[0] = 1; masks[128] = 2;
  return { schemaVersion: 1, source: 'generated', gridHash: 'a'.repeat(64), masks,
    octants: [{ x: 0, y: 0, z: 0, maskIndex: 1 }, { x: 1, y: 0, z: 0, maskIndex: 0 },
      { x: -1, y: -1, z: -1, maskIndex: 0 }, { x: 1, y: 3, z: 2, maskIndex: 0 }],
    entities: [{ id: 'a', groups: [0] }, { id: 'b', groups: [1] }, { id: 'shared', groups: [0, 1] }] };
}

test('viewport selects native cell and mask independently, handles axes and boundaries, and fails open outside octants', () => {
  const view = new ViewportOcclusion(); view.setData(fixture());
  assert.equal(view.update({ x: 1, y: 1, z: -1 }, true, false), true);
  assert.equal(view.state.octant?.id, 0); assert.deepEqual([...view.hidden], ['a']);
  assert.equal(view.update({ x: 2, y: 2, z: -2 }, true, false), false);
  view.update({ x: 4, y: 1, z: -1 }, true, false);
  assert.equal(view.state.octant?.id, 1); assert.deepEqual([...view.hidden], ['b']);
  view.update({ x: -.01, y: -.01, z: .01 }, true, false);
  assert.equal(view.state.octant?.id, 2);
  view.update({ x: 6, y: 10, z: -14 }, true, false);
  assert.equal(view.state.octant?.id, 3);
  view.update({ x: 100, y: 100, z: 100 }, true, false);
  assert.equal(view.state.octant, undefined); assert.equal(view.hidden.size, 0);
  view.update({ x: 1, y: 1, z: -1 }, false, false);
  assert.equal(view.hidden.size, 0); assert.equal(view.hidden.has('unmapped-new-object'), false);
});

test('freeze holds the displayed octant while flying outside and survives mask reload; unfreeze and reset restore current visibility', () => {
  const view = new ViewportOcclusion(); view.setData(fixture());
  view.update({ x: 1, y: 1, z: -1 }, true, false);
  view.update({ x: 4, y: 1, z: -1 }, true, true);
  assert.equal(view.state.octant?.id, 0);
  view.update({ x: 100, y: 100, z: 100 }, true, true);
  assert.deepEqual([...view.hidden], ['a']);
  const next = fixture(); next.masks[128] = 1; view.setData(next);
  view.update({ x: 100, y: 100, z: 100 }, true, true);
  assert.equal(view.state.octant?.id, 0); assert.deepEqual([...view.hidden], ['b']);
  view.update({ x: 100, y: 100, z: 100 }, true, false);
  assert.equal(view.hidden.size, 0);
  view.reset(); view.update({ x: 1, y: 1, z: -1 }, true, false);
  assert.equal(view.hidden.size, 0);
});

test('simulation culling restores the simulator’s own visibility instead of unconditionally unhiding objects', () => {
  const view = new ViewportOcclusion(); view.setData(fixture()); view.update({ x: 1, y: 1, z: -1 }, true, false);
  const shown = new THREE.Group(), hidden = new THREE.Group(); hidden.visible = false;
  const objects = new Map([[shown, 'a'], [hidden, 'a']]);
  view.applySimulation(objects); assert.equal(shown.visible, false);
  view.restoreSimulation(); assert.equal(shown.visible, true); assert.equal(hidden.visible, false);
});

test('occlusion is an additional projection filter and preserves manual visibility and entity state', () => {
  const entity = (id: string, hidden: boolean): EditorEntity => ({ id, name: id, layer: 'ties',
    transform: { position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 } },
    transformModes: [], state: { dirty: false, hidden, disabled: false, locked: false, readOnly: false, invalid: false, missingAsset: false } });
  const entities = [entity('a', false), entity('b', true)], before = structuredClone(entities), projection = new SceneProjection();
  projection.sync(entities); assert.equal(projection.getObject('a')?.visible, true);
  projection.setOcclusionHiddenEntities(new Set(['a']));
  assert.equal(projection.getObject('a')?.visible, false);
  projection.setOcclusionHiddenEntities(new Set());
  assert.equal(projection.getObject('a')?.visible, true); assert.equal(projection.getObject('b')?.visible, false);
  assert.deepEqual(entities, before); projection.dispose();
});

test('octant transitions update model/terrain visibility and picking without revisiting static collision', () => {
  const make = (id: string): EditorEntity => ({ id, name: id, layer: 'ties',
    asset: { id: 'model', kind: 'Tie' }, transformModes: [],
    transform: { position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 } },
    state: { dirty: false, hidden: false, disabled: false, locked: false, readOnly: false, invalid: false, missingAsset: false } });
  const model = make('model'), manuallyHidden = make('manual'), mirrored = make('mirror'), terrain = make('terrain');
  manuallyHidden.state.hidden = true; mirrored.transform.scale.x = -1;
  terrain.layer = 'tfrags'; terrain.asset = { id: 'terrain', kind: 'Tfrag' };
  terrain.tfrag = { sourceArchive: 'level', sourceKind: 'primary', sourceIndex: 0, groupIndex: 0,
    occlusionIndex: 0, occlusionIndexStash: 0, textureIds: [] };
  const collision = Array.from({ length: 100 }, (_, i) => {
    const e = make(`collision-${i}`); e.layer = 'collision'; e.asset = { id: 'collision', kind: 'collision' };
    e.collision = { kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: i, faceCount: 1, vertexCount: 3, types: [] };
    return e;
  });
  const entities = [model, manuallyHidden, mirrored, terrain, ...collision];
  const geometry = new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute([0, 0, 0, 1, 0, 0, 0, 1, 0], 3));
  const material = new THREE.MeshBasicMaterial(), templates = new Map<string, THREE.Object3D>();
  for (const e of entities) templates.set(assetTemplateKey(e)!, new THREE.Mesh(geometry, material));
  const projection = new SceneProjection(); projection.setAssetTemplates(templates); projection.sync(entities, ['terrain']);
  const instanced: THREE.InstancedMesh[] = [], batches: THREE.BatchedMesh[] = [];
  projection.root.traverse(o => { if (o instanceof THREE.InstancedMesh) instanced.push(o); if (o instanceof THREE.BatchedMesh) batches.push(o); });
  const normal = instanced.find(m => m.userData.forgeInstanceIds.includes('model'))!;
  const mirror = instanced.find(m => m.userData.forgeInstanceIds.includes('mirror'))!;
  const batch = batches.find(m => m.userData.forgeInstanceIds.includes('terrain'))!;
  const terrainIndex = (batch.userData.forgeInstanceIds as string[]).indexOf('terrain');
  let collisionReads = 0, batchMatrixWrites = 0;
  for (const e of collision) { const transform = e.transform; Object.defineProperty(e, 'transform', { get: () => { collisionReads++; return transform; } }); }
  const setMatrix = batch.setMatrixAt.bind(batch); batch.setMatrixAt = (i, matrix) => { batchMatrixWrites++; return setMatrix(i, matrix); };
  const hidden = new Set(['model', 'mirror', 'terrain']); projection.setOcclusionHiddenEntities(hidden);
  assert.equal(normal.count, 0); assert.equal(mirror.count, 0); assert.equal(batch.getVisibleAt(terrainIndex), false);
  assert.equal(projection.getSelectionOutlineObjects().length, 0);
  assert.equal(projection.resolvePick([{ object: batch, batchId: terrainIndex }] as unknown as THREE.Intersection[]), undefined);
  hidden.clear(); projection.setOcclusionHiddenEntities(hidden);
  assert.equal(normal.count, 1); assert.equal(mirror.count, 1); assert.equal(batch.getVisibleAt(terrainIndex), true);
  assert.deepEqual(normal.userData.forgeInstanceIds, ['model']);
  assert.equal(projection.getSelectionOutlineObjects().length, 1);
  assert.equal(projection.resolvePick([{ object: batch, batchId: terrainIndex }] as unknown as THREE.Intersection[]), 'terrain');
  assert.equal(collisionReads, 0); assert.equal(batchMatrixWrites, 0);
  projection.dispose(); geometry.dispose(); material.dispose();
});
