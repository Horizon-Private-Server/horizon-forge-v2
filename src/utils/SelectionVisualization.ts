import type { SettingEntry } from '../types/ForgeApi.js';
import { isHexColor } from './SceneTreeColors.ts';

export const DEFAULT_SELECTION_COLOR = '#22d3ee';
export const SELECTION_COLOR_KEY = 'ui.selectionColor' as const;

export function readSelectionColor(entries: readonly SettingEntry[]): string {
  const value = entries.find((entry) => entry.key === SELECTION_COLOR_KEY)?.value;
  return isHexColor(value) ? value : DEFAULT_SELECTION_COLOR;
}
