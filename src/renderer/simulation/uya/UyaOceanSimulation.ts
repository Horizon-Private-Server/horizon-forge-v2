import * as THREE from 'three';

import type {
  MobySimulation,
  MobySimulationEntitySnapshot,
  MobySimulationFactory,
  MobySimulationFactoryContext,
  MobySimulationPVarFieldSnapshot,
  MobySimulationReferenceTargetSnapshot,
  MobySimulationResource,
  MobySimulationResolvedFxTexture,
} from '../../../types/MobySimulation.js';
import {
  createUyaOceanGeometry,
  UYA_OCEAN_PATCH_SIZE,
  updateUyaOceanGeometry,
} from './UyaOceanGeometry.ts';
import { createUyaOceanMaterial, type UyaOceanMaterialState } from './UyaOceanMaterial.ts';
import {
  createUyaOceanWaves,
  type UyaOceanWaveComponent,
  uyaOceanWaveSlopeNormalization,
} from './UyaOceanWaves.ts';

export const UYA_OCEAN_CLASS_ID = 0x0b37;
export const UYA_OCEAN_LEVEL_FX_BASE_INDEX = 0x64;

interface OceanColor {
  readonly color: THREE.Color;
  readonly opacity: number;
}

export interface UyaOceanConfig {
  readonly entity: MobySimulationEntitySnapshot;
  readonly overlayFxId: number;
  readonly underlayFxId: number;
  readonly overlayTiling: number;
  readonly overlayDirection: THREE.Vector2;
  readonly overlaySpeed: number;
  readonly overlayAdditive: boolean;
  readonly underlayColor: OceanColor;
  readonly overlayColor: OceanColor;
  readonly waves: readonly UyaOceanWaveComponent[];
  readonly waveAmplitude: number;
  readonly shimmerScale: number;
  readonly height: number;
  readonly hiddenCuboidIds: readonly string[];
  readonly fog?: {
    color: THREE.Color;
    nearDistance: number;
    farDistance: number;
    nearIntensity: number;
    farIntensity: number;
  };
}

interface OceanLayer {
  readonly mesh: THREE.Mesh<THREE.BufferGeometry, THREE.ShaderMaterial>;
  readonly material: UyaOceanMaterialState;
  readonly overlay: boolean;
  readonly tileSize: number;
  readonly direction: THREE.Vector2;
  readonly speed: number;
}

interface OceanSurface {
  readonly config: UyaOceanConfig;
  readonly geometry: THREE.BufferGeometry;
  readonly layers: OceanLayer[];
}

interface LoadedFxTexture {
  readonly texture: THREE.Texture;
  readonly resource: MobySimulationResource<MobySimulationResolvedFxTexture>;
}

class UyaOceanSimulation implements MobySimulation {
  entityIds: readonly string[] = [];
  selectionObjects: { object: THREE.Object3D; entityId: string }[] = [];
  private readonly root = new THREE.Group();
  private readonly surfaces: OceanSurface[] = [];
  private readonly textures: LoadedFxTexture[] = [];
  private readonly cameraPosition = new THREE.Vector3();
  private readonly cameraDirection = new THREE.Vector3();
  private readonly context: MobySimulationFactoryContext;
  private elapsedSeconds = 0;

  static async create(context: MobySimulationFactoryContext): Promise<UyaOceanSimulation | null> {
    const simulation = new UyaOceanSimulation(context);
    const textureLoads = new Map<string, Promise<LoadedFxTexture | undefined>>();
    const failures: string[] = [];
    try {
      for (const entity of context.entities) {
        let config: UyaOceanConfig;
        try {
          config = parseUyaOceanConfig(entity);
        } catch (cause) {
          failures.push(`${entity.id}: ${message(cause)}`);
          context.services.reportDiagnostic({
            code: 'UYA_OCEAN_PVAR_INVALID', severity: 'error', entityIds: [entity.id],
            message: `Water ${entity.id} cannot be simulated: ${message(cause)} Ordinary proxy retained.`,
          });
          continue;
        }
        const surface = await simulation.createSurface(config, textureLoads);
        if (surface.layers.length > 0) simulation.surfaces.push(surface);
        else {
          surface.geometry.dispose();
          failures.push(`${entity.id}: no renderable layer remains after FX resolution`);
        }
      }
      if (simulation.surfaces.length === 0) {
        simulation.dispose();
        throw new Error(failures.join('; ') || 'no renderable water layer is configured');
      }
      simulation.entityIds = simulation.surfaces.map((surface) => surface.config.entity.id);
      simulation.selectionObjects = simulation.surfaces.flatMap((surface) => surface.layers.map((layer) => ({
        object: layer.mesh,
        entityId: surface.config.entity.id,
      })));
      return simulation;
    } catch (cause) {
      simulation.dispose();
      throw cause;
    }
  }

  private constructor(context: MobySimulationFactoryContext) {
    this.context = context;
    this.root.name = 'UYA ocean preview';
    this.root.visible = false;
    this.context.services.root.add(this.root);
  }

  enable(): void {
    this.root.visible = true;
  }

  fixedUpdate(update: { timeSeconds: number }): void {
    this.elapsedSeconds = update.timeSeconds;
    for (const surface of this.surfaces)
      for (const layer of surface.layers) layer.material.setTime(this.elapsedSeconds);
  }

  renderUpdate(): void {
    const camera = this.context.services.camera;
    camera.getWorldPosition(this.cameraPosition);
    camera.getWorldDirection(this.cameraDirection);
    this.cameraDirection.y = 0;
    if (this.cameraDirection.lengthSq() > 1e-8) this.cameraDirection.normalize();
    const viewX = this.cameraDirection.x;
    const viewY = -this.cameraDirection.z;
    const cameraPs2X = this.cameraPosition.x;
    const cameraPs2Y = -this.cameraPosition.z;
    for (const surface of this.surfaces) {
      const { config } = surface;
      const hidden = this.cameraPosition.y < config.height
        || config.entity.hidden
        || config.entity.disabled
        || isInsideHiddenCuboid(cameraPs2X, cameraPs2Y, this.cameraPosition.y, config, this.context);
      for (const layer of surface.layers) layer.mesh.visible = !hidden;
      if (hidden) continue;
      updateUyaOceanGeometry(surface.geometry, camera, config.height, config.waveAmplitude);
      for (const layer of surface.layers) {
        layer.mesh.position.set(this.cameraPosition.x, config.height, this.cameraPosition.z);
        layer.material.setViewDirection(viewX, viewY);
        layer.material.setWorldOrigin(cameraPs2X, cameraPs2Y);
        layer.material.setCameraPs2(cameraPs2X, cameraPs2Y, this.cameraPosition.y);
        if (layer.overlay) {
          const scroll = layer.speed * this.elapsedSeconds;
          layer.material.setTextureOffset(
            layer.direction.x * scroll - cameraPs2Y / layer.tileSize,
            layer.direction.y * scroll + cameraPs2X / layer.tileSize,
          );
        }
      }
    }
  }

  reset(): void {
    this.elapsedSeconds = 0;
    for (const surface of this.surfaces)
      for (const layer of surface.layers) layer.material.setTime(0);
  }

  disable(): void {
    this.root.visible = false;
  }

  invalidate(): 'recreate' {
    return 'recreate';
  }

  dispose(): void {
    this.root.removeFromParent();
    for (const surface of this.surfaces) {
      surface.geometry.dispose();
      for (const layer of surface.layers) layer.material.dispose();
    }
    this.surfaces.length = 0;
    for (const loaded of this.textures) {
      loaded.texture.dispose();
      loaded.resource.release();
    }
    this.textures.length = 0;
    this.root.clear();
  }

  private async createSurface(
    config: UyaOceanConfig,
    textureLoads: Map<string, Promise<LoadedFxTexture | undefined>>,
  ): Promise<OceanSurface> {
    const geometry = createUyaOceanGeometry();
    const layers: OceanLayer[] = [];
    if (config.underlayColor.opacity > 0) {
      const texture = config.underlayFxId >= 0
        ? await this.loadFxTexture(config, 'underlayFxTexture', config.underlayFxId, textureLoads, true)
        : undefined;
      layers.push(this.addLayer(geometry, config, texture?.texture, config.underlayColor, false, false, 1, 0, 0));
    }
    if (config.overlayColor.opacity > 0 && config.overlayFxId >= 0) {
      const texture = await this.loadFxTexture(config, 'overlayFxTexture', config.overlayFxId, textureLoads, false);
      if (texture) layers.push(this.addLayer(
        geometry,
        config,
        texture.texture,
        config.overlayColor,
        true,
        config.overlayAdditive,
        config.overlayTiling,
        config.overlayDirection.x,
        config.overlayDirection.y,
      ));
    }
    return { config, geometry, layers };
  }

  private addLayer(
    geometry: THREE.BufferGeometry,
    config: UyaOceanConfig,
    texture: THREE.Texture | undefined,
    color: OceanColor,
    overlay: boolean,
    additive: boolean,
    tileSizeValue: number,
    directionX: number,
    directionY: number,
  ): OceanLayer {
    const tileSize = finiteNonZero(tileSizeValue, 1);
    const material = createUyaOceanMaterial({
      name: `uya_ocean_${config.entity.id}_${overlay ? 'overlay' : 'underlay'}`,
      texture,
      color: color.color,
      opacity: color.opacity,
      additive,
      depthWrite: overlay,
      underlayProjection: !overlay && Boolean(texture),
      textureRepeat: texture && overlay ? UYA_OCEAN_PATCH_SIZE / tileSize : 1,
      waves: config.waves,
      shimmerScale: overlay ? config.shimmerScale : 0,
      fog: config.fog,
    });
    const mesh = new THREE.Mesh(geometry, material.material);
    mesh.name = material.material.name;
    mesh.frustumCulled = false;
    mesh.rotation.x = -Math.PI / 2;
    mesh.renderOrder = overlay ? 1 : 0;
    mesh.onBeforeRender = (renderer) => material.captureFramebuffer(renderer);
    this.root.add(mesh);
    return {
      mesh,
      material,
      overlay,
      tileSize,
      direction: new THREE.Vector2(directionX, directionY),
      speed: overlay ? finite(config.overlaySpeed) / tileSize : 0,
    };
  }

  private async loadFxTexture(
    config: UyaOceanConfig,
    field: string,
    fxId: number,
    textureLoads: Map<string, Promise<LoadedFxTexture | undefined>>,
    underlayProjection: boolean,
  ): Promise<LoadedFxTexture | undefined> {
    const bankIndex = uyaOceanFxBankIndex(fxId);
    const textureKey = `${bankIndex}:${underlayProjection ? 'underlay' : 'layer'}`;
    let load = textureLoads.get(textureKey);
    if (!load) {
      load = (async () => {
        const resource = await this.context.services.acquireFxTexture(bankIndex);
        if (!resource) return undefined;
        try {
          const texture = await new THREE.TextureLoader().loadAsync(resource.value.url);
          texture.wrapS = THREE.RepeatWrapping;
          texture.wrapT = THREE.RepeatWrapping;
          texture.flipY = false;
          texture.colorSpace = THREE.NoColorSpace;
          texture.magFilter = THREE.LinearFilter;
          texture.minFilter = underlayProjection ? THREE.LinearFilter : THREE.LinearMipmapLinearFilter;
          texture.generateMipmaps = !underlayProjection;
          texture.needsUpdate = true;
          const loaded = { texture, resource };
          this.textures.push(loaded);
          return loaded;
        } catch (cause) {
          resource.release();
          throw cause;
        }
      })();
      textureLoads.set(textureKey, load);
    }
    try {
      const texture = await load;
      if (!texture) this.reportMissingFx(config, field, fxId, bankIndex, 'is not available in the active FX bank');
      return texture;
    } catch (cause) {
      this.reportMissingFx(config, field, fxId, bankIndex, `could not be decoded (${message(cause)})`);
      return undefined;
    }
  }

  private reportMissingFx(
    config: UyaOceanConfig,
    field: string,
    fxId: number,
    bankIndex: number,
    detail: string,
  ): void {
    this.context.services.reportDiagnostic({
      code: 'UYA_OCEAN_FX_MISSING', severity: 'error', entityIds: [config.entity.id],
      message: `Water ${config.entity.id} field ${field} references level FX ${fxId} (bank ${bankIndex}), which ${detail}.`,
    });
  }
}

export const createUyaOceanSimulation: MobySimulationFactory = (context) => UyaOceanSimulation.create(context);

export function parseUyaOceanConfig(entity: MobySimulationEntitySnapshot): UyaOceanConfig {
  const waves = createUyaOceanWaves({
    speed: numberField(entity, 'waveSpeed'),
    crest: numberField(entity, 'waveCrest'),
    surge: numberField(entity, 'waveSurge'),
    rippleSize: numberField(entity, 'waveRippleSize'),
    directionDegrees: numberField(entity, 'waveDirection'),
    directionVariation: numberField(entity, 'waveDirectionVariation'),
    shimmerIntensity: numberField(entity, 'waveShimmerIntensity'),
  });
  const overlayColor = colorField(entity, 'overlayColor');
  const slope = uyaOceanWaveSlopeNormalization(waves);
  const maxOverlay = Math.max(overlayColor.color.r, overlayColor.color.g, overlayColor.color.b) * 255;
  const brightnessScale = maxOverlay < 0x80 ? 1 : (0xff - maxOverlay) / 0x80;
  const shimmerThreshold = numberField(entity, 'waterShimmerThreshold');
  const nearDistance = Math.max(0, numberField(entity, 'fogNearDistance'));
  const farDistance = Math.max(0, numberField(entity, 'fogFarDistance'));
  const nearIntensity = THREE.MathUtils.clamp(numberField(entity, 'fogIntensityNear') / 100, 0, 1);
  const farIntensity = THREE.MathUtils.clamp(numberField(entity, 'fogIntensityFar') / 100, 0, 1);
  const fog = farDistance > nearDistance && (nearIntensity > 0 || farIntensity > 0) ? {
    color: colorField(entity, 'fogColor').color.clone().convertSRGBToLinear(),
    nearDistance,
    farDistance,
    nearIntensity,
    farIntensity,
  } : undefined;
  const overlayFxId = numberField(entity, 'overlayFxTexture');
  const overlayDirection = THREE.MathUtils.degToRad(numberField(entity, 'overlayDirection'));
  return {
    entity,
    overlayFxId: overlayFxId >= 0 && overlayColor.opacity >= 0x10 / 0x80 ? overlayFxId : -1,
    underlayFxId: numberField(entity, 'underlayFxTexture'),
    overlayTiling: finiteNonZero(numberField(entity, 'overlayTiling'), 1),
    overlayDirection: new THREE.Vector2(Math.sin(overlayDirection), -Math.cos(overlayDirection)),
    overlaySpeed: numberField(entity, 'overlaySpeed'),
    overlayAdditive: booleanField(entity, 'overlayAdditive'),
    underlayColor: colorField(entity, 'underlayColor'),
    overlayColor,
    waves,
    waveAmplitude: waves.reduce((total, wave) => total + Math.abs(wave.amplitude), 0),
    shimmerScale: slope > 0 ? brightnessScale * shimmerThreshold / slope : 0,
    height: numberField(entity, 'zPosition'),
    hiddenCuboidIds: ['hideWhenCameraInCuboid0', 'hideWhenCameraInCuboid1']
      .map((path) => field(entity, path).targetEntityId).filter((value): value is string => Boolean(value)),
    fog,
  };
}

export function uyaOceanFxBankIndex(levelFxId: number): number {
  return UYA_OCEAN_LEVEL_FX_BASE_INDEX + levelFxId;
}

function field(entity: MobySimulationEntitySnapshot, path: string): MobySimulationPVarFieldSnapshot {
  const value = entity.pvarFields.find((candidate) => candidate.path === path);
  if (!value) throw new Error(`required MobyDex field ${path} is unavailable`);
  if (value.invalid) throw new Error(`MobyDex field ${path} is invalid`);
  return value;
}

function numberField(entity: MobySimulationEntitySnapshot, path: string): number {
  const value = field(entity, path).value;
  if (value?.kind === 'float') return finite(value.value);
  if (value?.kind === 'integer') {
    const number = Number(value.value);
    if (Number.isFinite(number)) return number;
  }
  throw new Error(`MobyDex field ${path} is not numeric`);
}

function booleanField(entity: MobySimulationEntitySnapshot, path: string): boolean {
  const value = field(entity, path).value;
  if (value?.kind !== 'boolean') throw new Error(`MobyDex field ${path} is not boolean`);
  return value.value;
}

function colorField(entity: MobySimulationEntitySnapshot, path: string): OceanColor {
  const value = field(entity, path).value;
  if (value?.kind !== 'color' || value.value.length < 3)
    throw new Error(`MobyDex field ${path} is not a color`);
  const [red, green, blue, alpha = 0x80] = value.value;
  return {
    color: new THREE.Color(red / 255, green / 255, blue / 255),
    opacity: THREE.MathUtils.clamp(alpha / 0x80, 0, 1),
  };
}

function isInsideHiddenCuboid(
  x: number,
  y: number,
  z: number,
  config: UyaOceanConfig,
  context: MobySimulationFactoryContext,
): boolean {
  return config.hiddenCuboidIds.some((id) => {
    const cuboid = context.snapshot.referenceTargets.find((target) => target.id === id && target.kind === 'cuboid');
    return cuboid ? pointInsideCuboid(x, y, z, cuboid) : false;
  });
}

const cuboidPoint = new THREE.Vector3();
const cuboidRotation = new THREE.Quaternion();

function pointInsideCuboid(
  x: number,
  y: number,
  z: number,
  cuboid: MobySimulationReferenceTargetSnapshot,
): boolean {
  if (cuboid.disabled) return false;
  cuboidPoint.set(
    x - cuboid.transform.position.x,
    y - cuboid.transform.position.y,
    z - cuboid.transform.position.z,
  );
  cuboidRotation.set(
    cuboid.transform.rotation.x,
    cuboid.transform.rotation.y,
    cuboid.transform.rotation.z,
    cuboid.transform.rotation.w,
  ).invert();
  cuboidPoint.applyQuaternion(cuboidRotation);
  return Math.abs(cuboidPoint.x) <= Math.abs(cuboid.transform.scale.x)
    && Math.abs(cuboidPoint.y) <= Math.abs(cuboid.transform.scale.y)
    && Math.abs(cuboidPoint.z) <= Math.abs(cuboid.transform.scale.z);
}

function finite(value: number): number {
  return Number.isFinite(value) ? value : 0;
}

function finiteNonZero(value: number, fallback: number): number {
  return Number.isFinite(value) && value !== 0 ? value : fallback;
}

function message(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
