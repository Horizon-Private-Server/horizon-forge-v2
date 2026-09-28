import type { UiSize } from '../types/ForgeApi.js';

export const UI_SIZES = ['xs', 'sm', 'md', 'lg', 'xl'] as const;

export function isUiSize(value: unknown): value is UiSize {
  return typeof value === 'string' && UI_SIZES.includes(value as UiSize);
}

export function nextUiSize(value: UiSize): UiSize {
  return UI_SIZES[Math.min(UI_SIZES.indexOf(value) + 1, UI_SIZES.length - 1)];
}
