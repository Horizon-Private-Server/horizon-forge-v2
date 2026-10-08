import * as THREE from 'three';

import type { EditorCollisionFaceType } from '../../types/EditorRuntime.js';

const FACE_ID_ATTRIBUTE = '_collision_face_id';
const COLLISION_TYPE_ATTRIBUTE = '_collision_type';
const SOUND_TYPE_ATTRIBUTE = '_sound_type';

export function collisionFaceIdFromIntersection(
  intersection: THREE.Intersection,
): number | undefined {
  if (!(intersection.object instanceof THREE.Mesh) || !intersection.face) return undefined;
  const attribute = intersection.object.geometry.getAttribute(FACE_ID_ATTRIBUTE);
  if (!attribute) return undefined;
  const { a, b, c } = intersection.face;
  const faceId = attribute.getX(a);
  return Number.isSafeInteger(faceId) && faceId >= 0
    && attribute.getX(b) === faceId && attribute.getX(c) === faceId
    ? faceId
    : undefined;
}

export function collisionRawTypeFromIntersection(
  intersection: THREE.Intersection,
): number | undefined {
  if (!(intersection.object instanceof THREE.Mesh) || !intersection.face) return undefined;
  const collision = intersection.object.geometry.getAttribute(COLLISION_TYPE_ATTRIBUTE);
  const sound = intersection.object.geometry.getAttribute(SOUND_TYPE_ATTRIBUTE);
  if (!collision || !sound) return undefined;
  const collisionId = collision.getX(intersection.face.a);
  const soundId = sound.getX(intersection.face.a);
  return Number.isInteger(collisionId) && collisionId >= 0 && collisionId <= 15
    && Number.isInteger(soundId) && soundId >= 0 && soundId <= 15
    ? collisionId | soundId << 4
    : undefined;
}

export function applyCollisionFaceTypes(
  root: THREE.Object3D,
  defaultRawType: number,
  overrides: readonly EditorCollisionFaceType[],
): void {
  const values = new Map(overrides.map((value) => [value.faceIndex, value.rawType]));
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    const faceIds = object.geometry.getAttribute(FACE_ID_ATTRIBUTE);
    const collision = object.geometry.getAttribute(COLLISION_TYPE_ATTRIBUTE);
    const sound = object.geometry.getAttribute(SOUND_TYPE_ATTRIBUTE);
    if (!faceIds || !collision || !sound) return;
    for (let index = 0; index < faceIds.count; index += 1) {
      const rawType = values.get(faceIds.getX(index)) ?? defaultRawType;
      collision.setX(index, rawType & 0x0f);
      sound.setX(index, rawType >> 4 & 0x0f);
    }
    collision.needsUpdate = true;
    sound.needsUpdate = true;
  });
}

export function createInstancedCollisionWireframeOverlay(source: THREE.Object3D): THREE.Object3D {
  const overlay = source.clone(true);
  overlay.name = 'Collision wireframe overlay';
  overlay.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    object.renderOrder = 1;
    object.material = new THREE.MeshBasicMaterial({
      color: 0x000000,
      depthWrite: false,
      fog: false,
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -1,
      wireframe: true,
    });
  });
  return overlay;
}

export function interpolatePointerSegment(
  from: { x: number; y: number },
  to: { x: number; y: number },
  maximumStep = 4,
): Array<{ x: number; y: number }> {
  const distance = Math.hypot(to.x - from.x, to.y - from.y);
  const steps = Math.max(1, Math.ceil(distance / maximumStep));
  return Array.from({ length: steps }, (_, index) => {
    const amount = (index + 1) / steps;
    return { x: from.x + (to.x - from.x) * amount, y: from.y + (to.y - from.y) * amount };
  });
}

export function shouldOrbitWhileCollisionPainting(button: number, altKey: boolean): boolean {
  return button === 1 || button === 0 && altKey;
}
