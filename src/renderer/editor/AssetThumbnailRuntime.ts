import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

import type { AssetPreviewKind } from '../../types/AssetExplorer.js';
import type { ForgeApi } from '../../types/ForgeApi.js';
import { configurePs2AssetPreview } from '../../utils/Ps2Materials.ts';
import { disposeObject, frameCameraOnObject } from '../../utils/Scene.ts';
import { configureSkybox } from '../../utils/SkyboxScene.ts';

const THUMBNAIL_SIZE = 256;
const MAX_THUMBNAILS = 128;

interface ScheduledPreview {
  signal?: AbortSignal;
  run(): Promise<string>;
  resolve(value: string): void;
  reject(reason: unknown): void;
  abort?: () => void;
}

export class AssetPreviewMeshMissingError extends Error {
  override name = 'AssetPreviewMeshMissingError';

  constructor() {
    super('Asset has no renderable mesh data.');
  }
}

export class AssetPreviewScheduler {
  private readonly pending: ScheduledPreview[] = [];
  private active = 0;
  private disposed = false;

  schedule(signal: AbortSignal | undefined, run: () => Promise<string>): Promise<string> {
    if (this.disposed || signal?.aborted) return Promise.reject(cancelled(signal));
    return new Promise((resolve, reject) => {
      const job: ScheduledPreview = { signal, run, resolve, reject };
      job.abort = () => {
        const index = this.pending.indexOf(job);
        if (index < 0) return;
        this.pending.splice(index, 1);
        reject(cancelled(signal));
      };
      signal?.addEventListener('abort', job.abort, { once: true });
      this.pending.push(job);
      this.pump();
    });
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const job of this.pending.splice(0)) {
      if (job.abort) job.signal?.removeEventListener('abort', job.abort);
      job.reject(cancelled(job.signal));
    }
  }

  private pump(): void {
    while (!this.disposed && this.active < 4 && this.pending.length) {
      const job = this.pending.shift()!;
      if (job.abort) job.signal?.removeEventListener('abort', job.abort);
      this.active += 1;
      void Promise.resolve().then(job.run).then(job.resolve, job.reject).finally(() => {
        this.active -= 1;
        this.pump();
      });
    }
  }
}

export class AssetThumbnailRuntime {
  private readonly scheduler = new AssetPreviewScheduler();
  private readonly loader = new GLTFLoader();
  private readonly renderer = new THREE.WebGLRenderer({ antialias: true });
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(45, 1, 0.1, 10_000);
  private readonly target = new THREE.Vector3();
  private readonly cache = new Map<string, string>();
  private readonly activeRequests = new Set<string>();
  private readonly targetGame: string;
  private disposed = false;

  constructor(targetGame: string) {
    this.targetGame = targetGame;
    this.renderer.setPixelRatio(1);
    this.renderer.setSize(THUMBNAIL_SIZE, THUMBNAIL_SIZE, false);
    this.scene.background = new THREE.Color(0x151922);
    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x5c6370, 2));
  }

  get(assetId: string, kind: AssetPreviewKind, shellIndex?: number, signal?: AbortSignal): Promise<string> {
    const key = `${kind}:${assetId}:${shellIndex ?? ''}`;
    const cached = this.cache.get(key);
    if (cached) {
      this.cache.delete(key);
      this.cache.set(key, cached);
      return Promise.resolve(cached);
    }
    return this.scheduler.schedule(signal, async () => {
      this.throwIfCancelled(signal);
      const persisted = await forgeApi().getAssetThumbnail(
        this.targetGame, assetId, kind, shellIndex,
      ).catch(() => undefined);
      this.throwIfCancelled(signal);
      if (persisted) {
        return this.remember(key, persisted.url);
      }
      const requestToken = crypto.randomUUID();
      const cancel = () => { void forgeApi().cancelAssetPreview(requestToken); };
      signal?.addEventListener('abort', cancel, { once: true });
      this.activeRequests.add(requestToken);
      let root: THREE.Object3D | undefined;
      try {
        const source = await forgeApi().getAssetPreview(assetId, kind, requestToken, shellIndex);
        this.throwIfCancelled(signal);
        if (kind === 'texture') {
          const blob = await textureThumbnail(source.url, signal);
          this.throwIfCancelled(signal);
          const thumbnail = (await forgeApi().storeAssetThumbnail(
            this.targetGame, assetId, kind, new Uint8Array(await blob.arrayBuffer()),
            shellIndex,
          ).catch(() => undefined))?.url ?? source.url;
          return this.remember(key, thumbnail);
        }
        root = (await this.loader.loadAsync(source.url)).scene;
        this.throwIfCancelled(signal);
        if (!configurePs2AssetPreview(root, kind)) throw new AssetPreviewMeshMissingError();
        if (kind === 'sky') configureSkybox(root);
        frameCameraOnObject(this.camera, root, this.target);
        this.scene.add(root);
        this.renderer.render(this.scene, this.camera);
        const raster = this.renderer.domElement.toDataURL('image/png');
        root.removeFromParent();
        const bytes = decodeDataUrl(raster);
        const thumbnail = (await forgeApi().storeAssetThumbnail(this.targetGame, assetId, kind, bytes, shellIndex)
          .catch(() => undefined))?.url ?? raster;
        return this.remember(key, thumbnail);
      } finally {
        signal?.removeEventListener('abort', cancel);
        this.activeRequests.delete(requestToken);
        if (root) disposeObject(root);
      }
    });
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.scheduler.dispose();
    for (const requestToken of this.activeRequests) void forgeApi().cancelAssetPreview(requestToken);
    this.activeRequests.clear();
    this.cache.clear();
    this.scene.clear();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
  }

  private throwIfCancelled(signal?: AbortSignal): void {
    if (this.disposed || signal?.aborted) throw cancelled(signal);
  }

  private remember(key: string, url: string): string {
    this.cache.set(key, url);
    if (this.cache.size > MAX_THUMBNAILS) this.cache.delete(this.cache.keys().next().value!);
    return url;
  }
}

function cancelled(signal?: AbortSignal): Error {
  return signal?.reason instanceof Error
    ? signal.reason
    : new DOMException('Asset preview cancelled', 'AbortError');
}

function forgeApi(): ForgeApi {
  return (globalThis as unknown as { forge: ForgeApi }).forge;
}

function decodeDataUrl(value: string): Uint8Array {
  const encoded = value.slice(value.indexOf(',') + 1);
  const decoded = atob(encoded);
  return Uint8Array.from(decoded, (character) => character.charCodeAt(0));
}

async function textureThumbnail(url: string, signal?: AbortSignal): Promise<Blob> {
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(`Texture preview returned ${response.status}`);
  const image = await createImageBitmap(await response.blob());
  try {
    const canvas = document.createElement('canvas');
    canvas.width = THUMBNAIL_SIZE;
    canvas.height = THUMBNAIL_SIZE;
    const context = canvas.getContext('2d');
    if (!context) throw new Error('Texture thumbnail canvas is unavailable');
    context.imageSmoothingEnabled = false;
    const scale = Math.min(THUMBNAIL_SIZE / image.width, THUMBNAIL_SIZE / image.height);
    const width = Math.max(1, Math.round(image.width * scale));
    const height = Math.max(1, Math.round(image.height * scale));
    context.drawImage(image, (THUMBNAIL_SIZE - width) / 2, (THUMBNAIL_SIZE - height) / 2, width, height);
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'));
    if (!blob) throw new Error('Texture thumbnail encoding failed');
    return blob;
  } finally {
    image.close();
  }
}
