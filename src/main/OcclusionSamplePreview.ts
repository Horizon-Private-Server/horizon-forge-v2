import { lstat, readFile } from 'node:fs/promises';
import path from 'node:path';
import { setImmediate } from 'node:timers/promises';

import type { OcclusionSamplePreview } from '../types/Occlusion.js';

export async function readOcclusionSamplePreview(directory: string, signal: AbortSignal): Promise<OcclusionSamplePreview> {
  const file = path.join(directory, 'scene.json'), stat = await lstat(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 32 * 1024 * 1024)
    throw new Error('Occlusion sample preview exceeds its resource budget or is not a regular file.');
  const bytes = await readFile(file);
  if (bytes.length > 32 * 1024 * 1024) throw new Error('Occlusion sample preview exceeds its resource budget.');
  const scene = JSON.parse(bytes.toString()) as Partial<OcclusionSamplePreview> & { schemaVersion?: number };
  if (scene?.schemaVersion !== 1 || typeof scene.profile !== 'string' || !scene.profile.length || scene.profile.length > 128
    || !Array.isArray(scene.origins) || scene.origins.length < 1 || scene.origins.length > 250_000
    || !Array.isArray(scene.cells) || scene.cells.length < 1 || scene.cells.length > 100_000)
    throw new Error('Invalid occlusion sample preview.');
  const origins = scene.origins, cells: OcclusionSamplePreview['cells'] = [], positions = new Set<string>(), coordinates = new Set<string>();
  for (let i = 0; i < origins.length; i++) {
    if ((i & 4095) === 0) { await setImmediate(); signal.throwIfAborted(); }
    const p = origins[i];
    if (!Array.isArray(p) || p.length !== 3 || p.some(v => !Number.isFinite(v) || Math.abs(v) > 1_000_000)
      || positions.has(p.join(','))) throw new Error('Invalid or duplicate occlusion sample position.');
    positions.add(p.join(','));
  }
  for (let i = 0; i < scene.cells.length; i++) {
    if ((i & 255) === 0) { await setImmediate(); signal.throwIfAborted(); }
    const c = scene.cells[i];
    if (!c || ![c.x, c.y, c.z].every(v => Number.isInteger(v) && Math.abs(v) <= 250_000)
      || !Array.isArray(c.samples) || ![9, 13].includes(c.samples.length)
      || c.samples.some(id => !Number.isInteger(id) || id < 0 || id >= origins.length)
      || c.inwardCorners !== undefined && (!Number.isInteger(c.inwardCorners) || c.inwardCorners < 0 || c.inwardCorners > 255)
      || coordinates.has([c.x, c.y, c.z].join(','))) throw new Error('Invalid occlusion sample cell.');
    const minimum = [c.x * 4, c.y * 4, c.z * 4];
    if (c.samples.some((id, slot) => origins[id].some((v, a) => slot === 0
      ? v !== minimum[a] + 2 : v < minimum[a] || v > minimum[a] + 4)))
      throw new Error('Occlusion sample lies outside its cell.');
    coordinates.add([c.x, c.y, c.z].join(','));
    cells.push({ x: c.x, y: c.y, z: c.z, samples: c.samples,
      ...(c.inwardCorners === undefined ? {} : { inwardCorners: c.inwardCorners }) });
  }
  signal.throwIfAborted();
  return { profile: scene.profile, origins, cells };
}
