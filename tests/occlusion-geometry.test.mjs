import test from 'node:test';
import assert from 'node:assert/strict';
import { rasterBatches } from '../dist-electron/occlusion/RasterBounds.js';
import { cameraParameters, cameraParameterSize, sourceOrder } from '../dist-electron/occlusion/Cameras.js';
test('spatial batches conservatively contain float32 vertices without reordering', () => {
  const raw = new ArrayBuffer(257 * 80), v = new DataView(raw), order = Uint32Array.from({ length: 257 }, (_, i) => 256 - i);
  for (let i = 0; i < 257; i++)
    for (let axis = 0; axis < 3; axis++) {
      v.setFloat32(i * 80 + axis * 4, (i - 128) * 7000 + axis * .01, true);
      v.setFloat32(i * 80 + 16 + axis * 4, (axis - 1) * 300.333, true);
      v.setFloat32(i * 80 + 32 + axis * 4, (1 - axis) * 1234.777, true);
    }
  const before = order.slice(), packed = rasterBatches(raw, order), bounds = new DataView(packed);
  assert.deepEqual(order, before);
  assert.equal(packed.byteLength, 3 * 32);
  for (let batch = 0; batch < 3; batch++) {
    const first = bounds.getUint32(batch * 32 + 12, true), count = bounds.getUint32(batch * 32 + 28, true);
    assert.equal(first, batch * 128);
    assert.equal(count, batch === 2 ? 1 : 128);
    for (let j = first; j < first + count; j++)
      for (let axis = 0; axis < 3; axis++) {
        const a = v.getFloat32(order[j] * 80 + axis * 4, true), points = [a, Math.fround(a + v.getFloat32(order[j] * 80 + 16 + axis * 4, true)), Math.fround(a + v.getFloat32(order[j] * 80 + 32 + axis * 4, true))];
        for (const p of points)
          assert.ok(p >= bounds.getFloat32(batch * 32 + axis * 4, true) && p <= bounds.getFloat32(batch * 32 + 16 + axis * 4, true));
      }
  }
  assert.throws(() => rasterBatches(raw, order, 0));
  assert.throws(() => rasterBatches(raw, order.subarray(1)));
  const invalid = order.slice();
  invalid[0] = 257;
  assert.throws(() => rasterBatches(raw, invalid));
});
test('production cube cameras preserve ray directions on all six faces', () => {
  const { views } = cameraParameters({ checks: [], spheres: [], triangles: 1 }, [[2, 2, 2]]);
  const u = -.375, v = .625;
  for (let face = 0; face < 6; face++) {
    const [right, up, forward] = views[face].basis, point = forward.map((x, i) => x + u * right[i] + v * up[i]);
    const expected = [[1, v, -u], [-1, v, u], [u, 1, -v], [u, -1, v], [u, v, 1], [-u, v, -1]][face];
    assert.deepEqual(point, expected);
    const dot = (a, b) => a.reduce((s, x, i) => s + x * b[i], 0);
    assert.equal(dot(point, right), u);
    assert.equal(dot(point, up), v);
    assert.equal(dot(point, forward), 1);
  }
});
test('source ordering retains opaque depth tie precedence', () => {
  const bytes = new ArrayBuffer(4 * 80), v = new DataView(bytes);
  [3, 1, 3, 2].forEach((rank, i) => v.setUint32(i * 80 + 76, rank, true));
  assert.deepEqual([...sourceOrder(bytes)], [2, 0, 3, 1]);
});
test('barrier uniforms reach every cube face and calibration view without changing the 256-byte stride', () => {
  const m = { checks: [{ origin: [1, 2, 3], direction: [0, 0, 1] }], spheres: [], triangles: 1, barrierHeights: Array.from({ length: 16 }, (_, i) => i - 8) };
  const { views, bytes } = cameraParameters(m, [[2, 2, 2], [4, 4, 4]]), v = new DataView(bytes);
  assert.equal(cameraParameterSize, 176);
  assert.equal(bytes.byteLength, views.length * 256);
  for (let i = 0; i < views.length; i++) {
    assert.equal(v.getUint32(i * 256 + 160, true), 16);
    for (let j = 0; j < 16; j++)
      assert.equal(v.getFloat32(i * 256 + 96 + j * 4, true), m.barrierHeights[j]);
  }
  const plain = cameraParameters({ ...m, barrierHeights: null }, [[2, 2, 2]]);
  assert.equal(new DataView(plain.bytes).getUint32(160, true), 0);
});
test('center and edge samples share visibility rules while pixel edge allowance and calibration flags remain independent', () => {
  const scene = { checks: [{ origin: [2, 2, 2], direction: [0, 0, 1] }], spheres: [], triangles: 1 };
  for (const enabled of [undefined, true, false]) {
    const { bytes, views } = cameraParameters({ ...scene, pixelEdgeAllowance: enabled }, [[2, 2, 2], [0, 0, 0]]), v = new DataView(bytes);
    for (let i = 0; i < views.length; i++)
      assert.equal(v.getUint32(i * 256 + 164, true), enabled === true ? 1 : 0);
    for (let i = 0; i < views.length; i++)
      assert.equal(v.getUint32(i * 256 + 92, true), 0);
    assert.equal(v.getUint32(12 * 256 + 88, true), 1);
    assert.equal(bytes.byteLength, views.length * 256);
  }
});
test('translated cube cameras cover the full sphere through shared 90-degree edges', () => {
  const origins = [[102, 202, 302], [-14, 10, 6]], { views, bytes } = cameraParameters({ checks: [], spheres: [], triangles: 1 }, origins), uniforms = new DataView(bytes);
  const dot = (a, b) => a.reduce((s, x, i) => s + x * b[i], 0);
  for (let sample = 0; sample < origins.length; sample++) {
    const cube = views.slice(sample * 6, sample * 6 + 6);
    for (let face = 0; face < 6; face++) {
      const [right, up, forward] = cube[face].basis, offset = (sample * 6 + face) * 256;
      assert.deepEqual([0, 4, 8].map(o => uniforms.getFloat32(offset + o, true)), origins[sample]);
      assert.equal(cube[face].resolution, 1024);
      assert.equal(dot(right, forward), 0);
      assert.equal(dot(up, forward), 0);
      assert.equal(dot(right, up), 0);
      assert.equal(Math.atan2(Math.hypot(...right), Math.hypot(...forward)), Math.PI / 4);
    }
    for (const x of [-1, -.5, 0, .5, 1])
      for (const y of [-1, -.5, 0, .5, 1])
        for (const z of [-1, -.5, 0, .5, 1]) {
          if (x === 0 && y === 0 && z === 0)
            continue;
          assert.ok(cube.some(({ basis: [right, up, forward] }) => {
            const direction = [x, y, z], depth = dot(direction, forward);
            return depth > 0 && Math.abs(dot(direction, right)) <= depth && Math.abs(dot(direction, up)) <= depth;
          }), `uncovered direction ${[x, y, z]}`);
        }
  }
});
