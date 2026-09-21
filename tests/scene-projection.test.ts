import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import { SceneProjection } from '../src/renderer/editor/SceneProjection.ts';
import type { EditorEntity } from '../src/types/EditorRuntime.js';
import { disposeObject, framePs2Positions, rotateCamera, updateCameraFlight, updateCameraMovement } from '../src/utils/Scene.ts';
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js';

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

test('scene projection merges matching parts, instances assets, and resolves instance picks', () => {
  const template = new THREE.Group();
  const geometry = new THREE.BoxGeometry();
  const material = new THREE.MeshBasicMaterial();
  const billboard = new THREE.Mesh(geometry, material);
  billboard.name = 'shrub_billboard';
  template.add(new THREE.Mesh(geometry, material), new THREE.SkinnedMesh(geometry, material), billboard);
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('a'), entity('b', 10)], ['b']);

  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  assert.equal(meshes.length, 1);
  const mesh = meshes[0];
  assert.equal(mesh.count, 2);
  assert.equal(mesh.geometry.getAttribute('position').count, geometry.getAttribute('position').count * 2);
  assert.equal(projection.resolvePick([{ object: mesh, instanceId: 1 }] as unknown as THREE.Intersection[]), 'b');
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 10) < 1e-6);

  projection.dispose();
  disposeObject(template);
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

test('scene disposal releases shared geometry, materials, and textures once', () => {
  const root = new THREE.Group();
  const geometry = new THREE.BoxGeometry();
  const texture = new THREE.Texture();
  const material = new THREE.MeshBasicMaterial({ map: texture });
  root.add(new THREE.Mesh(geometry, material), new THREE.Mesh(geometry, material));
  const disposals = { geometry: 0, material: 0, texture: 0 };
  geometry.addEventListener('dispose', () => { disposals.geometry += 1; });
  material.addEventListener('dispose', () => { disposals.material += 1; });
  texture.addEventListener('dispose', () => { disposals.texture += 1; });
  disposeObject(root);
  assert.deepEqual(disposals, { geometry: 1, material: 1, texture: 1 });
  assert.equal(root.children.length, 0);
});

test('camera movement accelerates and translates the camera and orbit target together', () => {
  const camera = new THREE.PerspectiveCamera();
  camera.lookAt(0, 0, -1);
  const target = new THREE.Vector3(0, 0, -10);
  const velocity = new THREE.Vector3();
  updateCameraMovement(camera, target, new Set(['KeyW']), velocity, 1 / 60);
  assert.ok(camera.position.z < 0 && camera.position.z > -0.1);
  assert.ok(Math.abs(camera.position.z - (target.z + 10)) < 1e-9);
  for (let index = 0; index < 300; index++)
    updateCameraMovement(camera, target, new Set(['KeyW']), velocity, 1 / 60);
  assert.ok(velocity.length() <= 300);
});

test('camera look rotates in place and carries the orbit target', () => {
  const camera = new THREE.PerspectiveCamera();
  camera.lookAt(0, 0, -1);
  const position = camera.position.clone();
  const target = new THREE.Vector3(0, 0, -10);
  rotateCamera(camera, target, 100, 0);
  assert.ok(camera.position.equals(position));
  assert.ok(Math.abs(target.length() - 10) < 1e-6);
  assert.ok(Math.abs(target.x) > 2);
});

test('camera flight eases to its exact destination', () => {
  const position = new THREE.Vector3();
  const target = new THREE.Vector3();
  const flight = {
    cameraStart: position.clone(),
    cameraEnd: new THREE.Vector3(10, 20, 30),
    targetStart: target.clone(),
    targetEnd: new THREE.Vector3(20, 10, 0),
    elapsed: 0,
    duration: 0.4,
  };
  assert.equal(updateCameraFlight(position, target, flight, 0.2), false);
  assert.deepEqual(position.toArray(), [5, 10, 15]);
  assert.deepEqual(target.toArray(), [10, 5, 0]);
  assert.equal(updateCameraFlight(position, target, flight, 0.2), true);
  assert.deepEqual(position.toArray(), flight.cameraEnd.toArray());
  assert.deepEqual(target.toArray(), flight.targetEnd.toArray());
});

test('camera framing ignores distant entity outliers', () => {
  const camera = new THREE.PerspectiveCamera();
  const controls = { target: new THREE.Vector3(), update() {} } as unknown as OrbitControls;
  const cluster = Array.from({ length: 20 }, (_, index) => ({ x: index, y: 0, z: 0 }));
  assert.equal(framePs2Positions(camera, controls, [...cluster, { x: 10_000, y: 0, z: 0 }]), true);
  assert.ok(controls.target.x < 20);
  assert.ok(camera.position.distanceTo(controls.target) < 50);
  assert.ok(camera.far > 19_000);
});
