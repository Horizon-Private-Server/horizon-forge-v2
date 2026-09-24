import type { SettingEntry, SettingKey } from '../types/ForgeApi.js';
import type { SceneTreeColors, SceneTreeKind } from '../types/SceneTree.js';

export const SCENE_TREE_KINDS: readonly SceneTreeKind[] = ['tfrag', 'sky', 'tie', 'shrub', 'moby', 'object'];

export const SCENE_TREE_LABELS: Record<SceneTreeKind, string> = {
  tfrag: 'Tfrag',
  sky: 'Sky',
  tie: 'Tie',
  shrub: 'Shrub',
  moby: 'Moby',
  object: 'Object',
};

export const DEFAULT_SCENE_TREE_COLORS: SceneTreeColors = {
  tfrag: '#82c91e',
  sky: '#4c6ef5',
  tie: '#15aabf',
  shrub: '#40c057',
  moby: '#ae3ec9',
  object: '#868e96',
};

export const SCENE_TREE_COLOR_KEYS: Record<SceneTreeKind, SettingKey> = {
  tfrag: 'ui.sceneTreeColors.tfrag',
  sky: 'ui.sceneTreeColors.sky',
  tie: 'ui.sceneTreeColors.tie',
  shrub: 'ui.sceneTreeColors.shrub',
  moby: 'ui.sceneTreeColors.moby',
  object: 'ui.sceneTreeColors.object',
};

export const isHexColor = (value: unknown): value is string =>
  typeof value === 'string' && /^#[\da-f]{6}$/i.test(value);

export function readSceneTreeColors(entries: readonly SettingEntry[]): SceneTreeColors {
  return Object.fromEntries(SCENE_TREE_KINDS.map((kind) => {
    const value = entries.find((entry) => entry.key === SCENE_TREE_COLOR_KEYS[kind])?.value;
    return [kind, isHexColor(value) ? value : DEFAULT_SCENE_TREE_COLORS[kind]];
  })) as SceneTreeColors;
}

export function sceneTreeColorVariables(colors: SceneTreeColors): Record<string, string> {
  return Object.fromEntries(SCENE_TREE_KINDS.map((kind) => [`--forge-scene-tree-${kind}`, colors[kind]]));
}
