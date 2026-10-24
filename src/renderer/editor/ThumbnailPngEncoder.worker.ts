import type { ThumbnailPngRequest, ThumbnailPngResponse } from '../../types/ThumbnailPngEncoder.js';

self.addEventListener('message', async (event: MessageEvent<ThumbnailPngRequest>) => {
  const { id, bitmap } = event.data;
  let response: ThumbnailPngResponse;
  try {
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    const context = canvas.getContext('2d');
    if (!context) throw new Error('Thumbnail PNG canvas is unavailable');
    context.drawImage(bitmap, 0, 0);
    response = { id, blob: await canvas.convertToBlob({ type: 'image/png' }) };
  } catch (error) {
    response = { id, error: error instanceof Error ? error.message : 'Thumbnail PNG encoding failed' };
  } finally {
    bitmap.close();
  }
  self.postMessage(response);
});
