import type { SettingEntry } from '../types/ForgeApi.js';

export const HIDE_EMPTY_GROUPS_KEY = 'ui.hideEmptyGroups' as const;
export const DEFAULT_HIDE_EMPTY_GROUPS = true;

export function readHideEmptyGroups(entries: readonly SettingEntry[]): boolean {
  const value = entries.find((entry) => entry.key === HIDE_EMPTY_GROUPS_KEY)?.value;
  return typeof value === 'boolean' ? value : DEFAULT_HIDE_EMPTY_GROUPS;
}
