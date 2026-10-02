import * as THREE from 'three';

import type { CollisionVisualization } from '../types/CollisionVisualization.js';

const PLAYER_BARRIER_STRIPE_KEY = 'forgePlayerBarrierStripes';
const SOLID_COLLISION_TINT_KEY = 'forgeSolidCollisionNibbleTint';
const collisionStates = new WeakMap<THREE.Material, CollisionMaterialState>();

interface CollisionMaterialState {
  collisionColors: { value: THREE.Color[] };
  soundColors: { value: THREE.Color[] };
}

export function configureCollisionMaterials(
  root: THREE.Object3D,
  visualization: CollisionVisualization,
): void {
  root.traverse((object) => {
    if (!(object instanceof THREE.Mesh)) return;
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      if (material.name === 'player_barrier') {
        const color = (material as THREE.Material & { color?: THREE.Color }).color;
        color?.set(visualization.playerBarrierColor);
        configurePlayerBarrierMaterial(material);
      } else if (material.name === 'solid_collision'
        && object.geometry.hasAttribute('_collision_type')
        && object.geometry.hasAttribute('_sound_type')) {
        configureSolidCollisionMaterial(material, visualization);
      }
    }
  });
}

function configurePlayerBarrierMaterial(material: THREE.Material): void {
  if (material.userData[PLAYER_BARRIER_STRIPE_KEY] === true) return;
  (material as THREE.Material & { vertexColors: boolean }).vertexColors = false;
  const previousCompile = material.onBeforeCompile.bind(material);
  const previousCacheKey = material.customProgramCacheKey.bind(material);
  material.onBeforeCompile = (shader: THREE.WebGLProgramParametersWithUniforms, renderer: THREE.WebGLRenderer) => {
    previousCompile(shader, renderer);
    shader.vertexShader = shader.vertexShader
      .replace('void main() {', 'varying vec3 vForgeBarrierPosition;\nvoid main() {')
      .replace(
        '#include <begin_vertex>',
        '#include <begin_vertex>\nvForgeBarrierPosition = transformed;',
      );
    shader.fragmentShader = shader.fragmentShader.replace(
      'void main() {',
      'varying vec3 vForgeBarrierPosition;\nvoid main() {',
    );
    shader.fragmentShader = shader.fragmentShader.replace(
      '#include <color_fragment>',
      `#include <color_fragment>
vec3 forgeBarrierSelectionColor = vec3(1.0);
#if defined(USE_COLOR) || defined(USE_COLOR_ALPHA)
  forgeBarrierSelectionColor = vColor.rgb;
#endif
float forgeBarrierSelected = step(0.001, distance(forgeBarrierSelectionColor, vec3(1.0)));
diffuseColor.rgb = mix(diffuseColor.rgb, forgeBarrierSelectionColor, forgeBarrierSelected);`,
    );
    shader.fragmentShader = shader.fragmentShader.replace(
      '#include <dithering_fragment>',
      `float forgeBarrierCoordinate = (vForgeBarrierPosition.x + vForgeBarrierPosition.y + vForgeBarrierPosition.z) * 0.4;
float forgeBarrierDistance = abs(fract(forgeBarrierCoordinate) - 0.5);
float forgeBarrierEdge = fwidth(forgeBarrierCoordinate);
float forgeBarrierStripe = smoothstep(0.16 - forgeBarrierEdge, 0.16 + forgeBarrierEdge, forgeBarrierDistance);
gl_FragColor.rgb *= mix(0.82, 1.0, forgeBarrierStripe);
gl_FragColor.a *= 1.0 - forgeBarrierStripe;
#include <dithering_fragment>`,
    );
  };
  material.customProgramCacheKey = () => `${previousCacheKey()}-forge-player-barrier-object-stripes`;
  material.userData[PLAYER_BARRIER_STRIPE_KEY] = true;
  material.depthWrite = false;
  material.needsUpdate = true;
}

function configureSolidCollisionMaterial(
  material: THREE.Material,
  visualization: CollisionVisualization,
): void {
  const existing = collisionStates.get(material);
  if (existing) {
    updateCollisionColors(existing, visualization);
    return;
  }
  if (material.userData[SOLID_COLLISION_TINT_KEY] === true) return;
  (material as THREE.Material & { vertexColors: boolean }).vertexColors = false;
  const state: CollisionMaterialState = {
    collisionColors: { value: visualization.collisionTypeColors.map((color) => new THREE.Color(color)) },
    soundColors: { value: visualization.soundTypeColors.map((color) => new THREE.Color(color)) },
  };
  collisionStates.set(material, state);
  const previousCompile = material.onBeforeCompile.bind(material);
  const previousCacheKey = material.customProgramCacheKey.bind(material);
  material.onBeforeCompile = (shader, renderer) => {
    previousCompile(shader, renderer);
    shader.uniforms.forgeCollisionColors = state.collisionColors;
    shader.uniforms.forgeSoundColors = state.soundColors;
    shader.vertexShader = shader.vertexShader
      .replace('void main() {', `attribute float _collision_type;
attribute float _sound_type;
varying float vForgeCollisionType;
varying float vForgeSoundType;
varying vec3 vForgeCollisionViewPosition;
void main() {`)
      .replace('#include <begin_vertex>', `#include <begin_vertex>
vForgeCollisionType = _collision_type;
vForgeSoundType = _sound_type;`)
      .replace('#include <project_vertex>', `#include <project_vertex>
vForgeCollisionViewPosition = mvPosition.xyz;`);
    shader.fragmentShader = shader.fragmentShader
      .replace('void main() {', `uniform vec3 forgeCollisionColors[16];
uniform vec3 forgeSoundColors[16];
varying float vForgeCollisionType;
varying float vForgeSoundType;
varying vec3 vForgeCollisionViewPosition;
void main() {`)
      .replace('#include <color_fragment>', `#include <color_fragment>
int forgeCollisionIndex = int(clamp(floor(vForgeCollisionType + 0.5), 0.0, 15.0));
int forgeSoundIndex = int(clamp(floor(vForgeSoundType + 0.5), 0.0, 15.0));
vec3 forgeSelectionColor = vec3(1.0);
#if defined(USE_COLOR) || defined(USE_COLOR_ALPHA)
  forgeSelectionColor = vColor.rgb;
#endif
float forgeSelected = step(0.001, distance(forgeSelectionColor, vec3(1.0)));
vec3 forgeFaceNormal = normalize(cross(
  dFdx(vForgeCollisionViewPosition), dFdy(vForgeCollisionViewPosition)));
vec3 forgeViewDirection = normalize(-vForgeCollisionViewPosition);
float forgeLight = 0.55 + 0.45 * abs(dot(forgeFaceNormal, forgeViewDirection));
vec3 forgeSurfaceColor = mix(forgeCollisionColors[forgeCollisionIndex], forgeSoundColors[forgeSoundIndex], 0.2);
diffuseColor.rgb = mix(forgeSurfaceColor, forgeSelectionColor, forgeSelected) * forgeLight;`);
  };
  material.customProgramCacheKey = () => `${previousCacheKey()}-forge-solid-collision-nibble-tint-lit`;
  material.userData[SOLID_COLLISION_TINT_KEY] = true;
  material.needsUpdate = true;
}

function updateCollisionColors(state: CollisionMaterialState, visualization: CollisionVisualization): void {
  visualization.collisionTypeColors.forEach((color, index) => state.collisionColors.value[index].set(color));
  visualization.soundTypeColors.forEach((color, index) => state.soundColors.value[index].set(color));
}
