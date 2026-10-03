import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

import type { CollisionVisualization } from '../../types/CollisionVisualization.js';
import type { ProjectTransform } from '../../types/EditorRuntime.js';
import { configureCollisionMaterials } from '../../utils/CollisionMaterials.ts';
import { disposeObject } from '../../utils/Scene.ts';
import { projectTransformToSceneMatrix } from '../../utils/Transforms.ts';
import type { TieCollisionOverlay } from './EditorContext.ts';

export class TieCollisionOverlayProjection {
  readonly root = new THREE.Group();
  private candidate?: THREE.Group;
  private generation = 0;
  private visibility = { showOctants: false, showProxy: false, wireframe: false };

  constructor(
    private readonly collisionScenes: Set<THREE.Object3D>,
    private readonly onLoadError: () => void,
  ) {
    this.root.name = 'TIE collision candidate preview';
  }

  show(
    overlay: TieCollisionOverlay,
    transform: ProjectTransform,
    visualization: CollisionVisualization,
  ): void {
    this.clear();
    this.visibility = {
      showOctants: overlay.showOctants,
      showProxy: overlay.showProxy,
      wireframe: overlay.wireframe,
    };
    const generation = this.generation;
    const candidate = new THREE.Group();
    candidate.matrixAutoUpdate = false;
    candidate.matrix.copy(projectTransformToSceneMatrix(transform));
    const octants = createPressureOverlay(overlay.candidate.octants);
    octants.name = 'Candidate octant pressure';
    octants.visible = overlay.showOctants;
    candidate.add(octants);
    this.candidate = candidate;
    this.root.add(candidate);

    void new GLTFLoader().loadAsync(overlay.url).then((gltf) => {
      if (generation !== this.generation || this.candidate !== candidate) {
        disposeObject(gltf.scene);
        return;
      }
      gltf.scene.name = 'Candidate collision proxy';
      configureCollisionMaterials(gltf.scene, visualization);
      setOpacity(gltf.scene, 0.55);
      this.collisionScenes.add(gltf.scene);
      candidate.add(gltf.scene);
      this.updateVisibility();
    }).catch(() => {
      if (generation === this.generation && this.candidate === candidate) this.onLoadError();
    });
  }

  update(overlay: TieCollisionOverlay): void {
    this.visibility = {
      showOctants: overlay.showOctants,
      showProxy: overlay.showProxy,
      wireframe: overlay.wireframe,
    };
    this.updateVisibility();
  }

  clear(): void {
    this.generation += 1;
    if (!this.candidate) return;
    this.candidate.traverse((object) => this.collisionScenes.delete(object));
    disposeObject(this.candidate);
    this.candidate = undefined;
  }

  dispose(): void {
    this.clear();
    this.root.removeFromParent();
    this.root.clear();
  }

  private updateVisibility(): void {
    const proxy = this.candidate?.getObjectByName('Candidate collision proxy');
    const octants = this.candidate?.getObjectByName('Candidate octant pressure');
    if (proxy) {
      proxy.visible = this.visibility.showProxy;
      setWireframe(proxy, this.visibility.wireframe);
    }
    if (octants) octants.visible = this.visibility.showOctants;
  }
}

function createPressureOverlay(
  octants: TieCollisionOverlay['candidate']['octants'],
): THREE.InstancedMesh {
  const geometry = new THREE.BoxGeometry(4, 4, 4);
  const material = new THREE.MeshBasicMaterial({
    depthWrite: false, fog: false, opacity: 0.18, transparent: true, vertexColors: true,
  });
  const mesh = new THREE.InstancedMesh(geometry, material, octants.length);
  const matrix = new THREE.Matrix4();
  const color = new THREE.Color();
  const maximum = Math.max(1, ...octants.map((octant) => octant.encodedByteCount));
  octants.forEach((octant, index) => {
    matrix.makeTranslation(octant.x * 4 + 2, octant.z * 4 + 2, -(octant.y * 4 + 2));
    mesh.setMatrixAt(index, matrix);
    const pressure = octant.violations.length ? 1 : octant.encodedByteCount / maximum;
    mesh.setColorAt(index, color.setHSL((1 - pressure) * 0.33, 0.9, 0.5));
  });
  mesh.instanceMatrix.needsUpdate = true;
  if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
  return mesh;
}

function setOpacity(root: THREE.Object3D, opacity: number): void {
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      material.transparent = true;
      material.opacity = opacity;
      material.depthWrite = false;
      material.needsUpdate = true;
    }
  });
}

function setWireframe(root: THREE.Object3D, enabled: boolean): void {
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      (material as THREE.Material & { wireframe?: boolean }).wireframe = enabled;
      material.needsUpdate = true;
    }
  });
}
