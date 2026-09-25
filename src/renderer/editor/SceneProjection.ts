import * as THREE from 'three';
import { Line2 } from 'three/addons/lines/Line2.js';
import { LineGeometry } from 'three/addons/lines/LineGeometry.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
import { createPs2OpaquePassMaterial } from '../../utils/Ps2Materials.ts';
import { ps2PositionToScene } from '../../utils/Scene.ts';
import { DEFAULT_SCENE_TREE_COLORS } from '../../utils/SceneTreeColors.ts';

const ENTITY_ID_KEY = 'forgeEntityId';
const INSTANCE_IDS_KEY = 'forgeInstanceIds';
const MIRRORED_BATCH_KEY = 'forgeMirroredBatch';
const OWNED_GEOMETRY_KEY = 'forgeOwnedGeometry';
const PROJECTION_KEY = 'forgeProjectionKey';
const PS2_TO_SCENE_ROTATION = new THREE.Quaternion(-Math.SQRT1_2, 0, 0, Math.SQRT1_2);
const SCENE_TO_PS2_ROTATION = PS2_TO_SCENE_ROTATION.clone().invert();
const NORMAL_COLOR = new THREE.Color(0xffffff);
const SELECTED_COLOR = new THREE.Color(0x22d3ee);
const INSTANCE_MIRROR = new THREE.Matrix4().makeScale(-1, 1, 1);
const SPLINE_LINE_WIDTH = 5;

interface InstancedAsset {
  root: THREE.Group;
  meshes: THREE.InstancedMesh[];
  geometries: Set<THREE.BufferGeometry>;
  materials: Set<THREE.Material>;
  capacity: number;
  hasNormal: boolean;
  hasMirrored: boolean;
}

export class SceneProjection {
  readonly root = new THREE.Group();

  private readonly geometry = new THREE.BoxGeometry(16, 16, 16);
  private readonly cuboidGeometry = new THREE.BoxGeometry(2, 2, 2);
  private readonly areaGeometry = new THREE.SphereGeometry(1, 16, 8);
  private readonly cylinderGeometry = new THREE.CylinderGeometry(1, 1, 2, 16, 1, true);
  private readonly pillGeometry = new THREE.CapsuleGeometry(0.5, 1, 4, 8).scale(2, 1, 2);
  private readonly environmentSampleGeometry = new THREE.OctahedronGeometry(1);
  private readonly cameraGeometry = new THREE.ConeGeometry(2, 4, 4).rotateX(Math.PI / 2);
  private readonly assetMaterial = new THREE.MeshBasicMaterial({ color: 0x4363d8, wireframe: true, fog: false });
  private readonly modelLessMaterial = new THREE.MeshBasicMaterial({ color: 0xf032e6, wireframe: true, fog: false });
  private readonly cuboidMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly areaMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly sphereMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly cylinderMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly pillMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly splineMaterial = new LineMaterial({ linewidth: SPLINE_LINE_WIDTH, fog: false });
  private readonly grindPathMaterial = new LineMaterial({ linewidth: SPLINE_LINE_WIDTH, fog: false });
  private readonly directionalLightMaterial = new THREE.LineBasicMaterial({ fog: false });
  private readonly pointLightMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly environmentSampleMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly environmentTransitionMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly cameraMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly ambientSoundMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly failedMaterial = new THREE.MeshBasicMaterial({ color: 0xe6194b, wireframe: true, fog: false });
  private readonly selectedMaterial = new THREE.MeshBasicMaterial({
    color: SELECTED_COLOR, wireframe: true, fog: false, side: THREE.DoubleSide,
  });
  private readonly selectedGeometryMaterial = new THREE.MeshBasicMaterial({
    color: SELECTED_COLOR, wireframe: true, fog: false, side: THREE.DoubleSide,
  });
  private readonly selectedSplineMaterial = new LineMaterial({
    color: SELECTED_COLOR, linewidth: SPLINE_LINE_WIDTH, fog: false,
  });
  private readonly selectedLineMaterial = new THREE.LineBasicMaterial({ color: SELECTED_COLOR, fog: false });
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
    this.setGeometryColors(DEFAULT_SCENE_TREE_COLORS);
  }

  setGeometryColors(colors: SceneTreeColors): void {
    this.modelLessMaterial.color.set(colors.moby);
    this.cuboidMaterial.color.set(colors.cuboid);
    this.sphereMaterial.color.set(colors.sphere);
    this.cylinderMaterial.color.set(colors.cylinder);
    this.pillMaterial.color.set(colors.pill);
    this.splineMaterial.color.set(colors.spline);
    this.grindPathMaterial.color.set(colors.grindPath);
    this.areaMaterial.color.set(colors.area);
    this.directionalLightMaterial.color.set(colors.directionalLight);
    this.pointLightMaterial.color.set(colors.pointLight);
    this.environmentSampleMaterial.color.set(colors.environmentSample);
    this.environmentTransitionMaterial.color.set(colors.environmentTransition);
    this.cameraMaterial.color.set(colors.camera);
    this.ambientSoundMaterial.color.set(colors.ambientSound);
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

  sync(
    entities: readonly EditorEntity[],
    selection: readonly string[] = [],
    visibleLayers?: ReadonlySet<string>,
    showMarkers = true,
  ) {
    if (this.disposed) throw new Error('Scene projection is disposed');
    const selected = new Set(selection);
    const staticGroups = new Map<string, EditorEntity[]>();
    const projected = new Set<string>();
    this.pickable.clear();

    for (const entity of entities) {
      const template = entity.asset && this.templates.get(entity.asset.id);
      const visible = this.isVisible(entity, Boolean(template), visibleLayers, showMarkers);
      if (visible && !entity.state.locked) this.pickable.add(entity.id);
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
        if (object) this.removeObject(object);
        object = this.createObject(entity, key);
        this.objects.set(entity.id, object);
        this.root.add(object);
        created += 1;
        isNew = true;
      }
      if (this.updateObject(
        object,
        entity,
        selected.has(entity.id),
        this.isVisible(entity, false, visibleLayers, showMarkers),
      ) && !isNew) updated += 1;
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
      const hasNormal = group.some((entity) => !isMirrored(entity));
      const hasMirrored = group.some(isMirrored);
      let projection = this.instances.get(assetId);
      if (!projection || projection.capacity < group.length
        || projection.hasNormal !== hasNormal || projection.hasMirrored !== hasMirrored) {
        if (projection) {
          projection.root.removeFromParent();
          disposeInstancedAsset(projection);
        }
        projection = createInstancedAsset(template, group.length, hasNormal, hasMirrored);
        this.instances.set(assetId, projection);
        this.root.add(projection.root);
      }
      this.updateInstances(projection, group, selected, visibleLayers);
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
        target.union(this.instanceBounds.copy(mesh.geometry.boundingBox)
          .applyMatrix4(this.instanceMatrix).applyMatrix4(mesh.matrix));
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
      const id = this.resolveIntersectionEntityId(intersection);
      if (id && this.pickable.has(id)) return id;
    }
    return undefined;
  }

  resolveIntersectionEntityId(intersection: THREE.Intersection): string | undefined {
    const instanceIds = intersection.object.userData[INSTANCE_IDS_KEY] as string[] | undefined;
    return instanceIds && intersection.instanceId !== undefined
      ? instanceIds[intersection.instanceId]
      : this.resolveEntityId(intersection.object);
  }

  getWorldVertices(entityIds: readonly string[]): THREE.Vector3[] {
    const selected = new Set(entityIds);
    const seen = new Set<string>();
    const vertices: THREE.Vector3[] = [];
    this.root.updateMatrixWorld(true);
    for (const projection of this.instances.values()) {
      for (const mesh of projection.meshes) {
        const ids = mesh.userData[INSTANCE_IDS_KEY] as string[];
        ids.forEach((id, index) => {
          if (!selected.has(id) || seen.has(`${id}:${mesh.geometry.uuid}`)) return;
          seen.add(`${id}:${mesh.geometry.uuid}`);
          mesh.getMatrixAt(index, this.instanceMatrix);
          this.instanceMatrix.premultiply(mesh.matrixWorld);
          appendWorldVertices(mesh.geometry, this.instanceMatrix, vertices);
        });
      }
    }
    for (const id of selected) {
      const object = this.objects.get(id);
      object?.updateMatrixWorld(true);
      object?.traverse((child) => {
        if (child instanceof THREE.Mesh) appendWorldVertices(child.geometry, child.matrixWorld, vertices);
      });
    }
    return vertices;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.clearProjection();
    this.root.removeFromParent();
    this.geometry.dispose();
    this.cuboidGeometry.dispose();
    this.areaGeometry.dispose();
    this.cylinderGeometry.dispose();
    this.pillGeometry.dispose();
    this.environmentSampleGeometry.dispose();
    this.cameraGeometry.dispose();
    this.assetMaterial.dispose();
    this.modelLessMaterial.dispose();
    this.cuboidMaterial.dispose();
    this.areaMaterial.dispose();
    this.sphereMaterial.dispose();
    this.cylinderMaterial.dispose();
    this.pillMaterial.dispose();
    this.splineMaterial.dispose();
    this.grindPathMaterial.dispose();
    this.directionalLightMaterial.dispose();
    this.pointLightMaterial.dispose();
    this.environmentSampleMaterial.dispose();
    this.environmentTransitionMaterial.dispose();
    this.cameraMaterial.dispose();
    this.ambientSoundMaterial.dispose();
    this.failedMaterial.dispose();
    this.selectedMaterial.dispose();
    this.selectedGeometryMaterial.dispose();
    this.selectedSplineMaterial.dispose();
    this.selectedLineMaterial.dispose();
  }

  private removeStaleObjects(projected: ReadonlySet<string>, entities: readonly EditorEntity[]): number {
    const retained = new Set(entities.filter((entity) => !projected.has(entity.id)).map((entity) => entity.id));
    let removed = 0;
    for (const [id, object] of this.objects) {
      if (retained.has(id)) continue;
      this.removeObject(object);
      this.objects.delete(id);
      removed += 1;
    }
    return removed;
  }

  private createObject(entity: EditorEntity, key: string): THREE.Object3D {
    let object: THREE.Object3D;
    if (entity.geometry?.kind === 'cuboid') {
      object = new THREE.Mesh(this.cuboidGeometry, this.cuboidMaterial);
    } else if (entity.geometry?.kind === 'sphere') {
      object = new THREE.Mesh(this.areaGeometry, this.sphereMaterial);
    } else if (entity.geometry?.kind === 'cylinder') {
      object = new THREE.Mesh(this.cylinderGeometry, this.cylinderMaterial);
    } else if (entity.geometry?.kind === 'pill') {
      object = new THREE.Mesh(this.pillGeometry, this.pillMaterial);
    } else if (entity.geometry?.kind === 'area') {
      object = new THREE.Mesh(this.areaGeometry, this.areaMaterial);
    } else if (entity.geometry?.kind === 'pointLight') {
      object = new THREE.Mesh(this.areaGeometry, this.pointLightMaterial);
    } else if (entity.geometry?.kind === 'environmentSample') {
      object = new THREE.Mesh(this.environmentSampleGeometry, this.environmentSampleMaterial);
    } else if (entity.geometry?.kind === 'environmentTransition') {
      object = new THREE.Mesh(this.cuboidGeometry, this.environmentTransitionMaterial);
    } else if (entity.geometry?.kind === 'camera') {
      object = new THREE.Mesh(this.cameraGeometry, this.cameraMaterial);
    } else if (entity.geometry?.kind === 'ambientSound') {
      object = new THREE.Mesh(this.cuboidGeometry, this.ambientSoundMaterial);
    } else if (entity.geometry?.kind === 'spline' || entity.geometry?.kind === 'grindPath') {
      const points = entity.geometry.points.map((point) => ps2PositionToScene(point, new THREE.Vector3()));
      object = new Line2(
        new LineGeometry().setFromPoints(points),
        entity.geometry.kind === 'grindPath' ? this.grindPathMaterial : this.splineMaterial,
      );
      object.userData[OWNED_GEOMETRY_KEY] = true;
    } else if (entity.geometry?.kind === 'directionalLight') {
      const points = entity.geometry.points.map((point) => ps2PositionToScene(point, new THREE.Vector3()));
      object = new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), this.directionalLightMaterial);
      object.userData[OWNED_GEOMETRY_KEY] = true;
    } else {
      object = new THREE.Mesh(this.geometry, this.materialFor(entity, false));
    }
    object.userData[ENTITY_ID_KEY] = entity.id;
    object.userData[PROJECTION_KEY] = key;
    return object;
  }

  private updateObject(object: THREE.Object3D, entity: EditorEntity, selected: boolean, visible: boolean): boolean {
    this.projectedMatrix(entity);
    const marker = object.children.find((child) => child.userData.selectionMarker);
    const material = !marker && (object instanceof THREE.Mesh || object instanceof THREE.Line)
      ? this.materialFor(entity, selected)
      : undefined;
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
    else if ((object instanceof THREE.Mesh || object instanceof THREE.Line) && material) object.material = material;
    object.updateMatrix();
    return changed;
  }

  private updateInstances(
    projection: InstancedAsset,
    entities: readonly EditorEntity[],
    selected: ReadonlySet<string>,
    visibleLayers?: ReadonlySet<string>,
  ): void {
    const visible = entities.filter((entity) => this.isVisible(entity, true, visibleLayers, true));
    projection.meshes.forEach((mesh) => {
      const mirrored = mesh.userData[MIRRORED_BATCH_KEY] === true;
      const batch = visible.filter((entity) => isMirrored(entity) === mirrored);
      mesh.count = batch.length;
      mesh.userData[INSTANCE_IDS_KEY] = batch.map((entity) => entity.id);
      batch.forEach((entity, index) => {
        this.instanceMatrix.copy(this.projectedMatrix(entity));
        if (mirrored) this.instanceMatrix.premultiply(INSTANCE_MIRROR);
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

  private isVisible(
    entity: EditorEntity,
    hasTemplate: boolean,
    visibleLayers?: ReadonlySet<string>,
    showMarkers = true,
  ): boolean {
    return !entity.state.hidden
      && !entity.state.disabled
      && (visibleLayers?.has(entity.layer) ?? true)
      && (hasTemplate || showMarkers);
  }

  private projectionKey(entity: EditorEntity): string {
    if (entity.geometry) return `geometry:${JSON.stringify(entity.geometry)}`;
    if (!entity.asset) return 'meshless';
    return this.templates.has(entity.asset.id) ? `asset:${entity.asset.id}` : `proxy:${entity.asset.id}`;
  }

  private materialFor(entity: EditorEntity, selected: boolean): THREE.Material {
    if (entity.geometry?.kind === 'spline') return selected ? this.selectedSplineMaterial : this.splineMaterial;
    if (entity.geometry?.kind === 'grindPath') return selected ? this.selectedSplineMaterial : this.grindPathMaterial;
    if (entity.geometry?.kind === 'directionalLight')
      return selected ? this.selectedLineMaterial : this.directionalLightMaterial;
    if (selected && entity.geometry) return this.selectedGeometryMaterial;
    if (selected) return this.selectedMaterial;
    if (entity.geometry?.kind === 'cuboid') return this.cuboidMaterial;
    if (entity.geometry?.kind === 'sphere') return this.sphereMaterial;
    if (entity.geometry?.kind === 'cylinder') return this.cylinderMaterial;
    if (entity.geometry?.kind === 'pill') return this.pillMaterial;
    if (entity.geometry?.kind === 'area') return this.areaMaterial;
    if (entity.geometry?.kind === 'pointLight') return this.pointLightMaterial;
    if (entity.geometry?.kind === 'environmentSample') return this.environmentSampleMaterial;
    if (entity.geometry?.kind === 'environmentTransition') return this.environmentTransitionMaterial;
    if (entity.geometry?.kind === 'camera') return this.cameraMaterial;
    if (entity.geometry?.kind === 'ambientSound') return this.ambientSoundMaterial;
    if (!entity.asset) return this.modelLessMaterial;
    return this.failedAssets.has(entity.asset.id) ? this.failedMaterial : this.assetMaterial;
  }

  private clearProjection(): void {
    this.objects.forEach((object) => this.removeObject(object));
    this.objects.clear();
    this.instances.forEach((projection) => {
      projection.root.removeFromParent();
      disposeInstancedAsset(projection);
    });
    this.instances.clear();
    this.pickable.clear();
    this.root.clear();
  }

  private removeObject(object: THREE.Object3D): void {
    object.removeFromParent();
    if (object.userData[OWNED_GEOMETRY_KEY] && (object instanceof THREE.Line || object instanceof THREE.Mesh))
      object.geometry.dispose();
  }
}

function createInstancedAsset(
  template: THREE.Object3D,
  capacity: number,
  hasNormal: boolean,
  hasMirrored: boolean,
): InstancedAsset {
  const root = new THREE.Group();
  const meshes: THREE.InstancedMesh[] = [];
  const geometries = new Set<THREE.BufferGeometry>();
  const materials = new Set<THREE.Material>();
  const buckets = new Map<THREE.Material | THREE.Material[], Map<string, THREE.BufferGeometry[]>>();
  template.traverse((object) => {
    // Moby metal overlays intentionally stay disabled until the map chrome texture is extracted and applied.
    if (!(object instanceof THREE.Mesh) || object.name === 'shrub_billboard' || isMobyMetalObject(object)) return;
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
        addInstancedMesh(root, meshes, geometries, materials, geometry, material, capacity, hasNormal, hasMirrored);
      } else {
        parts.forEach((part) => addInstancedMesh(
          root, meshes, geometries, materials, part, material, capacity, hasNormal, hasMirrored,
        ));
      }
    }
  }
  return { root, meshes, geometries, materials, capacity, hasNormal, hasMirrored };
}

function isMobyMetalObject(object: THREE.Object3D): boolean {
  for (let current: THREE.Object3D | null = object; current; current = current.parent)
    if (current.name === 'metals') return true;
  return false;
}

function geometrySignature(geometry: THREE.BufferGeometry): string {
  return `${geometry.index ? 'indexed' : 'plain'}:${Object.entries(geometry.attributes)
    .map(([name, value]) => `${name}/${value.array.constructor.name}/${value.itemSize}/${value.normalized}`)
    .sort().join(',')}`;
}

function appendWorldVertices(
  geometry: THREE.BufferGeometry,
  matrix: THREE.Matrix4,
  target: THREE.Vector3[],
): void {
  const starts = geometry.getAttribute('instanceStart');
  const ends = geometry.getAttribute('instanceEnd');
  if (starts && ends) {
    for (let index = 0; index < starts.count; index += 1)
      target.push(new THREE.Vector3().fromBufferAttribute(starts, index).applyMatrix4(matrix));
    if (ends.count > 0)
      target.push(new THREE.Vector3().fromBufferAttribute(ends, ends.count - 1).applyMatrix4(matrix));
    return;
  }
  const position = geometry.getAttribute('position');
  if (!position) return;
  for (let index = 0; index < position.count; index += 1)
    target.push(new THREE.Vector3().fromBufferAttribute(position, index).applyMatrix4(matrix));
}

function addInstancedMesh(
  root: THREE.Group,
  meshes: THREE.InstancedMesh[],
  geometries: Set<THREE.BufferGeometry>,
  materials: Set<THREE.Material>,
  geometry: THREE.BufferGeometry,
  material: THREE.Material | THREE.Material[],
  capacity: number,
  hasNormal: boolean,
  hasMirrored: boolean,
): void {
  const renderMaterials: (THREE.Material | THREE.Material[])[] = [];
  if (!Array.isArray(material)) {
    const opaqueMaterial = createPs2OpaquePassMaterial(material);
    if (opaqueMaterial) {
      materials.add(opaqueMaterial);
      renderMaterials.push(opaqueMaterial);
    }
  }
  renderMaterials.push(material);
  for (const renderMaterial of renderMaterials) {
    if (hasNormal) addMesh(root, meshes, geometry, renderMaterial, capacity, false);
    if (hasMirrored) addMesh(root, meshes, geometry, renderMaterial, capacity, true);
  }
  geometries.add(geometry);
}

function addMesh(
  root: THREE.Group,
  meshes: THREE.InstancedMesh[],
  geometry: THREE.BufferGeometry,
  material: THREE.Material | THREE.Material[],
  capacity: number,
  mirrored: boolean,
): void {
  const mesh = new THREE.InstancedMesh(geometry, material, capacity);
  mesh.userData[MIRRORED_BATCH_KEY] = mirrored;
  if (mirrored) {
    mesh.matrix.copy(INSTANCE_MIRROR);
    mesh.matrixAutoUpdate = false;
    mesh.matrixWorldNeedsUpdate = true;
  }
  root.add(mesh);
  meshes.push(mesh);
}

function isMirrored(entity: EditorEntity): boolean {
  const { x, y, z } = entity.transform.scale;
  return x * y * z < 0;
}

function disposeInstancedAsset(asset: InstancedAsset): void {
  asset.meshes.forEach((mesh) => mesh.dispose());
  asset.geometries.forEach((geometry) => geometry.dispose());
  asset.materials.forEach((material) => material.dispose());
}
