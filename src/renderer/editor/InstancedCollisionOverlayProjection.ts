import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

import type { CollisionVisualization } from '../../types/CollisionVisualization.js';
import type { EditorCollisionOctantCost, ProjectTransform } from '../../types/EditorRuntime.js';
import { configureCollisionMaterials } from '../../utils/CollisionMaterials.ts';
import { disposeObject } from '../../utils/Scene.ts';
import { projectTransformToSceneMatrix } from '../../utils/Transforms.ts';
import type { InstancedCollisionOverlay } from './EditorContext.ts';
import {
  applyCollisionFaceTypes, collisionFaceIdFromIntersection, collisionRawTypeFromIntersection,
  createInstancedCollisionWireframeOverlay,
} from './CollisionPainting.ts';

interface PaintGeometry {
  collisionTypes: THREE.BufferAttribute | THREE.InterleavedBufferAttribute;
  soundTypes: THREE.BufferAttribute | THREE.InterleavedBufferAttribute;
  colors: THREE.BufferAttribute;
  verticesByFace: Map<number, number[]>;
}

export class InstancedCollisionOverlayProjection {
  readonly root = new THREE.Group();
  private candidate?: THREE.Group;
  private proxy?: THREE.Object3D;
  private wireframe?: THREE.Object3D;
  private paint?: InstancedCollisionOverlay['paint'];
  private paintGeometry: PaintGeometry[] = [];
  private pendingFaceTypes = new Map<number, number>();
  private hoveredFace?: { faceId: number | undefined; color: string };
  private generation = 0;
  private visibility = { showOctants: false, showProxy: false, wireframe: false };

  constructor(
    private readonly collisionScenes: Set<THREE.Object3D>,
    private readonly onLoadError: () => void,
  ) {
    this.root.name = 'Instanced collision candidate preview';
  }

  show(
    overlay: InstancedCollisionOverlay,
    transform: ProjectTransform,
    visualization: CollisionVisualization,
  ): void {
    this.clear();
    this.visibility = {
      showOctants: overlay.showOctants,
      showProxy: overlay.showProxy,
      wireframe: overlay.wireframe,
    };
    this.paint = overlay.paint;
    const generation = this.generation;
    const candidate = new THREE.Group();
    candidate.matrixAutoUpdate = false;
    candidate.matrix.copy(projectTransformToSceneMatrix(transform));
    const octants = createPressureOverlay(overlay.candidate?.octants ?? []);
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
      setOpacity(gltf.scene, this.paint ? 1 : 0.55);
      this.collisionScenes.add(gltf.scene);
      candidate.add(gltf.scene);
      this.proxy = gltf.scene;
      this.wireframe = createInstancedCollisionWireframeOverlay(gltf.scene);
      candidate.add(this.wireframe);
      this.paintGeometry = this.paint ? indexPaintGeometry(gltf.scene) : [];
      this.applyPaintPreview();
      this.updateVisibility();
    }).catch(() => {
      if (generation === this.generation && this.candidate === candidate) this.onLoadError();
    });
  }

  update(overlay: InstancedCollisionOverlay): void {
    this.visibility = {
      showOctants: overlay.showOctants,
      showProxy: overlay.showProxy,
      wireframe: overlay.wireframe,
    };
    const previousPaint = this.paint;
    this.paint = overlay.paint;
    if (this.pendingFaceTypes.size === 0
      && (previousPaint?.defaultRawType !== this.paint?.defaultRawType
        || previousPaint?.faceTypes !== this.paint?.faceTypes)) this.applyPaintPreview();
    this.updateVisibility();
  }

  pick(raycaster: THREE.Raycaster): { faceId: number; rawType: number } | undefined {
    if (!this.proxy || !this.visibility.showProxy) return undefined;
    const hit = raycaster.intersectObject(this.proxy, true).find((value) =>
      collisionFaceIdFromIntersection(value) !== undefined);
    if (!hit) return undefined;
    const faceId = collisionFaceIdFromIntersection(hit);
    const rawType = collisionRawTypeFromIntersection(hit);
    return faceId === undefined || rawType === undefined ? undefined : { faceId, rawType };
  }

  previewFace(faceId: number, rawType: number): void {
    this.pendingFaceTypes.set(faceId, rawType);
    for (const geometry of this.paintGeometry) {
      const vertices = geometry.verticesByFace.get(faceId);
      if (!vertices) continue;
      for (const vertex of vertices) {
        geometry.collisionTypes.setX(vertex, rawType & 0x0f);
        geometry.soundTypes.setX(vertex, rawType >> 4 & 0x0f);
      }
      geometry.collisionTypes.needsUpdate = true;
      geometry.soundTypes.needsUpdate = true;
    }
  }

  finishStroke(committed: boolean): void {
    if (committed && this.paint) {
      const faceTypes = new Map(this.paint.faceTypes.map((value) => [value.faceIndex, value.rawType]));
      this.pendingFaceTypes.forEach((rawType, faceIndex) => {
        if (rawType === this.paint!.defaultRawType) faceTypes.delete(faceIndex);
        else faceTypes.set(faceIndex, rawType);
      });
      this.paint = {
        ...this.paint,
        faceTypes: [...faceTypes].sort(([left], [right]) => left - right)
          .map(([faceIndex, rawType]) => ({ faceIndex, rawType })),
      };
    }
    this.pendingFaceTypes.clear();
    this.applyPaintPreview();
  }

  setHoveredFace(faceId: number | undefined, color: string): void {
    const hovered = this.hoveredFace;
    if (!this.proxy || hovered && hovered.faceId === faceId && hovered.color === color) return;
    const selected = new THREE.Color(color);
    for (const geometry of this.paintGeometry) {
      if (hovered?.faceId !== faceId) setFaceColor(geometry, hovered?.faceId, new THREE.Color(0xffffff));
      setFaceColor(geometry, faceId, selected);
    }
    this.hoveredFace = { faceId, color };
  }

  clear(): void {
    this.generation += 1;
    if (!this.candidate) return;
    this.candidate.traverse((object) => this.collisionScenes.delete(object));
    disposeObject(this.candidate);
    this.candidate = undefined;
    this.proxy = undefined;
    this.wireframe = undefined;
    this.paint = undefined;
    this.paintGeometry = [];
    this.pendingFaceTypes.clear();
    this.hoveredFace = undefined;
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
    }
    if (this.wireframe)
      this.wireframe.visible = this.visibility.showProxy && this.visibility.wireframe;
    if (octants) octants.visible = this.visibility.showOctants;
  }

  private applyPaintPreview(): void {
    if (!this.proxy || !this.paint) return;
    applyCollisionFaceTypes(this.proxy, this.paint.defaultRawType, [
      ...this.paint.faceTypes,
      ...[...this.pendingFaceTypes].map(([faceIndex, rawType]) => ({ faceIndex, rawType })),
    ]);
  }
}

function createPressureOverlay(
  octants: readonly EditorCollisionOctantCost[],
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
  const translucent = opacity < 1;
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      material.transparent = translucent;
      material.opacity = opacity;
      material.depthWrite = !translucent;
      material.needsUpdate = true;
    }
  });
}

function indexPaintGeometry(root: THREE.Object3D): PaintGeometry[] {
  const indexed: PaintGeometry[] = [];
  const meshes: THREE.Mesh[] = [];
  root.traverse((object) => {
    if (object instanceof THREE.Mesh) meshes.push(object);
  });
  for (const object of meshes) {
    const faceIds = object.geometry.getAttribute('_collision_face_id');
    const collisionTypes = object.geometry.getAttribute('_collision_type');
    const soundTypes = object.geometry.getAttribute('_sound_type');
    if (!faceIds || !collisionTypes || !soundTypes) continue;
    // Keep hover in the existing color path; binding integer face IDs in the shader hides some applied proxies.
    const colors = new THREE.Float32BufferAttribute(new Float32Array(faceIds.count * 3).fill(1), 3);
    object.geometry.setAttribute('color', colors);
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      (material as THREE.Material & { vertexColors?: boolean }).vertexColors = true;
      material.needsUpdate = true;
    }
    const verticesByFace = new Map<number, number[]>();
    for (let vertex = 0; vertex < faceIds.count; vertex += 1) {
      const faceId = faceIds.getX(vertex);
      const vertices = verticesByFace.get(faceId);
      if (vertices) vertices.push(vertex);
      else verticesByFace.set(faceId, [vertex]);
    }
    indexed.push({ collisionTypes, soundTypes, colors, verticesByFace });
  }
  return indexed;
}

function setFaceColor(
  geometry: PaintGeometry,
  faceId: number | undefined,
  color: THREE.Color,
): void {
  if (faceId === undefined) return;
  const vertices = geometry.verticesByFace.get(faceId);
  if (!vertices) return;
  for (const vertex of vertices) geometry.colors.setXYZ(vertex, color.r, color.g, color.b);
  geometry.colors.needsUpdate = true;
}
