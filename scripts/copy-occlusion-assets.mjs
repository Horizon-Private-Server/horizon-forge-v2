import { cp, mkdir, rm } from 'node:fs/promises';

const directory = 'dist-electron/occlusion';
await mkdir(directory, { recursive: true });
// Remove worker modules left by builds before the TypeScript migration.
await Promise.all(['Scene', 'Cameras', 'Gpu', 'RasterBounds', 'SpatialGpu', 'Worker']
  .map(name => rm(`${directory}/${name}.mjs`, { force: true })));
await Promise.all(['index.html', 'raster.wgsl', 'spatial.wgsl']
  .map(name => cp(`src/occlusion/${name}`, `${directory}/${name}`)));
