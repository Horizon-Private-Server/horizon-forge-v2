import { lstat, readFile } from 'node:fs/promises';
import path from 'node:path';
import { setImmediate } from 'node:timers/promises';

import type { OcclusionVisibilityData } from '../types/Occlusion.js';

export async function readOcclusionVisibility(directory: string, signal: AbortSignal): Promise<OcclusionVisibilityData> {
  const file = path.join(directory, 'visibility.json'), stat = await lstat(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 64 * 1024 * 1024)
    throw new Error('Occlusion visibility exceeds its resource budget or is not a regular file.');
  const bytes = await readFile(file);
  if (bytes.length > 64 * 1024 * 1024) throw new Error('Occlusion visibility exceeds its resource budget.');
  const data = JSON.parse(bytes.toString()) as Omit<OcclusionVisibilityData, 'masks'> & { masks: string };
  if (data?.schemaVersion !== 1 || !['vanilla', 'generated'].includes(data.source) || !/^[a-f0-9]{64}$/.test(data.gridHash)
    || !Array.isArray(data.octants) || data.octants.length > 100_000
    || !Array.isArray(data.entities) || data.entities.length > 100_000
    || typeof data.masks !== 'string' || data.masks.length > 24 * 1024 * 1024
    || data.masks.length % 4 || !/^[A-Za-z0-9+/]*={0,2}$/.test(data.masks))
    throw new Error('Invalid occlusion visibility data.');
  const masks = Buffer.from(data.masks, 'base64'), coordinates = new Set<string>(), ids = new Set<string>();
  if (masks.length > 16 * 1024 * 1024 || masks.length % 128 || masks.toString('base64') !== data.masks)
    throw new Error('Invalid occlusion mask table.');
  for (let i = 0; i < data.octants.length; i++) {
    if ((i & 255) === 0) { await setImmediate(); signal.throwIfAborted(); }
    const c = data.octants[i];
    if (!c || ![c.x, c.y, c.z].every(v => Number.isInteger(v) && Math.abs(v) <= 250_000)
      || !Number.isInteger(c.maskIndex) || c.maskIndex < 0 || (c.maskIndex + 1) * 128 > masks.length
      || coordinates.has([c.x, c.y, c.z].join(','))) throw new Error('Invalid occlusion visibility octant.');
    coordinates.add([c.x, c.y, c.z].join(','));
  }
  for (let i = 0; i < data.entities.length; i++) {
    if ((i & 255) === 0) { await setImmediate(); signal.throwIfAborted(); }
    const e = data.entities[i];
    if (!e || typeof e.id !== 'string' || !/^(?:[a-f0-9]{32}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})$/i.test(e.id) || ids.has(e.id)
      || !Array.isArray(e.groups) || !e.groups.length || e.groups.length > 1023
      || !e.groups.every(g => Number.isInteger(g) && g >= 0 && g < 1023)) throw new Error('Invalid occlusion visibility entity.');
    ids.add(e.id);
  }
  signal.throwIfAborted();
  return { ...data, masks: new Uint8Array(masks) };
}
