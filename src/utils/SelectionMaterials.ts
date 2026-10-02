import * as THREE from 'three';

export const INSTANCE_SELECTION_MARKER = new THREE.Color(0xfffeff);

export function createSelectionOverrideMaterial(
  source: THREE.Material,
  selectionColor: THREE.Color,
): THREE.Material {
  const material = source.clone();
  const color = { value: selectionColor };
  const marker = { value: INSTANCE_SELECTION_MARKER };
  const previousCompile = source.onBeforeCompile.bind(source);
  const previousCacheKey = source.customProgramCacheKey.bind(source);
  material.onBeforeCompile = (shader, renderer) => {
    previousCompile(shader, renderer);
    shader.uniforms.forgeSelectionColor = color;
    shader.uniforms.forgeInstanceSelectionMarker = marker;
    shader.vertexShader = shader.vertexShader
      .replace('void main() {', `varying vec3 vForgeInstanceColor;
void main() {
vForgeInstanceColor = vec3(1.0);`)
      .replace('#include <color_vertex>', `#include <color_vertex>
#ifdef USE_INSTANCING_COLOR
  vForgeInstanceColor = instanceColor.rgb;
#endif`);
    shader.fragmentShader = shader.fragmentShader
      .replace('void main() {', `uniform vec3 forgeSelectionColor;
uniform vec3 forgeInstanceSelectionMarker;
varying vec3 vForgeInstanceColor;
void main() {`)
      .replace('#include <opaque_fragment>', `if (distance(vForgeInstanceColor, forgeInstanceSelectionMarker) < 0.001) {
  float forgeSelectionLuminance = dot(outgoingLight, vec3(0.2126, 0.7152, 0.0722));
  outgoingLight = forgeSelectionColor * mix(0.35, 1.15, clamp(forgeSelectionLuminance, 0.0, 1.0));
}
#include <opaque_fragment>`);
  };
  material.customProgramCacheKey = () => `${previousCacheKey()}-forge-selection-override`;
  material.needsUpdate = true;
  return material;
}
