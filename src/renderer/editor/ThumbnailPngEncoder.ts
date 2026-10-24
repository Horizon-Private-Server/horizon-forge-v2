import type { ThumbnailPngRequest, ThumbnailPngResponse } from '../../types/ThumbnailPngEncoder.js';

export class ThumbnailPngEncoder {
  private worker?: Worker;
  private nextId = 0;
  private disposed = false;
  private readonly pending = new Map<number, { resolve(blob: Blob): void; reject(error: Error): void }>();

  async encode(canvas: HTMLCanvasElement): Promise<Blob> {
    if (this.disposed) return Promise.reject(new DOMException('Asset preview cancelled', 'AbortError'));
    // Capture immediately; the shared WebGL canvas can be redrawn before encoding finishes.
    const snapshot = new OffscreenCanvas(canvas.width, canvas.height);
    const context = snapshot.getContext('2d');
    if (!context) return Promise.reject(new Error('Thumbnail PNG canvas is unavailable'));
    context.drawImage(canvas, 0, 0);
    const bitmap = snapshot.transferToImageBitmap();
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      try {
        if (!this.worker) {
          // Encoding in the renderer waits for idle time and stalls under sustained map drawing.
          const worker = new Worker(new URL('./ThumbnailPngEncoder.worker.ts', import.meta.url), { type: 'module' });
          this.worker = worker;
          worker.onmessage = (event: MessageEvent<ThumbnailPngResponse>) => {
            const job = this.pending.get(event.data.id);
            if (!job) return;
            this.pending.delete(event.data.id);
            if ('blob' in event.data) job.resolve(event.data.blob);
            else job.reject(new Error(event.data.error));
          };
          worker.onerror = () => {
            if (this.worker === worker) this.fail(new Error('Thumbnail PNG worker failed'));
          };
          worker.onmessageerror = () => {
            if (this.worker === worker) this.fail(new Error('Thumbnail PNG worker response is invalid'));
          };
        }
        this.pending.set(id, { resolve, reject });
        this.worker.postMessage({ id, bitmap } satisfies ThumbnailPngRequest, [bitmap]);
      } catch (error) {
        this.pending.delete(id);
        bitmap.close();
        reject(error);
      }
    });
  }

  dispose(): void {
    this.disposed = true;
    this.fail(new DOMException('Asset preview cancelled', 'AbortError'));
  }

  private fail(error: Error): void {
    this.worker?.terminate();
    this.worker = undefined;
    for (const job of this.pending.values()) job.reject(error);
    this.pending.clear();
  }
}
