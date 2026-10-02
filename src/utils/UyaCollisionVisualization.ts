import type { SettingEntry } from '../types/ForgeApi.js';
import type { CollisionVisualization } from '../types/CollisionVisualization.js';
import { isHexColor } from './SceneTreeColors.ts';

export const UYA_COLLISION_VISUALIZATION_KEY = 'visualization.uya.collisionPalette' as const;

export const DEFAULT_UYA_COLLISION_VISUALIZATION: CollisionVisualization = {
  collisionTypeColors: [
    '#2f80ed', '#b7f000', '#405457', '#8b6b3f',
    '#730000', '#a855f7', '#5c3f3f', '#005720',
    '#783e00', '#14b838', '#4d8000', '#b50000',
    '#914e00', '#818cf8', '#38bdf8', '#187800',
  ],
  soundTypeColors: [
    '#2b2b2b', '#4a6150', '#826a8a', '#518f8f',
    '#874d84', '#8a995d', '#a66d6d', '#008080',
    '#e6beff', '#9a6324', '#fffac8', '#800000',
    '#aaffc3', '#e6194b', '#3cb44b', '#ffe119',
  ],
  playerBarrierColor: '#fff200',
};

export const UYA_COLLISION_TYPE_LABELS = [
  'Swimmable water', 'Acid', 'Magnet wall', 'Mud',
  'Ring of fire', 'Electricity', 'Sliding magnet wall', 'Walkable',
  'Sliding tile', 'Walkable (no ledge grab)', 'Walkable (unconfirmed)', 'Lethal water',
  'Sliding tile', 'Lethal water (ice cube)', 'Walkable (water trail)', 'Walkable',
] as const;

export function serializeUyaCollisionVisualization(value: CollisionVisualization): string {
  return JSON.stringify(value);
}

export function parseUyaCollisionVisualization(value: unknown): CollisionVisualization | undefined {
  if (typeof value !== 'string') return undefined;
  try {
    const parsed = JSON.parse(value) as Partial<CollisionVisualization>;
    if (!validPalette(parsed.collisionTypeColors) || !validPalette(parsed.soundTypeColors)
      || !isHexColor(parsed.playerBarrierColor)) return undefined;
    return {
      collisionTypeColors: [...parsed.collisionTypeColors],
      soundTypeColors: [...parsed.soundTypeColors],
      playerBarrierColor: parsed.playerBarrierColor,
    };
  } catch {
    return undefined;
  }
}

export function readUyaCollisionVisualization(entries: readonly SettingEntry[]): CollisionVisualization {
  const stored = entries.find((entry) => entry.key === UYA_COLLISION_VISUALIZATION_KEY)?.value;
  return parseUyaCollisionVisualization(stored) ?? cloneUyaCollisionVisualization(DEFAULT_UYA_COLLISION_VISUALIZATION);
}

export function cloneUyaCollisionVisualization(value: CollisionVisualization): CollisionVisualization {
  return {
    collisionTypeColors: [...value.collisionTypeColors],
    soundTypeColors: [...value.soundTypeColors],
    playerBarrierColor: value.playerBarrierColor,
  };
}

function validPalette(value: unknown): value is string[] {
  return Array.isArray(value) && value.length === 16 && value.every(isHexColor);
}
