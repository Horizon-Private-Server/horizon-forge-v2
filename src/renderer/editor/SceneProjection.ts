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
import { createSelectionOverrideMaterial, INSTANCE_SELECTION_MARKER } from '../../utils/SelectionMaterials.ts';
import { parseSplinePointId, splinePointId } from '../../utils/SplinePoints.ts';

const ENTITY_ID_KEY = 'forgeEntityId';
const INSTANCE_IDS_KEY = 'forgeInstanceIds';
const MIRRORED_BATCH_KEY = 'forgeMirroredBatch';
const OWNED_GEOMETRY_KEY = 'forgeOwnedGeometry';
const PICK_THROUGH_KEY = 'forgePickThrough';
const PLACEMENT_PROXY_KEY = 'forgePlacementProxy';
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

interface CollisionBatchEntry {
  mesh: THREE.BatchedMesh;
  instanceId: number;
  geometryId: number;
}

interface CollisionBatch {
  root: THREE.Group;
  meshes: THREE.BatchedMesh[];
  entityIds: Set<string>;
  entries: Map<string, CollisionBatchEntry[]>;
  states: Map<string, string>;
  templates: Map<string, THREE.Object3D>;
  matrices: Map<string, THREE.Matrix4>;
}

export class SceneProjection {
  readonly root = new THREE.Group();

  private readonly selectionColor = SELECTED_COLOR.clone();
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
  private readonly selectionOutlineMaterial = new THREE.MeshBasicMaterial({
    colorWrite: false, depthTest: false, depthWrite: false,
  });
  private readonly objects = new Map<string, THREE.Object3D>();
  private readonly instances = new Map<string, InstancedAsset>();
  private readonly selectionOutlines = new Map<string, THREE.Group>();
  private directSelectionOutlines: THREE.Object3D[] = [];
  private collisionBatch?: CollisionBatch;
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

  setSelectionColor(color: THREE.ColorRepresentation): void {
    this.selectionColor.set(color);
    this.selectedMaterial.color.copy(this.selectionColor);
    this.selectedGeometryMaterial.color.copy(this.selectionColor);
    this.selectedSplineMaterial.color.copy(this.selectionColor);
    this.selectedSplineNodeMaterial.color.copy(this.selectionColor);
    this.selectedLineMaterial.color.copy(this.selectionColor);
    this.collisionBatch?.states.clear();
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

  hasAssetTemplate(assetId: string): boolean {
    return this.templates.has(assetId);
  }

  addAssetTemplate(assetId: string, template: THREE.Object3D): void {
    template.updateMatrixWorld(true);
    this.templates.set(assetId, template);
    this.failedAssets.delete(assetId);
  }

  isPlacementSurface(intersection: THREE.Intersection): boolean {
    if (!(intersection.object instanceof THREE.Mesh)) return false;
    for (let current: THREE.Object3D | null = intersection.object; current; current = current.parent) {
      if (current.userData[PLACEMENT_PROXY_KEY] === true) return false;
      if (current === this.root) break;
    }
    return true;
  }

  sync(
    entities: readonly EditorEntity[],
    selection: readonly string[] = [],
    visibleLayers?: ReadonlySet<string>,
    showMarkers = true,
    visibleCollisionKinds?: ReadonlySet<'solid' | 'playerBarrier'>,
  ) {
    if (this.disposed) throw new Error('Scene projection is disposed');
    entities = entities.filter((entity) => !entity.skyShell);
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
    const collisionEntities: EditorEntity[] = [];
    const projected = new Set<string>();
    this.pickable.clear();

    for (const entity of entities) {
      const templateKey = assetTemplateKey(entity);
      const template = templateKey && this.templates.get(templateKey);
      const visible = this.isVisible(entity, Boolean(template), visibleLayers, showMarkers, visibleCollisionKinds);
      if (visible && !entity.state.locked) this.pickable.add(entity.id);
      if (entity.asset && template) {
        if (entity.collision && !isMirrored(entity)) collisionEntities.push(entity);
        else {
          const group = staticGroups.get(templateKey!);
          if (group) group.push(entity);
          else staticGroups.set(templateKey!, [entity]);
        }
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
        this.isVisible(entity, false, visibleLayers, showMarkers, visibleCollisionKinds),
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
        projection = createInstancedAsset(template, group.length, hasNormal, hasMirrored, this.selectionColor);
        this.instances.set(assetId, projection);
        this.root.add(projection.root);
      }
      this.updateInstances(projection, group, selected, visibleLayers, visibleCollisionKinds);
    }

    const collisionIds = new Set(collisionEntities.map((entity) => entity.id));
    if (this.collisionBatch && !sameIds(this.collisionBatch.entityIds, collisionIds)) {
      this.collisionBatch.root.removeFromParent();
      disposeCollisionBatch(this.collisionBatch);
      this.collisionBatch = undefined;
      removed += 1;
    }
    if (!this.collisionBatch && collisionEntities.length) {
      this.collisionBatch = createCollisionBatch(collisionEntities, this.templates);
      this.root.add(this.collisionBatch.root);
      created += 1;
    }
    if (this.collisionBatch)
      this.updateCollisionBatch(this.collisionBatch, collisionEntities, selected, visibleLayers, visibleCollisionKinds);
    this.updateSelectionOutlines(entities, selected, visibleLayers, visibleCollisionKinds);

    return { created, updated, removed };
  }

  getObject(entityId: string): THREE.Object3D | undefined {
    const object = this.objects.get(entityId);
    if (object) return object;
    for (const projection of this.instances.values())
      if (projection.meshes.some((mesh) => (mesh.userData[INSTANCE_IDS_KEY] as string[]).includes(entityId)))
        return projection.root;
    if (this.collisionBatch?.entityIds.has(entityId)) return this.collisionBatch.root;
    return undefined;
  }

  getSelectionOutlineObjects(): THREE.Object3D[] {
    return [...this.directSelectionOutlines, ...this.selectionOutlines.values()];
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
    const collisionEntries = this.collisionBatch?.entries.get(entityId);
    if (collisionEntries) {
      for (const entry of collisionEntries) {
        const bounds = entry.mesh.getBoundingBoxAt(entry.geometryId, this.instanceBounds);
        if (!bounds) continue;
        entry.mesh.getMatrixAt(entry.instanceId, this.instanceMatrix);
        target.union(bounds.applyMatrix4(this.instanceMatrix).applyMatrix4(entry.mesh.matrixWorld));
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
    const instanceId = intersection.instanceId ?? intersection.batchId;
    return instanceIds && instanceId !== undefined
      ? instanceIds[instanceId]
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
    for (const id of selected) {
      const template = this.collisionBatch?.templates.get(id);
      const matrix = this.collisionBatch?.matrices.get(id);
      if (!template || !matrix) continue;
      template.traverseVisible((child) => {
        if (child instanceof THREE.Mesh)
          appendWorldVertices(child.geometry, this.instanceMatrix.copy(matrix).multiply(child.matrixWorld), vertices);
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
    this.selectionOutlineMaterial.dispose();
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
      const geometry = new LineGeometry();
      if (points.length > 1) geometry.setFromPoints(points);
      const line = new Line2(
        geometry,
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
    object.userData[PLACEMENT_PROXY_KEY] = !entity.geometry
      && (!assetTemplateKey(entity) || !this.templates.has(assetTemplateKey(entity)!));
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
    visibleCollisionKinds?: ReadonlySet<'solid' | 'playerBarrier'>,
  ): void {
    const visible = entities.filter((entity) =>
      this.isVisible(entity, true, visibleLayers, true, visibleCollisionKinds));
    projection.meshes.forEach((mesh) => {
      const mirrored = mesh.userData[MIRRORED_BATCH_KEY] === true;
      const batch = visible.filter((entity) => isMirrored(entity) === mirrored);
      mesh.count = batch.length;
      mesh.userData[INSTANCE_IDS_KEY] = batch.map((entity) => entity.id);
      batch.forEach((entity, index) => {
        this.instanceMatrix.copy(this.projectedMatrix(entity));
        if (mirrored) this.instanceMatrix.premultiply(INSTANCE_MIRROR);
        mesh.setMatrixAt(index, this.instanceMatrix);
        mesh.setColorAt(index, selected.has(entity.id) ? INSTANCE_SELECTION_MARKER : NORMAL_COLOR);
      });
      mesh.instanceMatrix.needsUpdate = true;
      if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
      mesh.computeBoundingSphere();
    });
  }

  private updateCollisionBatch(
    batch: CollisionBatch,
    entities: readonly EditorEntity[],
    selected: ReadonlySet<string>,
    visibleLayers?: ReadonlySet<string>,
    visibleCollisionKinds?: ReadonlySet<'solid' | 'playerBarrier'>,
  ): void {
    for (const entity of entities) {
      const visible = this.isVisible(entity, true, visibleLayers, true, visibleCollisionKinds);
      const isSelected = selected.has(entity.id);
      const { position, rotation, scale } = entity.transform;
      const state = `${position.x},${position.y},${position.z};${rotation.x},${rotation.y},${rotation.z},${rotation.w};${scale.x},${scale.y},${scale.z};${visible};${isSelected}`;
      if (batch.states.get(entity.id) === state) continue;
      const matrix = this.projectedMatrix(entity);
      for (const entry of batch.entries.get(entity.id) ?? []) {
        entry.mesh.setMatrixAt(entry.instanceId, matrix);
        entry.mesh.setVisibleAt(entry.instanceId, visible);
        entry.mesh.setColorAt(entry.instanceId, isSelected ? this.selectionColor : NORMAL_COLOR);
      }
      batch.matrices.set(entity.id, matrix.clone());
      batch.states.set(entity.id, state);
    }
  }

  private updateSelectionOutlines(
    entities: readonly EditorEntity[],
    selected: ReadonlySet<string>,
    visibleLayers?: ReadonlySet<string>,
    visibleCollisionKinds?: ReadonlySet<'solid' | 'playerBarrier'>,
  ): void {
    const desired = new Map(entities.filter((entity) => entity.asset && this.templates.has(assetTemplateKey(entity)!)
      && selected.has(entity.id)
      && this.isVisible(entity, true, visibleLayers, true, visibleCollisionKinds))
      .map((entity) => [entity.id, entity]));
    this.directSelectionOutlines = entities.flatMap((entity) => {
      if (!selected.has(entity.id) || desired.has(entity.id)) return [];
      const object = this.objects.get(entity.id);
      return object?.visible ? [object] : [];
    });
    for (const [id, outline] of this.selectionOutlines) {
      if (desired.has(id)) continue;
      disposeSelectionOutline(outline);
      this.selectionOutlines.delete(id);
    }
    for (const [id, entity] of desired) {
      let outline = this.selectionOutlines.get(id);
      if (!outline) {
        const template = this.templates.get(assetTemplateKey(entity)!);
        if (!template) continue;
        outline = createSelectionOutline(template, this.selectionOutlineMaterial);
        this.selectionOutlines.set(id, outline);
        this.root.add(outline);
      }
      outline.matrix.copy(this.projectedMatrix(entity));
      outline.matrixWorldNeedsUpdate = true;
    }
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
    visibleCollisionKinds?: ReadonlySet<'solid' | 'playerBarrier'>,
  ): boolean {
    return !entity.state.hidden
      && !entity.state.disabled
      && (visibleLayers?.has(entity.layer) ?? true)
      && (!entity.collision || visibleCollisionKinds?.has(entity.collision.kind) !== false)
      && (hasTemplate || showMarkers);
  }

  private projectionKey(entity: EditorEntity): string {
    if (entity.geometry) return `geometry:${JSON.stringify(entity.geometry)}`;
    if (!entity.asset) return 'meshless';
    const templateKey = assetTemplateKey(entity)!;
    return this.templates.has(templateKey) ? `asset:${templateKey}` : `proxy:${templateKey}`;
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
    if (this.collisionBatch) {
      this.collisionBatch.root.removeFromParent();
      disposeCollisionBatch(this.collisionBatch);
      this.collisionBatch = undefined;
    }
    this.selectionOutlines.forEach(disposeSelectionOutline);
    this.selectionOutlines.clear();
    this.directSelectionOutlines = [];
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

export function assetTemplateKey(entity: EditorEntity): string | undefined {
  if (!entity.asset) return undefined;
  return entity.collision
    ? `${entity.asset.id}:${entity.collision.kind}:${entity.collision.sourcePieceIndex}`
    : entity.asset.id;
}

function createEdgePickerGeometry(source: THREE.BufferGeometry): LineSegmentsGeometry {
  const edges = new THREE.EdgesGeometry(source);
  const geometry = new LineSegmentsGeometry().fromEdgesGeometry(edges);
  edges.dispose();
  return geometry;
}

function createSelectionOutline(template: THREE.Object3D, material: THREE.Material): THREE.Group {
  const root = new THREE.Group();
  root.name = 'Selected object outline';
  root.matrixAutoUpdate = false;
  template.updateMatrixWorld(true);
  template.traverseVisible((object) => {
    if (!(object instanceof THREE.Mesh) || object.name === 'shrub_billboard' || isMobyMetalObject(object)) return;
    const mesh = new THREE.Mesh(object.geometry.clone().applyMatrix4(object.matrixWorld), material);
    mesh.raycast = () => {};
    root.add(mesh);
  });
  return root;
}

function disposeSelectionOutline(root: THREE.Group): void {
  root.removeFromParent();
  root.traverse((object) => {
    if (object instanceof THREE.Mesh) object.geometry.dispose();
  });
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

function sameIds(left: ReadonlySet<string>, right: ReadonlySet<string>): boolean {
  return left.size === right.size && [...left].every((id) => right.has(id));
}

function createCollisionBatch(
  entities: readonly EditorEntity[],
  templates: ReadonlyMap<string, THREE.Object3D>,
): CollisionBatch {
  interface Part { entityId: string; geometry: THREE.BufferGeometry }
  const buckets = new Map<THREE.Material, Map<string, Part[]>>();
  const entityIds = new Set(entities.map((entity) => entity.id));
  const entityTemplates = new Map<string, THREE.Object3D>();
  for (const entity of entities) {
    const template = templates.get(assetTemplateKey(entity)!);
    if (template) entityTemplates.set(entity.id, template);
    template?.traverseVisible((object) => {
      if (!(object instanceof THREE.Mesh) || Array.isArray(object.material)) return;
      const geometry = object.geometry.clone();
      geometry.deleteAttribute('skinIndex');
      geometry.deleteAttribute('skinWeight');
      geometry.morphAttributes = {};
      geometry.applyMatrix4(object.matrixWorld);
      const byLayout = buckets.get(object.material) ?? new Map<string, Part[]>();
      buckets.set(object.material, byLayout);
      const signature = geometrySignature(geometry);
      const parts = byLayout.get(signature) ?? [];
      parts.push({ entityId: entity.id, geometry });
      byLayout.set(signature, parts);
    });
  }

  const root = new THREE.Group();
  root.name = 'Forge collision batch';
  const meshes: THREE.BatchedMesh[] = [];
  const entries = new Map<string, CollisionBatchEntry[]>();
  for (const [material, byLayout] of buckets) {
    for (const parts of byLayout.values()) {
      const vertexCount = parts.reduce((total, part) => total + part.geometry.getAttribute('position').count, 0);
      const indexCount = parts.reduce((total, part) => total + (part.geometry.index?.count ?? 0), 0);
      const mesh = new THREE.BatchedMesh(parts.length, vertexCount, indexCount, material);
      const ids: string[] = [];
      mesh.frustumCulled = false;
      mesh.sortObjects = material.transparent;
      for (const part of parts) {
        const geometryId = mesh.addGeometry(part.geometry);
        const instanceId = mesh.addInstance(geometryId);
        ids[instanceId] = part.entityId;
        const entityEntries = entries.get(part.entityId) ?? [];
        entityEntries.push({ mesh, instanceId, geometryId });
        entries.set(part.entityId, entityEntries);
        part.geometry.dispose();
      }
      mesh.userData[INSTANCE_IDS_KEY] = ids;
      root.add(mesh);
      meshes.push(mesh);
    }
  }
  return {
    root, meshes, entityIds, entries, states: new Map(), templates: entityTemplates, matrices: new Map(),
  };
}

function disposeCollisionBatch(batch: CollisionBatch): void {
  batch.meshes.forEach((mesh) => mesh.dispose());
}

function createInstancedAsset(
  template: THREE.Object3D,
  capacity: number,
  hasNormal: boolean,
  hasMirrored: boolean,
  selectionColor: THREE.Color,
): InstancedAsset {
  const root = new THREE.Group();
  const meshes: THREE.InstancedMesh[] = [];
  const geometries = new Set<THREE.BufferGeometry>();
  const materials = new Set<THREE.Material>();
  const buckets = new Map<THREE.Material | THREE.Material[], Map<string, THREE.BufferGeometry[]>>();
  template.traverseVisible((object) => {
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
        addInstancedMesh(
          root, meshes, geometries, materials, geometry, material, capacity, hasNormal, hasMirrored, selectionColor,
        );
      } else {
        parts.forEach((part) => addInstancedMesh(
          root, meshes, geometries, materials, part, material, capacity, hasNormal, hasMirrored, selectionColor,
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
  selectionColor: THREE.Color,
): void {
  const renderMaterials: (THREE.Material | THREE.Material[])[] = [];
  const temporaryMaterials: THREE.Material[] = [];
  if (!Array.isArray(material)) {
    const opaqueMaterial = createPs2OpaquePassMaterial(material);
    if (opaqueMaterial) {
      renderMaterials.push(opaqueMaterial);
      temporaryMaterials.push(opaqueMaterial);
    }
  }
  renderMaterials.push(material);
  for (const sourceMaterial of renderMaterials) {
    const renderMaterial = Array.isArray(sourceMaterial)
      ? sourceMaterial.map((value) => createSelectionOverrideMaterial(value, selectionColor))
      : createSelectionOverrideMaterial(sourceMaterial, selectionColor);
    for (const owned of Array.isArray(renderMaterial) ? renderMaterial : [renderMaterial]) materials.add(owned);
    if (hasNormal) addMesh(root, meshes, geometry, renderMaterial, capacity, false);
    if (hasMirrored) addMesh(root, meshes, geometry, renderMaterial, capacity, true);
  }
  temporaryMaterials.forEach((value) => value.dispose());
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
