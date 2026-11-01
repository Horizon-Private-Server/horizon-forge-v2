import * as THREE from 'three';
import type { OcclusionCoverageCell } from '../../types/OcclusionCoverage.js';

export class OcclusionOctantOverlay extends THREE.Group {
  private readonly fill: THREE.InstancedMesh<THREE.BoxGeometry, THREE.MeshBasicMaterial>;
  private readonly edges: THREE.LineSegments<THREE.EdgesGeometry, THREE.LineBasicMaterial>;

  constructor(octants: readonly OcclusionCoverageCell[], color: string, cellSize = 4) {
    super();
    this.name = 'Occlusion octants';
    const geometry = new THREE.BoxGeometry(cellSize, cellSize, cellSize);
    this.fill = new THREE.InstancedMesh(geometry, new THREE.MeshBasicMaterial({
      color, depthWrite: false, fog: false, opacity: 0.08, transparent: true,
    }), octants.length);
    const edgeGeometry = new THREE.EdgesGeometry(geometry);
    const edgeVertices = edgeGeometry.getAttribute('position');
    const edgePositions = new Float32Array(octants.length * edgeVertices.count * 3);
    const matrix = new THREE.Matrix4();
    octants.forEach((octant, index) => {
      const x = (octant.x + .5) * cellSize;
      const y = (octant.z + .5) * cellSize;
      const z = -(octant.y + .5) * cellSize;
      this.fill.setMatrixAt(index, matrix.makeTranslation(x, y, z));
      for (let vertex = 0; vertex < edgeVertices.count; vertex++) {
        const offset = (index * edgeVertices.count + vertex) * 3;
        edgePositions[offset] = edgeVertices.getX(vertex) + x;
        edgePositions[offset + 1] = edgeVertices.getY(vertex) + y;
        edgePositions[offset + 2] = edgeVertices.getZ(vertex) + z;
      }
    });
    this.fill.instanceMatrix.needsUpdate = true;
    this.fill.computeBoundingSphere();
    edgeGeometry.setAttribute('position', new THREE.BufferAttribute(edgePositions, 3));
    edgeGeometry.computeBoundingSphere();
    this.edges = new THREE.LineSegments(edgeGeometry, new THREE.LineBasicMaterial({
      color, fog: false, opacity: 0.55, transparent: true,
    }));
    this.fill.raycast = this.edges.raycast = () => {};
    this.add(this.fill, this.edges);
  }

  setColor(color: string): void {
    this.fill.material.color.set(color);
    this.edges.material.color.set(color);
  }

  dispose(): void {
    this.fill.geometry.dispose(); this.fill.material.dispose(); this.fill.dispose();
    this.edges.geometry.dispose(); this.edges.material.dispose();
    this.removeFromParent(); this.clear();
  }
}
