import * as THREE from 'three';

import type { EditorSnapTarget } from '../../types/EditorViewport.js';
import type { SceneProjection } from './SceneProjection.ts';

interface VertexNode {
  axis: 0 | 1 | 2;
  left?: VertexNode;
  point: THREE.Vector3;
  right?: VertexNode;
}

const instanceMatrix = new THREE.Matrix4();
const worldMatrix = new THREE.Matrix4();
const candidate = new THREE.Vector3();

export class VertexSnapIndex {
  private readonly root?: VertexNode;

  constructor(points: readonly THREE.Vector3[]) {
    this.root = buildNode([...points], 0);
  }

  nearest(target: THREE.Vector3): THREE.Vector3 | undefined {
    let nearest: THREE.Vector3 | undefined;
    let nearestDistance = Infinity;
    const visit = (node?: VertexNode) => {
      if (!node) return;
      const distance = node.point.distanceToSquared(target);
      if (distance < nearestDistance) {
        nearest = node.point;
        nearestDistance = distance;
      }
      const difference = target.getComponent(node.axis) - node.point.getComponent(node.axis);
      visit(difference < 0 ? node.left : node.right);
      if (difference * difference < nearestDistance) visit(difference < 0 ? node.right : node.left);
    };
    visit(this.root);
    return nearest;
  }
}

export function resolvePointerSnapTarget(
  mode: Exclude<EditorSnapTarget, 'grid'>,
  raycaster: THREE.Raycaster,
  projection: SceneProjection,
  targets: THREE.Object3D[],
  excludedIds: ReadonlySet<string>,
  target = new THREE.Vector3(),
): THREE.Vector3 | undefined {
  targets.forEach((object) => object.updateMatrixWorld(true));
  for (const intersection of raycaster.intersectObjects(targets, true)) {
    const entityId = projection.resolveIntersectionEntityId(intersection);
    if (entityId && excludedIds.has(entityId)) continue;
    if (mode === 'surface') return target.copy(intersection.point);
    if (mode === 'center') {
      const bounds = entityId && projection.getBounds(entityId);
      if (bounds) return bounds.getCenter(target);
      continue;
    }
    const object = intersection.object;
    if (!(object instanceof THREE.Mesh) || !intersection.face) continue;
    worldMatrix.copy(object.matrixWorld);
    if (object instanceof THREE.InstancedMesh && intersection.instanceId !== undefined) {
      object.getMatrixAt(intersection.instanceId, instanceMatrix);
      worldMatrix.multiply(instanceMatrix);
    }
    const position = object.geometry.getAttribute('position');
    if (!position) continue;
    let distance = Infinity;
    for (const index of [intersection.face.a, intersection.face.b, intersection.face.c]) {
      candidate.fromBufferAttribute(position as THREE.BufferAttribute, index).applyMatrix4(worldMatrix);
      const nextDistance = candidate.distanceToSquared(intersection.point);
      if (nextDistance < distance) {
        target.copy(candidate);
        distance = nextDistance;
      }
    }
    if (Number.isFinite(distance)) return target;
  }
  return undefined;
}

function buildNode(points: THREE.Vector3[], depth: number): VertexNode | undefined {
  if (!points.length) return undefined;
  const axis = depth % 3 as 0 | 1 | 2;
  points.sort((left, right) => left.getComponent(axis) - right.getComponent(axis));
  const middle = Math.floor(points.length / 2);
  return {
    axis,
    point: points[middle],
    left: buildNode(points.slice(0, middle), depth + 1),
    right: buildNode(points.slice(middle + 1), depth + 1),
  };
}
