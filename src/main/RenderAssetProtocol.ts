import { nativeImage, net, protocol } from 'electron';
import { randomUUID } from 'node:crypto';
import { copyFile, realpath, rename, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import type { AssetPreviewKind, AssetPreviewSource, EditorTerrainSource } from '../types/ForgeApi.js';
import type { AssetPreviewResult, RenderPackageResult } from '../types/BridgePayloads.js';
import { isPathInside } from '../utils/Security.js';
import { AssetThumbnailCache } from './AssetThumbnailCache.js';

export class RenderAssetProtocol {
  private readonly roots = new Map<string, string>();
  private readonly thumbnails: AssetThumbnailCache;
  private editorRootKey?: string;

  constructor(private readonly cacheRoot: string) {
    this.thumbnails = new AssetThumbnailCache(cacheRoot, (bytes) => {
      if (!Buffer.from(bytes.subarray(0, 8)).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) return false;
      const image = nativeImage.createFromBuffer(Buffer.from(bytes));
      const size = image.getSize();
      return !image.isEmpty() && size.width === 256 && size.height === 256;
    });
  }

  register(): void {
    protocol.handle('forge-asset', async (request) => {
      if (request.method !== 'GET') return new Response(null, { status: 405 });
      try {
        const url = new URL(request.url);
        const root = this.roots.get(url.hostname);
        if (!root) return new Response(null, { status: 404 });
        const relative = decodeURIComponent(url.pathname).replace(/^\/+/, '');
        const candidate = await realpath(path.resolve(root, relative));
        if (!isPathInside(root, candidate)) return new Response(null, { status: 404 });
        return net.fetch(pathToFileURL(candidate).href);
      } catch {
        return new Response(null, { status: 404 });
      }
    });
  }

  async addUyaPackage(value: RenderPackageResult): Promise<EditorTerrainSource> {
    const url = await this.registerRoot(value.cacheKey, value.rootPath);
    if (this.editorRootKey && this.editorRootKey !== value.cacheKey) this.roots.delete(this.editorRootKey);
    this.editorRootKey = value.cacheKey;
    return {
      urls: value.terrainPaths.map(url),
      skyUrl: value.skyPath ? url(value.skyPath) : undefined,
      environment: value.environment,
      occlusionOctants: value.occlusionOctants,
      assets: value.assets.map((asset) => ({
        assetId: asset.assetId,
        kind: asset.kind,
        url: asset.path ? url(asset.path) : undefined,
        error: asset.error,
      })),
      cacheHit: value.cacheHit,
    };
  }

  async addPreview(value: AssetPreviewResult): Promise<AssetPreviewSource> {
    const url = await this.registerRoot(value.cacheKey, value.rootPath);
    return {
      url: url(value.modelPath),
      cacheHit: value.cacheHit,
      ...(value.detailsPath ? { detailsUrl: url(value.detailsPath) } : {}),
      ...(value.diagnosticGlbPath ? { diagnosticGlbUrl: url(value.diagnosticGlbPath) } : {}),
    };
  }

  async getThumbnail(
    targetGame: string,
    assetId: string,
    kind: AssetPreviewKind,
    sdkRevision: string,
    shellIndex?: number,
  ): Promise<AssetPreviewSource | undefined> {
    const value = await this.thumbnails.get(targetGame, assetId, kind, sdkRevision, shellIndex);
    if (!value) return undefined;
    const url = await this.registerRoot(value.cacheKey, value.rootPath);
    return { url: url(value.path), cacheHit: true };
  }

  async storeThumbnail(
    targetGame: string,
    assetId: string,
    kind: AssetPreviewKind,
    sdkRevision: string,
    bytes: Uint8Array,
    shellIndex?: number,
  ): Promise<AssetPreviewSource> {
    const value = await this.thumbnails.store(targetGame, assetId, kind, sdkRevision, bytes, shellIndex);
    const url = await this.registerRoot(value.cacheKey, value.rootPath);
    return { url: url(value.path), cacheHit: false };
  }

  async exportDiagnostic(sourceUrl: string, destinationPath: string): Promise<void> {
    const source = new URL(sourceUrl);
    if (source.protocol !== 'forge-asset:' || source.pathname !== '/diagnostic-target.glb')
      throw new Error('Diagnostic export source is invalid');
    const root = this.roots.get(source.hostname);
    if (!root) throw new Error('Diagnostic export source has expired');
    const candidate = await realpath(path.join(root, 'diagnostic-target.glb'));
    if (!isPathInside(root, candidate) || (await stat(candidate)).size > 128 * 1024 * 1024)
      throw new Error('Diagnostic export source is invalid');
    const partial = `${destinationPath}.${randomUUID()}.partial`;
    try {
      await copyFile(candidate, partial);
      await rename(partial, destinationPath);
    } finally {
      await rm(partial, { force: true });
    }
  }

  private async registerRoot(cacheKey: string, rootPath: string): Promise<(entry: string) => string> {
    if (!/^[a-z0-9-]+$/.test(cacheKey)) throw new Error('Render package key is invalid');
    const cacheRoot = await realpath(this.cacheRoot);
    const root = await realpath(rootPath);
    if (!isPathInside(cacheRoot, root)) throw new Error('Render package is outside the Forge cache');
    this.roots.set(cacheKey, root);
    return (entry: string) =>
      `forge-asset://${cacheKey}/${entry.split('/').map(encodeURIComponent).join('/')}`;
  }
}
