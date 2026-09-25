export type SceneTreeKind =
  | 'tfrag'
  | 'sky'
  | 'tie'
  | 'shrub'
  | 'moby'
  | 'cuboid'
  | 'sphere'
  | 'cylinder'
  | 'pill'
  | 'spline'
  | 'grindPath'
  | 'area'
  | 'directionalLight'
  | 'pointLight'
  | 'environmentSample'
  | 'environmentTransition'
  | 'camera'
  | 'ambientSound'
  | 'occlusionOctant'
  | 'object';

export type SceneTreeColors = Record<SceneTreeKind, string>;
