import * as THREE from 'three';
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js';

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

export function disposeObject(root: THREE.Object3D): void {
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    object.geometry.dispose();
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) material.dispose();
  });
}
