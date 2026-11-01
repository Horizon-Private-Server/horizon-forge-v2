import * as THREE from 'three';

import type { LevelSettingsOverlayProjection } from './LevelSettingsOverlayProjection.ts';
import type { OcclusionCoverageOverlay } from './OcclusionCoverageOverlay.ts';
import type { SceneProjection } from './SceneProjection.ts';

type ViewportBounds = Pick<DOMRect, 'left' | 'top' | 'width' | 'height'>;

export class ViewportPicking {
  private readonly raycaster = new THREE.Raycaster();
  private readonly pointer = new THREE.Vector2();
  private readonly hoverPosition = new THREE.Vector2();
  private hoverPending = false;
  private readonly camera: THREE.Camera;
  private readonly projection: SceneProjection;
  private readonly overlays: LevelSettingsOverlayProjection;
  private readonly simulationRoot: THREE.Object3D;
  private readonly coverage?: OcclusionCoverageOverlay;

  constructor(
    camera: THREE.Camera,
    projection: SceneProjection,
    overlays: LevelSettingsOverlayProjection,
    simulationRoot: THREE.Object3D,
    coverage?: OcclusionCoverageOverlay,
  ) {
    this.camera = camera;
    this.projection = projection;
    this.overlays = overlays;
    this.simulationRoot = simulationRoot;
    this.coverage = coverage;
    this.raycaster.params.Line.threshold = 8;
  }

  get hasPendingHover(): boolean {
    return this.hoverPending;
  }

  queueHover(clientX: number, clientY: number): void {
    this.hoverPosition.set(clientX, clientY);
    this.hoverPending = true;
  }

  clearHover(): void {
    this.hoverPending = false;
    this.overlays.setHovered();
    this.projection.setHoveredEntityId();
  }

  flushHover(bounds: ViewportBounds): void {
    if (!this.hoverPending) return;
    this.hoverPending = false;
    this.setRay(this.hoverPosition.x, this.hoverPosition.y, bounds);
    if (this.coverage?.pickMarker(this.pointer, this.camera, bounds.width, bounds.height) || this.coverage?.pick(this.raycaster)) {
      this.overlays.setHovered(); this.projection.setHoveredEntityId(); return;
    }
    const markerId = this.pickOverlay();
    this.overlays.setHovered(markerId);
    this.projection.setHoveredEntityId(markerId ? undefined
      : this.projection.pickHoveredEntity(this.raycaster, [this.simulationRoot]));
  }

  pick(clientX: number, clientY: number, bounds: ViewportBounds) {
    this.clearHover();
    this.setRay(clientX, clientY, bounds);
    const coverageMarker = this.coverage?.pickMarker(this.pointer, this.camera, bounds.width, bounds.height);
    const coverageId = coverageMarker?.sourceId ?? this.coverage?.pick(this.raycaster);
    const markerId = coverageId ? undefined : this.pickOverlay();
    const entityId = coverageId || markerId ? undefined : this.projection.resolvePick(
      this.raycaster.intersectObjects([this.simulationRoot, this.projection.root], true),
    );
    return { markerId, entityId, ...(coverageId ? { coverageId } : {}), ...(coverageMarker ? { coverageMarkerId: coverageMarker.markerId } : {}) };
  }

  private pickOverlay(): string | undefined {
    return this.overlays.resolvePick(this.raycaster.intersectObject(this.overlays.root, true));
  }

  private setRay(clientX: number, clientY: number, bounds: ViewportBounds): void {
    this.pointer.set(
      (clientX - bounds.left) / Math.max(bounds.width, 1) * 2 - 1,
      -(clientY - bounds.top) / Math.max(bounds.height, 1) * 2 + 1,
    );
    this.camera.updateWorldMatrix(true, false);
    this.projection.root.updateWorldMatrix(true, true);
    this.overlays.root.updateWorldMatrix(true, true);
    this.simulationRoot.updateWorldMatrix(true, true);
    this.raycaster.setFromCamera(this.pointer, this.camera);
  }
}
