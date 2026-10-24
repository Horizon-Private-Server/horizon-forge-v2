import * as THREE from 'three';

type Source = THREE.Mesh<THREE.BufferGeometry, THREE.MeshBasicMaterial>;

interface Entry {
  mesh: THREE.InstancedMesh;
  index: number;
  matrix: THREE.Matrix4;
}

export class SceneGeometryBatch {
  readonly root = new THREE.Group();
  private readonly materials = new Map<THREE.MeshBasicMaterial, THREE.MeshBasicMaterial>();
  private readonly sources = new Map<THREE.MeshBasicMaterial, THREE.MeshBasicMaterial>();
  private readonly meshes = new Map<string, THREE.InstancedMesh>();
  private entries = new Map<Source, Entry>();

  constructor(materials: readonly THREE.MeshBasicMaterial[]) {
    this.root.name = 'Forge editor geometry batch';
    this.root.matrixAutoUpdate = false;
    for (const source of materials) {
      const material = source.clone();
      material.color = source.color;
      material.visible = false;
      this.materials.set(source, material);
      this.sources.set(material, source);
    }
  }

  pickMaterial(source: THREE.MeshBasicMaterial): THREE.MeshBasicMaterial {
    return this.materials.get(source)!;
  }

  sync(objects: Iterable<THREE.Object3D>): void {
    const groups = new Map<string, Source[]>();
    for (const object of objects) {
      if (!(object instanceof THREE.Mesh) || !object.visible
        || !(object.material instanceof THREE.MeshBasicMaterial) || !this.sources.has(object.material)) continue;
      const key = `${object.geometry.uuid}:${object.material.uuid}`;
      const group = groups.get(key) ?? [];
      group.push(object as Source);
      groups.set(key, group);
    }
    for (const [key, mesh] of this.meshes) {
      if (groups.has(key)) continue;
      mesh.removeFromParent();
      mesh.dispose();
      this.meshes.delete(key);
    }
    const entries = new Map<Source, Entry>();
    for (const [key, sources] of groups) {
      let mesh = this.meshes.get(key);
      if (!mesh || mesh.instanceMatrix.count < sources.length) {
        mesh?.removeFromParent();
        mesh?.dispose();
        // Original render materials retain their ordering relative to splines and models.
        mesh = new THREE.InstancedMesh(sources[0].geometry, this.sources.get(sources[0].material)!, sources.length);
        mesh.name = 'Instanced editor geometry';
        mesh.matrixAutoUpdate = false;
        mesh.raycast = () => {};
        this.meshes.set(key, mesh);
        this.root.add(mesh);
      }
      let changed = mesh.count !== sources.length;
      mesh.count = sources.length;
      sources.forEach((source, index) => {
        let entry = this.entries.get(source);
        if (!entry || entry.mesh !== mesh || entry.index !== index) {
          entry = { mesh, index, matrix: source.matrix.clone() };
          mesh.setMatrixAt(index, source.matrix);
          changed = true;
        } else if (!entry.matrix.equals(source.matrix)) {
          entry.matrix.copy(source.matrix);
          mesh.setMatrixAt(index, source.matrix);
          changed = true;
        }
        entries.set(source, entry);
      });
      if (changed) {
        mesh.instanceMatrix.needsUpdate = true;
        mesh.computeBoundingSphere();
      }
    }
    this.entries = entries;
    if (!groups.size) this.root.removeFromParent();
  }

  previewTransforms(objects: Iterable<THREE.Object3D | undefined>): void {
    const touched = new Set<THREE.InstancedMesh>();
    for (const object of objects) {
      const entry = this.entries.get(object as Source);
      if (!object || !entry || entry.matrix.equals(object.matrix)) continue;
      entry.matrix.copy(object.matrix);
      entry.mesh.setMatrixAt(entry.index, object.matrix);
      touched.add(entry.mesh);
    }
    for (const mesh of touched) {
      mesh.instanceMatrix.needsUpdate = true;
      mesh.computeBoundingSphere();
    }
  }

  clear(): void {
    this.root.removeFromParent();
    this.root.clear();
    for (const mesh of this.meshes.values()) mesh.dispose();
    this.meshes.clear();
    this.entries.clear();
  }

  dispose(): void {
    this.clear();
    for (const material of this.materials.values()) material.dispose();
    this.materials.clear();
    this.sources.clear();
  }
}
