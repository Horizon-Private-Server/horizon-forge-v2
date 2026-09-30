import * as THREE from 'three';

const ps2ToGltfBasis = new THREE.Matrix4().set(
  1, 0, 0, 0,
  0, 0, 1, 0,
  0, -1, 0, 0,
  0, 0, 0, 1,
);
const gltfToPs2Basis = ps2ToGltfBasis.clone().invert();
const skyRotationQuaternion = new THREE.Quaternion();
const skySourceRotationMatrix = new THREE.Matrix4();
const skyGltfRotationMatrix = new THREE.Matrix4();

interface Vector3Like {
  x: number;
  y: number;
  z: number;
}

interface SkyShellAnimation {
  object: THREE.Object3D;
  baseQuaternion: THREE.Quaternion;
  sourceInitial: THREE.Vector3;
  sourceVelocity: THREE.Vector3;
  sourceVelocityRadiansPerSecond: THREE.Vector3;
  initial: THREE.Vector3;
  velocity: THREE.Vector3;
  tickRadians: number;
  runtimeFrameRate: number;
}

export function configureSkybox(root: THREE.Object3D): {
  eye: THREE.Vector3;
  bounds: THREE.Box3;
  pieces: string[];
  update(deltaSeconds: number): void;
  setRotation(initial: Vector3Like, velocity: Vector3Like): void;
} {
  const pieces: string[] = [];
  const animations: SkyShellAnimation[] = [];
  root.traverse((object) => {
    object.frustumCulled = false;
    if (!(object instanceof THREE.Mesh)) return;
    pieces.push(object.name || `Sky piece ${pieces.length + 1}`);
    const metadata = { ...object.userData, ...object.geometry.userData };
    const sourceInitial = skyVector(metadata.SkyboxShellRotationRaw);
    const sourceVelocity = skyVector(metadata.SkyboxShellRotationDeltaRaw);
    const sourceVelocityRadiansPerSecond = skyVector(metadata.SkyboxShellSourceAngularVelocityRadiansPerSecond);
    const initial = skyVector(metadata.SkyboxShellRotationRadians);
    const velocity = skyVector(metadata.SkyboxShellAngularVelocityRadiansPerSecond);
    if (metadata.SkyboxShellIndex !== undefined || hasSkyRotation(sourceInitial) || hasSkyRotation(sourceVelocity)
      || hasSkyRotation(sourceVelocityRadiansPerSecond) || hasSkyRotation(initial) || hasSkyRotation(velocity)) {
      animations.push({
        object,
        baseQuaternion: object.quaternion.clone(),
        sourceInitial,
        sourceVelocity,
        sourceVelocityRadiansPerSecond,
        initial,
        velocity,
        tickRadians: finiteNumber(metadata.SkyboxRotationTickRadians, Math.PI / 32768),
        runtimeFrameRate: finiteNumber(metadata.SkyboxRuntimeFrameRate, 60),
      });
    }
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
  let elapsedSeconds = 0;
  const update = (deltaSeconds: number) => {
    elapsedSeconds += Math.max(Number.isFinite(deltaSeconds) ? deltaSeconds : 0, 0);
    updateSkyAnimations(animations, elapsedSeconds);
  };
  const setRotation = (initial: Vector3Like, velocity: Vector3Like) => {
    for (const animation of animations) {
      animation.sourceInitial.set(0, 0, 0);
      animation.sourceVelocity.set(0, 0, 0);
      animation.sourceVelocityRadiansPerSecond.set(0, 0, 0);
      animation.initial.set(initial.x, initial.y, initial.z);
      animation.velocity.set(velocity.x, velocity.y, velocity.z);
    }
    updateSkyAnimations(animations, elapsedSeconds);
  };
  update(0);
  root.updateMatrixWorld(true);
  const bounds = new THREE.Box3().setFromObject(root);
  const eye = skyEyeFromBounds(bounds);
  return { eye, bounds, pieces, update, setRotation };
}

export function skyEyeFromBounds(bounds: THREE.Box3, target = new THREE.Vector3()): THREE.Vector3 {
  if (bounds.isEmpty()) return target.set(0, 0, 0);
  const center = bounds.getCenter(new THREE.Vector3());
  const size = bounds.getSize(new THREE.Vector3());
  const maxDimension = Math.max(size.x, size.y, size.z, 1);
  return target.set(
    center.x,
    bounds.min.y <= 0 && bounds.max.y >= 0 ? maxDimension / 10_000 : bounds.min.y,
    center.z,
  );
}

function updateSkyAnimations(animations: readonly SkyShellAnimation[], elapsedSeconds: number): void {
  for (const animation of animations) {
    if (hasSkyRotation(animation.sourceInitial) || hasSkyRotation(animation.sourceVelocity)) {
      const frame = elapsedSeconds * animation.runtimeFrameRate;
      setSkyRotationQuaternion(
        (animation.sourceInitial.x + animation.sourceVelocity.x * frame) * animation.tickRadians,
        (animation.sourceInitial.y + animation.sourceVelocity.y * frame) * animation.tickRadians,
        (animation.sourceInitial.z + animation.sourceVelocity.z * frame) * animation.tickRadians,
      );
    } else if (hasSkyRotation(animation.sourceVelocityRadiansPerSecond)) {
      setSkyRotationQuaternion(
        animation.sourceVelocityRadiansPerSecond.x * elapsedSeconds,
        animation.sourceVelocityRadiansPerSecond.y * elapsedSeconds,
        animation.sourceVelocityRadiansPerSecond.z * elapsedSeconds,
      );
    } else {
      setSkyRotationQuaternion(
        animation.initial.x + animation.velocity.x * elapsedSeconds,
        animation.initial.y + animation.velocity.y * elapsedSeconds,
        animation.initial.z + animation.velocity.z * elapsedSeconds,
      );
    }
    animation.object.quaternion.copy(animation.baseQuaternion).multiply(skyRotationQuaternion);
    animation.object.matrixWorldNeedsUpdate = true;
  }
}

function setSkyRotationQuaternion(x: number, y: number, z: number): void {
  const sx = Math.sin(x);
  const cx = Math.cos(x);
  const sy = Math.sin(y);
  const cy = Math.cos(y);
  const sz = Math.sin(z);
  const cz = Math.cos(z);
  skySourceRotationMatrix.set(
    cy * cz, -cy * sz, sy, 0,
    cz * sx * sy + cx * sz, cx * cz - sx * sy * sz, -cy * sx, 0,
    -cx * sy * cz + sx * sz, cz * sx + cx * sy * sz, cx * cy, 0,
    0, 0, 0, 1,
  );
  skyGltfRotationMatrix.copy(ps2ToGltfBasis).multiply(skySourceRotationMatrix).multiply(gltfToPs2Basis);
  skyRotationQuaternion.setFromRotationMatrix(skyGltfRotationMatrix);
}

function skyVector(value: unknown): THREE.Vector3 {
  if (!Array.isArray(value)) return new THREE.Vector3();
  return new THREE.Vector3(Number(value[0]) || 0, Number(value[1]) || 0, Number(value[2]) || 0);
}

function hasSkyRotation(value: THREE.Vector3): boolean {
  return value.lengthSq() > 1e-12;
}

function finiteNumber(value: unknown, fallback: number): number {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}
