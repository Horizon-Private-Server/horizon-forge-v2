import * as THREE from 'three';

import type {
  EditorEntity, EditorLevelSceneSettings, EditorTransformUpdate,
} from '../../types/EditorRuntime.js';
import { ps2PositionToScene } from '../../utils/Scene.ts';
import { SceneBillboardGizmo, SceneBillboardResources } from './SceneBillboardGizmo.ts';

export type LevelSettingsPositionField = 'shipPosition' | 'sphereCenter';

const MARKERS = {
  shipPosition: {
    id: 'level-settings:ship-position', label: 'Ship position', color: 0x4dabf7, glyph: 'airplane-tilt',
  },
  sphereCenter: {
    id: 'level-settings:sphere-center', label: 'Spherical world center', color: 0x63e6be, glyph: 'sphere',
  },
} as const;
const MARKER_FIELDS: ReadonlyMap<string, LevelSettingsPositionField> = new Map(
  Object.entries(MARKERS).map(([field, value]) =>
    [value.id, field as LevelSettingsPositionField]));
const DEATH_PLANE_FALLBACK_SIZE = 1_024;
const DEATH_PLANE_MIN_SIZE = 256;
const DEATH_PLANE_MAX_SIZE = 100_000;
const DEATH_PLANE_PADDING = 1.1;
const DEATH_PATTERN_SIZE = 128;
const DEATH_PATTERN_STRIPE_PERIOD = 32;
const DEATH_PATTERN_WORLD_SIZE = 128;

export class LevelSettingsOverlayProjection {
  readonly root = new THREE.Group();

  private readonly resources = new SceneBillboardResources();
  private readonly deathPlaneTexture = createDeathPlaneTexture();
  private readonly deathPlaneMaterial = new THREE.MeshBasicMaterial({
    alphaTest: 0.02,
    depthWrite: false,
    fog: false,
    map: this.deathPlaneTexture,
    side: THREE.DoubleSide,
    forceSinglePass: true,
    transparent: true,
    toneMapped: false,
  });
  private readonly deathPlane = new THREE.Mesh(
    new THREE.PlaneGeometry(1, 1),
    this.deathPlaneMaterial,
  );
  private readonly sceneBounds = new THREE.Box3();
  private readonly sceneCenter = new THREE.Vector3();
  private readonly sceneSize = new THREE.Vector3();
  private readonly markers = new Map<string, SceneBillboardGizmo>();
  private settings?: EditorLevelSceneSettings;
  private selectedId?: string;
  private hoveredId?: string;
  private deathPlaneVisible = true;

  constructor() {
    this.root.name = 'Level settings overlays';
    this.deathPlane.name = 'Death height';
    this.deathPlane.rotation.x = -Math.PI / 2;
    this.deathPlane.renderOrder = -1;
    this.deathPlane.userData.forgeAccessibleLabel = 'Death height';
    this.deathPlane.userData.forgeDeathHeightPlane = true;
    this.updateDeathPlane();
  }

  sync(settings?: EditorLevelSceneSettings, selectedId = this.selectedId): void {
    this.settings = settings;
    this.selectedId = selectedId;
    if (!settings) {
      this.clearMarkers();
      this.deathPlane.removeFromParent();
      return;
    }
    if (!this.deathPlane.parent) this.root.add(this.deathPlane);
    this.updateDeathPlane();
    const visible = new Set<LevelSettingsPositionField>(['shipPosition']);
    if (settings.isSphericalWorld) visible.add('sphereCenter');
    for (const [field, descriptor] of Object.entries(MARKERS) as Array<
      [LevelSettingsPositionField, typeof MARKERS[LevelSettingsPositionField]]
    >) {
      if (!visible.has(field)) {
        this.removeMarker(descriptor.id);
        continue;
      }
      const options = {
        entityId: descriptor.id,
        label: descriptor.label,
        worldPosition: ps2PositionToScene(settings[field]),
        color: descriptor.color,
        glyph: descriptor.glyph,
        depthPolicy: 'occluded' as const,
        selected: selectedId === descriptor.id,
        hovered: this.hoveredId === descriptor.id,
        worldSize: 40,
        minScreenSize: 22,
        maxScreenSize: 52,
      };
      const marker = this.markers.get(descriptor.id);
      if (marker) marker.configure(options);
      else {
        const created = this.resources.create(options);
        this.markers.set(descriptor.id, created);
        this.root.add(created);
      }
    }
    if (selectedId && !this.markers.has(selectedId)) this.selectedId = undefined;
    if (this.hoveredId && !this.markers.has(this.hoveredId)) this.hoveredId = undefined;
  }

  setSceneBounds(bounds?: THREE.Box3): void {
    if (!bounds || bounds.isEmpty() || !finiteBox(bounds)) this.sceneBounds.makeEmpty();
    else this.sceneBounds.copy(bounds);
    this.updateDeathPlane();
  }

  setDeathPlaneVisible(visible: boolean): void {
    this.deathPlaneVisible = visible;
    this.updateDeathPlane();
  }

  getTransformEntities(): EditorEntity[] {
    if (!this.settings) return [];
    return [...this.markers.keys()].map((id) => {
      const field = levelSettingsPositionField(id)!;
      return {
        id,
        name: MARKERS[field].label,
        layer: 'level-settings',
        transform: {
          position: { ...this.settings![field] },
          rotation: { x: 0, y: 0, z: 0, w: 1 },
          scale: { x: 1, y: 1, z: 1 },
        },
        transformModes: ['translate'],
        state: {
          dirty: false, hidden: false, disabled: false, locked: false,
          readOnly: false, invalid: false, missingAsset: false,
        },
      };
    });
  }

  previewTransforms(updates?: readonly EditorTransformUpdate[]): void {
    if (!updates) {
      this.sync(this.settings);
      return;
    }
    for (const update of updates) {
      const marker = this.markers.get(update.entityId);
      if (marker) marker.position.copy(ps2PositionToScene(update.transform.position));
    }
  }

  setSelected(id?: string): void {
    this.selectedId = id && this.markers.has(id) ? id : undefined;
    this.markers.forEach((marker, markerId) => marker.setState(
      markerId === this.selectedId,
      markerId === this.hoveredId,
    ));
  }

  setHovered(id?: string): void {
    this.hoveredId = id && this.markers.has(id) ? id : undefined;
    this.markers.forEach((marker, markerId) => marker.setState(
      markerId === this.selectedId,
      markerId === this.hoveredId,
    ));
  }

  resolvePick(intersections: readonly THREE.Intersection[]): string | undefined {
    for (const intersection of intersections) {
      for (let object: THREE.Object3D | null = intersection.object; object; object = object.parent) {
        if (object === this.root) break;
        if (this.markers.has(object.userData.forgeEntityId as string))
          return object.userData.forgeEntityId as string;
      }
    }
    return undefined;
  }

  getBounds(id: string, target = new THREE.Box3()): THREE.Box3 | undefined {
    const marker = this.markers.get(id);
    return marker ? target.set(marker.position, marker.position) : undefined;
  }

  updateForCamera(camera: THREE.Camera, viewportHeight: number): void {
    this.markers.forEach((marker) => marker.updateForCamera(camera, viewportHeight));
  }

  getResourceCounts() {
    return this.resources.counts();
  }

  dispose(): void {
    this.clearMarkers();
    this.deathPlane.removeFromParent();
    this.deathPlane.geometry.dispose();
    this.deathPlaneMaterial.dispose();
    this.deathPlaneTexture.dispose();
    this.root.removeFromParent();
    this.root.clear();
    this.resources.dispose();
  }

  private updateDeathPlane(): void {
    this.deathPlane.visible = Boolean(this.settings) && this.deathPlaneVisible;
    const hasBounds = !this.sceneBounds.isEmpty();
    const center = hasBounds
      ? this.sceneBounds.getCenter(this.sceneCenter)
      : this.sceneCenter.set(0, 0, 0);
    const size = hasBounds
      ? this.sceneBounds.getSize(this.sceneSize)
      : this.sceneSize.set(DEATH_PLANE_FALLBACK_SIZE, 0, DEATH_PLANE_FALLBACK_SIZE);
    const width = planeSize(size.x);
    const depth = planeSize(size.z);
    this.deathPlane.position.set(center.x, this.settings?.deathHeight ?? 0, center.z);
    this.deathPlane.scale.set(width, depth, 1);
    this.deathPlaneTexture.repeat.set(
      Math.max(1, width / DEATH_PATTERN_WORLD_SIZE),
      Math.max(1, depth / DEATH_PATTERN_WORLD_SIZE),
    );
  }

  private removeMarker(id: string): void {
    this.markers.get(id)?.dispose();
    this.markers.delete(id);
  }

  private clearMarkers(): void {
    this.markers.forEach((marker) => marker.dispose());
    this.markers.clear();
    this.selectedId = undefined;
    this.hoveredId = undefined;
  }
}

export function levelSettingsPositionField(id: string): LevelSettingsPositionField | undefined {
  return MARKER_FIELDS.get(id);
}

function planeSize(value: number): number {
  return THREE.MathUtils.clamp(
    Math.max(value * DEATH_PLANE_PADDING, DEATH_PLANE_MIN_SIZE),
    DEATH_PLANE_MIN_SIZE,
    DEATH_PLANE_MAX_SIZE,
  );
}

function finiteBox(bounds: THREE.Box3): boolean {
  return [bounds.min.x, bounds.min.y, bounds.min.z, bounds.max.x, bounds.max.y, bounds.max.z]
    .every(Number.isFinite);
}

function createDeathPlaneTexture(): THREE.DataTexture {
  const data = new Uint8Array(DEATH_PATTERN_SIZE * DEATH_PATTERN_SIZE * 4);
  for (let y = 0; y < DEATH_PATTERN_SIZE; y += 1) {
    for (let x = 0; x < DEATH_PATTERN_SIZE; x += 1) {
      const offset = (y * DEATH_PATTERN_SIZE + x) * 4;
      const stripe = (x + y) % DEATH_PATTERN_STRIPE_PERIOD < 10;
      const skull = skullPixel(
        (x - DEATH_PATTERN_SIZE / 2) / 1.5,
        (y - DEATH_PATTERN_SIZE / 2) / 1.5,
      );
      data[offset] = 255;
      data[offset + 1] = skull ? 210 : 36;
      data[offset + 2] = skull ? 210 : 48;
      data[offset + 3] = skull ? 210 : stripe ? 72 : 0;
    }
  }
  const texture = new THREE.DataTexture(
    data,
    DEATH_PATTERN_SIZE,
    DEATH_PATTERN_SIZE,
    THREE.RGBAFormat,
  );
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.magFilter = THREE.LinearFilter;
  texture.minFilter = THREE.LinearMipmapLinearFilter;
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.anisotropy = 4;
  texture.generateMipmaps = true;
  texture.needsUpdate = true;
  return texture;
}

function skullPixel(x: number, y: number): boolean {
  const cranium = x * x / 110 + (y + 5) * (y + 5) / 85 <= 1;
  const jaw = Math.abs(x) <= 7 && y >= 2 && y <= 12;
  const leftEye = (x + 5) * (x + 5) + (y + 5) * (y + 5) <= 7;
  const rightEye = (x - 5) * (x - 5) + (y + 5) * (y + 5) <= 7;
  const nose = Math.abs(x) <= 1 && y >= -1 && y <= 4;
  const teeth = jaw && y >= 7 && (Math.abs(x + 4) <= 1 || Math.abs(x) <= 1 || Math.abs(x - 4) <= 1);
  return (cranium || jaw) && !leftEye && !rightEye && !nose && !teeth;
}
