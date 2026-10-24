import * as THREE from 'three';
import { assetPickingMaterial } from './AssetMaterialPasses.ts';

type AssetMesh = THREE.InstancedMesh | THREE.BatchedMesh;

interface Entry {
  id: number;
  bounds: THREE.Box3;
  leaf?: Node;
}

interface Node {
  bounds: THREE.Box3;
  parent?: Node;
  entries?: Entry[];
  left?: Node;
  right?: Node;
}

const indexes = new WeakMap<AssetMesh, AssetPickIndex>();
const MIN_INDEX_INSTANCES = 32;
const LEAF_SIZE = 8;

// Projection geometry is immutable and instance slots are dense; its owner refreshes membership and refits transforms.
export function refreshAssetPickIndex(mesh: AssetMesh): void {
  let index = indexes.get(mesh);
  if (!index) {
    const count = mesh instanceof THREE.InstancedMesh ? mesh.count : mesh.instanceCount;
    if (count < MIN_INDEX_INSTANCES) return;
    const created = new AssetPickIndex(mesh);
    index = created;
    indexes.set(mesh, created);
    mesh.raycast = (raycaster, hits) => created.raycast(raycaster, hits);
    const dispose = () => {
      mesh.raycast = created.originalRaycast;
      created.dispose();
      indexes.delete(mesh);
      if (mesh instanceof THREE.InstancedMesh) mesh.removeEventListener('dispose', dispose);
      else mesh.geometry.removeEventListener('dispose', dispose);
    };
    if (mesh instanceof THREE.InstancedMesh) mesh.addEventListener('dispose', dispose);
    else mesh.geometry.addEventListener('dispose', dispose);
  }
  index.refresh();
}

export function updateAssetPickBounds(mesh: AssetMesh, id: number, matrix: THREE.Matrix4): void {
  indexes.get(mesh)?.update(id, matrix);
}

class AssetPickIndex {
  readonly originalRaycast: AssetMesh['raycast'];
  private readonly mesh: AssetMesh;
  private readonly proxy: THREE.Mesh;
  private readonly view?: THREE.BufferGeometry;
  private readonly matrix = new THREE.Matrix4();
  private readonly inverse = new THREE.Matrix4();
  private readonly ray = new THREE.Ray();
  private readonly sphere = new THREE.Sphere();
  private readonly geometryBounds = new Map<number, THREE.Box3>();
  private entries: Entry[] = [];
  private root?: Node;
  private count = -1;
  private readonly changed = new Set<number>();

  constructor(mesh: AssetMesh) {
    this.mesh = mesh;
    this.originalRaycast = mesh.raycast;
    this.view = mesh instanceof THREE.BatchedMesh ? new THREE.BufferGeometry() : undefined;
    this.proxy = new THREE.Mesh(this.view ?? mesh.geometry, mesh.material);
  }

  refresh(): void {
    const count = this.mesh instanceof THREE.InstancedMesh ? this.mesh.count : this.mesh.instanceCount;
    // Repartition bulk edits/reordering; individual drags only refit the affected branch.
    if (count !== this.count || this.changed.size > count / 4) {
      this.count = count;
      this.entries = [];
      if (count >= MIN_INDEX_INSTANCES) for (let id = 0; id < count; id++) {
        this.mesh.getMatrixAt(id, this.matrix);
        this.entries.push({ id, bounds: paddedBounds(this.boundsAt(id).clone().applyMatrix4(this.matrix)) });
      }
      this.root = this.entries.length ? buildNode([...this.entries]) : undefined;
    }
    this.changed.clear();
  }

  update(id: number, matrix: THREE.Matrix4): void {
    const entry = this.entries[id];
    if (!entry) return;
    entry.bounds.copy(this.boundsAt(id)).applyMatrix4(matrix);
    paddedBounds(entry.bounds);
    this.changed.add(id);
    for (let node = entry.leaf; node; node = node.parent) refitNode(node);
  }

  raycast(raycaster: THREE.Raycaster, hits: THREE.Intersection[]): void {
    const mesh = this.mesh;
    // Small groups use the platform path without hierarchy traversal or proxy setup.
    if (this.count < MIN_INDEX_INSTANCES || !this.root || mesh.matrixWorld.determinant() === 0) {
      this.originalRaycast.call(mesh, raycaster, hits);
      return;
    }
    if (mesh instanceof THREE.InstancedMesh) {
      if (!mesh.boundingSphere) mesh.computeBoundingSphere();
      this.sphere.copy(mesh.boundingSphere!).applyMatrix4(mesh.matrixWorld);
      if (!raycaster.ray.intersectsSphere(this.sphere)) return;
    }
    this.ray.copy(raycaster.ray).applyMatrix4(this.inverse.copy(mesh.matrixWorld).invert());
    const candidates: number[] = [];
    collectCandidates(this.root, this.ray, candidates);
    candidates.sort((a, b) => a - b);
    this.proxy.material = assetPickingMaterial(mesh.material);
    this.proxy.geometry = this.view ?? mesh.geometry;
    const localHits: THREE.Intersection[] = [];
    for (const id of candidates) {
      if (mesh instanceof THREE.BatchedMesh) {
        if (!mesh.getVisibleAt(id)) continue;
        const geometryId = mesh.getGeometryIdAt(id);
        const range = mesh.getGeometryRangeAt(geometryId)!;
        const view = this.view!;
        view.index = mesh.geometry.index;
        view.attributes = mesh.geometry.attributes;
        view.setDrawRange(range.start, range.count);
        view.boundingBox ??= new THREE.Box3();
        view.boundingSphere ??= new THREE.Sphere();
        mesh.getBoundingBoxAt(geometryId, view.boundingBox);
        mesh.getBoundingSphereAt(geometryId, view.boundingSphere);
      }
      mesh.getMatrixAt(id, this.proxy.matrixWorld).premultiply(mesh.matrixWorld);
      this.proxy.raycast(raycaster, localHits);
      for (const hit of localHits) {
        hit.object = mesh;
        if (mesh instanceof THREE.InstancedMesh) hit.instanceId = id;
        else hit.batchId = id;
        hits.push(hit);
      }
      localHits.length = 0;
    }
  }

  dispose(): void {
    this.view?.dispose();
    this.entries = [];
    this.root = undefined;
    this.geometryBounds.clear();
    this.changed.clear();
  }

  private boundsAt(id: number): THREE.Box3 {
    const mesh = this.mesh;
    const geometryId = mesh instanceof THREE.InstancedMesh ? 0 : mesh.getGeometryIdAt(id);
    let bounds = this.geometryBounds.get(geometryId);
    if (!bounds) {
      if (mesh instanceof THREE.InstancedMesh) {
        bounds = mesh.geometry.boundingBox?.clone() ?? new THREE.Box3();
        const position = mesh.geometry.getAttribute('position');
        if (!mesh.geometry.boundingBox && position) {
          const point = new THREE.Vector3();
          for (let vertex = 0; vertex < position.count; vertex++)
            bounds.expandByPoint(point.fromBufferAttribute(position, vertex));
        }
      } else bounds = mesh.getBoundingBoxAt(geometryId, new THREE.Box3())!;
      this.geometryBounds.set(geometryId, bounds);
    }
    return bounds;
  }
}

function paddedBounds(bounds: THREE.Box3): THREE.Box3 {
  if (bounds.isEmpty()) return bounds;
  // Keep transformed planar edges in the broad phase despite floating-point roundoff.
  const { min, max } = bounds;
  const magnitude = Math.max(1, Math.abs(min.x), Math.abs(min.y), Math.abs(min.z),
    Math.abs(max.x), Math.abs(max.y), Math.abs(max.z));
  return bounds.expandByScalar(magnitude * Number.EPSILON * 64);
}

function refitNode(node: Node): void {
  node.bounds.makeEmpty();
  if (node.entries) for (const entry of node.entries) node.bounds.union(entry.bounds);
  else node.bounds.copy(node.left!.bounds).union(node.right!.bounds);
}

function buildNode(entries: Entry[], parent?: Node): Node {
  const node: Node = { bounds: new THREE.Box3(), parent };
  for (const entry of entries) node.bounds.union(entry.bounds);
  if (entries.length <= LEAF_SIZE) {
    node.entries = entries;
    entries.forEach((entry) => { entry.leaf = node; });
  } else {
    const size = node.bounds.getSize(new THREE.Vector3());
    const axis = size.x >= size.y && size.x >= size.z ? 'x' : size.y >= size.z ? 'y' : 'z';
    entries.sort((a, b) => a.bounds.min[axis] + a.bounds.max[axis] - b.bounds.min[axis] - b.bounds.max[axis]);
    const middle = Math.floor(entries.length / 2);
    node.left = buildNode(entries.slice(0, middle), node);
    node.right = buildNode(entries.slice(middle), node);
  }
  return node;
}

function collectCandidates(node: Node, ray: THREE.Ray, result: number[]): void {
  if (!ray.intersectsBox(node.bounds)) return;
  if (node.entries) {
    for (const entry of node.entries) if (ray.intersectsBox(entry.bounds)) result.push(entry.id);
  } else {
    collectCandidates(node.left!, ray, result);
    collectCandidates(node.right!, ray, result);
  }
}
