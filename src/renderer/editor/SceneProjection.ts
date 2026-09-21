import * as THREE from 'three';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import { ps2PositionToScene } from '../../utils/Scene.ts';

const ENTITY_ID_KEY = 'forgeEntityId';
const INSTANCE_IDS_KEY = 'forgeInstanceIds';
const PROJECTION_KEY = 'forgeProjectionKey';
const PS2_TO_SCENE_ROTATION = new THREE.Quaternion(-Math.SQRT1_2, 0, 0, Math.SQRT1_2);
const SCENE_TO_PS2_ROTATION = PS2_TO_SCENE_ROTATION.clone().invert();
const NORMAL_COLOR = new THREE.Color(0xffffff);
const SELECTED_COLOR = new THREE.Color(0x22d3ee);

interface InstancedAsset {
  root: THREE.Group;
  meshes: THREE.InstancedMesh[];
  geometries: THREE.BufferGeometry[];
  capacity: number;
}

export class SceneProjection {
  readonly root = new THREE.Group();

  private readonly geometry = new THREE.BoxGeometry(16, 16, 16);
  private readonly assetMaterial = new THREE.MeshBasicMaterial({ color: 0x4dabf7, wireframe: true });
  private readonly modelLessMaterial = new THREE.MeshBasicMaterial({ color: 0xffc078, wireframe: true });
  private readonly failedMaterial = new THREE.MeshBasicMaterial({ color: 0xff4dcd, wireframe: true });
  private readonly selectedMaterial = new THREE.MeshBasicMaterial({ color: SELECTED_COLOR, wireframe: true });
  private readonly objects = new Map<string, THREE.Object3D>();
  private readonly instances = new Map<string, InstancedAsset>();
  private readonly templates = new Map<string, THREE.Object3D>();
  private readonly failedAssets = new Set<string>();
  private readonly pickable = new Set<string>();
  private readonly sourceRotation = new THREE.Quaternion();
  private readonly projectedRotation = new THREE.Quaternion();
  private readonly position = new THREE.Vector3();
  private readonly scale = new THREE.Vector3();
  private readonly entityMatrix = new THREE.Matrix4();
  private readonly instanceMatrix = new THREE.Matrix4();
  private readonly instanceBounds = new THREE.Box3();
  private disposed = false;

  constructor() {
    this.root.name = 'Forge project entities';
  }

  setAssetTemplates(templates: ReadonlyMap<string, THREE.Object3D>, failedAssets: ReadonlySet<string> = new Set()): void {
    this.clearProjection();
    this.templates.clear();
    templates.forEach((template, id) => {
      template.updateMatrixWorld(true);
      this.templates.set(id, template);
    });
    this.failedAssets.clear();
    failedAssets.forEach((id) => this.failedAssets.add(id));
  }

  sync(entities: readonly EditorEntity[], selection: readonly string[] = []) {
    if (this.disposed) throw new Error('Scene projection is disposed');
    const selected = new Set(selection);
    const staticGroups = new Map<string, EditorEntity[]>();
    const projected = new Set<string>();
    this.pickable.clear();

    for (const entity of entities) {
      if (!entity.state.hidden && !entity.state.disabled && !entity.state.locked) this.pickable.add(entity.id);
      const template = entity.asset && this.templates.get(entity.asset.id);
      if (entity.asset && template) {
        const group = staticGroups.get(entity.asset.id);
        if (group) group.push(entity);
        else staticGroups.set(entity.asset.id, [entity]);
        projected.add(entity.id);
      }
    }

    let removed = this.removeStaleObjects(projected, entities);
    let created = 0;
    let updated = 0;
    for (const entity of entities) {
      if (projected.has(entity.id)) continue;
      const key = this.projectionKey(entity);
      let object = this.objects.get(entity.id);
      let isNew = false;
      if (object?.userData[PROJECTION_KEY] !== key) {
        object?.removeFromParent();
        object = this.createObject(entity, key);
        this.objects.set(entity.id, object);
        this.root.add(object);
        created += 1;
        isNew = true;
      }
      if (this.updateObject(object, entity, selected.has(entity.id)) && !isNew) updated += 1;
    }

    for (const [assetId, group] of this.instances) {
      if (staticGroups.has(assetId)) continue;
      group.root.removeFromParent();
      disposeInstancedAsset(group);
      this.instances.delete(assetId);
      removed += 1;
    }
    for (const [assetId, group] of staticGroups) {
      const template = this.templates.get(assetId)!;
      let projection = this.instances.get(assetId);
      if (!projection || projection.capacity < group.length) {
        if (projection) {
          projection.root.removeFromParent();
          disposeInstancedAsset(projection);
        }
        projection = createInstancedAsset(template, group.length);
        this.instances.set(assetId, projection);
        this.root.add(projection.root);
      }
      this.updateInstances(projection, group, selected);
    }

    return { created, updated, removed };
  }

  getObject(entityId: string): THREE.Object3D | undefined {
    const object = this.objects.get(entityId);
    if (object) return object;
    for (const projection of this.instances.values())
      if (projection.meshes.some((mesh) => (mesh.userData[INSTANCE_IDS_KEY] as string[]).includes(entityId)))
        return projection.root;
    return undefined;
  }

  getBounds(entityId: string, target = new THREE.Box3()): THREE.Box3 | undefined {
    const object = this.objects.get(entityId);
    if (object) return target.setFromObject(object);
    target.makeEmpty();
    for (const projection of this.instances.values()) {
      for (const mesh of projection.meshes) {
        const index = (mesh.userData[INSTANCE_IDS_KEY] as string[]).indexOf(entityId);
        if (index < 0) continue;
        if (!mesh.geometry.boundingBox) mesh.geometry.computeBoundingBox();
        if (!mesh.geometry.boundingBox) continue;
        mesh.getMatrixAt(index, this.instanceMatrix);
        target.union(this.instanceBounds.copy(mesh.geometry.boundingBox).applyMatrix4(this.instanceMatrix));
      }
    }
    return target.isEmpty() ? undefined : target;
  }

  resolveEntityId(object: THREE.Object3D): string | undefined {
    for (let current: THREE.Object3D | null = object; current; current = current.parent) {
      const value = current.userData[ENTITY_ID_KEY];
      if (typeof value === 'string') return value;
      if (current === this.root) break;
    }
    return undefined;
  }

  resolvePick(intersections: readonly THREE.Intersection[]): string | undefined {
    for (const intersection of intersections) {
      const instanceIds = intersection.object.userData[INSTANCE_IDS_KEY] as string[] | undefined;
      const id = instanceIds && intersection.instanceId !== undefined
        ? instanceIds[intersection.instanceId]
        : this.resolveEntityId(intersection.object);
      if (id && this.pickable.has(id)) return id;
    }
    return undefined;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.clearProjection();
    this.root.removeFromParent();
    this.geometry.dispose();
    this.assetMaterial.dispose();
    this.modelLessMaterial.dispose();
    this.failedMaterial.dispose();
    this.selectedMaterial.dispose();
  }

  private removeStaleObjects(projected: ReadonlySet<string>, entities: readonly EditorEntity[]): number {
    const retained = new Set(entities.filter((entity) => !projected.has(entity.id)).map((entity) => entity.id));
    let removed = 0;
    for (const [id, object] of this.objects) {
      if (retained.has(id)) continue;
      object.removeFromParent();
      this.objects.delete(id);
      removed += 1;
    }
    return removed;
  }

  private createObject(entity: EditorEntity, key: string): THREE.Object3D {
    const object = new THREE.Mesh(this.geometry, this.materialFor(entity, false));
    object.userData[ENTITY_ID_KEY] = entity.id;
    object.userData[PROJECTION_KEY] = key;
    return object;
  }

  private updateObject(object: THREE.Object3D, entity: EditorEntity, selected: boolean): boolean {
    const visible = !entity.state.hidden && !entity.state.disabled;
    this.projectedMatrix(entity);
    const marker = object.children.find((child) => child.userData.selectionMarker);
    const material = !marker && object instanceof THREE.Mesh ? this.materialFor(entity, selected) : undefined;
    const changed = object.name !== entity.name
      || object.visible !== visible
      || !object.position.equals(this.position)
      || !object.quaternion.equals(this.projectedRotation)
      || !object.scale.equals(this.scale)
      || material !== undefined && (object as THREE.Mesh).material !== material
      || marker !== undefined && marker.visible !== selected;
    object.name = entity.name;
    object.visible = visible;
    object.position.copy(this.position);
    object.quaternion.copy(this.projectedRotation);
    object.scale.copy(this.scale);
    if (marker) marker.visible = selected;
    else if (object instanceof THREE.Mesh && material) object.material = material;
    object.updateMatrix();
    return changed;
  }

  private updateInstances(projection: InstancedAsset, entities: readonly EditorEntity[], selected: ReadonlySet<string>): void {
    const visible = entities.filter((entity) => !entity.state.hidden && !entity.state.disabled);
    const ids = visible.map((entity) => entity.id);
    projection.meshes.forEach((mesh) => {
      mesh.count = visible.length;
      mesh.userData[INSTANCE_IDS_KEY] = ids;
      visible.forEach((entity, index) => {
        this.instanceMatrix.copy(this.projectedMatrix(entity));
        mesh.setMatrixAt(index, this.instanceMatrix);
        mesh.setColorAt(index, selected.has(entity.id) ? SELECTED_COLOR : NORMAL_COLOR);
      });
      mesh.instanceMatrix.needsUpdate = true;
      if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
      mesh.computeBoundingSphere();
    });
  }

  private projectedMatrix(entity: EditorEntity): THREE.Matrix4 {
    const { position, rotation, scale } = entity.transform;
    this.sourceRotation.set(rotation.x, rotation.y, rotation.z, rotation.w);
    this.projectedRotation.copy(PS2_TO_SCENE_ROTATION)
      .multiply(this.sourceRotation)
      .multiply(SCENE_TO_PS2_ROTATION);
    ps2PositionToScene(position, this.position);
    this.scale.set(scale.x, scale.z, scale.y);
    return this.entityMatrix.compose(this.position, this.projectedRotation, this.scale);
  }

  private projectionKey(entity: EditorEntity): string {
    if (!entity.asset) return 'meshless';
    return this.templates.has(entity.asset.id) ? `asset:${entity.asset.id}` : `proxy:${entity.asset.id}`;
  }

  private materialFor(entity: EditorEntity, selected: boolean): THREE.Material {
    if (selected) return this.selectedMaterial;
    if (!entity.asset) return this.modelLessMaterial;
    return this.failedAssets.has(entity.asset.id) ? this.failedMaterial : this.assetMaterial;
  }

  private clearProjection(): void {
    this.objects.forEach((object) => object.removeFromParent());
    this.objects.clear();
    this.instances.forEach((projection) => {
      projection.root.removeFromParent();
      disposeInstancedAsset(projection);
    });
    this.instances.clear();
    this.pickable.clear();
    this.root.clear();
  }
}

function createInstancedAsset(template: THREE.Object3D, capacity: number): InstancedAsset {
  const root = new THREE.Group();
  const meshes: THREE.InstancedMesh[] = [];
  const geometries: THREE.BufferGeometry[] = [];
  const buckets = new Map<THREE.Material | THREE.Material[], Map<string, THREE.BufferGeometry[]>>();
  template.traverse((object) => {
    if (!(object instanceof THREE.Mesh) || object.name === 'shrub_billboard') return;
    const geometry = object.geometry.clone();
    geometry.deleteAttribute('skinIndex');
    geometry.deleteAttribute('skinWeight');
    geometry.morphAttributes = {};
    geometry.applyMatrix4(object.matrixWorld);
    const byLayout = buckets.get(object.material) ?? new Map<string, THREE.BufferGeometry[]>();
    buckets.set(object.material, byLayout);
    const signature = geometrySignature(geometry);
    const parts = byLayout.get(signature) ?? [];
    parts.push(geometry);
    byLayout.set(signature, parts);
  });
  for (const [material, byLayout] of buckets) {
    for (const parts of byLayout.values()) {
      const geometry = parts.length === 1 ? parts[0] : mergeGeometries(parts);
      if (geometry) {
        if (parts.length > 1) parts.forEach((part) => part.dispose());
        addInstancedMesh(root, meshes, geometries, geometry, material, capacity);
      } else {
        parts.forEach((part) => addInstancedMesh(root, meshes, geometries, part, material, capacity));
      }
    }
  }
  return { root, meshes, geometries, capacity };
}

function geometrySignature(geometry: THREE.BufferGeometry): string {
  return `${geometry.index ? 'indexed' : 'plain'}:${Object.entries(geometry.attributes)
    .map(([name, value]) => `${name}/${value.array.constructor.name}/${value.itemSize}/${value.normalized}`)
    .sort().join(',')}`;
}

function addInstancedMesh(
  root: THREE.Group,
  meshes: THREE.InstancedMesh[],
  geometries: THREE.BufferGeometry[],
  geometry: THREE.BufferGeometry,
  material: THREE.Material | THREE.Material[],
  capacity: number,
): void {
  const mesh = new THREE.InstancedMesh(geometry, material, capacity);
  root.add(mesh);
  meshes.push(mesh);
  geometries.push(geometry);
}

function disposeInstancedAsset(asset: InstancedAsset): void {
  asset.meshes.forEach((mesh) => mesh.dispose());
  asset.geometries.forEach((geometry) => geometry.dispose());
}
