import * as THREE from 'three';
import { Line2 } from 'three/addons/lines/Line2.js';
import { LineGeometry } from 'three/addons/lines/LineGeometry.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';
import { LineSegments2 } from 'three/addons/lines/LineSegments2.js';
import { LineSegmentsGeometry } from 'three/addons/lines/LineSegmentsGeometry.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
import { createPs2OpaquePassMaterial } from '../../utils/Ps2Materials.ts';
import { ps2PositionToScene } from '../../utils/Scene.ts';
import { DEFAULT_SCENE_TREE_COLORS } from '../../utils/SceneTreeColors.ts';
import { parseSplinePointId, splinePointId } from '../../utils/SplinePoints.ts';

const ENTITY_ID_KEY = 'forgeEntityId';
const INSTANCE_IDS_KEY = 'forgeInstanceIds';
const MIRRORED_BATCH_KEY = 'forgeMirroredBatch';
const OWNED_GEOMETRY_KEY = 'forgeOwnedGeometry';
const PICK_THROUGH_KEY = 'forgePickThrough';
const PROJECTION_KEY = 'forgeProjectionKey';
const SPLINE_NODES_KEY = 'forgeSplineNodes';
const SPLINE_SELECTED_NODES_KEY = 'forgeSelectedSplineNodes';
const VOLUME_EDGE_PICKER_KEY = 'forgeVolumeEdgePicker';
const PS2_TO_SCENE_ROTATION = new THREE.Quaternion(-Math.SQRT1_2, 0, 0, Math.SQRT1_2);
const SCENE_TO_PS2_ROTATION = PS2_TO_SCENE_ROTATION.clone().invert();
const NORMAL_COLOR = new THREE.Color(0xffffff);
const SELECTED_COLOR = new THREE.Color(0x22d3ee);
const INSTANCE_MIRROR = new THREE.Matrix4().makeScale(-1, 1, 1);
const SPLINE_LINE_WIDTH = 5;
const SPLINE_NODE_SIZE = 12;
const VOLUME_PICK_WIDTH = 8;

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
  private readonly cuboidPickerGeometry = createEdgePickerGeometry(this.cuboidGeometry);
  private readonly areaPickerGeometry = createEdgePickerGeometry(this.areaGeometry);
  private readonly cylinderPickerGeometry = createEdgePickerGeometry(this.cylinderGeometry);
  private readonly pillPickerGeometry = createEdgePickerGeometry(this.pillGeometry);
  private readonly splineNodeTexture = createSplineNodeTexture();
  private readonly assetMaterial = new THREE.MeshBasicMaterial({ color: 0x4363d8, wireframe: true, fog: false });
  private readonly modelLessMaterial = new THREE.MeshBasicMaterial({ color: 0xf032e6, wireframe: true, fog: false });
  private readonly cuboidMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly areaMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly sphereMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly cylinderMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly pillMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false, side: THREE.DoubleSide });
  private readonly splineMaterial = new LineMaterial({ linewidth: SPLINE_LINE_WIDTH, fog: false });
  private readonly splineNodeMaterial = new THREE.PointsMaterial({
    alphaTest: 0.5, fog: false, map: this.splineNodeTexture, size: SPLINE_NODE_SIZE, sizeAttenuation: false,
  });
  private readonly grindPathMaterial = new LineMaterial({ linewidth: SPLINE_LINE_WIDTH, fog: false });
  private readonly directionalLightMaterial = new THREE.LineBasicMaterial({ fog: false });
  private readonly pointLightMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly environmentSampleMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly environmentTransitionMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly cameraMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly ambientSoundMaterial = new THREE.MeshBasicMaterial({ wireframe: true, fog: false });
  private readonly volumePickerMaterial = new LineMaterial({ linewidth: VOLUME_PICK_WIDTH, visible: false });
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
  private readonly selectedSplineNodeMaterial = new THREE.PointsMaterial({
    alphaTest: 0.5, color: SELECTED_COLOR, fog: false, map: this.splineNodeTexture,
    size: SPLINE_NODE_SIZE, sizeAttenuation: false,
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
    this.splineNodeMaterial.color.set(colors.spline);
    this.grindPathMaterial.color.set(colors.grindPath);
    this.areaMaterial.color.set(colors.area);
    this.directionalLightMaterial.color.set(colors.directionalLight);
    this.pointLightMaterial.color.set(colors.pointLight);
    this.environmentSampleMaterial.color.set(colors.environmentSample);
    this.environmentTransitionMaterial.color.set(colors.environmentTransition);
    this.cameraMaterial.color.set(colors.camera);
    this.ambientSoundMaterial.color.set(colors.ambientSound);
  }

  setViewportSize(width: number, height: number): void {
    this.volumePickerMaterial.resolution.set(Math.max(width, 1), Math.max(height, 1));
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
    const selectedPoints = new Map<string, number[]>();
    selection.forEach((value) => {
      const point = parseSplinePointId(value);
      if (!point) return;
      const indices = selectedPoints.get(point.entityId);
      if (indices) indices.push(point.index);
      else selectedPoints.set(point.entityId, [point.index]);
    });
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
        selected.has(entity.id) && !selectedPoints.has(entity.id),
        this.isVisible(entity, false, visibleLayers, showMarkers),
        selectedPoints.get(entity.id) ?? [],
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
    const point = parseSplinePointId(entityId);
    if (point) {
      const position = this.getSplinePointPosition(point.entityId, point.index, this.position);
      return position ? target.set(position, position) : undefined;
    }
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
    for (let index = 0; index < intersections.length; index += 1) {
      const intersection = intersections[index];
      if (!intersection.object.userData[SPLINE_NODES_KEY] || intersection.index === undefined) continue;
      const entityId = this.resolveEntityId(intersection.object);
      if (!entityId || !this.pickable.has(entityId)) continue;
      const blocked = intersections.slice(0, index).some((prior) => {
        const priorId = this.resolveIntersectionEntityId(prior);
        return priorId && this.pickable.has(priorId) && priorId !== entityId
          && (prior.object.userData[VOLUME_EDGE_PICKER_KEY] || !this.isPickThrough(prior.object));
      });
      if (!blocked) return splinePointId(entityId, intersection.index);
    }
    let fallback: string | undefined;
    for (const intersection of intersections) {
      const id = this.resolveIntersectionEntityId(intersection);
      if (!id || !this.pickable.has(id)) continue;
      if (intersection.object.userData[VOLUME_EDGE_PICKER_KEY]) return id;
      if (!this.isPickThrough(intersection.object)) return id;
      fallback ??= id;
    }
    return fallback;
  }

  resolveIntersectionEntityId(intersection: THREE.Intersection): string | undefined {
    const instanceIds = intersection.object.userData[INSTANCE_IDS_KEY] as string[] | undefined;
    return instanceIds && intersection.instanceId !== undefined
      ? instanceIds[intersection.instanceId]
      : this.resolveEntityId(intersection.object);
  }

  private isPickThrough(object: THREE.Object3D): boolean {
    for (let current: THREE.Object3D | null = object; current && current !== this.root; current = current.parent)
      if (current.userData[PICK_THROUGH_KEY]) return true;
    return false;
  }

  getWorldVertices(entityIds: readonly string[]): THREE.Vector3[] {
    const selected = new Set(entityIds.filter((id) => !parseSplinePointId(id)));
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
        if (child instanceof THREE.Mesh
          && !child.userData[SPLINE_NODES_KEY] && !child.userData[VOLUME_EDGE_PICKER_KEY])
          appendWorldVertices(child.geometry, child.matrixWorld, vertices);
      });
    }
    entityIds.forEach((id) => {
      const point = parseSplinePointId(id);
      if (point) {
        const position = this.getSplinePointPosition(point.entityId, point.index, new THREE.Vector3());
        if (position) vertices.push(position);
      }
    });
    return vertices;
  }

  getSplinePointPosition(entityId: string, index: number, target = new THREE.Vector3()): THREE.Vector3 | undefined {
    const object = this.objects.get(entityId);
    const nodes = object?.children.find((child) => child.userData[SPLINE_NODES_KEY]);
    const positions = nodes instanceof THREE.Points ? nodes.geometry.getAttribute('position') : undefined;
    if (!nodes || !positions || index >= positions.count) return undefined;
    object!.updateMatrixWorld(true);
    return target.fromBufferAttribute(positions, index).applyMatrix4(nodes.matrixWorld);
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
    this.cuboidPickerGeometry.dispose();
    this.areaPickerGeometry.dispose();
    this.cylinderPickerGeometry.dispose();
    this.pillPickerGeometry.dispose();
    this.splineNodeTexture.dispose();
    this.assetMaterial.dispose();
    this.modelLessMaterial.dispose();
    this.cuboidMaterial.dispose();
    this.areaMaterial.dispose();
    this.sphereMaterial.dispose();
    this.cylinderMaterial.dispose();
    this.pillMaterial.dispose();
    this.splineMaterial.dispose();
    this.splineNodeMaterial.dispose();
    this.grindPathMaterial.dispose();
    this.directionalLightMaterial.dispose();
    this.pointLightMaterial.dispose();
    this.environmentSampleMaterial.dispose();
    this.environmentTransitionMaterial.dispose();
    this.cameraMaterial.dispose();
    this.ambientSoundMaterial.dispose();
    this.volumePickerMaterial.dispose();
    this.failedMaterial.dispose();
    this.selectedMaterial.dispose();
    this.selectedGeometryMaterial.dispose();
    this.selectedSplineMaterial.dispose();
    this.selectedSplineNodeMaterial.dispose();
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
      const line = new Line2(
        new LineGeometry().setFromPoints(points),
        entity.geometry.kind === 'grindPath' ? this.grindPathMaterial : this.splineMaterial,
      );
      if (points.length > 0) {
        const nodes = new THREE.Points(new THREE.BufferGeometry().setFromPoints(points), this.splineNodeMaterial);
        nodes.userData[SPLINE_NODES_KEY] = true;
        const selectedNodes = new THREE.Points(new THREE.BufferGeometry(), this.selectedSplineNodeMaterial);
        selectedNodes.raycast = () => {};
        selectedNodes.userData[SPLINE_SELECTED_NODES_KEY] = true;
        line.add(nodes, selectedNodes);
      }
      object = line;
      object.userData[OWNED_GEOMETRY_KEY] = true;
    } else if (entity.geometry?.kind === 'directionalLight') {
      const points = entity.geometry.points.map((point) => ps2PositionToScene(point, new THREE.Vector3()));
      object = new THREE.Line(new THREE.BufferGeometry().setFromPoints(points), this.directionalLightMaterial);
      object.userData[OWNED_GEOMETRY_KEY] = true;
    } else {
      object = new THREE.Mesh(this.geometry, this.materialFor(entity, false));
    }
    const pickerGeometry = this.volumePickerGeometry(entity);
    if (pickerGeometry) {
      const picker = new LineSegments2(pickerGeometry, this.volumePickerMaterial);
      picker.userData[VOLUME_EDGE_PICKER_KEY] = true;
      object.add(picker);
    }
    object.userData[ENTITY_ID_KEY] = entity.id;
    object.userData[PICK_THROUGH_KEY] = entity.geometry !== undefined
      && ['cuboid', 'sphere', 'cylinder', 'pill', 'area', 'pointLight', 'environmentTransition', 'ambientSound']
        .includes(entity.geometry.kind);
    object.userData[PROJECTION_KEY] = key;
    return object;
  }

  private updateObject(
    object: THREE.Object3D,
    entity: EditorEntity,
    selected: boolean,
    visible: boolean,
    selectedPointIndices: readonly number[],
  ): boolean {
    this.projectedMatrix(entity);
    const marker = object.children.find((child) => child.userData.selectionMarker);
    const splineNodes = object.children.find((child) => child.userData[SPLINE_NODES_KEY]) as
      THREE.Points | undefined;
    const selectedSplineNodes = object.children.find((child) => child.userData[SPLINE_SELECTED_NODES_KEY]) as
      THREE.Points | undefined;
    const material = !marker && (object instanceof THREE.Mesh || object instanceof THREE.Line)
      ? this.materialFor(entity, selected)
      : undefined;
    const nodeMaterial = splineNodes
      ? selected ? this.selectedSplineNodeMaterial : this.splineNodeMaterial
      : undefined;
    const pointSelection = selectedPointIndices.join(',');
    const changed = object.name !== entity.name
      || object.visible !== visible
      || !object.position.equals(this.position)
      || !object.quaternion.equals(this.projectedRotation)
      || !object.scale.equals(this.scale)
      || material !== undefined && (object as THREE.Mesh).material !== material
      || nodeMaterial !== undefined && splineNodes!.material !== nodeMaterial
      || selectedSplineNodes !== undefined && selectedSplineNodes.userData.pointSelection !== pointSelection
      || marker !== undefined && marker.visible !== selected;
    object.name = entity.name;
    object.visible = visible;
    object.position.copy(this.position);
    object.quaternion.copy(this.projectedRotation);
    object.scale.copy(this.scale);
    if (marker) marker.visible = selected;
    else if ((object instanceof THREE.Mesh || object instanceof THREE.Line) && material) object.material = material;
    if (splineNodes && nodeMaterial) splineNodes.material = nodeMaterial;
    if (splineNodes && selectedSplineNodes && selectedSplineNodes.userData.pointSelection !== pointSelection) {
      const positions = splineNodes.geometry.getAttribute('position');
      const indices = selectedPointIndices.filter((index) => index < positions.count);
      const selectedPositions = new Float32Array(indices.length * 3);
      indices.forEach((index, selectedIndex) => {
        selectedPositions[selectedIndex * 3] = positions.getX(index);
        selectedPositions[selectedIndex * 3 + 1] = positions.getY(index);
        selectedPositions[selectedIndex * 3 + 2] = positions.getZ(index);
      });
      selectedSplineNodes.geometry.setAttribute('position', new THREE.BufferAttribute(selectedPositions, 3));
      selectedSplineNodes.geometry.computeBoundingSphere();
      selectedSplineNodes.userData.pointSelection = pointSelection;
    }
    object.updateMatrix();
    return changed;
  }

  private volumePickerGeometry(entity: EditorEntity): LineSegmentsGeometry | undefined {
    switch (entity.geometry?.kind) {
      case 'cuboid': case 'environmentTransition': case 'ambientSound': return this.cuboidPickerGeometry;
      case 'sphere': case 'area': case 'pointLight': return this.areaPickerGeometry;
      case 'cylinder': return this.cylinderPickerGeometry;
      case 'pill': return this.pillPickerGeometry;
      default: return undefined;
    }
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
    object.traverse((child) => {
      if (child instanceof THREE.Points
        && (child.userData[SPLINE_NODES_KEY] || child.userData[SPLINE_SELECTED_NODES_KEY])) child.geometry.dispose();
    });
    if (object.userData[OWNED_GEOMETRY_KEY] && (object instanceof THREE.Line || object instanceof THREE.Mesh))
      object.geometry.dispose();
  }
}

function createEdgePickerGeometry(source: THREE.BufferGeometry): LineSegmentsGeometry {
  const edges = new THREE.EdgesGeometry(source);
  const geometry = new LineSegmentsGeometry().fromEdgesGeometry(edges);
  edges.dispose();
  return geometry;
}

function createSplineNodeTexture(): THREE.DataTexture {
  const size = 32;
  const data = new Uint8Array(size * size * 4);
  for (let y = 0; y < size; y += 1) {
    for (let x = 0; x < size; x += 1) {
      const dx = (x + 0.5) / size * 2 - 1;
      const dy = (y + 0.5) / size * 2 - 1;
      const radiusSquared = dx * dx + dy * dy;
      if (radiusSquared > 1) continue;
      const shade = 0.65 + Math.sqrt(1 - radiusSquared) * 0.35;
      const offset = (y * size + x) * 4;
      data[offset] = data[offset + 1] = data[offset + 2] = Math.round(shade * 255);
      data[offset + 3] = 255;
    }
  }
  const texture = new THREE.DataTexture(data, size, size, THREE.RGBAFormat);
  texture.needsUpdate = true;
  return texture;
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
