import * as THREE from 'three';

import type { UyaOceanWaveComponent } from './UyaOceanWaves.ts';

export interface UyaOceanMaterialState {
  readonly material: THREE.ShaderMaterial;
  captureFramebuffer(renderer: THREE.WebGLRenderer): void;
  setTime(seconds: number): void;
  setViewDirection(x: number, y: number): void;
  setWorldOrigin(x: number, y: number): void;
  setCameraPs2(x: number, y: number, z: number): void;
  setTextureOffset(x: number, y: number): void;
  dispose(): void;
}

export function createUyaOceanMaterial(options: {
  name: string;
  texture?: THREE.Texture;
  color: THREE.Color;
  opacity: number;
  additive: boolean;
  depthWrite: boolean;
  underlayProjection: boolean;
  textureRepeat: number;
  waves: readonly UyaOceanWaveComponent[];
  shimmerScale: number;
  fog?: {
    color: THREE.Color;
    nearDistance: number;
    farDistance: number;
    nearIntensity: number;
    farIntensity: number;
  };
}): UyaOceanMaterialState {
  const waveData = Array.from({ length: 8 }, (_, index) => {
    const wave = options.waves[index];
    return new THREE.Vector4(
      wave?.waveVector.x ?? 0,
      wave?.waveVector.y ?? 0,
      wave?.angularSpeed ?? 0,
      wave?.phase ?? 0,
    );
  });
  const waveAmplitudes = Array.from({ length: 8 }, (_, index) => options.waves[index]?.amplitude ?? 0);
  const fog = options.fog;
  const destinationTexture = new THREE.FramebufferTexture(1, 1);
  destinationTexture.name = `${options.name}_destination`;
  destinationTexture.colorSpace = THREE.NoColorSpace;
  // EffectComposer renders into RGBA16F; WebGL rejects framebuffer copies into an RGBA8 texture.
  destinationTexture.type = THREE.HalfFloatType;
  const destinationSize = new THREE.Vector2(1, 1);
  const uniforms = {
    waterTime: { value: 0 },
    worldOriginPs2: { value: new THREE.Vector2() },
    cameraPs2: { value: new THREE.Vector3() },
    viewDirectionPs2: { value: new THREE.Vector2(0, 1) },
    waveData: { value: waveData },
    waveAmplitudes: { value: waveAmplitudes },
    waveCount: { value: Math.min(options.waves.length, 8) },
    waterTexture: { value: options.texture ?? null },
    useTexture: { value: Boolean(options.texture) },
    underlayProjection: { value: options.underlayProjection },
    textureRepeat: { value: options.textureRepeat },
    textureOffset: { value: new THREE.Vector2() },
    layerColor: { value: options.color },
    layerOpacity: { value: THREE.MathUtils.clamp(options.opacity, 0, 1) },
    shimmerScale: { value: options.shimmerScale },
    fogColor: { value: fog?.color ?? new THREE.Color() },
    fogNearDistance: { value: fog?.nearDistance ?? 0 },
    fogFarDistance: { value: fog?.farDistance ?? 0 },
    fogNearIntensity: { value: fog?.nearIntensity ?? 0 },
    fogFarIntensity: { value: fog?.farIntensity ?? 0 },
    useWaterFog: { value: Boolean(fog) },
    destinationTexture: { value: destinationTexture },
    destinationSize: { value: destinationSize },
    additive: { value: options.additive },
  };
  const material = new THREE.ShaderMaterial({
    name: options.name,
    uniforms,
    vertexShader: VERTEX_SHADER,
    fragmentShader: FRAGMENT_SHADER,
    transparent: true,
    depthWrite: options.depthWrite,
    depthTest: true,
    side: THREE.DoubleSide,
    blending: THREE.NoBlending,
    toneMapped: false,
  });
  return {
    material,
    captureFramebuffer: (renderer) => {
      const target = renderer.getRenderTarget();
      if (target) destinationSize.set(target.width, target.height);
      else renderer.getDrawingBufferSize(destinationSize);
      const image = destinationTexture.image;
      if (image.width !== destinationSize.x || image.height !== destinationSize.y) {
        image.width = destinationSize.x;
        image.height = destinationSize.y;
        destinationTexture.needsUpdate = true;
      }
      renderer.copyFramebufferToTexture(destinationTexture);
    },
    setTime: (seconds) => { uniforms.waterTime.value = seconds; },
    setViewDirection: (x, y) => uniforms.viewDirectionPs2.value.set(x, y),
    setWorldOrigin: (x, y) => uniforms.worldOriginPs2.value.set(x, y),
    setCameraPs2: (x, y, z) => uniforms.cameraPs2.value.set(x, y, z),
    setTextureOffset: (x, y) => uniforms.textureOffset.value.set(wrapUnit(x), wrapUnit(y)),
    dispose: () => {
      material.dispose();
      destinationTexture.dispose();
    },
  };
}

function wrapUnit(value: number): number {
  return value - Math.floor(value);
}

const VERTEX_SHADER = /* glsl */`
  attribute float waterWaveBank0Scale;
  attribute float waterWaveBank1Scale;
  uniform float waterTime;
  uniform vec2 worldOriginPs2;
  uniform vec4 waveData[8];
  uniform float waveAmplitudes[8];
  uniform int waveCount;
  varying vec2 vWaterUv;
  varying vec2 vPs2Position;
  varying vec3 vWorldPosition;
  varying float vWaveBank0Scale;
  varying float vWaveBank1Scale;
  varying float vViewDepth;

  void main() {
    vec3 transformed = position;
    vec2 ps2Position = worldOriginPs2 + position.xy;
    float height = 0.0;
    for (int index = 0; index < 8; index++) {
      if (index >= waveCount) break;
      vec4 wave = waveData[index];
      float bankScale = index < 4 ? waterWaveBank0Scale : waterWaveBank1Scale;
      float amplitude = waveAmplitudes[index] * bankScale;
      float phase = dot(ps2Position, wave.xy) + waterTime * wave.z + wave.w;
      height += sin(phase) * amplitude;
    }
    transformed.z += height;
    vec4 localPosition = vec4(transformed, 1.0);
    vec4 worldPosition = modelMatrix * localPosition;
    vec4 viewPosition = modelViewMatrix * localPosition;
    vViewDepth = -viewPosition.z;
    vWaterUv = uv;
    vPs2Position = ps2Position;
    vWorldPosition = worldPosition.xyz;
    vWaveBank0Scale = waterWaveBank0Scale;
    vWaveBank1Scale = waterWaveBank1Scale;
    gl_Position = projectionMatrix * viewPosition;
  }
`;

const FRAGMENT_SHADER = /* glsl */`
  uniform sampler2D waterTexture;
  uniform bool useTexture;
  uniform bool underlayProjection;
  uniform float textureRepeat;
  uniform vec2 textureOffset;
  uniform float waterTime;
  uniform vec3 cameraPs2;
  uniform vec4 waveData[8];
  uniform float waveAmplitudes[8];
  uniform int waveCount;
  uniform vec3 layerColor;
  uniform float layerOpacity;
  uniform vec2 viewDirectionPs2;
  uniform float shimmerScale;
  uniform vec3 fogColor;
  uniform float fogNearDistance;
  uniform float fogFarDistance;
  uniform float fogNearIntensity;
  uniform float fogFarIntensity;
  uniform bool useWaterFog;
  uniform sampler2D destinationTexture;
  uniform vec2 destinationSize;
  uniform bool additive;
  varying vec2 vWaterUv;
  varying vec2 vPs2Position;
  varying vec3 vWorldPosition;
  varying float vWaveBank0Scale;
  varying float vWaveBank1Scale;
  varying float vViewDepth;

  vec2 waterSlopeAtFragment() {
    vec2 slope = vec2(0.0);
    for (int index = 0; index < 8; index++) {
      if (index >= waveCount) break;
      vec4 wave = waveData[index];
      float bankScale = index < 4 ? vWaveBank0Scale : vWaveBank1Scale;
      float amplitude = waveAmplitudes[index] * bankScale;
      float phase = dot(vPs2Position, wave.xy) + waterTime * wave.z + wave.w;
      slope += cos(phase) * amplitude * wave.xy;
    }
    return slope;
  }

  vec2 underlayStqUv(vec2 slope) {
    vec3 normal = normalize(vec3(-slope.x, -slope.y, 1.0));
    vec3 pointToCamera = vec3(
      cameraPs2.x - vPs2Position.x,
      cameraPs2.y - vPs2Position.y,
      cameraPs2.z - vWorldPosition.y
    );
    vec3 projected = normal * dot(pointToCamera, normal)
      - pointToCamera * (dot(normal, normal) * 0.5);
    float q = (length(projected) + projected.z) * 2.0;
    return projected.xy / q;
  }

  vec3 ps2Encode(vec3 linearColor) {
    return pow(clamp(linearColor, 0.0, 1.0), vec3(1.0 / 2.2));
  }

  vec3 ps2Decode(vec3 encodedColor) {
    return pow(clamp(encodedColor, 0.0, 1.0), vec3(2.2));
  }

  void main() {
    vec2 waterSlope = waterSlopeAtFragment();
    float shimmer = 1.0 - dot(waterSlope, viewDirectionPs2) * shimmerScale;
    vec3 sourcePs2 = layerColor * shimmer;
    vec2 layerUv = underlayProjection
      ? underlayStqUv(waterSlope)
      : vWaterUv * textureRepeat + textureOffset;
    if (useTexture) sourcePs2 *= texture2D(waterTexture, layerUv).rgb * (255.0 / 128.0);
    sourcePs2 = clamp(sourcePs2, 0.0, 1.0);
    if (useWaterFog && fogFarDistance > fogNearDistance) {
      float distanceMix = clamp((vViewDepth - fogNearDistance) / (fogFarDistance - fogNearDistance), 0.0, 1.0);
      float fogAmount = clamp(mix(fogNearIntensity, fogFarIntensity, distanceMix), 0.0, 1.0);
      sourcePs2 = mix(sourcePs2, ps2Encode(fogColor), fogAmount);
    }
    vec2 destinationUv = gl_FragCoord.xy / destinationSize;
    vec4 destination = texture2D(destinationTexture, destinationUv);
    vec3 destinationPs2 = ps2Encode(destination.rgb);
    vec3 blendedPs2 = additive
      ? destinationPs2 + sourcePs2 * layerOpacity
      : mix(destinationPs2, sourcePs2, layerOpacity);
    float blendedAlpha = additive
      ? destination.a + layerOpacity
      : mix(destination.a, 1.0, layerOpacity);
    gl_FragColor = vec4(ps2Decode(blendedPs2), clamp(blendedAlpha, 0.0, 1.0));
  }
`;
