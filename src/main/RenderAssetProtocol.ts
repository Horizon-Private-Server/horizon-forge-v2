import { net, protocol } from 'electron';
import { realpath } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import type { AssetPreviewSource, EditorTerrainSource } from '../types/ForgeApi.js';
import type { AssetPreviewResult, UyaRenderPackageResult } from '../types/BridgePayloads.js';
import { isPathInside } from '../utils/Security.js';

export class RenderAssetProtocol {
  private readonly roots = new Map<string, string>();

  constructor(private readonly cacheRoot: string) {}

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

  async addPackage(value: UyaRenderPackageResult): Promise<EditorTerrainSource> {
    const url = await this.registerRoot(value.cacheKey, value.rootPath);
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
    return { url: url(value.modelPath), cacheHit: value.cacheHit };
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
