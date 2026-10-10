import * as THREE from 'three';

import type { EditorEntity, EditorTransformUpdate } from '../types/EditorRuntime.js';
import { projectTransformToSceneMatrix, sceneMatrixToProjectTransform } from './Transforms.ts';

export type TransformSelectionMode = 'translate' | 'rotate' | 'scale';

export interface TransformSelectionEligibility {
  entities: EditorEntity[];
  message?: string;
}

export interface LinkedCollisionTransformIndex {
  entitiesById: ReadonlyMap<string, EditorEntity>;
  adjacency: ReadonlyMap<string, ReadonlySet<string>>;
}

const MODE_LABELS: Record<TransformSelectionMode, { action: string; capability: string }> = {
  translate: { action: 'move', capability: 'moving' },
  rotate: { action: 'rotate', capability: 'rotation' },
  scale: { action: 'scale', capability: 'scaling' },
};

export function inspectTransformSelection(
  entities: readonly EditorEntity[],
  selection: readonly string[],
  mode: TransformSelectionMode,
  missingMemberCount = 0,
): TransformSelectionEligibility {
  const ids = [...new Set(selection)];
  const entitiesById = new Map(entities.map((entity) => [entity.id, entity]));
  const selected = ids.flatMap((id) => {
    const entity = entitiesById.get(id);
    return entity ? [entity] : [];
  });
  if (ids.length === 0 && missingMemberCount === 0) return { entities: [] };

  const counts = new Map<string, number>();
  const add = (label: string, count = 1) => counts.set(label, (counts.get(label) ?? 0) + count);
  const missingIds = ids.length - selected.length;
  if (missingIds + missingMemberCount > 0) add('missing', missingIds + missingMemberCount);
  const collisionLinked = mode === 'scale'
    ? new Set(entities.flatMap((entity) => entity.collision?.linkedInstanceEntityIds ?? []))
    : undefined;

  for (const entity of selected) {
    if (entity.state.locked) add('locked');
    if (entity.state.readOnly) add('read-only');
    if (entity.state.hidden) add('hidden');
    if (entity.state.disabled) add('disabled');
    if (entity.state.invalid) add('invalid');
    if (entity.state.missingAsset) add('missing asset');
    if (collisionLinked?.has(entity.id)) add('linked to source collision');
    if (!entity.transformModes.includes(mode)) add(`without ${MODE_LABELS[mode].capability} support`);
  }

  if (counts.size === 0) return { entities: selected };
  const reasons = [...counts].map(([label, count]) => `${count} ${label}`).join(', ');
  return {
    entities: [],
    message: `Cannot ${MODE_LABELS[mode].action} the whole selection: ${reasons}.`,
  };
}

export function indexLinkedCollisionTransforms(
  entities: readonly EditorEntity[],
): LinkedCollisionTransformIndex {
  const entitiesById = new Map(entities.map((entity) => [entity.id, entity]));
  const adjacency = new Map<string, Set<string>>();
  const addEdge = (source: string, target: string) => {
    const values = adjacency.get(source) ?? new Set<string>();
    values.add(target);
    adjacency.set(source, values);
  };
  entities.forEach((entity) => {
    if (!entity.collision?.linkedInstanceEntityIds?.length) return;
    entity.collision.linkedInstanceEntityIds.forEach((instanceId) => {
      if (!entitiesById.has(instanceId)) return;
      addEdge(entity.id, instanceId);
      addEdge(instanceId, entity.id);
    });
  });
  return { entitiesById, adjacency };
}

export function expandLinkedCollisionTransforms(
  index: LinkedCollisionTransformIndex,
  updates: readonly EditorTransformUpdate[],
): EditorTransformUpdate[] {
  const { adjacency, entitiesById } = index;
  const explicit = new Map(updates.map((update) => [update.entityId, update]));
  const result = new Map(explicit);
  const visited = new Set<string>();
  explicit.forEach((_update, start) => {
    if (!adjacency.has(start) || visited.has(start)) return;
    const component: string[] = [];
    const pending = [start];
    visited.add(start);
    while (pending.length) {
      const current = pending.shift()!;
      component.push(current);
      adjacency.get(current)?.forEach((linked) => {
        if (visited.has(linked)) return;
        visited.add(linked);
        pending.push(linked);
      });
    }
    const changed = component.flatMap((entityId) => {
      const entity = entitiesById.get(entityId);
      const update = explicit.get(entityId);
      if (!entity || !update) return [];
      const source = projectTransformToSceneMatrix(entity.transform);
      const delta = projectTransformToSceneMatrix(update.transform)
        .multiply(source.invert());
      return nearMatrix(delta, IDENTITY) ? [] : [{ entity, delta }];
    });
    if (!changed.length) return;
    const instanceChanges = changed.filter(({ entity }) => !entity.collision);
    const authoritative = instanceChanges.length ? instanceChanges : changed;
    const delta = authoritative[0].delta;
    if (authoritative.some((value) => !nearMatrix(value.delta, delta)) || !rigid(delta)) return;
    component.forEach((entityId) => {
      const entity = entitiesById.get(entityId)!;
      const update = explicit.get(entityId);
      const transformed = sceneMatrixToProjectTransform(
        delta.clone().multiply(projectTransformToSceneMatrix(entity.transform)),
      );
      result.set(entityId, {
        entityId,
        transform: update && !entity.collision
          ? update.transform
          : { ...transformed, scale: { ...entity.transform.scale } },
      });
    });
  });
  return [...result.values()].sort((left, right) => left.entityId.localeCompare(right.entityId));
}

const IDENTITY = new THREE.Matrix4();
const decompositionPosition = new THREE.Vector3();
const decompositionRotation = new THREE.Quaternion();
const decompositionScale = new THREE.Vector3();

function rigid(value: THREE.Matrix4): boolean {
  value.decompose(decompositionPosition, decompositionRotation, decompositionScale);
  return Math.abs(decompositionScale.x - 1) <= 0.0001
    && Math.abs(decompositionScale.y - 1) <= 0.0001
    && Math.abs(decompositionScale.z - 1) <= 0.0001;
}

function nearMatrix(left: THREE.Matrix4, right: THREE.Matrix4): boolean {
  return left.elements.every((value, index) => Math.abs(value - right.elements[index]) <= 0.0001);
}
