export type TextureChannel = 'rgba' | 'rgb' | 'red' | 'green' | 'blue' | 'alpha';

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
