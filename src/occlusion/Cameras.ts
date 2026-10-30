import type { OcclusionCameraView, OcclusionVector, OcclusionWorkerScene } from '../types/OcclusionWorker.js';

// Each basis contains right, up and forward in native game coordinates.
const cubeBases: OcclusionCameraView['basis'][] = [
  [[0, 0, -1], [0, 1, 0], [1, 0, 0]], [[0, 0, 1], [0, 1, 0], [-1, 0, 0]],
  [[1, 0, 0], [0, 0, -1], [0, 1, 0]], [[1, 0, 0], [0, 0, 1], [0, -1, 0]],
  [[1, 0, 0], [0, 1, 0], [0, 0, 1]], [[-1, 0, 0], [0, 1, 0], [0, 0, -1]],
];
export const cameraParameterSize = 176;
export const cameraParameterStride = 256;
export const rasterResolution = 1024;

function cameraBasis(direction: OcclusionVector): OcclusionCameraView['basis'] {
  const normalize = (vector: OcclusionVector): OcclusionVector => {
    const length = Math.hypot(...vector);
    return [vector[0] / length, vector[1] / length, vector[2] / length];
  };
  const cross = (a: OcclusionVector, b: OcclusionVector): OcclusionVector => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const forward = normalize(direction);
  const right = normalize(cross(Math.abs(forward[1]) > .9 ? [1, 0, 0] : [0, 1, 0], forward));
  return [right, cross(forward, right), forward];
}

export function cameraParameters(
  scene: Pick<OcclusionWorkerScene, 'checks' | 'spheres' | 'triangles' | 'barrierHeights' | 'pixelEdgeAllowance'>,
  samples: OcclusionVector[],
) {
  const views: OcclusionCameraView[] = samples.flatMap((origin, sample) => cubeBases.map((basis, face) => ({
    origin, sample, face, resolution: rasterResolution, basis,
  })));
  // An odd viewport puts the CPU reference ray exactly through its center pixel.
  scene.checks.forEach((check, i) => views.push({ origin: check.origin, sample: samples.length + i,
    face: 0, resolution: rasterResolution - 1, basis: cameraBasis(check.direction), check: true }));
  const bytes = new ArrayBuffer(views.length * cameraParameterStride), uniforms = new DataView(bytes);
  views.forEach((view, i) => {
    const offset = i * cameraParameterStride;
    [view.origin, ...view.basis].forEach((vector, j) => vector.forEach((value, axis) =>
      uniforms.setFloat32(offset + j * 16 + axis * 4, value, true)));
    [view.sample, view.face, view.resolution, scene.spheres.length, scene.triangles,
      samples.length + scene.checks.length, view.check ? 1 : 0, 0].forEach((value, j) =>
      uniforms.setUint32(offset + 64 + j * 4, value, true));
    const barriers = scene.barrierHeights ?? [];
    barriers.forEach((height, j) => uniforms.setFloat32(offset + 96 + j * 4, height, true));
    uniforms.setUint32(offset + 160, barriers.length, true);
    uniforms.setUint32(offset + 164, scene.pixelEdgeAllowance === true ? 1 : 0, true);
  });
  return { views, bytes };
}

export function sourceOrder(bytes: ArrayBuffer) {
  const triangles = new DataView(bytes), count = bytes.byteLength / 80;
  return Uint32Array.from({ length: count }, (_, i) => i).sort((a, b) =>
    triangles.getUint32(b * 80 + 76, true) - triangles.getUint32(a * 80 + 76, true) || b - a);
}
