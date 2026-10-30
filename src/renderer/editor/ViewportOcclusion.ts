import type * as THREE from 'three';

import type { OcclusionVisibilityData, OcclusionViewportState } from '../../types/Occlusion.js';

export class ViewportOcclusion {
  readonly hidden = new Set<string>();
  state: OcclusionViewportState = { hiddenEntities: 0 };
  private data?: OcclusionVisibilityData;
  private octants = new Map<string, OcclusionVisibilityData['octants'][number] & { id: number }>();
  private lastKey: string | null | undefined;
  private heldKey: string | null = null;
  private frozen = false;
  private dirty = true;
  private simulationOverrides = new Map<THREE.Object3D, boolean>();

  setData(data?: OcclusionVisibilityData): void {
    this.data = data;
    this.octants = new Map(data?.octants.map((c, id) => [[c.x, c.y, c.z].join(','), { ...c, id }]));
    this.dirty = true;
  }

  reset(): void {
    this.restoreSimulation(); this.setData(); this.frozen = false; this.heldKey = null; this.lastKey = undefined;
  }

  update(position: { x: number; y: number; z: number }, enabled: boolean, freeze: boolean): boolean {
    // Scene axes are X, Z, -Y in native game space; cells occupy [coordinate*4, coordinate*4+4).
    const cameraKey = [Math.floor(position.x / 4), Math.floor(-position.z / 4), Math.floor(position.y / 4)].join(',');
    if (freeze && !this.frozen) this.heldKey = this.lastKey === undefined
      ? this.octants.has(cameraKey) ? cameraKey : null : this.lastKey;
    this.frozen = freeze;
    const requested = freeze ? this.heldKey : cameraKey;
    const key = enabled && requested && this.octants.has(requested) ? requested : null;
    if (!this.dirty && key === this.lastKey) return false;
    this.dirty = false; this.lastKey = key; this.hidden.clear();
    const octant = key ? this.octants.get(key) : undefined;
    if (octant && this.data) {
      const offset = octant.maskIndex * 128;
      for (const e of this.data.entities)
        if (!e.groups.some(g => (this.data!.masks[offset + (g >> 3)] & (1 << (g & 7))) !== 0)) this.hidden.add(e.id);
    }
    this.state = { octant, hiddenEntities: this.hidden.size };
    return true;
  }

  restoreSimulation(): void {
    for (const [object, visible] of this.simulationOverrides) object.visible = visible;
    this.simulationOverrides.clear();
  }

  applySimulation(objects: ReadonlyMap<THREE.Object3D, string>): void {
    for (const [object, id] of objects) if (this.hidden.has(id)) {
      this.simulationOverrides.set(object, object.visible); object.visible = false;
    }
  }
}
