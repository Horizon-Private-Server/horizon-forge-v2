import type { SettingEntry, SettingKey } from '../types/ForgeApi.js';
import type { SceneTreeColors, SceneTreeKind } from '../types/SceneTree.js';

export const SCENE_TREE_KINDS: readonly SceneTreeKind[] = [
  'tfrag', 'sky', 'tie', 'shrub', 'moby', 'cuboid', 'sphere', 'cylinder', 'pill',
  'spline', 'grindPath', 'area', 'directionalLight', 'pointLight', 'environmentSample',
  'environmentTransition', 'camera', 'ambientSound', 'occlusionOctant', 'object',
];

export const SCENE_TREE_LABELS: Record<SceneTreeKind, string> = {
  tfrag: 'Tfrag',
  sky: 'Sky',
  tie: 'Tie',
  shrub: 'Shrub',
  moby: 'Moby',
  cuboid: 'Cuboid',
  sphere: 'Sphere',
  cylinder: 'Cylinder',
  pill: 'Pill',
  spline: 'Spline',
  grindPath: 'Grind Path',
  area: 'Area',
  directionalLight: 'Directional Light',
  pointLight: 'Point Light',
  environmentSample: 'Environment Sample',
  environmentTransition: 'Environment Transition',
  camera: 'Camera',
  ambientSound: 'Ambient Sound',
  occlusionOctant: 'Occlusion Octant',
  object: 'Object',
};

export const DEFAULT_SCENE_TREE_COLORS: SceneTreeColors = {
  tfrag: '#aaffc3',
  sky: '#dcbeff',
  tie: '#ffd8b1',
  shrub: '#4dc900',
  moby: '#ff9d00',
  cuboid: '#e6e619',
  sphere: '#3cb44b',
  cylinder: '#ffe119',
  pill: '#4363d8',
  spline: '#2bff99',
  grindPath: '#743c85',
  area: '#aaf442',
  directionalLight: '#ad57a9',
  pointLight: '#698c6f',
  environmentSample: '#469990',
  environmentTransition: '#9a6324',
  camera: '#cc3737',
  ambientSound: '#0062ff',
  occlusionOctant: '#22ff00',
  object: '#a9a9a9',
};

export const SCENE_TREE_COLOR_KEYS: Record<SceneTreeKind, SettingKey> = {
  tfrag: 'ui.sceneTreeColors.tfrag',
  sky: 'ui.sceneTreeColors.sky',
  tie: 'ui.sceneTreeColors.tie',
  shrub: 'ui.sceneTreeColors.shrub',
  moby: 'ui.sceneTreeColors.moby',
  cuboid: 'ui.sceneTreeColors.cuboid',
  sphere: 'ui.sceneTreeColors.sphere',
  cylinder: 'ui.sceneTreeColors.cylinder',
  pill: 'ui.sceneTreeColors.pill',
  spline: 'ui.sceneTreeColors.spline',
  grindPath: 'ui.sceneTreeColors.grindPath',
  area: 'ui.sceneTreeColors.area',
  directionalLight: 'ui.sceneTreeColors.directionalLight',
  pointLight: 'ui.sceneTreeColors.pointLight',
  environmentSample: 'ui.sceneTreeColors.environmentSample',
  environmentTransition: 'ui.sceneTreeColors.environmentTransition',
  camera: 'ui.sceneTreeColors.camera',
  ambientSound: 'ui.sceneTreeColors.ambientSound',
  occlusionOctant: 'ui.sceneTreeColors.occlusionOctant',
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
