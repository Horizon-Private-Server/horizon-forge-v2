import * as THREE from 'three';
import { TransformControls } from 'three/addons/controls/TransformControls.js';

import type { EditorEntity, EditorTransformUpdate, ProjectTransform } from '../../types/EditorRuntime.js';
import { cloneProjectTransform, projectTransformToSceneMatrix, sceneMatrixToProjectTransform } from '../../utils/Transforms.ts';
import type { SceneProjection } from './SceneProjection.ts';

export type EditorTransformMode = 'select' | 'translate' | 'rotate' | 'scale';
export type EditorTransformSpace = 'world' | 'local';

interface TransformToolCallbacks {
  preview(entities?: readonly EditorEntity[]): void;
  commit(updates: EditorTransformUpdate[]): void;
}

export class TransformTool {
  readonly controls: TransformControls;

  private readonly proxy = new THREE.Object3D();
  private readonly bounds = new THREE.Box3();
  private readonly entityBounds = new THREE.Box3();
  private readonly startPivot = new THREE.Matrix4();
  private readonly inversePivot = new THREE.Matrix4();
  private readonly delta = new THREE.Matrix4();
  private readonly source = new THREE.Matrix4();
  private readonly transformed = new THREE.Matrix4();
  private readonly pivotScale = new THREE.Vector3();
  private readonly pivotPosition = new THREE.Vector3();
  private entities: readonly EditorEntity[] = [];
  private activeIds: string[] = [];
  private originals = new Map<string, ProjectTransform>();
  private projection?: SceneProjection;
  private mode: EditorTransformMode = 'select';
  private space: EditorTransformSpace = 'world';
  private cancelled = false;
  private pointerActive = false;

  constructor(
    camera: THREE.Camera,
    domElement: HTMLElement,
    overlay: THREE.Scene,
    private readonly callbacks: TransformToolCallbacks,
  ) {
    this.controls = new TransformControls(camera, domElement);
    this.controls.setSize(0.8);
    this.proxy.name = 'Transform pivot';
    overlay.add(this.proxy, this.controls.getHelper());
    this.controls.addEventListener('mouseDown', this.handleMouseDown);
    this.controls.addEventListener('objectChange', this.handleObjectChange);
    this.controls.addEventListener('mouseUp', this.handleMouseUp);
  }

  get isInteracting(): boolean {
    return this.pointerActive || this.controls.dragging;
  }

  setEnabled(enabled: boolean): void {
    this.controls.enabled = enabled;
  }

  setMode(mode: EditorTransformMode): void {
    this.mode = mode;
    if (mode === 'select') this.controls.detach();
    else this.controls.setMode(mode);
    this.refreshAttachment();
  }

  setSpace(space: EditorTransformSpace): void {
    this.space = space;
    this.controls.setSpace(space);
    this.refreshAttachment();
  }

  sync(entities: readonly EditorEntity[], selection: readonly string[], projection: SceneProjection): void {
    this.entities = entities;
    this.projection = projection;
    if (this.controls.dragging) return;
    const byId = new Map(entities.map((entity) => [entity.id, entity]));
    this.activeIds = selection.filter((id) => {
      const entity = byId.get(id);
      return entity && !entity.state.locked && !entity.state.hidden && !entity.state.disabled;
    });
    this.refreshAttachment();
  }

  cancel(): boolean {
    if (!this.controls.dragging || this.originals.size === 0) return false;
    this.cancelled = true;
    this.controls.reset();
    this.callbacks.preview();
    return true;
  }

  dispose(): void {
    this.controls.removeEventListener('mouseDown', this.handleMouseDown);
    this.controls.removeEventListener('objectChange', this.handleObjectChange);
    this.controls.removeEventListener('mouseUp', this.handleMouseUp);
    this.controls.detach();
    this.controls.getHelper().removeFromParent();
    this.proxy.removeFromParent();
    this.controls.dispose();
  }

  private readonly handleMouseDown = () => {
    this.pointerActive = true;
    this.cancelled = false;
    this.proxy.updateMatrix();
    this.startPivot.copy(this.proxy.matrix);
    this.originals = new Map(this.entities.filter((entity) => this.activeIds.includes(entity.id))
      .map((entity) => [entity.id, cloneProjectTransform(entity.transform)]));
  };

  private readonly handleObjectChange = () => {
    if (this.originals.size === 0 || this.cancelled) return;
    const updates = this.buildUpdates();
    const transforms = new Map(updates.map((update) => [update.entityId, update.transform]));
    this.callbacks.preview(this.entities.map((entity) => {
      const transform = transforms.get(entity.id);
      return transform ? { ...entity, transform } : entity;
    }));
  };

  private readonly handleMouseUp = () => {
    const updates = this.cancelled ? [] : this.buildUpdates();
    const changed = !this.proxy.matrix.equals(this.startPivot);
    this.originals.clear();
    if (this.cancelled) this.callbacks.preview();
    else if (changed && updates.length) this.callbacks.commit(updates);
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

  private refreshAttachment(): void {
    if (this.mode === 'select' || !this.projection || this.activeIds.length === 0 || this.controls.dragging) {
      if (!this.controls.dragging) this.controls.detach();
      return;
    }
    this.bounds.makeEmpty();
    for (const id of this.activeIds) {
      const value = this.projection.getBounds(id, this.entityBounds);
      if (value) this.bounds.union(value);
    }
    if (this.bounds.isEmpty()) return;
    this.bounds.getCenter(this.proxy.position);
    this.proxy.scale.set(1, 1, 1);
    this.proxy.quaternion.identity();
    if (this.space === 'local') {
      const active = this.entities.find((entity) => entity.id === this.activeIds.at(-1));
      if (active) projectTransformToSceneMatrix(active.transform, this.source)
        .decompose(this.pivotPosition, this.proxy.quaternion, this.pivotScale);
    }
    this.proxy.updateMatrix();
    this.controls.attach(this.proxy);
  }
}

function clampScale(transform: ProjectTransform): void {
  for (const key of ['x', 'y', 'z'] as const) {
    const value = transform.scale[key];
    if (Math.abs(value) < 0.0001) transform.scale[key] = value < 0 ? -0.0001 : 0.0001;
  }
}
