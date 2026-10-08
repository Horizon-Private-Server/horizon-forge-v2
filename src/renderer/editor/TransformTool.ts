import * as THREE from 'three';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import type { TransformControlsGizmo } from 'three/addons/controls/TransformControls.js';

import type { EditorEntity, EditorTransformUpdate, ProjectTransform, ProjectVector4 } from '../../types/EditorRuntime.js';
import type { EditorSnapSource, EditorSnapTarget } from '../../types/EditorViewport.js';
import { parseSplinePointId, transformSplinePoints } from '../../utils/SplinePoints.ts';
import { cloneProjectTransform, projectTransformToSceneMatrix, sceneMatrixToProjectTransform } from '../../utils/Transforms.ts';
import { VertexSnapIndex } from './SceneSnapping.ts';
import type { SceneProjection } from './SceneProjection.ts';

export type EditorTransformMode = 'select' | 'translate' | 'rotate' | 'scale';
export type EditorTransformSpace = 'world' | 'local';

const SCALE_PIXELS_PER_DOUBLING = 200;
const PLANE_HANDLE_OFFSET = 0.15;
const AXIS_PICKER_THICKNESS = 0.5;

interface TransformToolCallbacks {
  preview(entities?: readonly EditorEntity[]): void;
  commit(updates: EditorTransformUpdate[]): void;
  commitSplinePoints(entityId: string, points: ProjectVector4[]): void;
  collectSnapVertices(entityIds: readonly string[]): THREE.Vector3[];
  resolveSnapTarget(mode: Exclude<EditorSnapTarget, 'grid'>, entityIds: readonly string[]): THREE.Vector3 | undefined;
}

export class TransformTool {
  readonly controls: TransformControls;

  private readonly proxy = new THREE.Object3D();
  private readonly gizmo: TransformControlsGizmo;
  private readonly bounds = new THREE.Box3();
  private readonly entityBounds = new THREE.Box3();
  private readonly startPivot = new THREE.Matrix4();
  private readonly inversePivot = new THREE.Matrix4();
  private readonly delta = new THREE.Matrix4();
  private readonly source = new THREE.Matrix4();
  private readonly transformed = new THREE.Matrix4();
  private readonly pivotScale = new THREE.Vector3();
  private readonly pivotRotation = new THREE.Quaternion();
  private readonly pivotPosition = new THREE.Vector3();
  private readonly startPivotPosition = new THREE.Vector3();
  private readonly sourceOffset = new THREE.Vector3();
  private readonly snapQuery = new THREE.Vector3();
  private readonly translationDelta = new THREE.Vector3();
  private readonly cameraOffset = new THREE.Vector3();
  private readonly proxyWorldPosition = new THREE.Vector3();
  private readonly inverseProxyRotation = new THREE.Quaternion();
  private readonly planeSigns = new THREE.Vector3(1, 1, 1);
  private readonly scaleDragStart = new THREE.Vector3();
  private readonly camera: THREE.Camera;
  private readonly domElement: HTMLElement;
  private readonly callbacks: TransformToolCallbacks;
  private entities: readonly EditorEntity[] = [];
  private selection: readonly string[] = [];
  private activeIds: string[] = [];
  private activePointIds: string[] = [];
  private activeSpline?: EditorEntity;
  private originalSplinePoints?: ProjectVector4[];
  private originals = new Map<string, ProjectTransform>();
  private projection?: SceneProjection;
  private mode: EditorTransformMode = 'select';
  private space: EditorTransformSpace = 'world';
  private snapEnabled = false;
  private snapInverted = false;
  private translationSnap = 1;
  private rotationSnap = THREE.MathUtils.degToRad(15);
  private scaleSnap = 0.1;
  private snapSource: EditorSnapSource = 'center';
  private snapTarget: EditorSnapTarget = 'grid';
  private vertexIndex?: VertexSnapIndex;
  private entitiesById = new Map<string, EditorEntity>();
  private cancelled = false;
  private pointerActive = false;
  private scaleDragStartY?: number;
  private pointerY = 0;

  constructor(
    camera: THREE.Camera,
    domElement: HTMLElement,
    overlay: THREE.Scene,
    callbacks: TransformToolCallbacks,
  ) {
    this.camera = camera;
    this.domElement = domElement;
    this.callbacks = callbacks;
    this.controls = new TransformControls(camera, domElement);
    this.controls.setSize(0.8);
    const gizmo = this.controls.getHelper().children.find((child) =>
      (child as TransformControlsGizmo).isTransformControlsGizmo);
    if (!gizmo) throw new Error('Transform controls gizmo is unavailable.');
    this.gizmo = gizmo as TransformControlsGizmo;
    this.tightenAxisPickers();
    this.proxy.name = 'Transform pivot';
    overlay.add(this.proxy, this.controls.getHelper());
    domElement.addEventListener('pointerdown', this.handlePointerDownCapture, true);
    domElement.addEventListener('pointermove', this.handlePointerMoveCapture, true);
    domElement.addEventListener('pointerup', this.handlePointerEndCapture, true);
    domElement.addEventListener('pointercancel', this.handlePointerEndCapture, true);
    this.controls.addEventListener('mouseDown', this.handleMouseDown);
    this.controls.addEventListener('objectChange', this.applyScreenSpaceScale);
    this.controls.addEventListener('objectChange', this.handleObjectChange);
    this.controls.addEventListener('mouseUp', this.handleMouseUp);
  }

  get isInteracting(): boolean {
    return this.pointerActive || this.controls.dragging;
  }

  setEnabled(enabled: boolean): void {
    this.controls.enabled = enabled;
  }

  update(): void {
    this.updatePlaneHandleQuadrants();
  }

  setMode(mode: EditorTransformMode): void {
    this.mode = mode;
    if (mode === 'select') this.controls.detach();
    else this.controls.setMode(mode);
    this.updateActiveSelection();
    this.refreshAttachment();
  }

  setSpace(space: EditorTransformSpace): void {
    this.space = space;
    this.controls.setSpace(space);
    this.refreshAttachment();
  }

  setSnapping(
    enabled: boolean,
    translation: number,
    rotationDegrees: number,
    scale: number,
    source: EditorSnapSource,
    target: EditorSnapTarget,
  ): void {
    this.snapEnabled = enabled;
    this.translationSnap = translation;
    this.rotationSnap = THREE.MathUtils.degToRad(rotationDegrees);
    this.scaleSnap = scale;
    this.snapSource = source;
    this.snapTarget = target;
    this.applySnapping();
  }

  setSnapInverted(inverted: boolean): void {
    if (this.snapInverted === inverted) return;
    this.snapInverted = inverted;
    this.applySnapping();
  }

  sync(entities: readonly EditorEntity[], selection: readonly string[], projection: SceneProjection): void {
    this.entities = entities;
    this.entitiesById = new Map(entities.map((entity) => [entity.id, entity]));
    this.selection = selection;
    this.projection = projection;
    if (this.controls.dragging) return;
    this.updateActiveSelection();
    this.refreshAttachment();
  }

  private updateActiveSelection(): void {
    const { selection } = this;
    const points = selection.map((id) => ({ id, point: parseSplinePointId(id) }))
      .filter((value) => value.point !== undefined);
    const pointEntity = points.length ? this.entitiesById.get(points.at(-1)!.point!.entityId) : undefined;
    this.activeSpline = (pointEntity?.geometry?.kind === 'spline' || pointEntity?.geometry?.kind === 'grindPath')
      && !pointEntity.state.locked && !pointEntity.state.readOnly
      && !pointEntity.state.hidden && !pointEntity.state.disabled
      && (this.mode === 'select' || pointEntity.transformModes.includes(this.mode)) ? pointEntity : undefined;
    this.activePointIds = this.activeSpline
      ? points.filter((value) => value.point!.entityId === this.activeSpline!.id
        && value.point!.index < this.activeSpline!.geometry!.points.length).map((value) => value.id)
      : [];
    this.activeIds = this.activePointIds.length ? [] : selection.filter((id) => {
      const entity = this.entitiesById.get(id);
      return entity && !entity.state.locked && !entity.state.readOnly && !entity.state.hidden && !entity.state.disabled
        && (this.mode === 'select' || entity.transformModes.includes(this.mode));
    });
  }

  cancel(): boolean {
    if (!this.controls.dragging || this.originals.size === 0 && !this.originalSplinePoints) return false;
    this.cancelled = true;
    this.controls.reset();
    this.callbacks.preview();
    return true;
  }

  dispose(): void {
    this.domElement.removeEventListener('pointerdown', this.handlePointerDownCapture, true);
    this.domElement.removeEventListener('pointermove', this.handlePointerMoveCapture, true);
    this.domElement.removeEventListener('pointerup', this.handlePointerEndCapture, true);
    this.domElement.removeEventListener('pointercancel', this.handlePointerEndCapture, true);
    this.controls.removeEventListener('mouseDown', this.handleMouseDown);
    this.controls.removeEventListener('objectChange', this.applyScreenSpaceScale);
    this.controls.removeEventListener('objectChange', this.handleObjectChange);
    this.controls.removeEventListener('mouseUp', this.handleMouseUp);
    this.controls.detach();
    this.controls.getHelper().removeFromParent();
    this.proxy.removeFromParent();
    this.controls.dispose();
  }

  private readonly handlePointerDownCapture = (event: PointerEvent) => {
    this.updatePlaneHandleQuadrants();
    if (event.button !== 0 || this.mode !== 'scale') return;
    this.scaleDragStartY = event.clientY;
    this.pointerY = event.clientY;
    this.scaleDragStart.copy(this.proxy.scale);
  };

  private readonly handlePointerMoveCapture = (event: PointerEvent) => {
    this.pointerY = event.clientY;
    if (this.controls.dragging) this.setSnapInverted(event.ctrlKey);
    if (!this.controls.dragging) this.updatePlaneHandleQuadrants();
  };

  private readonly handlePointerEndCapture = () => {
    this.scaleDragStartY = undefined;
  };

  private readonly applyScreenSpaceScale = () => {
    if (this.mode !== 'scale' || this.scaleDragStartY === undefined || !this.controls.dragging) return;
    const axis = this.controls.axis;
    if (!axis) return;
    const deltaPixels = this.scaleDragStartY - this.pointerY;
    for (const key of ['x', 'y', 'z'] as const) {
      if (!axis.includes(key.toUpperCase())) continue;
      this.proxy.scale[key] = verticalDragScale(
        this.scaleDragStart[key],
        deltaPixels,
        this.controls.scaleSnap,
      );
    }
  };

  private readonly handleMouseDown = () => {
    this.pointerActive = true;
    this.cancelled = false;
    this.proxy.updateMatrix();
    this.startPivot.copy(this.proxy.matrix);
    this.startPivotPosition.setFromMatrixPosition(this.startPivot);
    this.sourceOffset.set(0, 0, 0);
    this.vertexIndex = undefined;
    if (this.snapSource === 'origin') {
      const active = this.activeSpline ?? this.entitiesById.get(this.activeIds.at(-1)!);
      if (active) {
        projectTransformToSceneMatrix(active.transform, this.source)
          .decompose(this.pivotPosition, this.pivotRotation, this.pivotScale);
        this.sourceOffset.copy(this.pivotPosition).sub(this.startPivotPosition);
      }
    } else if (this.snapSource === 'vertex') {
      this.vertexIndex = new VertexSnapIndex(this.callbacks.collectSnapVertices(
        this.activePointIds.length ? this.activePointIds : this.activeIds,
      ));
    }
    this.originals = new Map(this.entities.filter((entity) => this.activeIds.includes(entity.id))
      .map((entity) => [entity.id, cloneProjectTransform(entity.transform)]));
    this.originalSplinePoints = this.activeSpline?.geometry?.points.map((point) => ({ ...point }));
  };

  private readonly handleObjectChange = () => {
    if (this.originals.size === 0 && !this.originalSplinePoints || this.cancelled) return;
    this.applyTargetSnap();
    if (this.originalSplinePoints && this.activeSpline) {
      const points = this.buildSplinePoints();
      if (!points) return;
      this.callbacks.preview(this.entities.map((entity) => entity.id === this.activeSpline!.id
        ? { ...entity, geometry: { kind: this.activeSpline!.geometry!.kind, points } }
        : entity));
      return;
    }
    const updates = this.buildUpdates();
    const transforms = new Map(updates.map((update) => [update.entityId, update.transform]));
    const explicit = new Set(transforms.keys());
    for (const update of updates) {
      const entity = this.entitiesById.get(update.entityId);
      const parentId = entity?.collision?.attachment?.parentEntityId;
      if (!entity || !parentId) continue;
      transforms.delete(entity.id);
      if (explicit.has(parentId) || transforms.has(parentId)) continue;
      const parent = this.entitiesById.get(parentId);
      if (!parent) continue;
      transforms.set(parentId, {
        ...parent.transform,
        position: {
          x: parent.transform.position.x + update.transform.position.x - entity.transform.position.x,
          y: parent.transform.position.y + update.transform.position.y - entity.transform.position.y,
          z: parent.transform.position.z + update.transform.position.z - entity.transform.position.z,
        },
      });
    }
    this.callbacks.preview(this.entities.map((entity) => {
      const transform = transforms.get(entity.id);
      return transform ? { ...entity, transform } : entity;
    }));
  };

  private readonly handleMouseUp = () => {
    const updates = this.cancelled ? [] : this.buildUpdates();
    const splinePoints = this.cancelled ? undefined : this.buildSplinePoints();
    const changed = !this.proxy.matrix.equals(this.startPivot);
    this.originals.clear();
    this.vertexIndex = undefined;
    if (this.cancelled) this.callbacks.preview();
    else if (changed && splinePoints && this.activeSpline)
      this.callbacks.commitSplinePoints(this.activeSpline.id, splinePoints);
    else if (changed && updates.length) this.callbacks.commit(updates);
    this.originalSplinePoints = undefined;
    this.cancelled = false;
    queueMicrotask(() => { this.pointerActive = false; });
  };

  private buildUpdates(): EditorTransformUpdate[] {
    this.proxy.updateMatrix();
    this.inversePivot.copy(this.startPivot).invert();
    this.delta.copy(this.proxy.matrix).multiply(this.inversePivot);
    return [...this.originals].map(([entityId, original]) => {
      projectTransformToSceneMatrix(original, this.source);
      this.transformed.copy(this.delta).multiply(this.source);
      const transform = sceneMatrixToProjectTransform(this.transformed);
      clampScale(transform);
      return { entityId, transform };
    });
  }

  private buildSplinePoints(): ProjectVector4[] | undefined {
    if (!this.activeSpline || !this.originalSplinePoints) return undefined;
    this.proxy.updateMatrix();
    this.inversePivot.copy(this.startPivot).invert();
    this.delta.copy(this.proxy.matrix).multiply(this.inversePivot);
    const selected = new Set(this.activePointIds.map((id) => parseSplinePointId(id)!.index));
    return transformSplinePoints(this.originalSplinePoints, selected, this.activeSpline.transform, this.delta);
  }

  private refreshAttachment(): void {
    if (this.mode === 'select' || !this.projection
      || this.activeIds.length === 0 && this.activePointIds.length === 0 || this.controls.dragging) {
      if (!this.controls.dragging) this.controls.detach();
      return;
    }
    this.bounds.makeEmpty();
    for (const id of [...this.activeIds, ...this.activePointIds]) {
      const value = this.projection.getBounds(id, this.entityBounds);
      if (value) this.bounds.union(value);
    }
    if (this.bounds.isEmpty()) return;
    this.bounds.getCenter(this.proxy.position);
    this.proxy.scale.set(1, 1, 1);
    this.proxy.quaternion.identity();
    if (this.space === 'local') {
      const active = this.activeSpline ?? this.entities.find((entity) => entity.id === this.activeIds.at(-1));
      if (active) projectTransformToSceneMatrix(active.transform, this.source)
        .decompose(this.pivotPosition, this.proxy.quaternion, this.pivotScale);
    }
    this.proxy.updateMatrix();
    this.controls.attach(this.proxy);
    this.updatePlaneHandleQuadrants();
  }

  private tightenAxisPickers(): void {
    for (const mode of ['translate', 'scale'] as const) {
      for (const handle of this.gizmo.picker[mode].children) {
        if (!(handle instanceof THREE.Mesh) || !['X', 'Y', 'Z'].includes(handle.name)) continue;
        handle.geometry.scale(
          handle.name === 'X' ? 1 : AXIS_PICKER_THICKNESS,
          handle.name === 'Y' ? 1 : AXIS_PICKER_THICKNESS,
          handle.name === 'Z' ? 1 : AXIS_PICKER_THICKNESS,
        );
      }
    }
  }

  private updatePlaneHandleQuadrants(): void {
    if (!this.controls.object || this.controls.dragging) return;
    this.camera.getWorldPosition(this.cameraOffset);
    this.proxy.getWorldPosition(this.proxyWorldPosition);
    this.cameraOffset.sub(this.proxyWorldPosition);
    if (this.mode === 'scale' || this.space === 'local') {
      this.proxy.getWorldQuaternion(this.inverseProxyRotation).invert();
      this.cameraOffset.applyQuaternion(this.inverseProxyRotation);
    }
    const nextX = nonZeroSign(this.cameraOffset.x, this.planeSigns.x);
    const nextY = nonZeroSign(this.cameraOffset.y, this.planeSigns.y);
    const nextZ = nonZeroSign(this.cameraOffset.z, this.planeSigns.z);
    const dx = PLANE_HANDLE_OFFSET * (nextX - this.planeSigns.x);
    const dy = PLANE_HANDLE_OFFSET * (nextY - this.planeSigns.y);
    const dz = PLANE_HANDLE_OFFSET * (nextZ - this.planeSigns.z);
    if (dx === 0 && dy === 0 && dz === 0) return;
    for (const mode of ['translate', 'scale'] as const) {
      for (const group of [this.gizmo.gizmo[mode], this.gizmo.picker[mode]]) {
        for (const handle of group.children) {
          if (!(handle instanceof THREE.Mesh) || !['XY', 'YZ', 'XZ'].includes(handle.name)) continue;
          handle.geometry.translate(
            handle.name.includes('X') ? dx : 0,
            handle.name.includes('Y') ? dy : 0,
            handle.name.includes('Z') ? dz : 0,
          );
        }
      }
    }
    this.planeSigns.set(nextX, nextY, nextZ);
  }

  private applySnapping(): void {
    const enabled = this.snapEnabled !== this.snapInverted;
    this.controls.setTranslationSnap(enabled && this.snapTarget === 'grid' ? this.translationSnap : null);
    this.controls.setRotationSnap(enabled ? this.rotationSnap : null);
    this.controls.setScaleSnap(enabled ? this.scaleSnap : null);
  }

  private applyTargetSnap(): void {
    if (this.mode !== 'translate' || this.snapTarget === 'grid'
      || this.snapEnabled === this.snapInverted) return;
    const target = this.callbacks.resolveSnapTarget(
      this.snapTarget,
      this.activeSpline ? [this.activeSpline.id] : this.activeIds,
    );
    if (!target) return;
    if (this.snapSource === 'vertex') {
      this.translationDelta.copy(this.proxy.position).sub(this.startPivotPosition);
      const source = this.vertexIndex?.nearest(this.snapQuery.copy(target).sub(this.translationDelta));
      if (!source) return;
      this.sourceOffset.copy(source).sub(this.startPivotPosition);
    }
    this.proxy.position.copy(target).sub(this.sourceOffset);
    this.proxy.updateMatrix();
  }
}

export function verticalDragScale(startScale: number, deltaPixels: number, snap: number | null): number {
  const scale = startScale * 2 ** (deltaPixels / SCALE_PIXELS_PER_DOUBLING);
  return snap ? Math.round(scale / snap) * snap || snap : scale;
}

function nonZeroSign(value: number, fallback: number): number {
  return value === 0 ? fallback : Math.sign(value);
}

function clampScale(transform: ProjectTransform): void {
  for (const key of ['x', 'y', 'z'] as const) {
    const value = transform.scale[key];
    if (Math.abs(value) < 0.0001) transform.scale[key] = value < 0 ? -0.0001 : 0.0001;
  }
}
