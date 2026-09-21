import * as THREE from 'three';

export type Ps2MaterialFamily = 'tfrag' | 'tie' | 'shrub' | 'moby' | 'sky';

const DEFAULT_FULL_OPACITY_BYTE = 127;
const ALPHA_PATCH_KEY = 'forgePs2FullOpacityAlpha';
const ALPHA_SPLIT_KEY = 'forgePs2AlphaSplit';
const TRANSLUCENT_PASS_KEY = 'forgePs2TranslucentPass';
const OPAQUE_ALPHA_CUTOFF = 254 / 255;
const alphaCompile = new WeakMap<THREE.Material, THREE.Material['onBeforeCompile']>();
const alphaCacheKey = new WeakMap<THREE.Material, THREE.Material['customProgramCacheKey']>();

export function configurePs2MaterialAlpha(root: THREE.Object3D, family: Ps2MaterialFamily): void {
  const materials = new Set<THREE.Material>();
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) materials.add(material);
  });
  materials.forEach((material) => configureMaterial(material, family));
}

function configureMaterial(material: THREE.Material, family: Ps2MaterialFamily): void {
  if ((!material.transparent && material.alphaTest <= 0) || !materialMap(material)) return;
  const fullOpacity = resolveFullOpacity(material, family);
  if (material.userData[ALPHA_PATCH_KEY] === fullOpacity) return;
  const previousCompile = material.onBeforeCompile.bind(material);
  const previousCacheKey = material.customProgramCacheKey.bind(material);
  material.onBeforeCompile = (shader, renderer) => {
    previousCompile(shader, renderer);
    shader.fragmentShader = shader.fragmentShader.replace(
      '#include <map_fragment>',
      `#include <map_fragment>\ndiffuseColor.a = min(diffuseColor.a / ${fullOpacity.toFixed(8)}, 1.0);`,
    );
  };
  material.customProgramCacheKey = () => `${previousCacheKey()}-forge-ps2-alpha-${fullOpacity}`;
  material.userData[ALPHA_PATCH_KEY] = fullOpacity;
  material.userData[ALPHA_SPLIT_KEY] = supportsAlphaSplit(family)
    && hasOpaqueTexels(material, family, fullOpacity);
  material.opacity = 1;
  material.needsUpdate = true;
  alphaCompile.set(material, material.onBeforeCompile);
  alphaCacheKey.set(material, material.customProgramCacheKey);
}

export function createPs2OpaquePassMaterial(source: THREE.Material): THREE.Material | undefined {
  if (source.userData[ALPHA_SPLIT_KEY] !== true) return undefined;
  const material = source.clone();
  material.name = `${source.name || 'model'}_alpha_opaque_pass`;
  material.onBeforeCompile = alphaCompile.get(source) ?? source.onBeforeCompile;
  material.customProgramCacheKey = alphaCacheKey.get(source) ?? source.customProgramCacheKey;
  material.transparent = false;
  material.blending = THREE.NoBlending;
  material.depthWrite = true;
  material.alphaTest = OPAQUE_ALPHA_CUTOFF;
  material.forceSinglePass = true;
  material.needsUpdate = true;
  configureTranslucentPass(source);
  return material;
}

function resolveFullOpacity(material: THREE.Material, family: Ps2MaterialFamily): number {
  const prefix = family === 'sky' ? 'Skybox' : family[0].toUpperCase() + family.slice(1);
  const value = Number(material.userData[`${prefix}TextureFullOpacityAlpha`]);
  const normalized = Number.isFinite(value) && value > 0
    ? (value > 1 ? value / 255 : value)
    : DEFAULT_FULL_OPACITY_BYTE / 255;
  return THREE.MathUtils.clamp(normalized, 1 / 255, 1);
}

function hasOpaqueTexels(material: THREE.Material, family: Ps2MaterialFamily, fullOpacity: number): boolean {
  const prefix = family[0].toUpperCase() + family.slice(1);
  const value = Number(material.userData[`${prefix}TextureMaxAlpha`] ?? material.userData.MaxAlpha);
  if (!Number.isFinite(value)) return true;
  const normalized = (value > 1 ? value / 255 : value) / fullOpacity;
  return normalized >= OPAQUE_ALPHA_CUTOFF;
}

function supportsAlphaSplit(family: Ps2MaterialFamily): boolean {
  return family === 'tie' || family === 'shrub' || family === 'moby';
}

function configureTranslucentPass(material: THREE.Material): void {
  if (material.userData[TRANSLUCENT_PASS_KEY] === true) return;
  const previousCompile = material.onBeforeCompile.bind(material);
  const previousCacheKey = material.customProgramCacheKey.bind(material);
  material.onBeforeCompile = (shader, renderer) => {
    previousCompile(shader, renderer);
    shader.fragmentShader = shader.fragmentShader.replace(
      '#include <alphatest_fragment>',
      `if (diffuseColor.a >= ${OPAQUE_ALPHA_CUTOFF.toFixed(8)}) discard;\n#include <alphatest_fragment>`,
    );
  };
  material.customProgramCacheKey = () => `${previousCacheKey()}-forge-ps2-translucent`;
  material.userData[TRANSLUCENT_PASS_KEY] = true;
  material.depthWrite = false;
  material.needsUpdate = true;
}

function materialMap(material: THREE.Material): THREE.Texture | null {
  return (material as THREE.Material & { map?: THREE.Texture | null }).map ?? null;
}
