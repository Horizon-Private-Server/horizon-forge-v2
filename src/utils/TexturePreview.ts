export type TextureChannel = 'rgba' | 'rgb' | 'red' | 'green' | 'blue' | 'alpha';

export async function prepareTextureImage(
  file: File,
  validate: (bytes: Uint8Array) => { valid: boolean; width: number; height: number; diagnostic?: string },
) {
  const bytes = new Uint8Array(await file.arrayBuffer());
  const validation = validate(bytes);
  if (!validation.valid) throw new Error(validation.diagnostic ?? 'Texture image is invalid.');
  const bitmap = await createImageBitmap(file);
  try {
    if (bitmap.width !== validation.width || bitmap.height !== validation.height)
      throw new Error('PNG dimensions do not match its decoded image.');
  } finally {
    bitmap.close();
  }
  return {
    bytes,
    width: validation.width,
    height: validation.height,
    url: URL.createObjectURL(file),
    fileName: file.name,
  };
}

export function formatTextureDimensions(
  sourceWidth: number,
  sourceHeight: number,
  effective?: { width: number; height: number },
): string {
  const source = `${sourceWidth} × ${sourceHeight}`;
  return effective && (effective.width !== sourceWidth || effective.height !== sourceHeight)
    ? `${source} → ${effective.width} × ${effective.height}`
    : source;
}

export function applyTextureChannel(pixels: Uint8ClampedArray, channel: TextureChannel): void {
  if (channel === 'rgba') return;
  for (let index = 0; index < pixels.length; index += 4) {
    if (channel === 'rgb') {
      pixels[index + 3] = 255;
      continue;
    }
    const offset = channel === 'red' ? 0 : channel === 'green' ? 1 : channel === 'blue' ? 2 : 3;
    const value = pixels[index + offset];
    pixels[index] = value;
    pixels[index + 1] = value;
    pixels[index + 2] = value;
    pixels[index + 3] = 255;
  }
}
