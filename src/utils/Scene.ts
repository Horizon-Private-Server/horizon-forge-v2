import * as THREE from 'three';
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const cameraMovement = new THREE.Vector3();
const cameraForward = new THREE.Vector3();
const cameraRight = new THREE.Vector3();
const cameraEuler = new THREE.Euler(0, 0, 0, 'YXZ');
const frameOffset = new THREE.Vector3();

interface Vector3Like {
  x: number;
  y: number;
  z: number;
}

export interface CameraFlight {
  cameraStart: THREE.Vector3;
  cameraEnd: THREE.Vector3;
  targetStart: THREE.Vector3;
  targetEnd: THREE.Vector3;
  elapsed: number;
  duration: number;
}

export function frameObject(camera: THREE.PerspectiveCamera, controls: OrbitControls, object: THREE.Object3D): void {
  const sphere = new THREE.Box3().setFromObject(object).getBoundingSphere(new THREE.Sphere());
  const radius = Math.max(sphere.radius, 1);
  controls.target.copy(sphere.center);
  camera.position.copy(sphere.center).add(new THREE.Vector3(radius, radius * 0.7, radius));
  camera.near = Math.max(radius / 10_000, 0.1);
  camera.far = radius * 10;
  camera.updateProjectionMatrix();
  controls.update();
}

export function framePs2Positions(
  camera: THREE.PerspectiveCamera,
  controls: OrbitControls,
  positions: readonly Vector3Like[],
): boolean {
  if (!positions.length) return false;
  const center = new THREE.Vector3(
    median(positions.map((position) => position.x)),
    median(positions.map((position) => position.z)),
    median(positions.map((position) => -position.y)),
  );
  const distances = positions.map((position) => ps2PositionToScene(position, frameOffset).distanceTo(center)).sort((a, b) => a - b);
  const radius = Math.max(distances[Math.floor((distances.length - 1) * 0.75)], 1);
  controls.target.copy(center);
  camera.position.copy(center).add(frameOffset.set(radius, radius * 0.7, radius));
  camera.near = Math.max(radius / 10_000, 0.1);
  camera.far = Math.max(radius * 10, distances.at(-1)! * 2);
  camera.updateProjectionMatrix();
  controls.update();
  return true;
}

export function updateCameraMovement(
  camera: THREE.PerspectiveCamera,
  target: THREE.Vector3,
  keys: ReadonlySet<string>,
  velocity: THREE.Vector3,
  deltaSeconds: number,
): void {
  cameraMovement.set(0, 0, 0);
  camera.getWorldDirection(cameraForward);
  cameraRight.set(1, 0, 0).applyQuaternion(camera.quaternion);
  if (keys.has('KeyW')) cameraMovement.add(cameraForward);
  if (keys.has('KeyS')) cameraMovement.sub(cameraForward);
  if (keys.has('KeyA')) cameraMovement.sub(cameraRight);
  if (keys.has('KeyD')) cameraMovement.add(cameraRight);
  if (keys.has('KeyE') || keys.has('Space')) cameraMovement.y += 1;
  if (keys.has('KeyQ')) cameraMovement.y -= 1;
  const delta = Math.min(Math.max(deltaSeconds, 0), 0.05);
  if (cameraMovement.lengthSq()) {
    const fast = keys.has('ShiftLeft') || keys.has('ShiftRight');
    velocity.addScaledVector(cameraMovement.normalize(), (fast ? 900 : 180) * delta);
    velocity.clampLength(0, fast ? 1_200 : 300);
  } else {
    velocity.multiplyScalar(Math.exp(-7 * delta));
    if (velocity.lengthSq() < 0.0001) velocity.set(0, 0, 0);
  }
  cameraMovement.copy(velocity).multiplyScalar(delta);
  camera.position.add(cameraMovement);
  target.add(cameraMovement);
}

export function updateCameraFlight(
  cameraPosition: THREE.Vector3,
  target: THREE.Vector3,
  flight: CameraFlight,
  deltaSeconds: number,
): boolean {
  flight.elapsed = Math.min(flight.duration, flight.elapsed + Math.max(deltaSeconds, 0));
  const progress = flight.duration > 0 ? flight.elapsed / flight.duration : 1;
  const eased = progress * progress * (3 - 2 * progress);
  cameraPosition.lerpVectors(flight.cameraStart, flight.cameraEnd, eased);
  target.lerpVectors(flight.targetStart, flight.targetEnd, eased);
  return progress >= 1;
}

export function rotateCamera(
  camera: THREE.PerspectiveCamera,
  target: THREE.Vector3,
  deltaX: number,
  deltaY: number,
): void {
  const targetDistance = Math.max(camera.position.distanceTo(target), 1);
  cameraEuler.setFromQuaternion(camera.quaternion, 'YXZ');
  cameraEuler.y -= deltaX * 0.0022;
  cameraEuler.x = THREE.MathUtils.clamp(cameraEuler.x - deltaY * 0.0022, -Math.PI / 2 + 0.001, Math.PI / 2 - 0.001);
  camera.quaternion.setFromEuler(cameraEuler);
  camera.getWorldDirection(cameraForward);
  target.copy(camera.position).addScaledVector(cameraForward, targetDistance);
}

export function ps2PositionToScene(position: Vector3Like, target = new THREE.Vector3()): THREE.Vector3 {
  return target.set(position.x, position.z, -position.y);
}

function median(values: number[]): number {
  values.sort((a, b) => a - b);
  return values[Math.floor(values.length / 2)];
}

export function disposeObject(root: THREE.Object3D): void {
  const geometries = new Set<THREE.BufferGeometry>();
  const materials = new Set<THREE.Material>();
  const textures = new Set<THREE.Texture>();
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    geometries.add(object.geometry);
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      materials.add(material);
      for (const value of Object.values(material)) if (value instanceof THREE.Texture) textures.add(value);
    }
  });
  for (const geometry of geometries) geometry.dispose();
  for (const material of materials) material.dispose();
  for (const texture of textures) texture.dispose();
  root.removeFromParent();
  root.clear();
}
