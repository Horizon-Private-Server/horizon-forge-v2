import * as THREE from 'three';

import type { EditorTerrainSource } from '../../types/ForgeApi.js';

export class OcclusionOctantLabels {
  readonly element: HTMLDivElement;
  visible = false;
  frozenOctantId?: number;
  private octants: { id: number; position: THREE.Vector3; text: string }[] = [];
  private labels: HTMLDivElement[] = [];
  private eye = new THREE.Vector3();
  private projected = new THREE.Vector3();

  constructor(parent: HTMLElement) {
    this.element = parent.ownerDocument.createElement('div');
    this.element.className = 'occlusion-octant-labels';
    this.element.setAttribute('aria-hidden', 'true');
    this.element.hidden = true;
    parent.append(this.element);
  }

  setOctants(octants: EditorTerrainSource['occlusionOctants']): void {
    this.octants = octants.map((c, id) => ({ id,
      position: new THREE.Vector3(c.x * 4 + 2, c.z * 4 + 2, -(c.y * 4 + 2)),
      text: `#${id} (${c.x}, ${c.y}, ${c.z})`,
    }));
  }

  update(camera: THREE.PerspectiveCamera, width: number, height: number): void {
    this.element.hidden = !this.visible || !this.octants.length;
    if (this.element.hidden) return;
    camera.updateMatrixWorld();
    camera.getWorldPosition(this.eye);
    const nearby: { octant: OcclusionOctantLabels['octants'][number]; distance: number; x: number; y: number }[] = [];
    for (const octant of this.octants) {
      const distance = this.eye.distanceToSquared(octant.position);
      if (octant.id !== this.frozenOctantId && distance > 24 * 24) continue;
      this.projected.copy(octant.position).applyMatrix4(camera.matrixWorldInverse);
      const depth = -this.projected.z;
      if (depth < camera.near || depth > camera.far) continue;
      this.projected.applyMatrix4(camera.projectionMatrix);
      if (Math.abs(this.projected.x) > 1 || Math.abs(this.projected.y) > 1 || Math.abs(this.projected.z) > 1) continue;
      const x = (this.projected.x + 1) * width / 2, y = (1 - this.projected.y) * height / 2;
      const belowDot = Math.max(8, 0.17 * height / (4 * depth) + 4);
      nearby.push({ octant, distance, x, y: y + belowDot });
    }
    nearby.sort((a, b) => Number(b.octant.id === this.frozenOctantId) - Number(a.octant.id === this.frozenOctantId)
      || a.distance - b.distance || a.octant.id - b.octant.id);
    // Bound DOM work independently of the level's total octant count.
    const count = Math.min(nearby.length, 64);
    for (let i = 0; i < count; i++) {
      let label = this.labels[i];
      if (!label) {
        label = this.element.ownerDocument.createElement('div');
        label.className = 'occlusion-octant-label';
        label.style.zIndex = String(64 - i);
        this.labels.push(label); this.element.append(label);
      }
      const { octant, x, y } = nearby[i];
      if (label.textContent !== octant.text) {
        label.textContent = octant.text;
        label.dataset.octantId = String(octant.id);
      }
      label.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px) translateX(-50%)`;
      label.hidden = false;
    }
    for (let i = count; i < this.labels.length; i++) this.labels[i].hidden = true;
  }

  dispose(): void {
    this.element.remove(); this.labels = []; this.octants = [];
  }
}
