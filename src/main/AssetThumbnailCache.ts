import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, readdir, rename, stat, unlink, utimes, writeFile } from 'node:fs/promises';
import path from 'node:path';

import type { AssetPreviewKind } from '../types/AssetExplorer.js';

const CACHE_DIRECTORY = 'asset-thumbnails';
const CACHE_BYTES = 128 * 1024 * 1024;
const MAX_ENTRY_BYTES = 2 * 1024 * 1024;
const MODEL_PREVIEW_SCHEMA = 1;
// Bump when raster size, format, material setup, framing, or lighting changes.
const MODEL_THUMBNAIL_SCHEMA = 'png-256-model-v3';
const TEXTURE_THUMBNAIL_SCHEMA = 'png-256-texture-v2';

export interface CachedAssetThumbnail {
  cacheKey: string;
  rootPath: string;
  path: string;
}

export class AssetThumbnailCache {
  private readonly pendingWrites = new Map<string, Promise<CachedAssetThumbnail>>();
  private readonly cacheRoot: string;
  private readonly validate: (bytes: Uint8Array) => boolean;
  private readonly maxBytes: number;

  constructor(
    cacheRoot: string,
    validate: (bytes: Uint8Array) => boolean,
    maxBytes = CACHE_BYTES,
  ) {
    this.cacheRoot = cacheRoot;
    this.validate = validate;
    this.maxBytes = maxBytes;
  }

  async get(assetId: string, kind: AssetPreviewKind, sdkRevision: string): Promise<CachedAssetThumbnail | undefined> {
    const rootPath = path.join(this.cacheRoot, CACHE_DIRECTORY);
    const entryPath = path.join(rootPath, this.fileName(assetId, kind, sdkRevision));
    try {
      const info = await stat(entryPath);
      if (!info.isFile() || info.size <= 0 || info.size > MAX_ENTRY_BYTES) throw new Error('Invalid thumbnail size');
      const bytes = await readFile(entryPath);
      if (!this.validate(bytes)) throw new Error('Invalid thumbnail image');
      const now = new Date();
      await utimes(entryPath, now, now);
      return { cacheKey: CACHE_DIRECTORY, rootPath, path: path.basename(entryPath) };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return undefined;
      await unlink(entryPath).catch(() => undefined);
      return undefined;
    }
  }

  async store(
    assetId: string,
    kind: AssetPreviewKind,
    sdkRevision: string,
    bytes: Uint8Array,
  ): Promise<CachedAssetThumbnail> {
    if (bytes.byteLength <= 0 || bytes.byteLength > MAX_ENTRY_BYTES || !this.validate(bytes))
      throw new TypeError('Asset thumbnail is not a valid 256px PNG image');
    const fileName = this.fileName(assetId, kind, sdkRevision);
    const existing = this.pendingWrites.get(fileName);
    if (existing) return existing;
    const write = this.write(fileName, bytes);
    this.pendingWrites.set(fileName, write);
    try {
      return await write;
    } finally {
      if (this.pendingWrites.get(fileName) === write) this.pendingWrites.delete(fileName);
    }
  }

  private async write(fileName: string, bytes: Uint8Array): Promise<CachedAssetThumbnail> {
    const rootPath = path.join(this.cacheRoot, CACHE_DIRECTORY);
    await mkdir(rootPath, { recursive: true });
    const entryPath = path.join(rootPath, fileName);
    const partialPath = path.join(rootPath, `.${fileName}.${randomUUID()}.partial`);
    try {
      const current = await this.getByPath(rootPath, entryPath);
      if (current) return current;
      await writeFile(partialPath, bytes, { flag: 'wx' });
      await rename(partialPath, entryPath);
      await this.prune(rootPath, entryPath);
      return { cacheKey: CACHE_DIRECTORY, rootPath, path: fileName };
    } finally {
      await unlink(partialPath).catch(() => undefined);
    }
  }

  private async getByPath(rootPath: string, entryPath: string): Promise<CachedAssetThumbnail | undefined> {
    try {
      const bytes = await readFile(entryPath);
      if (bytes.byteLength <= 0 || bytes.byteLength > MAX_ENTRY_BYTES || !this.validate(bytes)) {
        await unlink(entryPath).catch(() => undefined);
        return undefined;
      }
      return { cacheKey: CACHE_DIRECTORY, rootPath, path: path.basename(entryPath) };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return undefined;
      throw error;
    }
  }

  private async prune(rootPath: string, keepPath: string): Promise<void> {
    const entries = await readdir(rootPath, { withFileTypes: true });
    const files: { path: string; size: number; modified: number }[] = [];
    for (const entry of entries) {
      const entryPath = path.join(rootPath, entry.name);
      if (entry.name.endsWith('.partial')) {
        const info = await stat(entryPath).catch(() => undefined);
        if (info && Date.now() - info.mtimeMs > 60 * 60 * 1000)
          await unlink(entryPath).catch(() => undefined);
        continue;
      }
      if (!entry.isFile() || !/\.(?:png|webp)$/.test(entry.name)) continue;
      const info = await stat(entryPath).catch(() => undefined);
      if (!info) continue;
      files.push({ path: entryPath, size: info.size, modified: info.mtimeMs });
    }
    let total = files.reduce((sum, file) => sum + file.size, 0);
    for (const file of files.sort((left, right) => left.modified - right.modified)) {
      if (total <= this.maxBytes) break;
      if (file.path === keepPath) continue;
      await unlink(file.path).catch(() => undefined);
      total -= file.size;
    }
  }

  private fileName(assetId: string, kind: AssetPreviewKind, sdkRevision: string): string {
    const previewSchema = kind === 'texture' ? 2 : MODEL_PREVIEW_SCHEMA;
    const preset = kind === 'texture' ? 'texture-default' : 'model-default';
    const thumbnailSchema = kind === 'texture' ? TEXTURE_THUMBNAIL_SCHEMA : MODEL_THUMBNAIL_SCHEMA;
    const identity = `UYA\n${assetId}\n${kind}\n${sdkRevision}\n${previewSchema}\n${preset}\n${thumbnailSchema}`;
    return `${createHash('sha256').update(identity).digest('hex')}.png`;
  }
}
