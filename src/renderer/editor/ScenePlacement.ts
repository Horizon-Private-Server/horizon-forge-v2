import * as THREE from 'three';

import type { EditorEntity, EditorTransformUpdate } from '../../types/EditorRuntime.js';
import { cloneProjectTransform } from '../../utils/Transforms.ts';
import type { SceneProjection } from './SceneProjection.ts';

export interface GroundPlacementResult {
  message: string;
  updates: EditorTransformUpdate[];
}

const DOWN = new THREE.Vector3(0, -1, 0);
const bounds = new THREE.Box3();
const entityBounds = new THREE.Box3();
const center = new THREE.Vector3();
const origin = new THREE.Vector3();
const raycaster = new THREE.Raycaster();

export function buildGroundPlacement(
  entities: readonly EditorEntity[],
  selection: readonly string[],
  projection: SceneProjection,
  targets: THREE.Object3D[],
): GroundPlacementResult {
  const selectedIds = new Set(selection);
  const selected = entities.filter((entity) => selectedIds.has(entity.id)
    && !entity.state.locked && !entity.state.hidden && !entity.state.disabled);
  if (!selected.length) return { message: 'Select an unlocked object to place.', updates: [] };

  bounds.makeEmpty();
  for (const entity of selected) {
    const value = projection.getBounds(entity.id, entityBounds);
    if (value) bounds.union(value);
  }
  if (bounds.isEmpty()) return { message: 'The selection has no placeable bounds.', updates: [] };

  targets.forEach((target) => target.updateMatrixWorld(true));
  bounds.getCenter(center);
  let ground = -Infinity;
  const samples = [
    [center.x, center.z],
    [bounds.min.x, bounds.min.z],
    [bounds.min.x, bounds.max.z],
    [bounds.max.x, bounds.min.z],
    [bounds.max.x, bounds.max.z],
  ];
  for (const [x, z] of samples) {
    raycaster.set(origin.set(x, bounds.min.y + 0.01, z), DOWN);
    const hit = raycaster.intersectObjects(targets, true).find((intersection) => {
      const entityId = projection.resolveIntersectionEntityId(intersection);
      return !entityId || !selectedIds.has(entityId);
    });
    if (hit) ground = Math.max(ground, hit.point.y);
  }
  if (!Number.isFinite(ground)) return { message: 'No surface found below the selection.', updates: [] };

  const distance = ground - bounds.min.y;
  if (Math.abs(distance) < 0.0001) return { message: 'The selection is already on a surface.', updates: [] };
  return {
    message: `Placed ${selected.length} object${selected.length === 1 ? '' : 's'} on the surface.`,
    updates: selected.map((entity) => {
      const transform = cloneProjectTransform(entity.transform);
      transform.position.z += distance;
      return { entityId: entity.id, transform };
    }),
  };
}
