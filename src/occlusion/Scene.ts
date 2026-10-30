import type { OcclusionVector, OcclusionWorkerResult, OcclusionWorkerScene } from '../types/OcclusionWorker.js';

export const profile = 'raster1024-lod0-uniform-samples-no-feather-v21';
export const limits = Object.freeze({ cells: 100_000, origins: 250_000,
  sceneBytes: 32 * 1024 * 1024, resultBytes: 128 * 1024 * 1024 });
export const hex = (bytes: ArrayBuffer | Uint8Array<ArrayBuffer>) => Array.from(new Uint8Array(bytes), value => value.toString(16).padStart(2, '0')).join('');
export const hash = async (bytes: ArrayBuffer) => hex(await crypto.subtle.digest('SHA-256', bytes));

export function base64(bytes: Uint8Array<ArrayBuffer>) {
  const data = new Uint8Array(bytes);
  let text = '';
  for (let i = 0; i < data.length; i += 32768)
    text += String.fromCharCode(...data.subarray(i, i + 32768));
  return btoa(text);
}

const vector = (value: unknown): value is OcclusionVector => Array.isArray(value) && value.length === 3
  && value.every(component => Number.isFinite(component) && Math.abs(component) <= 1_000_000);
const maskHex = (value: string) => /^[a-f0-9]{256}$/.test(value);

export function validateScene(value: unknown, triangles: ArrayBuffer, textures: ArrayBuffer, alpha: ArrayBuffer): asserts value is OcclusionWorkerScene {
  const scene = value as OcclusionWorkerScene;
  if (scene?.schemaVersion !== 1 || scene.profile !== profile || !/^[a-f0-9]{64}$/.test(scene.inputHash)
    || scene.pixelEdgeAllowance !== undefined && typeof scene.pixelEdgeAllowance !== 'boolean'
    || !Number.isInteger(scene.triangles) || scene.triangles < 1 || scene.triangles > 1_000_000
    || triangles.byteLength !== scene.triangles * 80
    || textures.byteLength % 16 || textures.byteLength > 65_536
    || alpha.byteLength > 67_108_864 || alpha.byteLength % 4
    || !Array.isArray(scene.spheres) || scene.spheres.length > 100_000
    || !Array.isArray(scene.checks) || scene.checks.length < 1 || scene.checks.length > 16
    || !Array.isArray(scene.origins) || scene.origins.length < 1 || scene.origins.length > limits.origins
    || !Array.isArray(scene.cells) || scene.cells.length < 1 || scene.cells.length > limits.cells
    || !maskHex(scene.fallbackMask))
    throw Error('Invalid occlusion scene or resource limit exceeded.');

  if (!scene.spheres.every(sphere => vector(sphere.center) && Number.isFinite(sphere.radius)
      && sphere.radius > 0 && sphere.radius <= 100_000
      && Number.isInteger(sphere.group) && sphere.group >= 0 && sphere.group < 1023)
    || !scene.checks.every(check => vector(check.origin) && vector(check.direction)
      && Math.abs(Math.hypot(...check.direction) - 1) < .001 && maskHex(check.geometry) && maskHex(check.mobys))
    || !scene.origins.every(vector)
    || new Set(scene.origins.map(point => point.join(','))).size !== scene.origins.length)
    throw Error('Invalid occlusion scene or resource limit exceeded.');

  const barriers = scene.barrierHeights;
  if (barriers != null && (!Array.isArray(barriers) || barriers.length > 16
    || !barriers.every(height => Number.isFinite(height) && Math.abs(height) <= 1_000_000)
    || new Set(barriers).size !== barriers.length))
    throw Error('Invalid occlusion scene or resource limit exceeded.');
  if (new Set(scene.cells.map(cell => [cell.x, cell.y, cell.z].join(','))).size !== scene.cells.length
    || !scene.cells.every(cell => [cell.x, cell.y, cell.z].every(value => Number.isInteger(value) && Math.abs(value) <= 250_000)
      && typeof cell.reservedVisible === 'boolean' && Array.isArray(cell.samples) && [9, 13].includes(cell.samples.length)
      && cell.samples.every(id => Number.isInteger(id) && id >= 0 && id < scene.origins.length)
      && (cell.inwardCorners === undefined
        || Number.isInteger(cell.inwardCorners) && cell.inwardCorners >= 0 && cell.inwardCorners <= 255)))
    throw Error('Invalid occlusion scene or resource limit exceeded.');

  for (const cell of scene.cells) {
    const minimum = [cell.x * 4, cell.y * 4, cell.z * 4];
    for (let slot = 0; slot < cell.samples.length; slot++) {
      const point = scene.origins[cell.samples[slot]];
      if (point.some((value, axis) => slot === 0
        ? value !== minimum[axis] + 2 : value < minimum[axis] || value > minimum[axis] + 4))
        throw Error('Occlusion cameras do not match their octant.');
    }
  }
  validateTextures(textures, alpha.byteLength);
  validateTriangles(triangles, textures.byteLength / 16);
}

function validateTextures(bytes: ArrayBuffer, alphaBytes: number) {
  const textures = new DataView(bytes);
  let end = 0;
  for (let i = 0; i < bytes.byteLength; i += 16) {
    const offset = textures.getUint32(i, true);
    const width = textures.getUint32(i + 4, true), height = textures.getUint32(i + 8, true);
    if (offset !== end || width < 1 || height < 1 || width > 4096 || height > 4096
      || offset + width * height > alphaBytes)
      throw Error('Invalid alpha atlas.');
    end = offset + width * height;
  }
}

function validateTriangles(bytes: ArrayBuffer, textureCount: number) {
  const triangles = new DataView(bytes);
  for (let i = 0; i < bytes.byteLength; i += 80) {
    for (const offset of [0, 4, 8, 16, 20, 24, 32, 36, 40, 48, 52, 56, 60, 64, 68]) {
      const value = triangles.getFloat32(i + offset, true);
      if (!Number.isFinite(value) || Math.abs(value) > 1_000_000) throw Error('Invalid triangle coordinate.');
    }
    const group = triangles.getInt32(i + 12, true), texture = triangles.getInt32(i + 44, true);
    const flags = triangles.getUint32(i + 28, true), wrapFlags = triangles.getUint32(i + 72, true);
    const rank = triangles.getUint32(i + 76, true);
    if (group < -1 || group > 1023 || texture < -1 || texture >= textureCount
      || flags > 3 || wrapFlags > 3 || rank < 1 || rank > 1_000_000)
      throw Error('Invalid triangle metadata.');
  }
}

export function validateResult(value: unknown, scene: OcclusionWorkerScene, sceneHash: string): asserts value is OcclusionWorkerResult {
  const result = value as OcclusionWorkerResult;
  const validMask = (value: unknown, size: number) => typeof value === 'string' && value.length === 4 * Math.ceil(size / 3)
    && /^[A-Za-z0-9+/]*={0,2}$/.test(value) && atob(value).length === size;
  if (result?.schemaVersion !== 2 || result.profile !== profile || result.sceneHash !== sceneHash
    || result.inputHash !== scene.inputHash || result.origins !== scene.origins.length || result.octants !== scene.cells.length
    || !Number.isFinite(result.wallMs) || result.wallMs < 0
    || result.gpuMs !== null && (!Number.isFinite(result.gpuMs) || result.gpuMs < 0)
    || !validMask(result.sampleMasks, scene.origins.length * 256)
    || result.batches !== Math.ceil(scene.origins.length / 256))
    throw Error('Incomplete occlusion bake result.');
}
