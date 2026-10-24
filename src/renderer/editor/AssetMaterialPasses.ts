import * as THREE from 'three';

interface Passes {
  front: THREE.Material;
  back: THREE.Material;
}

const passes = new WeakMap<THREE.Material, Passes>();
const pickingMaterials = new WeakMap<THREE.Material, THREE.Material>();

export function assetPickingMaterial(material: THREE.Material | THREE.Material[]): THREE.Material | THREE.Material[] {
  return Array.isArray(material) ? material : pickingMaterials.get(material) ?? material;
}

export function configureAssetMaterialPasses(mesh: THREE.Mesh, ownedMaterials: Set<THREE.Material>): void {
  const source = mesh.material;
  if (Array.isArray(source) || !source.transparent || source.side !== THREE.DoubleSide || source.forceSinglePass) return;
  let pair = passes.get(source);
  if (!pair) {
    const front = source.clone();
    const back = source.clone();
    for (const material of [front, back]) {
      material.onBeforeCompile = source.onBeforeCompile;
      material.customProgramCacheKey = source.customProgramCacheKey;
    }
    front.side = THREE.FrontSide;
    back.side = THREE.BackSide;
    front.onBeforeRender = function(renderer, scene, camera, geometry, object, group) {
      source.onBeforeRender.call(this, renderer, scene, camera, geometry, object, group);
      // This hook runs after object matrices are ready; keep the back/front draws adjacent.
      renderer.renderBufferDirect(camera, scene, geometry, back, object, group as unknown as THREE.GeometryGroup);
    };
    pair = { front, back };
    passes.set(source, pair);
    pickingMaterials.set(front, source);
  }
  const { front, back } = pair;
  ownedMaterials.add(front);
  ownedMaterials.add(back);
  mesh.material = front;
  const raycast = mesh.raycast;
  mesh.raycast = function(raycaster, hits) {
    const material = this.material;
    this.material = source;
    try { raycast.call(this, raycaster, hits); }
    finally { this.material = material; }
  };
}
