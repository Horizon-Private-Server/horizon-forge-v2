import type { OcclusionWorkerResult } from '../types/OcclusionWorker.js';
import { hash, base64, profile, validateScene } from './Scene.js';
import { createWorker, sampleBatchSize } from './Gpu.js';

const api = window.occlusion;
try {
  const start = performance.now();
  const raw = await (await fetch('./scene.json')).arrayBuffer();
  const sceneHash = await hash(raw), scene: unknown = JSON.parse(new TextDecoder().decode(raw));
  const names = ['triangles', 'textures', 'alpha'] as const;
  const [triangles, textures, alpha] = await Promise.all(names.map(async name =>
    (await fetch(`./${name}.bin`)).arrayBuffer()));
  validateScene(scene, triangles, textures, alpha);
  for (const [i, bytes] of [triangles, textures, alpha].entries())
    if (await hash(bytes) !== scene[`${names[i]}Hash`]) throw Error('Occlusion scene hash mismatch.');
  const worker = await createWorker({ m: scene, triangles, textures, alpha }, api);
  const masks = new Uint8Array(scene.origins.length * 256);
  let gpuMs: number | null = 0, batches = 0;
  try {
    for (let first = 0; first < scene.origins.length; first += sampleBatchSize) {
      if (api.cancelled()) throw Error('Occlusion bake cancelled.');
      const samples = scene.origins.slice(first, first + sampleBatchSize);
      const result = await worker.run(samples);
      masks.set(result.masks, first * 256);
      gpuMs = result.gpuMs === null || gpuMs === null ? null : gpuMs + result.gpuMs;
      batches++;
      api.progress({ completed: first + samples.length, total: scene.origins.length,
        message: `Calculating occlusion: ${first + samples.length}/${scene.origins.length} shared camera positions.` });
    }
    const result: OcclusionWorkerResult = { schemaVersion: 2, profile, inputHash: scene.inputHash, sceneHash,
      origins: scene.origins.length, octants: scene.cells.length, batches, wallMs: 0, gpuMs,
      sampleMasks: base64(masks), adapter: worker.adapter };
    result.wallMs = performance.now() - start;
    await api.finish(result);
  } finally {
    worker.destroy();
  }
} catch (error) {
  api.fail(error instanceof Error ? error.stack || error.message : String(error));
}
