import type { EditorFx, EditorHud } from '../types/EditorRuntime.js';

export function buildHudBankItems(
  hud: EditorHud,
  search: string,
  filter: 'all' | 'changed' | 'issues',
) {
  const items = [
    ...hud.sourceIcons.flatMap((icon) => icon.frames.map((frame) => ({
      key: `source:${icon.sourceIconIndex}:${frame.sourceFrameIndex}`,
      kind: 'frame' as const,
      spriteId: icon.spriteId,
      sourceIconIndex: icon.sourceIconIndex,
      sourceFrameIndex: frame.sourceFrameIndex,
      sourcePaletteIndex: frame.sourcePaletteIndex,
      sourceTextureIndex: frame.sourceTextureIndex,
      paletteBankIndex: frame.paletteBankIndex,
      textureBankIndex: frame.textureBankIndex,
      width: frame.width,
      height: frame.height,
      sourceAssetId: frame.sourceTexture?.id,
      previewAssetId: frame.effectiveTexture?.id ?? frame.sourceTexture?.id,
      state: frame.diagnostic || !frame.sourceTexture || !frame.effectiveTexture
        ? 'invalid' as const
        : frame.effectiveTexture.id !== frame.sourceTexture.id ? 'override' as const : 'source' as const,
      diagnostic: frame.diagnostic ?? (!frame.sourceTexture || !frame.effectiveTexture
        ? 'Texture dependency is missing.' : undefined),
    }))),
    ...hud.additions.map((addition, index) => ({
      key: `addition:${addition.spriteId}:${index}`,
      kind: 'addition' as const,
      spriteId: addition.spriteId,
      sourceIconIndex: undefined,
      sourceFrameIndex: undefined,
      sourcePaletteIndex: undefined,
      sourceTextureIndex: undefined,
      paletteBankIndex: addition.bankIndex,
      textureBankIndex: addition.bankIndex,
      width: addition.width,
      height: addition.height,
      sourceAssetId: undefined,
      previewAssetId: addition.texture.id,
      state: 'new' as const,
      diagnostic: undefined,
    })),
  ];
  const query = search.trim().toLocaleLowerCase();
  return items.filter((item) => {
    if (filter === 'changed' && item.state !== 'override' && item.state !== 'new') return false;
    if (filter === 'issues' && item.state !== 'invalid') return false;
    return !query || [
      formatHudSpriteId(item.spriteId), item.kind, item.state,
      item.sourceIconIndex, item.sourceFrameIndex, item.sourcePaletteIndex, item.sourceTextureIndex,
      item.paletteBankIndex, item.textureBankIndex, item.previewAssetId, item.diagnostic,
    ].some((value) => String(value ?? '').toLocaleLowerCase().includes(query));
  });
}

export function buildFxTextureItems(
  fx: EditorFx,
  search: string,
  filter: 'all' | 'changed' | 'issues',
) {
  const items = [
    ...fx.sourceTextures.map((texture) => ({
      key: `source:${texture.sourceIndex}`,
      kind: 'source' as const,
      index: texture.sourceIndex,
      label: texture.label,
      width: texture.width,
      height: texture.height,
      paletteOffset: texture.paletteOffset,
      pixelOffset: texture.pixelOffset,
      isSwizzled: texture.isSwizzled,
      sourceAssetId: texture.sourceTexture?.id,
      previewAssetId: texture.effectiveTexture?.id ?? texture.sourceTexture?.id,
      state: texture.diagnostic || !texture.sourceTexture || !texture.effectiveTexture
        ? 'invalid' as const
        : texture.effectiveTexture.id !== texture.sourceTexture.id ? 'override' as const : 'source' as const,
      diagnostic: texture.diagnostic ?? (!texture.sourceTexture || !texture.effectiveTexture
        ? 'Texture dependency is missing.' : undefined),
    })),
    ...fx.additions.map((addition, offset) => ({
      key: `addition:${fx.sourceTextures.length + offset}`,
      kind: 'addition' as const,
      index: fx.sourceTextures.length + offset,
      label: `FX_TEXTURE_${fx.sourceTextures.length + offset}`,
      width: addition.width,
      height: addition.height,
      paletteOffset: undefined,
      pixelOffset: undefined,
      isSwizzled: false,
      sourceAssetId: undefined,
      previewAssetId: addition.texture.id,
      state: 'new' as const,
      diagnostic: undefined,
    })),
  ];
  const query = search.trim().toLocaleLowerCase();
  return items.filter((item) => {
    if (filter === 'changed' && item.state !== 'override' && item.state !== 'new') return false;
    if (filter === 'issues' && item.state !== 'invalid') return false;
    return !query || [
      item.index, item.label, item.kind, item.state, item.previewAssetId, item.diagnostic,
    ].some((value) => String(value ?? '').toLocaleLowerCase().includes(query));
  });
}

export function formatHudSpriteId(value: number): string {
  return value.toString(16).toUpperCase().padStart(4, '0');
}

export function hudThumbnailDimensions(width: number, height: number): { width: number; height: number } {
  if (width <= 0 || height <= 0) return { width: 1, height: 1 };
  const scale = Math.min(2, 300 / width, 150 / height);
  return { width: Math.round(width * scale), height: Math.round(height * scale) };
}

export function nextHudSpriteId(hud: EditorHud, minimum: number, maximum: number): number | undefined {
  const occupied = new Set([
    ...hud.sourceIcons.map((icon) => icon.spriteId),
    ...hud.additions.map((addition) => addition.spriteId),
  ]);
  for (let value = minimum; value <= maximum; value += 1) if (!occupied.has(value)) return value;
  return undefined;
}

export function validateHudPng(bytes: Uint8Array): TextureImageValidation {
  return validateTexturePng(bytes, 1_024, 'HUD');
}

export function validateFxPng(bytes: Uint8Array): TextureImageValidation {
  return validateTexturePng(bytes, 4_096, 'FX');
}

interface TextureImageValidation {
  valid: boolean;
  width: number;
  height: number;
  diagnostic?: string;
}

function validateTexturePng(bytes: Uint8Array, maximumDimension: number, family: string): TextureImageValidation {
  if (bytes.byteLength === 0 || bytes.byteLength > 16 * 1024 * 1024)
    return { valid: false, width: 0, height: 0, diagnostic: 'PNG must be no larger than 16 MiB.' };
  if (bytes.byteLength < 24 || ![0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]
    .every((value, index) => bytes[index] === value))
    return { valid: false, width: 0, height: 0, diagnostic: 'Choose a valid PNG image.' };
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const width = view.getUint32(16);
  const height = view.getUint32(20);
  if (width === 0 || height === 0 || width > maximumDimension || height > maximumDimension
    || (width & (width - 1)) !== 0 || (height & (height - 1)) !== 0)
    return {
      valid: false, width, height,
      diagnostic: `${family} dimensions must be powers of two no larger than ${maximumDimension} × ${maximumDimension}.`,
    };
  return { valid: true, width, height };
}
