import * as THREE from 'three';

import type { ProjectTransform } from '../types/EditorRuntime.js';

const PS2_TO_SCENE = new THREE.Matrix4().makeRotationX(-Math.PI / 2);
const SCENE_TO_PS2 = PS2_TO_SCENE.clone().invert();
const position = new THREE.Vector3();
const rotation = new THREE.Quaternion();
const scale = new THREE.Vector3();
const source = new THREE.Matrix4();

export function projectTransformToSceneMatrix(value: ProjectTransform, target = new THREE.Matrix4()): THREE.Matrix4 {
  source.compose(
    position.set(value.position.x, value.position.y, value.position.z),
    rotation.set(value.rotation.x, value.rotation.y, value.rotation.z, value.rotation.w).normalize(),
    scale.set(value.scale.x, value.scale.y, value.scale.z),
  );
  return target.copy(PS2_TO_SCENE).multiply(source).multiply(SCENE_TO_PS2);
}

export function sceneMatrixToProjectTransform(value: THREE.Matrix4): ProjectTransform {
  source.copy(SCENE_TO_PS2).multiply(value).multiply(PS2_TO_SCENE).decompose(position, rotation, scale);
  rotation.normalize();
  return {
    position: { x: position.x, y: position.y, z: position.z },
    rotation: { x: rotation.x, y: rotation.y, z: rotation.z, w: rotation.w },
    scale: { x: scale.x, y: scale.y, z: scale.z },
  };
}

export function cloneProjectTransform(value: ProjectTransform): ProjectTransform {
  return {
    position: { ...value.position },
    rotation: { ...value.rotation },
    scale: { ...value.scale },
  };
}
