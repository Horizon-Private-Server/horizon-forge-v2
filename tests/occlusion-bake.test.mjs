import test from 'node:test';
import assert from 'node:assert/strict';
import { profile, validateScene, validateResult, base64, limits } from '../dist-electron/occlusion/Scene.js';
import { cameraParameters } from '../dist-electron/occlusion/Cameras.js';
function fixture() {
  const origins = [[2, 2, 2]];
  for (const z of [0, 4])
    for (const y of [0, 4])
      for (const x of [0, 4])
        origins.push([x, y, z]);
  const scene = { schemaVersion: 1, profile, inputHash: 'a'.repeat(64), triangles: 1, origins, cells: [{ x: 0, y: 0, z: 0, reservedVisible: false, samples: origins.map((_, i) => i) }], spheres: [], checks: [{ origin: [2, 2, 2], direction: [0, 0, 1], geometry: '00'.repeat(128), mobys: '00'.repeat(128) }], fallbackMask: '04' + '00'.repeat(127) };
  const triangles = new ArrayBuffer(80), v = new DataView(triangles);
  v.setInt32(12, 5, true);
  v.setUint32(28, 3, true);
  v.setInt32(44, -1, true);
  v.setUint32(76, 1, true);
  return { scene, triangles, textures: new ArrayBuffer(0), alpha: new ArrayBuffer(0) };
}
test('shared production scene validates native centers, qualified corners and bounded alpha ranges', () => {
  const f = fixture();
  validateScene(f.scene, f.triangles, f.textures, f.alpha);
  for (const outdated of ['obsolete-profile', `${profile}-unexpected`, 'raster1024-lod0-uniform-samples-no-feather-v20', 'raster1024-lod0-inward-local-corners-no-feather-v19'])
    assert.throws(() => validateScene({ ...f.scene, profile: outdated }, f.triangles, f.textures, f.alpha), /Invalid/);
  f.scene.origins[1] = [1, 1, 1];
  f.scene.cells[0].samples[8] = 0;
  validateScene(f.scene, f.triangles, f.textures, f.alpha);
  f.scene.origins[1] = [-.001, 1, 1];
  assert.throws(() => validateScene(f.scene, f.triangles, f.textures, f.alpha), /cameras/);
  const shifted = fixture();
  shifted.scene.origins[0] = [2, 2, 2.01];
  assert.throws(() => validateScene(shifted.scene, shifted.triangles, shifted.textures, shifted.alpha), /cameras/);
  const g = fixture();
  new DataView(g.triangles).setInt32(44, 1, true);
  assert.throws(() => validateScene(g.scene, g.triangles, g.textures, g.alpha), /metadata/);
  const h = fixture();
  h.scene.origins[0][0] = Infinity;
  assert.throws(() => validateScene(h.scene, h.triangles, h.textures, h.alpha), /Invalid/);
});
test('horizontal barrier heights are finite, distinct and bounded', () => {
  const f = fixture();
  for (const barrierHeights of [null, [], [61.35], [-1000000, 0, 1000000]])
    validateScene({ ...f.scene, barrierHeights }, f.triangles, f.textures, f.alpha);
  for (const barrierHeights of [[NaN], [Infinity], [1000001], [0, 0], Array.from({ length: 17 }, (_, i) => i), '61.35'])
    assert.throws(() => validateScene({ ...f.scene, barrierHeights }, f.triangles, f.textures, f.alpha), /Invalid/);
});
test('pixel edge allowance accepts optional booleans and rejects other values', () => {
  const f = fixture();
  for (const pixelEdgeAllowance of [undefined, true, false])
    validateScene({ ...f.scene, pixelEdgeAllowance }, f.triangles, f.textures, f.alpha);
  for (const pixelEdgeAllowance of [null, 0, 1, 'false', []])
    assert.throws(() => validateScene({ ...f.scene, pixelEdgeAllowance }, f.triangles, f.textures, f.alpha), /Invalid/);
});
test('inward corner diagnostics are bounded to the eight native corner bits', () => {
  const f = fixture();
  for (const inwardCorners of [undefined, 0, 13, 255]) {
    f.scene.cells[0].inwardCorners = inwardCorners;
    validateScene(f.scene, f.triangles, f.textures, f.alpha);
  }
  for (const inwardCorners of [-1, 256, 1.5, null, '13']) {
    f.scene.cells[0].inwardCorners = inwardCorners;
    assert.throws(() => validateScene(f.scene, f.triangles, f.textures, f.alpha), /Invalid/);
  }
});
test('qualified cells accept four extra edge views while retaining octant bounds', () => {
  const f = fixture(), m = f.scene;
  m.origins.push([0, 0, 2], [4, 0, 2], [0, 4, 2], [4, 4, 2]);
  m.cells[0].samples.push(9, 10, 11, 12);
  validateScene(m, f.triangles, f.textures, f.alpha);
  for (const length of [10, 11, 12, 14])
    assert.throws(() => validateScene({ ...m, cells: [{ ...m.cells[0], samples: Array(length).fill(0) }] }, f.triangles, f.textures, f.alpha), /Invalid/);
  m.origins[12] = [4, 4, 5];
  assert.throws(() => validateScene(m, f.triangles, f.textures, f.alpha), /cameras/);
});
test('result validation accepts sample-only results and rejects incomplete masks or changed identity', () => {
  const { scene } = fixture(), samples = new Uint8Array(scene.origins.length * 256);
  const r = { schemaVersion: 2, profile, inputHash: scene.inputHash, sceneHash: 'b'.repeat(64), origins: scene.origins.length, octants: 1, batches: 1, wallMs: 1, gpuMs: null, sampleMasks: base64(samples) };
  validateResult(r, scene, r.sceneHash);
  assert.equal(Object.hasOwn(r, 'cellMasks'), false);
  for (const changed of [{ schemaVersion: 1 }, { batches: 0 }, { sampleMasks: '' },
    { sampleMasks: '!'.repeat(r.sampleMasks.length) }, { gpuMs: NaN }, { origins: 0 }])
    assert.throws(() => validateResult({ ...r, ...changed }, scene, r.sceneHash), /Incomplete/);
  assert.throws(() => validateResult(r, scene, 'c'.repeat(64)), /Incomplete/);
});

test('production cameras retain the calibrated cube bases, stride and odd CPU check viewport', () => {
  const { scene } = fixture(), r = cameraParameters(scene, [[2, 2, 2]]), v = new DataView(r.bytes);
  assert.equal(r.views.length, 7);
  assert.equal(r.bytes.byteLength, 7 * 256);
  assert.deepEqual(r.views[0].basis, [[0, 0, -1], [0, 1, 0], [1, 0, 0]]);
  assert.equal(v.getUint32(6 * 256 + 72, true), 1023);
  assert.equal(v.getUint32(6 * 256 + 88, true), 1);
  assert.equal(v.getUint32(92, true), 0);
  assert.equal(v.getUint32(6 * 256 + 92, true), 0);
});
test('negative-coordinate centers use the same visibility flags and camera bases as corners', () => {
  const { scene } = fixture(), r = cameraParameters(scene, [[-2, 6, 10], [0, 4, 8]]), v = new DataView(r.bytes);
  for (let face = 0; face < 6; face++) {
    assert.equal(v.getUint32(face * 256 + 92, true), 0);
    assert.equal(v.getUint32((6 + face) * 256 + 92, true), 0);
    assert.deepEqual(r.views[face].basis, r.views[6 + face].basis);
  }
  assert.equal(v.getUint32(12 * 256 + 92, true), 0);
});
test('large stock-style maps validate beyond the old cell, camera and result-size caps', () => {
  const f = fixture(), m = f.scene, origins = [], ids = new Map(), cells = [];
  function sample(x, y, z) {
    const key = [x, y, z].join(','); let id = ids.get(key); if (id === undefined) {
      id = origins.length;
      ids.set(key, id);
      origins.push([x, y, z]);
    } return id;
  }
  for (let i = 0; i < 40000; i++) {
    const x = i % 40, y = Math.floor(i / 40) % 40, z = Math.floor(i / 1600), samples = [sample(x * 4 + 2, y * 4 + 2, z * 4 + 2)];
    for (const dz of [0, 4])
      for (const dy of [0, 4])
        for (const dx of [0, 4])
          samples.push(sample(x * 4 + dx, y * 4 + dy, z * 4 + dz));
    cells.push({ x, y, z, reservedVisible: false, samples });
  }
  m.origins = origins;
  m.cells = cells;
  assert.equal(origins.length, 83706);
  validateScene(m, f.triangles, f.textures, f.alpha);
  const samples = new Uint8Array(origins.length * 256);
  samples[0] = 8;
  const r = { schemaVersion: 2, profile, inputHash: m.inputHash, sceneHash: 'b'.repeat(64), origins: origins.length, octants: cells.length, batches: Math.ceil(origins.length / 256), wallMs: 1, gpuMs: null, sampleMasks: base64(samples) };
  const bytes = Buffer.byteLength(JSON.stringify(r));
  assert.ok(bytes < 32 * 1024 * 1024);
  assert.ok(bytes < limits.resultBytes);
  validateResult(r, m, r.sceneHash);
  assert.throws(() => validateScene({ ...m, origins: new Array(limits.origins + 1) }, f.triangles, f.textures, f.alpha), /Invalid/);
  assert.throws(() => validateScene({ ...m, cells: new Array(limits.cells + 1) }, f.triangles, f.textures, f.alpha), /Invalid/);
});
