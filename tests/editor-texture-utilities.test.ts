import assert from 'node:assert/strict';
import test from 'node:test';

import type { EditorFx, EditorHud } from '../src/types/EditorRuntime.js';
import { BUILD_LAYERS } from '../src/utils/BuildLayers.ts';
import {
  buildFxTextureItems, buildHudBankItems, formatHudSpriteId, hudThumbnailDimensions,
  nextHudSpriteId, validateFxPng, validateHudPng,
} from '../src/utils/TextureInventory.ts';
import { assetGridWindow, virtualGridWindow } from '../src/utils/VirtualGrid.ts';

test('shared editor build-layer allowlist includes FX textures', () => {
  assert.ok(BUILD_LAYERS.includes('Fx'));
});

test('asset grid keeps DOM work bounded around visible rows', () => {
  const first = assetGridWindow(10_000, 680, 440, 0);
  assert.equal(first.columns, 4);
  assert.equal(first.startIndex, 0);
  assert.ok(first.endIndex <= 20);
  const middle = assetGridWindow(10_000, 680, 440, 100_000);
  assert.ok(middle.startIndex > 0);
  assert.ok(middle.endIndex - middle.startIndex <= 28);
  assert.equal(middle.totalHeight, first.totalHeight);
});

test('HUD bank state exposes native placement, change, issue, and search metadata', () => {
  const sourceId = 'a'.repeat(64);
  const replacementId = 'b'.repeat(64);
  const hud: EditorHud = {
    canRead: true,
    isDirty: false,
    canReplace: true,
    canAppend: true,
    physicalBankCount: 5,
    minimumAppendBank: 3,
    minimumAppendSpriteId: 0xe000,
    maximumAppendSpriteId: 0xefff,
    maximumIconCount: 1_024,
    sourceIcons: [{
      sourceIconIndex: 4,
      spriteId: 0x1234,
      frames: [{
        sourceFrameIndex: 7,
        sourcePaletteIndex: 8,
        sourceTextureIndex: 9,
        paletteBankIndex: 1,
        textureBankIndex: 2,
        width: 32,
        height: 64,
        sourceTexture: { id: sourceId, kind: 'Texture' },
        effectiveTexture: { id: replacementId, kind: 'Texture' },
      }, {
        sourceFrameIndex: 10,
        sourcePaletteIndex: -1,
        sourceTextureIndex: 11,
        paletteBankIndex: -1,
        textureBankIndex: 2,
        width: 0,
        height: 0,
        diagnostic: 'Palette is missing.',
      }],
    }],
    additions: [{ spriteId: 0xe001, bankIndex: 4, width: 16, height: 16,
      texture: { id: 'c'.repeat(64), kind: 'Texture' } }],
  };
  assert.equal(buildHudBankItems(hud, '', 'all').length, 3);
  assert.equal(buildHudBankItems(hud, '1234', 'all')[0].state, 'override');
  assert.equal(buildHudBankItems(hud, '', 'changed').length, 2);
  assert.equal(buildHudBankItems(hud, 'palette', 'issues')[0].state, 'invalid');
  assert.equal(nextHudSpriteId(hud, 0xe000, 0xefff), 0xe000);
  assert.equal(formatHudSpriteId(0xe001), 'E001');
  assert.deepEqual(hudThumbnailDimensions(16, 16), { width: 32, height: 32 });
  assert.deepEqual(hudThumbnailDimensions(32, 32), { width: 64, height: 64 });
  assert.deepEqual(hudThumbnailDimensions(64, 64), { width: 128, height: 128 });
  assert.deepEqual(hudThumbnailDimensions(256, 64), { width: 300, height: 75 });

  const large = virtualGridWindow(1_024, 1_200, 650, 80_000, 280, 284);
  assert.ok(large.endIndex - large.startIndex <= 32);
});

test('HUD PNG preflight rejects decode limits before a project command', () => {
  const png = new Uint8Array(24);
  png.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  new DataView(png.buffer).setUint32(16, 64);
  new DataView(png.buffer).setUint32(20, 32);
  assert.deepEqual(validateHudPng(png), { valid: true, width: 64, height: 32 });
  new DataView(png.buffer).setUint32(16, 63);
  assert.match(validateHudPng(png).diagnostic ?? '', /powers of two/);
  assert.match(validateHudPng(new Uint8Array([1, 2, 3])).diagnostic ?? '', /valid PNG/);
});

test('FX texture state preserves source indexes and filters changes and issues', () => {
  const fx: EditorFx = {
    canRead: true,
    isDirty: true,
    canReplace: true,
    canAppend: true,
    maximumTextureCount: 4_096,
    sourceTextures: [{
      sourceIndex: 0,
      label: 'FX_LAME_SHADOW',
      width: 64,
      height: 32,
      paletteOffset: 0,
      pixelOffset: 0x400,
      isSwizzled: false,
      sourceTexture: { id: 'a'.repeat(64), kind: 'Texture' },
      effectiveTexture: { id: 'b'.repeat(64), kind: 'Texture' },
    }, {
      sourceIndex: 1,
      label: 'FX_CLOUDY_CIRCLE_1',
      width: 0,
      height: 0,
      paletteOffset: -1,
      pixelOffset: -1,
      isSwizzled: false,
      diagnostic: 'FX texture 1 is invalid.',
    }],
    additions: [{ width: 16, height: 16, texture: { id: 'c'.repeat(64), kind: 'Texture' } }],
  };
  assert.deepEqual(buildFxTextureItems(fx, '', 'all').map((value) => value.index), [0, 1, 2]);
  assert.equal(buildFxTextureItems(fx, '', 'changed').length, 2);
  assert.equal(buildFxTextureItems(fx, 'cloudy', 'issues')[0].index, 1);
  assert.equal(buildFxTextureItems(fx, 'FX_TEXTURE_2', 'all')[0].kind, 'addition');
});

test('FX PNG preflight accepts the native limit and rejects non-power-of-two dimensions', () => {
  const png = new Uint8Array(24);
  png.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const view = new DataView(png.buffer);
  view.setUint32(16, 4_096);
  view.setUint32(20, 2);
  assert.deepEqual(validateFxPng(png), { valid: true, width: 4_096, height: 2 });
  view.setUint32(16, 3);
  assert.match(validateFxPng(png).diagnostic ?? '', /powers of two/);
});

