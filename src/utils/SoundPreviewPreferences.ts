import type { SettingEntry } from '../types/ForgeApi.js';

export const SOUND_PREVIEW_DEFAULT_VOLUME = 0.75;

export interface SoundPreviewPreferences {
  directVolume: number;
  directMuted: boolean;
  ambientVolume: number;
  ambientMuted: boolean;
}

export function clampSoundPreviewVolume(value: number): number {
  return Math.min(1, Math.max(0, Number.isFinite(value) ? value : SOUND_PREVIEW_DEFAULT_VOLUME));
}

export function readSoundPreviewPreferences(entries: SettingEntry[]): SoundPreviewPreferences {
  const value = (key: SettingEntry['key']) => entries.find((entry) => entry.key === key)?.value;
  return {
    directVolume: volume(value('preview.sound.directVolume')),
    directMuted: value('preview.sound.directMuted') === true,
    ambientVolume: volume(value('preview.sound.ambientVolume')),
    ambientMuted: value('preview.sound.ambientMuted') === true,
  };
}

function volume(value: unknown): number {
  return typeof value === 'number' ? clampSoundPreviewVolume(value) : SOUND_PREVIEW_DEFAULT_VOLUME;
}
