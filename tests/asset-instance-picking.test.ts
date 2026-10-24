import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import { refreshAssetPickIndex, updateAssetPickBounds } from '../src/renderer/editor/AssetInstancePicking.ts';
import { configureAssetMaterialPasses } from '../src/renderer/editor/AssetMaterialPasses.ts';

function fixture(batched: boolean, stablePasses = false) {
  const box = new THREE.BoxGeometry(2, 2, 2);
  if (!batched) {
    const source = box.getAttribute('position');
    const data = new Float32Array(source.count * 4);
    for (let i = 0; i < source.count; i++) data.set([source.getX(i), source.getY(i), source.getZ(i)], i * 4);
    box.setAttribute('position', new THREE.InterleavedBufferAttribute(new THREE.InterleavedBuffer(data, 4), 3, 0));
  }
  const sphere = new THREE.SphereGeometry(1.2, 8, 6);
  const material = new THREE.MeshBasicMaterial({ side: THREE.DoubleSide, transparent: true });
  const mesh = batched
    ? new THREE.BatchedMesh(128, 512, 1_024, material)
    : new THREE.InstancedMesh<THREE.BufferGeometry, THREE.Material | THREE.Material[]>(box, material, 128);
  const geometries = mesh instanceof THREE.BatchedMesh ? [mesh.addGeometry(box), mesh.addGeometry(sphere)] : [];
  const matrices: THREE.Matrix4[] = [];
  for (let id = 0; id < 128; id++) {
    if (mesh instanceof THREE.BatchedMesh) mesh.addInstance(geometries[id % 2]);
    const matrix = new THREE.Matrix4().compose(
      new THREE.Vector3((id % 16) * 4, Math.floor(id / 16) * 4, id % 3),
      new THREE.Quaternion().setFromEuler(new THREE.Euler(id % 3 * .2, id % 5 * .1, id % 7 * .1)),
      new THREE.Vector3(id % 5 === 0 ? -1 : 1, 1.5, .7),
    );
    mesh.setMatrixAt(id, matrix);
    matrices.push(matrix);
    if (mesh instanceof THREE.BatchedMesh && id % 11 === 0) mesh.setVisibleAt(id, false);
  }
  if (mesh instanceof THREE.InstancedMesh) mesh.computeBoundingSphere();
  const parent = new THREE.Group();
  parent.position.set(-30, -10, 3);
  parent.rotation.set(.1, .2, .3);
  parent.scale.set(1.2, .8, 1.4);
  parent.add(mesh);
  parent.updateMatrixWorld(true);
  const owned = new Set<THREE.Material>([material]);
  if (stablePasses) configureAssetMaterialPasses(mesh, owned);
  refreshAssetPickIndex(mesh);
  const native = mesh instanceof THREE.InstancedMesh
    ? THREE.InstancedMesh.prototype.raycast : THREE.BatchedMesh.prototype.raycast;
  const compare = (raycaster: THREE.Raycaster) => {
    const expected: THREE.Intersection[] = [];
    const actual: THREE.Intersection[] = [];
    const renderMaterial = mesh.material;
    mesh.material = stablePasses ? material : renderMaterial;
    try { native.call(mesh, raycaster, expected); }
    finally { mesh.material = renderMaterial; }
    mesh.raycast(raycaster, actual);
    assert.deepEqual(actual, expected);
    return expected.length;
  };
  let disposed = false;
  const disposeMesh = () => {
    if (disposed) return;
    mesh.dispose();
    disposed = true;
  };
  return {
    mesh, box, material, matrices, parent, compare, native, disposeMesh,
    dispose() { disposeMesh(); box.dispose(); sphere.dispose(); owned.forEach(value => value.dispose()); },
  };
}

for (const batched of [false, true]) {
  test(`${batched ? 'batched' : 'instanced'} stable transparent passes preserve indexed two-sided hits`, (t) => {
    const f = fixture(batched, true);
    t.after(() => f.dispose());
    const front = f.mesh.material as THREE.Material;
    assert.equal(front.side, THREE.FrontSide);
    let hits = 0;
    for (let i = 0; i < 128; i++) {
      const center = new THREE.Vector3().setFromMatrixPosition(f.matrices[i]).applyMatrix4(f.mesh.matrixWorld);
      for (const side of [-1, 1]) hits += f.compare(new THREE.Raycaster(
        center.clone().add(new THREE.Vector3(0, 0, 80 * side)), new THREE.Vector3(0, 0, -side),
      ));
    }
    assert.ok(hits > 200);
    assert.equal(f.mesh.material, front);
    if (f.mesh instanceof THREE.InstancedMesh) {
      f.mesh.count = 1;
      refreshAssetPickIndex(f.mesh);
      const center = new THREE.Vector3().setFromMatrixPosition(f.matrices[0]).applyMatrix4(f.mesh.matrixWorld);
      f.compare(new THREE.Raycaster(center.clone().add(new THREE.Vector3(0, 0, -80)), new THREE.Vector3(0, 0, 1)));
      assert.equal(f.mesh.material, front);
    }
  });

  test(`${batched ? 'batched' : 'instanced'} hierarchy retains planar edge and corner hits`, (t) => {
    const geometry = new THREE.PlaneGeometry(2, 2);
    const material = new THREE.MeshBasicMaterial({ side: THREE.DoubleSide });
    const mesh = batched ? new THREE.BatchedMesh(64, 4, 6, material)
      : new THREE.InstancedMesh(geometry, material, 64);
    const geometryId = mesh instanceof THREE.BatchedMesh ? mesh.addGeometry(geometry) : 0;
    for (let id = 0; id < 64; id++) {
      if (mesh instanceof THREE.BatchedMesh) mesh.addInstance(geometryId);
      mesh.setMatrixAt(id, new THREE.Matrix4().compose(
        new THREE.Vector3(id % 8 * 4, Math.floor(id / 8) * 4, 0),
        new THREE.Quaternion().setFromEuler(new THREE.Euler(.7, .1, .2)), new THREE.Vector3(1.2, .9, 1),
      ));
    }
    mesh.position.set(-10, 3, 5);
    mesh.rotation.set(.13, .63, -.21);
    mesh.scale.set(1.1, .5, 2);
    mesh.updateMatrixWorld(true);
    if (mesh instanceof THREE.InstancedMesh) mesh.computeBoundingSphere();
    refreshAssetPickIndex(mesh);
    t.after(() => { mesh.dispose(); geometry.dispose(); material.dispose(); });
    const native = mesh instanceof THREE.InstancedMesh ? THREE.InstancedMesh.prototype.raycast : THREE.BatchedMesh.prototype.raycast;
    let hitCount = 0;
    for (let id = 0; id < 64; id++) for (const x of [-1, 0, 1]) for (const y of [-1, 0, 1]) {
      const matrix = mesh.getMatrixAt(id, new THREE.Matrix4()).premultiply(mesh.matrixWorld);
      const point = new THREE.Vector3(x, y, 0).applyMatrix4(matrix);
      const normal = new THREE.Vector3(0, 0, 1).applyNormalMatrix(new THREE.Matrix3().getNormalMatrix(matrix));
      const raycaster = new THREE.Raycaster(point.clone().addScaledVector(normal, 10), normal.clone().negate());
      const expected: THREE.Intersection[] = [];
      const actual: THREE.Intersection[] = [];
      native.call(mesh, raycaster, expected);
      mesh.raycast(raycaster, actual);
      assert.deepEqual(actual, expected);
      hitCount += expected.length;
    }
    assert.ok(hitCount > 100);
  });

  test(`${batched ? 'batched' : 'instanced'} hierarchy preserves full native hit data, ranges, ties, clipping and transforms`, (t) => {
    const f = fixture(batched);
    t.after(() => f.dispose());
    if (!batched) assert.equal(f.box.boundingBox, null);
    let hits = 0;
    for (let i = 0; i < 400; i++) {
      const id = i % f.matrices.length;
      const center = new THREE.Vector3().setFromMatrixPosition(f.matrices[id]).applyMatrix4(f.mesh.matrixWorld);
      const origin = center.clone().add(new THREE.Vector3(i % 3 - 1, i % 5 - 2, 100));
      const raycaster = new THREE.Raycaster(origin, center.clone().sub(origin).normalize(), i % 19 === 0 ? 105 : 0,
        i % 17 === 0 ? 95 : Infinity);
      hits += f.compare(raycaster);
    }
    assert.ok(hits > 200);
    const duplicate = f.matrices[1].clone();
    f.mesh.setMatrixAt(3, duplicate);
    updateAssetPickBounds(f.mesh, 3, duplicate);
    if (f.mesh instanceof THREE.InstancedMesh) f.mesh.computeBoundingSphere();
    refreshAssetPickIndex(f.mesh);
    const center = new THREE.Vector3().setFromMatrixPosition(duplicate).applyMatrix4(f.mesh.matrixWorld);
    f.compare(new THREE.Raycaster(center.clone().add(new THREE.Vector3(0, 0, 100)), new THREE.Vector3(0, 0, -1)));
    for (let i = 0; i < 100; i++) {
      const direction = new THREE.Vector3(Math.sin(i), Math.cos(i), Math.sin(i * .7)).normalize();
      f.compare(new THREE.Raycaster(center, direction));
    }
    f.parent.scale.set(-.8, 1.3, 1.1);
    f.parent.rotation.y += .5;
    f.parent.updateMatrixWorld(true);
    for (let i = 0; i < 128; i++) {
      const target = new THREE.Vector3().setFromMatrixPosition(f.matrices[i]).applyMatrix4(f.mesh.matrixWorld);
      f.compare(new THREE.Raycaster(target.clone().add(new THREE.Vector3(0, 0, 80)), new THREE.Vector3(0, 0, -1)));
    }
    f.parent.scale.x = 0;
    f.parent.updateMatrixWorld(true);
    f.compare(new THREE.Raycaster(new THREE.Vector3(0, 0, 100), new THREE.Vector3(0, 0, -1)));
  });

  test(`${batched ? 'batched' : 'instanced'} hierarchy refits moves and bulk edits without changing native results`, (t) => {
    const f = fixture(batched);
    t.after(() => f.dispose());
    const moved = new THREE.Matrix4().makeTranslation(300, 200, 100);
    f.mesh.setMatrixAt(1, moved);
    updateAssetPickBounds(f.mesh, 1, moved);
    if (f.mesh instanceof THREE.InstancedMesh) f.mesh.computeBoundingSphere();
    refreshAssetPickIndex(f.mesh);
    const target = new THREE.Vector3(300, 200, 100).applyMatrix4(f.mesh.matrixWorld);
    assert.ok(f.compare(new THREE.Raycaster(target.clone().add(new THREE.Vector3(0, 0, 100)), new THREE.Vector3(0, 0, -1))) > 0);
    for (let i = 0; i < 128; i++) {
      f.mesh.setMatrixAt(i, f.matrices[127 - i]);
      updateAssetPickBounds(f.mesh, i, f.matrices[127 - i]);
    }
    if (f.mesh instanceof THREE.InstancedMesh) f.mesh.computeBoundingSphere();
    refreshAssetPickIndex(f.mesh);
    if (f.mesh instanceof THREE.BatchedMesh) for (let i = 0; i < 128; i++) f.mesh.setVisibleAt(i, i % 3 === 0);
    for (let i = 0; i < 128; i++) {
      const point = new THREE.Vector3().setFromMatrixPosition(f.matrices[i]).applyMatrix4(f.mesh.matrixWorld);
      f.compare(new THREE.Raycaster(point.clone().add(new THREE.Vector3(0, 0, 100)), new THREE.Vector3(0, 0, -1)));
    }
    if (f.mesh instanceof THREE.InstancedMesh) {
      f.mesh.count = 8;
      f.mesh.computeBoundingSphere();
      refreshAssetPickIndex(f.mesh);
      f.compare(new THREE.Raycaster(new THREE.Vector3(0, 0, 100), new THREE.Vector3(0, 0, -1)));
      f.mesh.count = 128;
      f.mesh.computeBoundingSphere();
      refreshAssetPickIndex(f.mesh);
      f.compare(new THREE.Raycaster(new THREE.Vector3(0, 0, 100), new THREE.Vector3(0, 0, -1)));
    }
    let geometryDisposals = 0;
    let materialDisposals = 0;
    f.box.addEventListener('dispose', () => geometryDisposals++);
    f.material.addEventListener('dispose', () => materialDisposals++);
    f.disposeMesh();
    assert.equal(f.mesh.raycast, f.native);
    assert.equal(geometryDisposals, 0);
    assert.equal(materialDisposals, 0);
  });
}

test('indexed instances retain material-group and face-side behavior', (t) => {
  const f = fixture(false);
  t.after(() => f.dispose());
  const materials = Array.from({ length: 6 }, (_, i) => new THREE.MeshBasicMaterial({
    side: i % 3 === 0 ? THREE.FrontSide : i % 3 === 1 ? THREE.BackSide : THREE.DoubleSide,
  }));
  t.after(() => materials.forEach(material => material.dispose()));
  assert.ok(f.mesh instanceof THREE.InstancedMesh);
  f.mesh.material = materials;
  let hits = 0;
  for (const matrix of f.matrices) {
    const point = new THREE.Vector3().setFromMatrixPosition(matrix).applyMatrix4(f.mesh.matrixWorld);
    hits += f.compare(new THREE.Raycaster(point.clone().add(new THREE.Vector3(0, 0, 100)), new THREE.Vector3(0, 0, -1)));
  }
  assert.ok(hits > 100);
});

test('stable transparent passes retain shader hooks, back/front order, overrides and material versions', () => {
  const geometry = new THREE.PlaneGeometry();
  const source = new THREE.MeshBasicMaterial({ transparent: true, side: THREE.DoubleSide });
  source.onBeforeCompile = () => {};
  source.customProgramCacheKey = () => 'asset-test';
  const mesh = new THREE.Mesh(geometry, source);
  const sibling = new THREE.Mesh(geometry, source);
  const stages: string[] = [];
  source.onBeforeRender = () => { stages.push('prepare'); };
  const before = mesh.onBeforeRender;
  const owned = new Set<THREE.Material>([source]);
  configureAssetMaterialPasses(mesh, owned);
  configureAssetMaterialPasses(sibling, owned);
  assert.equal(mesh.material, sibling.material);
  assert.equal(mesh.onBeforeRender, before, 'object callbacks and batched draw-list preparation are retained');
  assert.equal(owned.size, 3);
  const front = mesh.material;
  const back = [...owned].find(material => material.side === THREE.BackSide)!;
  for (const material of [front, back]) {
    assert.equal(material.onBeforeCompile, source.onBeforeCompile);
    assert.equal(material.customProgramCacheKey(), 'asset-test');
  }
  mesh.position.set(3, 4, 5);
  mesh.updateMatrixWorld(true);
  const camera = new THREE.PerspectiveCamera();
  camera.position.z = 20;
  camera.updateMatrixWorld(true);
  const expected = new THREE.Matrix4().multiplyMatrices(camera.matrixWorldInverse, mesh.matrixWorld);
  mesh.modelViewMatrix.copy(expected);
  mesh.normalMatrix.getNormalMatrix(expected);
  const scene = new THREE.Scene();
  const renderer = { renderBufferDirect(_camera: unknown, _scene: unknown, _geometry: unknown, material: THREE.Material) {
    assert.equal(material, back);
    assert.ok(mesh.modelViewMatrix.equals(expected));
    stages.push('back');
  } } as unknown as THREE.WebGLRenderer;
  const versions = [...owned].map(material => material.version);
  for (let i = 0; i < 5; i++) {
    front.onBeforeRender(renderer, scene, camera, geometry, mesh, null as unknown as THREE.Group);
    stages.push('front');
  }
  assert.deepEqual(stages, Array(5).fill(['prepare', 'back', 'front']).flat());
  assert.deepEqual([...owned].map(material => material.version), versions);
  stages.length = 0;
  source.onBeforeRender(renderer, scene, camera, geometry, mesh, null as unknown as THREE.Group);
  assert.deepEqual(stages, ['prepare'], 'override materials do not receive an extra backface draw');
  owned.forEach(material => material.dispose());
  geometry.dispose();
});

test('stable-pass picking restores the render material when raycasting fails', () => {
  const geometry = new THREE.PlaneGeometry();
  const source = new THREE.MeshBasicMaterial({ transparent: true, side: THREE.DoubleSide });
  const mesh = new THREE.Mesh(geometry, source);
  mesh.raycast = () => { assert.equal(mesh.material, source); throw Error('pick failure'); };
  const owned = new Set<THREE.Material>([source]);
  configureAssetMaterialPasses(mesh, owned);
  const front = mesh.material;
  assert.throws(() => mesh.raycast(new THREE.Raycaster(), []), /pick failure/);
  assert.equal(mesh.material, front);
  owned.forEach(material => material.dispose());
  geometry.dispose();
});

test('opaque, one-sided, explicit single-pass and material-array assets retain their existing path', () => {
  const geometry = new THREE.PlaneGeometry();
  const materials = [
    new THREE.MeshBasicMaterial({ side: THREE.DoubleSide }),
    new THREE.MeshBasicMaterial({ transparent: true }),
    new THREE.MeshBasicMaterial({ transparent: true, side: THREE.DoubleSide, forceSinglePass: true }),
  ];
  for (const material of [...materials, materials]) {
    const mesh = new THREE.Mesh(geometry, material);
    const before = mesh.onBeforeRender;
    const raycast = mesh.raycast;
    const owned = new Set<THREE.Material>();
    configureAssetMaterialPasses(mesh, owned);
    assert.equal(mesh.material, material);
    assert.equal(mesh.onBeforeRender, before);
    assert.equal(mesh.raycast, raycast);
    assert.equal(owned.size, 0);
  }
  geometry.dispose();
  materials.forEach(material => material.dispose());
});
