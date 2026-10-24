import * as THREE from 'three';

import type { EditorEntity, ProjectVector4 } from '../../types/EditorRuntime.js';

interface Light {
  topColor: THREE.Vector4;
  topDirection: THREE.Vector3;
  inverseColor: THREE.Vector4;
  inverseDirection: THREE.Vector3;
}

const SOURCE_COLOR_ATTRIBUTE = '_forge_tfrag_cache_color';
const MATERIAL_KEY = 'forgeTfragLighting';

export function configureTfragLighting(root: THREE.Object3D, entities: readonly EditorEntity[]): void {
  const lights = new Map<number, Light>();
  for (const entity of entities) {
    if (!entity.directionalLight || entity.provenance === undefined) continue;
    const { topColor, topDirection, inverseColor, inverseDirection } = entity.directionalLight;
    const q = entity.transform.rotation;
    const rotation = new THREE.Quaternion(q.x, q.y, q.z, q.w).normalize();
    const direction = (value: ProjectVector4) => {
      const vector = new THREE.Vector3(value.x, value.y, value.z).applyQuaternion(rotation);
      return new THREE.Vector3(vector.x, vector.z, -vector.y);
    };
    lights.set(entity.provenance.sourceIndex, {
      topColor: new THREE.Vector4(topColor.x, topColor.y, topColor.z, topColor.w),
      topDirection: direction(topDirection),
      inverseColor: new THREE.Vector4(inverseColor.x, inverseColor.y, inverseColor.z, inverseColor.w),
      inverseDirection: direction(inverseDirection),
    });
  }
  const selectedLights = new Map<number, Light | undefined>();
  const selectLight = (value: number) => {
    if (selectedLights.has(value)) return selectedLights.get(value);
    const primary = lights.get(value & 15);
    const secondary = lights.get((value >> 4) & 15);
    const blend = secondary ? ((value >> 8) & 255) / 256 : 0;
    const light = primary ? {
      topColor: primary.topColor.clone().lerp(secondary?.topColor ?? primary.topColor, blend),
      topDirection: normalize(primary.topDirection.clone().lerp(secondary?.topDirection ?? primary.topDirection, blend)),
      inverseColor: primary.inverseColor.clone().lerp(secondary?.inverseColor ?? primary.inverseColor, blend),
      inverseDirection: normalize(primary.inverseDirection.clone().lerp(secondary?.inverseDirection ?? primary.inverseDirection, blend)),
    } : undefined;
    selectedLights.set(value, light);
    return light;
  };
  const seen = new Set<THREE.BufferGeometry>();
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    const geometry = object.geometry;
    const selector = geometry.getAttribute('_dl_light_selector');
    const base = geometry.getAttribute('_dl_light_base_color');
    const normals = geometry.getAttribute('_dl_light_normal') ?? geometry.getAttribute('normal');
    const postScale = geometry.getAttribute('_dl_light_post_scale');
    const count = geometry.getAttribute('position')?.count;
    if (!count || !selector || !base || !normals
      || selector.count !== count || base.count !== count || normals.count !== count) return;
    if (!seen.has(geometry)) {
      seen.add(geometry);
      const source = geometry.getAttribute(SOURCE_COLOR_ATTRIBUTE) ?? geometry.getAttribute('color');
      // Keep the unlit cache with geometry so batching and cloning preserve the fallback on light edits.
      if (source) geometry.setAttribute(SOURCE_COLOR_ATTRIBUTE, source);
      const existing = geometry.getAttribute('color');
      const colors = existing instanceof THREE.BufferAttribute && existing.itemSize === 3
        && existing.array instanceof Float32Array && existing !== source
        ? existing : new THREE.BufferAttribute(new Float32Array(count * 3), 3);
      const normal = new THREE.Vector3();
      for (let index = 0; index < count; index++) {
        const value = Math.round(Math.max(0, selector.getX(index)));
        const light = selectLight(value);
        normal.set(normals.getX(index), normals.getY(index), normals.getZ(index));
        normalize(normal);
        const top = light ? lightDot(normal, light.topDirection, light.topColor.w) : 0;
        const inverse = light ? lightDot(normal, light.inverseDirection, light.inverseColor.w) : 0;
        for (let component = 0; component < 3; component++) {
          // Match Map-o-Matic's RGB5 expansion and the VU's byte quantization before post-scaling.
          const color = light
            ? THREE.MathUtils.clamp(Math.round(base.getComponent(index, component) * 31), 0, 31) / 16
              + Math.max(0, light.topColor.getComponent(component) * top + light.inverseColor.getComponent(component) * inverse)
            : source?.getComponent(index, component) ?? 1;
          const byte = THREE.MathUtils.clamp(Math.round(color * 128), 0, 255);
          colors.setComponent(index, component, THREE.MathUtils.clamp(
            Math.floor(byte * Math.max(0, postScale?.getX(index) ?? 1)), 0, 255,
          ) / 128);
        }
      }
      geometry.setAttribute('color', colors);
      colors.needsUpdate = true;
    }
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      configureMaterial(material);
    }
  });
}

function normalize(vector: THREE.Vector3): THREE.Vector3 {
  return vector.lengthSq() > 1e-12 ? vector.normalize() : vector.set(0, 1, 0);
}

function lightDot(normal: THREE.Vector3, direction: THREE.Vector3, alpha: number): number {
  const dot = normal.dot(direction);
  return Math.max(dot, dot * alpha);
}

function configureMaterial(material: THREE.Material): void {
  if (material.userData[MATERIAL_KEY] === true) return;
  material.userData[MATERIAL_KEY] = true;
  const map = (material as THREE.MeshBasicMaterial).map;
  if (map) {
    map.colorSpace = THREE.NoColorSpace;
    map.needsUpdate = true;
  }
  const previousCompile = material.onBeforeCompile.bind(material);
  const previousKey = material.customProgramCacheKey.bind(material);
  material.onBeforeCompile = (shader, renderer) => {
    previousCompile(shader, renderer);
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <map_fragment>', `#include <map_fragment>
diffuseColor.rgb = floor(clamp(diffuseColor.rgb, 0.0, 1.0) * 255.0 + 0.05) / 255.0;`)
      .replace('#include <color_fragment>', `#include <color_fragment>
diffuseColor.rgb = sRGBTransferEOTF(vec4(clamp(floor(diffuseColor.rgb * 255.0), 0.0, 255.0) / 255.0, 1.0)).rgb;`);
  };
  material.customProgramCacheKey = () => `${previousKey()}-forge-tfrag-lighting`;
  material.needsUpdate = true;
}
