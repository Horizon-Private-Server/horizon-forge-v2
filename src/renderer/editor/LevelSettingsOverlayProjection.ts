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

export class LevelSettingsOverlayProjection {
  readonly root = new THREE.Group();

  private readonly resources = new SceneBillboardResources();
  private readonly markers = new Map<string, SceneBillboardGizmo>();
  private settings?: EditorLevelSceneSettings;
  private selectedId?: string;
  private hoveredId?: string;

  constructor() {
    this.root.name = 'Level settings overlays';
  }

  sync(settings?: EditorLevelSceneSettings, selectedId = this.selectedId): void {
    this.settings = settings;
    this.selectedId = selectedId;
    if (!settings) {
      this.clearMarkers();
      return;
    }
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
    this.root.removeFromParent();
    this.root.clear();
    this.resources.dispose();
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
