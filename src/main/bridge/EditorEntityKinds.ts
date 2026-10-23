import type { EditorEntityKind } from '../../types/EditorRuntime.js';

export const editorEntityKinds: Record<number, EditorEntityKind> = {
  1: 'entity', 2: 'moby', 3: 'tie', 4: 'shrub', 5: 'tfrag', 6: 'cuboid', 7: 'sphere',
  8: 'cylinder', 9: 'pill', 10: 'spline', 11: 'grindPath', 12: 'area', 13: 'collision',
  14: 'skyShell', 15: 'directionalLight', 16: 'pointLight', 17: 'environmentSample',
  18: 'environmentTransition', 19: 'camera', 20: 'ambientSound',
};

export const editorEntityKindIds = Object.fromEntries(
  Object.entries(editorEntityKinds).map(([id, kind]) => [kind, Number(id)]),
) as Record<EditorEntityKind, number>;
