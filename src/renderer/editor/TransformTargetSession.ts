import * as THREE from 'three';
import type { EditorTransformTarget } from '../../types/EditorViewport.js';
import type { ProjectTransform } from '../../types/EditorRuntime.js';
import { projectTransformToSceneMatrix, sceneMatrixToProjectTransform } from '../../utils/Transforms.ts';

export class TransformTargetSession {
  readonly proxy = new THREE.Object3D();
  target?: EditorTransformTarget;
  private readonly start = new THREE.Matrix4();
  private candidate?: ProjectTransform;
  private axis?: string | null;
  private active = false;
  private cancelled = false;

  get interacting(): boolean { return this.active; }

  sync(target?: EditorTransformTarget): boolean {
    const changed = this.target?.key !== target?.key;
    if (changed) this.cancel();
    this.target = target;
    if (target && !this.active) {
      projectTransformToSceneMatrix(target.transform).decompose(this.proxy.position, this.proxy.quaternion, this.proxy.scale);
      this.proxy.updateMatrix();
    }
    return changed;
  }

  begin(): void {
    if (!this.target) return;
    this.active = true; this.cancelled = false; this.candidate = undefined;
    this.proxy.updateMatrix(); this.start.copy(this.proxy.matrix);
  }

  update(axis?: string | null): void {
    if (!this.target || !this.active || this.cancelled) return;
    this.proxy.updateMatrix(); this.axis = axis;
    if (this.proxy.matrix.equals(this.start)) {
      this.candidate = undefined; this.target.preview(); return;
    }
    const transform = sceneMatrixToProjectTransform(this.proxy.matrix);
    this.candidate = this.target.preview(transform, axis) ? transform : undefined;
  }

  finish(): void {
    if (!this.active) return;
    const candidate = this.candidate;
    this.active = false; this.candidate = undefined;
    if (candidate && !this.cancelled) this.target?.commit(candidate, this.axis);
    else this.target?.preview();
    this.cancelled = false;
  }

  cancel(): boolean {
    if (!this.active) return false;
    this.cancelled = true; this.candidate = undefined;
    this.target?.preview();
    this.start.decompose(this.proxy.position, this.proxy.quaternion, this.proxy.scale);
    this.proxy.updateMatrix(); this.active = false;
    return true;
  }
}
