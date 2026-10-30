export function rasterBatches(bytes: ArrayBuffer, order: Uint32Array<ArrayBuffer>, size = 128) {
  if (bytes.byteLength % 80 || order.length !== bytes.byteLength / 80 || !Number.isInteger(size) || size < 1)
    throw Error('Invalid raster batch geometry');
  const count = Math.ceil(order.length / size), out = new ArrayBuffer(count * 32);
  const target = new DataView(out), source = new DataView(bytes);
  for (let batch = 0; batch < count; batch++) {
    const first = batch * size, length = Math.min(size, order.length - first);
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    for (let j = first; j < first + length; j++) {
      const index = order[j];
      if (index >= order.length) throw Error('Raster order outside geometry');
      for (let axis = 0; axis < 3; axis++) {
        const a = source.getFloat32(index * 80 + axis * 4, true);
        const b = a + source.getFloat32(index * 80 + 16 + axis * 4, true);
        const c = a + source.getFloat32(index * 80 + 32 + axis * 4, true);
        lo[axis] = Math.min(lo[axis], a, b, c);
        hi[axis] = Math.max(hi[axis], a, b, c);
      }
    }
    for (let axis = 0; axis < 3; axis++) {
      // Expand before packing to cover float32 edge sums and camera subtraction.
      const pad = .002 + Math.max(Math.abs(lo[axis]), Math.abs(hi[axis])) * .000001;
      target.setFloat32(batch * 32 + axis * 4, lo[axis] - pad, true);
      target.setFloat32(batch * 32 + 16 + axis * 4, hi[axis] + pad, true);
    }
    target.setUint32(batch * 32 + 12, first, true);
    target.setUint32(batch * 32 + 28, length, true);
  }
  return out;
}
