const PREFIX = 'spline-point:';

export function splinePointId(entityId: string, index: number): string {
  return `${PREFIX}${entityId}:${index}`;
}

export function parseSplinePointId(value: string): { entityId: string; index: number } | undefined {
  if (!value.startsWith(PREFIX)) return undefined;
  const separator = value.lastIndexOf(':');
  const index = Number(value.slice(separator + 1));
  const entityId = value.slice(PREFIX.length, separator);
  return entityId && Number.isInteger(index) && index >= 0 ? { entityId, index } : undefined;
}

export function transformSplinePoints(
  points: readonly ProjectVector4[],
  selected: ReadonlySet<number>,
  entityTransform: ProjectTransform,
  delta: THREE.Matrix4,
): ProjectVector4[] {
  const entity = projectTransformToSceneMatrix(entityTransform);
  const inverse = entity.clone().invert();
  const position = new THREE.Vector3();
  return points.map((point, index) => {
    if (!selected.has(index)) return { ...point };
    ps2PositionToScene(point, position).applyMatrix4(entity).applyMatrix4(delta).applyMatrix4(inverse);
    return { x: position.x, y: -position.z, z: position.y, w: point.w };
  });
}

export function removeSplinePoints(
  points: readonly ProjectVector4[],
  selected: ReadonlySet<number>,
): ProjectVector4[] {
  return points.filter((_, index) => !selected.has(index));
}
import * as THREE from 'three';

import type { ProjectTransform, ProjectVector4 } from '../types/EditorRuntime.js';
import { ps2PositionToScene } from './Scene.ts';
import { projectTransformToSceneMatrix } from './Transforms.ts';
