import { net, protocol } from 'electron';
import { realpath } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import type { EditorTerrainSource } from '../types/ForgeApi.js';
import type { UyaRenderPackageResult } from '../types/BridgePayloads.js';
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
    if (!/^[a-z0-9-]+$/.test(value.cacheKey)) throw new Error('Render package key is invalid');
    const cacheRoot = await realpath(this.cacheRoot);
    const root = await realpath(value.rootPath);
    if (!isPathInside(cacheRoot, root)) throw new Error('Render package is outside the Forge cache');
    this.roots.set(value.cacheKey, root);
    const url = (entry: string) =>
      `forge-asset://${value.cacheKey}/${entry.split('/').map(encodeURIComponent).join('/')}`;
    return {
      urls: value.terrainPaths.map(url),
      assets: value.assets.map((asset) => ({
        assetId: asset.assetId,
        url: asset.path ? url(asset.path) : undefined,
        error: asset.error,
      })),
      cacheHit: value.cacheHit,
    };
  }
}
