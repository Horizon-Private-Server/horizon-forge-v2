import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import { SceneProjection } from '../src/renderer/editor/SceneProjection.ts';
import type { EditorEntity } from '../src/types/EditorRuntime.js';

function entity(id: string, x = 0, asset = true, state: Partial<EditorEntity['state']> = {}): EditorEntity {
  return {
    id,
    name: `Entity ${id}`,
    layer: 'mobys',
    transform: {
      position: { x, y: 2, z: 3 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
    asset: asset ? { id: 'asset', kind: 'moby' } : undefined,
    state: {
      dirty: false, hidden: false, disabled: false, locked: false, invalid: false, missingAsset: false, ...state,
    },
  };
}

test('scene projection diffs entities by ID and updates transforms in place', () => {
  const projection = new SceneProjection();
  assert.deepEqual(projection.sync([entity('a'), entity('b', 10, false)]), {
    created: 2, updated: 0, removed: 0,
  });

  const first = projection.getObject('a') as THREE.Mesh;
  const geometry = first.geometry;
  assert.equal(projection.resolveEntityId(first), 'a');
  assert.deepEqual(projection.sync([entity('a', 20), entity('c')]), {
    created: 1, updated: 1, removed: 1,
  });
  assert.equal(projection.getObject('a'), first);
  assert.equal((projection.getObject('a') as THREE.Mesh).geometry, geometry);
  assert.equal(projection.getObject('a')?.position.x, 20);
  assert.equal(projection.getObject('a')?.position.y, 3);
  assert.equal(projection.getObject('a')?.position.z, -2);
  assert.equal(projection.getObject('b'), undefined);
  assert.deepEqual(projection.sync([entity('a', 20), entity('c')]), {
    created: 0, updated: 0, removed: 0,
  });
  projection.dispose();
});

test('scene projection converts PS2 Z-up transforms to Three.js Y-up', () => {
  const projection = new SceneProjection();
  const value = entity('rotated');
  const rotation = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 0, 1), Math.PI / 2);
  value.transform.rotation = { x: rotation.x, y: rotation.y, z: rotation.z, w: rotation.w };
  value.transform.scale = { x: 2, y: 3, z: 4 };
  projection.sync([value]);

  const object = projection.getObject(value.id)!;
  const rotatedX = new THREE.Vector3(1, 0, 0).applyQuaternion(object.quaternion);
  assert.ok(rotatedX.distanceTo(new THREE.Vector3(0, 0, -1)) < 1e-6);
  assert.deepEqual(object.scale.toArray(), [2, 4, 3]);
  projection.dispose();
});

test('scene projection applies entity states and picks the nearest eligible entity', () => {
  const projection = new SceneProjection();
  const selected = entity('selected');
  const normal = entity('normal');
  const locked = entity('locked', 0, true, { locked: true });
  const hidden = entity('hidden', 0, true, { hidden: true });
  const disabled = entity('disabled', 0, true, { disabled: true });
  projection.sync([selected, normal, locked, hidden, disabled], [selected.id]);

  assert.notEqual(
    (projection.getObject(selected.id) as THREE.Mesh).material,
    (projection.getObject(normal.id) as THREE.Mesh).material,
  );
  assert.equal(projection.getObject(locked.id)?.visible, true);
  assert.equal(projection.getObject(hidden.id)?.visible, false);
  assert.equal(projection.getObject(disabled.id)?.visible, false);
  const intersections = [locked, hidden, disabled, normal].map((value) => ({
    object: projection.getObject(value.id)!,
  })) as unknown as THREE.Intersection[];
  assert.equal(projection.resolvePick(intersections), normal.id);
  assert.equal(projection.resolvePick(intersections.slice(0, 3)), undefined);
  projection.dispose();
});

test('projection and scene resources dispose once across repeated loads', () => {
  for (let index = 0; index < 25; index += 1) {
    const projection = new SceneProjection();
    projection.sync([entity(String(index))]);
    const object = projection.getObject(String(index)) as THREE.Mesh;
    let geometryDisposals = 0;
    let materialDisposals = 0;
    object.geometry.addEventListener('dispose', () => { geometryDisposals += 1; });
    (object.material as THREE.Material).addEventListener('dispose', () => { materialDisposals += 1; });
    projection.dispose();
    projection.dispose();
    assert.equal(geometryDisposals, 1);
    assert.equal(materialDisposals, 1);
    assert.equal(projection.root.children.length, 0);
  }
});
