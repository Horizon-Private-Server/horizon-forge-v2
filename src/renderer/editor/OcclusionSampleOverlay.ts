import * as THREE from 'three';

import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import type { OcclusionSamplePreview } from '../../types/Occlusion.js';

export function occlusionSampleSceneKey(
  project: Pick<EditorSnapshot, 'projectId' | 'entities' | 'target' | 'customModels'>, renderRevision: number, samplePullIn = 0,
): string {
  return JSON.stringify([project.projectId, renderRevision, samplePullIn, project.target, project.customModels,
    project.entities.filter(e => e.asset && !e.collision && !e.skyShell)
      .map(e => [e.id, e.asset, e.transform, e.tfrag, e.sourceClassId, e.state.disabled, e.mobyProperties])]);
}

export class OcclusionSampleOverlay extends THREE.Group {
  constructor(preview: OcclusionSamplePreview) {
    super();
    this.name = 'Qualified occlusion samples';
    const positions: number[][] = [[], [], [], [], []], ids = positions.map(() => new Set<number>());
    const lines: number[] = [], inwardLines: number[] = [], boxes: number[] = [];
    const append = (out: number[], p: readonly number[]) => out.push(p[0], p[2], -p[1]);
    for (const cell of preview.cells) {
      if (cell.inwardCorners) {
        const corner = (i: number) => [cell.x * 4 + (i & 1) * 4, cell.y * 4 + ((i >> 1) & 1) * 4,
          cell.z * 4 + ((i >> 2) & 1) * 4];
        for (let i = 0; i < 8; i++) for (const axis of [1, 2, 4]) if (!(i & axis)) {
          append(boxes, corner(i)); append(boxes, corner(i | axis));
        }
      }
      cell.samples.forEach((id, slot) => {
        const point = preview.origins[id];
        const midpoint = slot >= 9, corner = midpoint ? slot - 9 : slot - 1;
        const original = slot === 0 ? [cell.x * 4 + 2, cell.y * 4 + 2, cell.z * 4 + 2]
          : [cell.x * 4 + (corner & 1) * 4, cell.y * 4 + ((corner >> 1) & 1) * 4, cell.z * 4 + (midpoint ? 2 : ((corner >> 2) & 1) * 4)];
        const moved = point.some((v, a) => v !== original[a]);
        const inward = slot > 0 && !midpoint && Boolean((cell.inwardCorners ?? 0) & (1 << corner));
        const kind = slot === 0 ? 0 : midpoint ? 3 : moved ? inward ? 4 : 2 : 1;
        if (!ids[kind].has(id)) { ids[kind].add(id); append(positions[kind], point); }
        if (moved) { const out = inward ? inwardLines : lines; append(out, original); append(out, point); }
      });
    }
    const colors = ['#ffffff', '#42cfff', '#ffb34d', '#d58bff', '#ff66b3'];
    positions.forEach((values, kind) => {
      if (!values.length) return;
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.Float32BufferAttribute(values, 3));
      geometry.computeBoundingSphere();
      const material = new THREE.PointsMaterial({ color: colors[kind], size: kind === 1 ? 0.1 : 0.17,
        depthTest: false, depthWrite: false, fog: false, toneMapped: false, transparent: true, alphaTest: 0.1,
        map: kind === 0 ? null : markerTexture(kind === 2, kind === 3, kind === 4) });
      const points = new THREE.Points(geometry, material);
      points.name = ['Sample centers', 'Unchanged corner samples', 'Relocated corner samples', 'Vertical-edge midpoint samples', 'Inward-qualified corner samples'][kind];
      points.renderOrder = 1001 + kind;
      this.add(points);
    });
    for (const [values, color, name] of [
      [lines, colors[2], 'Sample relocation paths'],
      [inwardLines, colors[4], 'Inward-qualified relocation paths'],
      [boxes, colors[4], 'Inward-qualified octants'],
    ] as const) {
      if (!values.length) continue;
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.Float32BufferAttribute(values, 3));
      geometry.computeBoundingSphere();
      const segments = new THREE.LineSegments(geometry, new THREE.LineBasicMaterial({ color,
        depthTest: false, depthWrite: false, fog: false, toneMapped: false, transparent: true, opacity: 0.45 }));
      segments.name = name; segments.renderOrder = 1000;
      this.add(segments);
    }
  }

  dispose(): void {
    this.traverse(object => {
      if (!(object instanceof THREE.Points || object instanceof THREE.LineSegments)) return;
      object.geometry.dispose();
      for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
        if (material instanceof THREE.PointsMaterial) material.map?.dispose();
        material.dispose();
      }
    });
    this.removeFromParent(); this.clear();
  }
}

function markerTexture(diamond: boolean, ring = false, cross = false): THREE.DataTexture {
  const size = 16, pixels = new Uint8Array(size * size * 4);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const dx = (x + 0.5) / size * 2 - 1, dy = (y + 0.5) / size * 2 - 1, offset = (y * size + x) * 4;
    const radius = dx * dx + dy * dy;
    const visible = cross ? Math.abs(dx) <= 0.25 || Math.abs(dy) <= 0.25
      : diamond ? Math.abs(dx) + Math.abs(dy) <= 1 : radius <= 1 && (!ring || radius >= 0.3);
    pixels.set([255, 255, 255, visible ? 255 : 0], offset);
  }
  const texture = new THREE.DataTexture(pixels, size, size);
  texture.needsUpdate = true;
  return texture;
}
