import * as THREE from 'three';

export const UYA_OCEAN_PATCH_SIZE = 500;
export const UYA_OCEAN_PATCH_TRIANGLES = 6_720;

const FAR_DISTANCE = UYA_OCEAN_PATCH_SIZE * 3;
const SEGMENTS_X = 32;
const NEAR_STRIPS = 50;
const MID_STRIPS = 50;
const FAR_STRIPS = 5;
const MID_END_ROW = NEAR_STRIPS + MID_STRIPS;
const SEGMENTS_Y = MID_END_ROW + FAR_STRIPS;
const point = new THREE.Vector3();
const direction = new THREE.Vector3();
const cameraPosition = new THREE.Vector3();
const cameraForward = new THREE.Vector3();

export function createUyaOceanGeometry(): THREE.BufferGeometry {
  const geometry = new THREE.BufferGeometry();
  const vertexCount = (SEGMENTS_X + 1) * (SEGMENTS_Y + 1);
  const indices: number[] = [];
  for (let y = 0; y < SEGMENTS_Y; y += 1) {
    for (let x = 0; x < SEGMENTS_X; x += 1) {
      const first = y * (SEGMENTS_X + 1) + x;
      indices.push(first, first + 1, first + SEGMENTS_X + 2, first, first + SEGMENTS_X + 2, first + SEGMENTS_X + 1);
    }
  }
  geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(vertexCount * 3), 3)
    .setUsage(THREE.DynamicDrawUsage));
  geometry.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(vertexCount * 2), 2)
    .setUsage(THREE.DynamicDrawUsage));
  geometry.setAttribute('waterWaveBank0Scale', new THREE.Float32BufferAttribute(
    Array.from({ length: vertexCount }, (_, index) => Math.min(1, Math.max(0,
      (MID_END_ROW - Math.floor(index / (SEGMENTS_X + 1))) / MID_STRIPS))), 1,
  ));
  geometry.setAttribute('waterWaveBank1Scale', new THREE.Float32BufferAttribute(
    Array.from({ length: vertexCount }, (_, index) => Math.max(0,
      (NEAR_STRIPS - Math.floor(index / (SEGMENTS_X + 1))) / NEAR_STRIPS)), 1,
  ));
  geometry.setIndex(indices);
  return geometry;
}

export function updateUyaOceanGeometry(
  geometry: THREE.BufferGeometry,
  camera: THREE.Camera,
  waterY: number,
  waveAmplitude: number,
): void {
  const positions = geometry.getAttribute('position') as THREE.BufferAttribute;
  const uvs = geometry.getAttribute('uv') as THREE.BufferAttribute;
  camera.getWorldPosition(cameraPosition);
  camera.getWorldDirection(cameraForward);
  cameraForward.y = 0;
  if (cameraForward.lengthSq() <= 1e-8) cameraForward.set(0, 0, -1);
  cameraForward.normalize();
  const waveTop = waterY + Math.abs(waveAmplitude);
  const waveBottom = waterY - Math.abs(waveAmplitude);
  point.set(0, -1, 1).unproject(camera);
  direction.copy(point).sub(cameraPosition).normalize();
  const nearDistance = direction.y < -1e-6
    ? (waveTop - cameraPosition.y) / direction.y
      * (direction.x * cameraForward.x + direction.z * cameraForward.z)
    : 0;
  let rowDistance = nearDistance;
  for (let y = 0; y <= SEGMENTS_Y; y += 1) {
    if (y > 0) rowDistance += y <= NEAR_STRIPS ? 1 : y <= MID_END_ROW ? 2 : 200;
    point.copy(cameraPosition).addScaledVector(cameraForward, Math.min(rowDistance, FAR_DISTANCE));
    point.y = waveBottom;
    const ndcY = point.project(camera).y;
    for (let x = 0; x <= SEGMENTS_X; x += 1) {
      point.set(-1 + 2 * x / SEGMENTS_X, ndcY, 1).unproject(camera);
      direction.copy(point).sub(cameraPosition).normalize();
      const horizontalLength = Math.hypot(direction.x, direction.z);
      const planeDistance = direction.y < -1e-6
        ? (waveBottom - cameraPosition.y) / direction.y * horizontalLength
        : FAR_DISTANCE;
      const horizontalDistance = Math.min(Math.max(planeDistance, 0), FAR_DISTANCE);
      const scale = horizontalLength > 1e-6 ? horizontalDistance / horizontalLength : 0;
      const localX = direction.x * scale;
      const localY = -direction.z * scale;
      const index = y * (SEGMENTS_X + 1) + x;
      positions.setXYZ(index, localX, localY, 0);
      uvs.setXY(index, -localY / UYA_OCEAN_PATCH_SIZE, localX / UYA_OCEAN_PATCH_SIZE);
    }
  }
  positions.needsUpdate = true;
  uvs.needsUpdate = true;
}
