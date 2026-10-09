import * as THREE from 'three';

import type {
  SceneBillboardGlyph, SceneBillboardOptions, SceneBillboardResourceCounts,
} from '../../types/SceneBillboard.js';

const BILLBOARD_KEY = 'forgeBillboardGizmo';
const ENTITY_ID_KEY = 'forgeEntityId';
const GLYPH_KEY = 'forgeBillboardGlyph';
const LABEL_KEY = 'forgeAccessibleLabel';
const TEXTURE_SIZE = 64;

interface Resource<T> {
  value: T;
  references: number;
}

export class SceneBillboardResources {
  readonly geometry = new THREE.PlaneGeometry(1, 1).translate(0, 0.5, 0);

  private readonly materials = new Map<string, Resource<THREE.MeshBasicMaterial>>();
  private readonly textures = new Map<string, Resource<THREE.DataTexture>>();
  private disposed = false;

  create(options: SceneBillboardOptions): SceneBillboardGizmo {
    if (this.disposed) throw new Error('Scene billboard resources are disposed');
    return new SceneBillboardGizmo(this, options);
  }

  acquireMaterial(options: SceneBillboardOptions): { key: string; value: THREE.MeshBasicMaterial } {
    const textureKey = `${options.glyph}:${options.selected ? 1 : 0}:${options.hovered ? 1 : 0}`;
    const color = new THREE.Color(options.color).getHexString();
    const key = `${textureKey}:${color}:${options.depthPolicy}`;
    const retained = this.materials.get(key);
    if (retained) {
      retained.references += 1;
      return { key, value: retained.value };
    }
    const texture = this.acquireTexture(textureKey, options.glyph, options.selected, options.hovered);
    const value = new THREE.MeshBasicMaterial({
      alphaTest: 0.08,
      color: `#${color}`,
      depthTest: options.depthPolicy === 'occluded',
      depthWrite: false,
      fog: false,
      map: texture,
      side: THREE.DoubleSide,
      transparent: true,
      toneMapped: false,
    });
    this.materials.set(key, { value, references: 1 });
    return { key, value };
  }

  releaseMaterial(key: string): void {
    const resource = this.materials.get(key);
    if (!resource || --resource.references > 0) return;
    this.materials.delete(key);
    const textureKey = key.slice(0, key.indexOf(':', key.indexOf(':', key.indexOf(':') + 1) + 1));
    resource.value.dispose();
    this.releaseTexture(textureKey);
  }

  counts(): SceneBillboardResourceCounts {
    return {
      geometries: this.disposed ? 0 : 1,
      materials: this.materials.size,
      textures: this.textures.size,
    };
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.materials.forEach(({ value }) => value.dispose());
    this.textures.forEach(({ value }) => value.dispose());
    this.materials.clear();
    this.textures.clear();
    this.geometry.dispose();
  }

  private acquireTexture(
    key: string,
    glyph: SceneBillboardGlyph,
    selected: boolean,
    hovered: boolean,
  ): THREE.DataTexture {
    const retained = this.textures.get(key);
    if (retained) {
      retained.references += 1;
      return retained.value;
    }
    const value = createBillboardTexture(glyph, selected, hovered);
    this.textures.set(key, { value, references: 1 });
    return value;
  }

  private releaseTexture(key: string): void {
    const resource = this.textures.get(key);
    if (!resource || --resource.references > 0) return;
    this.textures.delete(key);
    resource.value.dispose();
  }
}

export class SceneBillboardGizmo extends THREE.Mesh<THREE.PlaneGeometry, THREE.MeshBasicMaterial> {
  private materialKey = '';
  private options: SceneBillboardOptions;
  private readonly resources: SceneBillboardResources;
  private disposed = false;

  constructor(resources: SceneBillboardResources, options: SceneBillboardOptions) {
    const acquired = resources.acquireMaterial(options);
    super(resources.geometry, acquired.value);
    this.resources = resources;
    this.materialKey = acquired.key;
    this.options = options;
    this.userData[BILLBOARD_KEY] = true;
    this.configure(options);
  }

  configure(options: SceneBillboardOptions): boolean {
    if (this.disposed) return false;
    const changed = this.options !== options
      && (this.name !== options.label
        || this.userData[ENTITY_ID_KEY] !== options.entityId
        || this.position.x !== options.worldPosition.x
        || this.position.y !== options.worldPosition.y
        || this.position.z !== options.worldPosition.z
        || this.options.color !== options.color
        || this.options.glyph !== options.glyph
        || this.options.depthPolicy !== options.depthPolicy
        || this.options.selected !== options.selected
        || this.options.hovered !== options.hovered
        || this.options.worldSize !== options.worldSize
        || this.options.minScreenSize !== options.minScreenSize
        || this.options.maxScreenSize !== options.maxScreenSize);
    const next = this.resources.acquireMaterial(options);
    if (next.key === this.materialKey) this.resources.releaseMaterial(next.key);
    else {
      this.resources.releaseMaterial(this.materialKey);
      this.materialKey = next.key;
      this.material = next.value;
    }
    this.options = { ...options, worldPosition: { ...options.worldPosition } };
    this.name = options.label;
    this.position.copy(options.worldPosition);
    this.renderOrder = options.depthPolicy === 'overlay' ? 1_000 : 0;
    this.userData[ENTITY_ID_KEY] = options.entityId;
    this.userData[GLYPH_KEY] = options.glyph;
    this.userData[LABEL_KEY] = options.label;
    return changed;
  }

  setState(selected: boolean, hovered: boolean): void {
    if (selected === this.options.selected && hovered === this.options.hovered) return;
    this.configure({
      ...this.options,
      worldPosition: this.position,
      selected,
      hovered,
    });
  }

  setHovered(hovered: boolean): void {
    this.setState(this.options.selected, hovered);
  }

  updateForCamera(camera: THREE.Camera, viewportHeight: number): void {
    this.quaternion.copy(camera.quaternion);
    const pixelsToWorld = worldUnitsPerPixel(camera, this.position, viewportHeight);
    const naturalPixels = this.options.worldSize / pixelsToWorld;
    const pixels = THREE.MathUtils.clamp(
      naturalPixels,
      this.options.minScreenSize,
      this.options.maxScreenSize,
    );
    this.scale.setScalar(pixels * pixelsToWorld);
    this.updateMatrix();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.removeFromParent();
    this.resources.releaseMaterial(this.materialKey);
  }
}

export function isSceneBillboardGizmo(object: THREE.Object3D): object is SceneBillboardGizmo {
  return object.userData[BILLBOARD_KEY] === true;
}

function worldUnitsPerPixel(camera: THREE.Camera, position: THREE.Vector3, viewportHeight: number): number {
  const height = Math.max(1, viewportHeight);
  if (camera instanceof THREE.PerspectiveCamera) {
    const distance = Math.max(camera.position.distanceTo(position), camera.near);
    return 2 * distance * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / height;
  }
  if (camera instanceof THREE.OrthographicCamera)
    return (camera.top - camera.bottom) / Math.max(camera.zoom, Number.EPSILON) / height;
  return 1 / height;
}

function createBillboardTexture(
  glyph: SceneBillboardGlyph,
  selected: boolean,
  hovered: boolean,
): THREE.DataTexture {
  const data = new Uint8Array(TEXTURE_SIZE * TEXTURE_SIZE * 4);
  for (let y = 0; y < TEXTURE_SIZE; y += 1) {
    for (let x = 0; x < TEXTURE_SIZE; x += 1) {
      const nx = (x + 0.5) / TEXTURE_SIZE * 2 - 1;
      const ny = (y + 0.5) / TEXTURE_SIZE * 2 - 1;
      const circleDistance = Math.hypot(nx, ny - 0.25);
      const pin = circleDistance <= 0.55
        || ny >= -0.9 && ny <= 0.12 && Math.abs(nx) <= (ny + 0.9) * 0.34;
      const halo = (selected || hovered) && circleDistance >= 0.62
        && circleDistance <= (selected ? 0.78 : 0.72);
      const alpha = glyphCutout(glyph, nx, ny) ? 0 : pin ? 255 : halo ? selected ? 255 : 150 : 0;
      const offset = (y * TEXTURE_SIZE + x) * 4;
      data[offset] = data[offset + 1] = data[offset + 2] = 255;
      data[offset + 3] = alpha;
    }
  }
  const texture = new THREE.DataTexture(data, TEXTURE_SIZE, TEXTURE_SIZE, THREE.RGBAFormat);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.magFilter = THREE.LinearFilter;
  texture.minFilter = THREE.LinearFilter;
  texture.generateMipmaps = false;
  texture.needsUpdate = true;
  return texture;
}

function glyphCutout(glyph: SceneBillboardGlyph, x: number, y: number): boolean {
  const localY = y - 0.25;
  if (glyph === 'robot') {
    const headBorder = Math.abs(x) < 0.25 && Math.abs(localY) < 0.18
      && (Math.abs(x) > 0.2 || Math.abs(localY) > 0.13);
    const eyes = Math.hypot(x - 0.1, localY - 0.04) < 0.045
      || Math.hypot(x + 0.1, localY - 0.04) < 0.045;
    const mouth = Math.abs(x) < 0.12 && Math.abs(localY + 0.08) < 0.025;
    const antenna = Math.abs(x) < 0.025 && localY > 0.18 && localY < 0.3;
    return headBorder || eyes || mouth || antenna;
  }
  if (glyph === 'area') return Math.hypot(x, localY) < 0.2;
  if (glyph === 'ship') return localY > -0.2 && localY < 0.2
    && x > -0.2 && x < 0.2 && localY < 0.28 - Math.abs(x) * 1.8;
  if (glyph === 'sphere') {
    const radius = Math.hypot(x, localY);
    return radius > 0.14 && radius < 0.2 || Math.abs(localY) < 0.035 && Math.abs(x) < 0.2;
  }
  if (glyph === 'settings') return Math.abs(x) < 0.055 && Math.abs(localY) < 0.22
    || Math.abs(localY) < 0.055 && Math.abs(x) < 0.22;
  return Math.abs(x) < 0.18 && Math.abs(localY) < 0.18;
}
