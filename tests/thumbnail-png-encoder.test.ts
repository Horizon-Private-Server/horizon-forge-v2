import assert from 'node:assert/strict';
import test from 'node:test';
import { ThumbnailPngEncoder } from '../src/renderer/editor/ThumbnailPngEncoder.ts';
import type { ThumbnailPngResponse } from '../src/types/ThumbnailPngEncoder.js';

test('PNG worker owns distinct snapshots, routes overlapping results, and rejects abandoned work', async () => {
  const workerDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'Worker');
  const canvasDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'OffscreenCanvas');
  const snapshots: Array<{ close(): void; closed: number }> = [];
  const workers: FakeWorker[] = [];
  let failPost = false;
  class FakeWorker {
    onmessage?: (event: { data: ThumbnailPngResponse }) => void;
    onerror?: () => void;
    onmessageerror?: () => void;
    terminated = false;
    requests: Array<{ id: number; bitmap: unknown }> = [];
    constructor() { workers.push(this); }
    postMessage(request: { id: number; bitmap: unknown }, transfer: unknown[]) {
      assert.deepEqual(transfer, [request.bitmap]);
      if (failPost) throw new Error('Transfer failed');
      this.requests.push(request);
    }
    terminate() { this.terminated = true; }
    reply(id: number, blob: Blob) { this.onmessage?.({ data: { id, blob } }); }
  }
  class FakeCanvas {
    getContext() { return { drawImage() {} }; }
    transferToImageBitmap() {
      const bitmap = { closed: 0, close() { this.closed++; } };
      snapshots.push(bitmap);
      return bitmap;
    }
  }
  Object.defineProperty(globalThis, 'Worker', { configurable: true, value: FakeWorker });
  Object.defineProperty(globalThis, 'OffscreenCanvas', { configurable: true, value: FakeCanvas });
  const encoder = new ThumbnailPngEncoder();
  const canvas = { width: 256, height: 256 } as HTMLCanvasElement;
  try {
    const first = encoder.encode(canvas);
    const second = encoder.encode(canvas);
    assert.equal(workers.length, 1);
    assert.notEqual(snapshots[0], snapshots[1]);
    const [a, b] = workers[0].requests;
    const firstBlob = new Blob(['first']);
    const secondBlob = new Blob(['second']);
    workers[0].reply(b.id, secondBlob);
    workers[0].reply(a.id, firstBlob);
    assert.equal(await first, firstBlob);
    assert.equal(await second, secondBlob);

    const rejected = assert.rejects(encoder.encode(canvas), /Encoding failed/);
    workers[0].onmessage?.({ data: { id: workers[0].requests.at(-1)!.id, error: 'Encoding failed' } });
    await rejected;
    const failed = assert.rejects(encoder.encode(canvas), /worker failed/);
    workers[0].onerror?.();
    await failed;
    assert.equal(workers[0].terminated, true);
    failPost = true;
    await assert.rejects(encoder.encode(canvas), /Transfer failed/);
    assert.equal(snapshots.at(-1)!.closed, 1, 'Failed transfers release the untransferred bitmap');
    failPost = false;
    workers[0].onerror?.();
    assert.equal(workers.at(-1)!.terminated, false, 'A stale worker error cannot terminate its replacement');
    const abandoned = assert.rejects(encoder.encode(canvas), { name: 'AbortError' });
    encoder.dispose();
    await abandoned;
    assert.equal(workers.at(-1)!.terminated, true);
    await assert.rejects(encoder.encode(canvas), { name: 'AbortError' });
  } finally {
    encoder.dispose();
    for (const [name, descriptor] of [['Worker', workerDescriptor], ['OffscreenCanvas', canvasDescriptor]] as const) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else Reflect.deleteProperty(globalThis, name);
    }
  }
});
