import * as THREE from 'three';
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js';

import type { EditorEntity } from '../types/EditorRuntime.js';
import type { EditorSceneEnvironment } from '../types/ForgeApi.js';
import { projectTransformToSceneMatrix } from './Transforms.ts';

const cameraMovement = new THREE.Vector3();
const cameraForward = new THREE.Vector3();
const cameraRight = new THREE.Vector3();
const cameraEuler = new THREE.Euler(0, 0, 0, 'YXZ');
const frameOffset = new THREE.Vector3();
const spawnMatrix = new THREE.Matrix4();
const spawnPosition = new THREE.Vector3();
const spawnRotation = new THREE.Quaternion();
const spawnScale = new THREE.Vector3();
const spawnRotationCorrection = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), -Math.PI / 2);
const preferredMobyClasses = [0x0000, 0x0057, 0x1c31];

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

export function positionCameraAtPreferredMoby(
  camera: THREE.PerspectiveCamera,
  controls: OrbitControls,
  entities: readonly EditorEntity[],
): boolean {
  const entity = preferredMobyClasses
    .map((classId) => entities.find((value) => value.layer === 'mobys' && value.sourceClassId === classId))
    .find(Boolean);
  if (!entity) return false;
  projectTransformToSceneMatrix(entity.transform, spawnMatrix).decompose(spawnPosition, spawnRotation, spawnScale);
  camera.position.copy(spawnPosition).addScaledVector(camera.up, 2);
  camera.quaternion.copy(spawnRotation).premultiply(spawnRotationCorrection);
  camera.getWorldDirection(cameraForward);
  controls.target.copy(camera.position).addScaledVector(cameraForward, 10);
  controls.update();
  return true;
}

export function applySceneEnvironment(
  scene: THREE.Scene,
  environment?: EditorSceneEnvironment,
  backgroundScene: THREE.Scene = scene,
): void {
  backgroundScene.background = environment
    ? colorFromRgb(environment.backgroundColor)
    : new THREE.Color(0x151922);
  if (backgroundScene !== scene) scene.background = null;
  scene.fog = environment ? createSceneFog(environment) : null;
}

export function configureSkybox(root: THREE.Object3D): { eye: THREE.Vector3; pieces: string[] } {
  root.updateMatrixWorld(true);
  const bounds = new THREE.Box3().setFromObject(root);
  const center = bounds.getCenter(new THREE.Vector3());
  const size = bounds.getSize(new THREE.Vector3());
  const maxDimension = Math.max(size.x, size.y, size.z, 1);
  const eye = new THREE.Vector3(
    center.x,
    bounds.min.y <= 0 && bounds.max.y >= 0 ? maxDimension / 10_000 : bounds.min.y,
    center.z,
  );
  const pieces: string[] = [];
  root.traverse((object) => {
    object.frustumCulled = false;
    if (!(object instanceof THREE.Mesh)) return;
    pieces.push(object.name || `Sky piece ${pieces.length + 1}`);
    const metadata = { ...object.userData, ...object.geometry.userData };
    const order = Number(metadata.SkyboxDrawOrder ?? metadata.SkyboxSourceDrawOrder ?? 0);
    object.renderOrder = -1_000 + (Number.isFinite(order) ? order : 0);
    const blendMode = String(metadata.SkyboxDrawBlendMode ?? '');
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      material.side = THREE.DoubleSide;
      material.depthTest = false;
      material.depthWrite = false;
      material.fog = false;
      material.toneMapped = false;
      if (blendMode === 'Bloom') material.blending = THREE.AdditiveBlending;
      material.needsUpdate = true;
    }
  });
  return { eye, pieces };
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

function createSceneFog(environment: EditorSceneEnvironment): THREE.Fog | null {
  const near = environment.fogNearDistance * 0.9;
  const far = environment.fogFarDistance * 0.9;
  const nearAmount = fogAmount(environment.fogNearIntensity);
  const farAmount = fogAmount(environment.fogFarIntensity);
  if (!Number.isFinite(near) || !Number.isFinite(far) || far <= near
    || Math.max(nearAmount, farAmount) <= 0) return null;
  return new THREE.Fog(colorFromRgb(environment.fogColor), Math.max(0, near), far);
}

function colorFromRgb([red, green, blue]: readonly number[]): THREE.Color {
  return new THREE.Color().setRGB(
    THREE.MathUtils.clamp(red / 255, 0, 1),
    THREE.MathUtils.clamp(green / 255, 0, 1),
    THREE.MathUtils.clamp(blue / 255, 0, 1),
    THREE.SRGBColorSpace,
  );
}

export function fogAmount(value: number): number {
  return 1 - THREE.MathUtils.clamp(Number.isFinite(value) ? value / 255 : 1, 0, 1) * (255 / 256);
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
