import * as THREE from 'three';

import type { EditorEntity, EditorTransformUpdate } from '../../types/EditorRuntime.js';
import { cloneProjectTransform } from '../../utils/Transforms.ts';
import { inspectTransformSelection } from '../../utils/TransformSelection.ts';
import type { SceneProjection } from './SceneProjection.ts';

export interface GroundPlacementResult {
  message: string;
  updates: EditorTransformUpdate[];
}

const DOWN = new THREE.Vector3(0, -1, 0);
const bounds = new THREE.Box3();
const entityBounds = new THREE.Box3();
const center = new THREE.Vector3();

export function createGroundSurfaceRaycast(
  entities: readonly EditorEntity[],
  projection: SceneProjection,
  targets: readonly THREE.Object3D[],
  excludedIds: ReadonlySet<string> = new Set(),
): (x: number, z: number, startY: number) => THREE.Vector3 | undefined {
  const entitiesById = new Map(entities.map((entity) => [entity.id, entity]));
  const raycaster = new THREE.Raycaster();
  const origin = new THREE.Vector3();
  targets.forEach((target) => target.updateMatrixWorld(true));
  const surfaces = collectSurfaceMeshes(targets);
  return (x, z, startY) => {
    raycaster.set(origin.set(x, startY, z), DOWN);
    return raycaster.intersectObjects(surfaces, false).find((intersection) => {
      if (isDescendantOf(intersection.object, targets[0])) return true;
      const entityId = projection.resolveIntersectionEntityId(intersection);
      if (!entityId || excludedIds.has(entityId)) return false;
      const entity = entitiesById.get(entityId);
      if (!entity || entity.state.hidden || entity.state.disabled) return false;
      const kind = entity.asset?.kind.toLowerCase();
      return kind === 'tie' || kind === 'tfrag';
    })?.point;
  };
}

export function buildGroundPlacement(
  entities: readonly EditorEntity[],
  selection: readonly string[],
  projection: SceneProjection,
  targets: THREE.Object3D[],
  missingSelectionMembers = 0,
): GroundPlacementResult {
  const selectedIds = new Set(selection);
  const eligibility = inspectTransformSelection(
    entities,
    selection,
    'translate',
    missingSelectionMembers,
  );
  if (eligibility.message) return { message: eligibility.message, updates: [] };
  const selected = eligibility.entities;
  if (!selected.length) return { message: 'Select an unlocked object to place.', updates: [] };

  bounds.makeEmpty();
  for (const entity of selected) {
    const value = projection.getBounds(entity.id, entityBounds);
    if (value) bounds.union(value);
  }
  if (bounds.isEmpty()) return { message: 'The selection has no placeable bounds.', updates: [] };

  const findGround = createGroundSurfaceRaycast(entities, projection, targets, selectedIds);
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
    const point = findGround(x, z, bounds.min.y + 0.01);
    if (point) ground = Math.max(ground, point.y);
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

function collectSurfaceMeshes(targets: readonly THREE.Object3D[]): THREE.Mesh[] {
  const surfaces: THREE.Mesh[] = [];
  for (const target of targets) target.traverse((object) => {
    if (object instanceof THREE.Mesh
      && !(object as THREE.Mesh & { isLineSegments2?: boolean }).isLineSegments2)
      surfaces.push(object);
  });
  return surfaces;
}

function isDescendantOf(object: THREE.Object3D, ancestor?: THREE.Object3D): boolean {
  for (let current: THREE.Object3D | null = object; current; current = current.parent)
    if (current === ancestor) return true;
  return false;
}
