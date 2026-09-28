import { Buffer } from 'node:buffer';

import type {
  AssetExplorerCategory,
  AssetExplorerItem,
  AssetExplorerPage,
} from '../../types/AssetExplorer.js';
import type { AssetExplorerBridgeRequest } from '../../types/BridgePayloads.js';
import { PayloadReader, PayloadWriter, malformed } from './PayloadIO.ts';

const MAX_PAGE_ITEMS = 128;
const MAX_METADATA_ITEMS = 1_024;
const MAX_FACET_ITEMS = MAX_PAGE_ITEMS * MAX_METADATA_ITEMS;
const categoryIds: Record<AssetExplorerCategory, number> = {
  ties: 1, shrubs: 2, mobys: 3, skyShells: 4, textures: 5,
};
const categories = Object.fromEntries(
  Object.entries(categoryIds).map(([category, id]) => [id, category]),
) as Record<number, AssetExplorerCategory>;

export function encodeAssetExplorerRequest(value: AssetExplorerBridgeRequest): Buffer {
  const writer = new PayloadWriter();
  writer.writeString(value.catalogRootPath);
  writer.writeUInt32(categoryIds[value.category]);
  writeOptionalString(writer, value.search);
  writeOptionalString(writer, value.game);
  writeOptionalString(writer, value.level);
  writeOptionalString(writer, value.region);
  writeOptionalString(writer, value.revision);
  writer.writeStrings(value.tags ?? []);
  writeOptionalString(writer, value.cursor);
  writer.writeUInt32(value.limit);
  writer.writeString(value.targetGame);
  writer.writeString(value.targetRegion);
  writer.writeString(value.targetRevision);
  return writer.toBuffer();
}

export function decodeAssetExplorerRequest(payload: Uint8Array): AssetExplorerBridgeRequest {
  const reader = new PayloadReader(payload);
  const catalogRootPath = reader.readString();
  const category = categoryValue(reader.readUInt32());
  const search = readOptionalString(reader);
  const game = readOptionalString(reader);
  const level = readOptionalString(reader);
  const region = readOptionalString(reader);
  const revision = readOptionalString(reader);
  const tags = reader.readStrings();
  const cursor = readOptionalString(reader);
  const limit = reader.readUInt32();
  const targetGame = reader.readString();
  const targetRegion = reader.readString();
  const targetRevision = reader.readString();
  reader.complete();
  return {
    catalogRootPath, category, search, game, level, region, revision, tags, cursor, limit,
    targetGame, targetRegion, targetRevision,
  };
}

export function encodeAssetExplorerPage(value: AssetExplorerPage): Buffer {
  if (value.items.length > MAX_PAGE_ITEMS) malformed('Asset explorer page exceeds item limit');
  const writer = new PayloadWriter();
  writer.writeUInt32(value.items.length);
  value.items.forEach((item) => {
    writer.writeString(item.assetId);
    writer.writeUInt32(categoryIds[item.category]);
    writer.writeString(item.displayLabel);
    writer.writeUInt32(item.canonicalFormatVersion);
    writer.writeUInt64(item.byteSize);
    writeStrings(writer, item.aliases, MAX_METADATA_ITEMS);
    writeStrings(writer, item.tags, MAX_METADATA_ITEMS);
    if (item.sources.length > MAX_METADATA_ITEMS) malformed('Asset source list exceeds item limit');
    writer.writeUInt32(item.sources.length);
    item.sources.forEach((source) => {
      writer.writeString(source.game);
      writer.writeString(source.region);
      writer.writeString(source.revision);
      writer.writeString(source.level);
      writer.writeString(source.archive);
      writer.writeUInt32(source.sourceIndex);
    });
    if (item.classIds.length > MAX_METADATA_ITEMS) malformed('Asset class list exceeds item limit');
    writer.writeUInt32(item.classIds.length);
    item.classIds.forEach((classId) => writer.writeUInt32(classId));
    writer.writeString(item.previewState);
    writer.writeBoolean(item.canPlace);
    writeOptionalString(writer, item.placementDisabledReason);
  });
  writeStrings(writer, value.facets.games, MAX_FACET_ITEMS);
  writeStrings(writer, value.facets.levels, MAX_FACET_ITEMS);
  writeStrings(writer, value.facets.regions, MAX_FACET_ITEMS);
  writeStrings(writer, value.facets.revisions, MAX_FACET_ITEMS);
  writeStrings(writer, value.facets.tags, MAX_FACET_ITEMS);
  writeOptionalString(writer, value.nextCursor);
  return writer.toBuffer();
}

export function decodeAssetExplorerPage(payload: Uint8Array): AssetExplorerPage {
  const reader = new PayloadReader(payload);
  const items = readList(reader, MAX_PAGE_ITEMS, () => readItem(reader));
  const facets = {
    games: readStrings(reader, MAX_FACET_ITEMS),
    levels: readStrings(reader, MAX_FACET_ITEMS),
    regions: readStrings(reader, MAX_FACET_ITEMS),
    revisions: readStrings(reader, MAX_FACET_ITEMS),
    tags: readStrings(reader, MAX_FACET_ITEMS),
  };
  const nextCursor = readOptionalString(reader);
  reader.complete();
  return { items, facets, nextCursor };
}

function readItem(reader: PayloadReader): AssetExplorerItem {
  const assetId = reader.readString();
  const category = categoryValue(reader.readUInt32());
  const displayLabel = reader.readString();
  const canonicalFormatVersion = reader.readUInt32();
  const byteSize = reader.readUInt64();
  const aliases = readStrings(reader, MAX_METADATA_ITEMS);
  const tags = readStrings(reader, MAX_METADATA_ITEMS);
  const sources = readList(reader, MAX_METADATA_ITEMS, () => ({
    game: reader.readString(),
    region: reader.readString(),
    revision: reader.readString(),
    level: reader.readString(),
    archive: reader.readString(),
    sourceIndex: reader.readUInt32(),
  }));
  const classIds = readList(reader, MAX_METADATA_ITEMS, () => reader.readUInt32());
  const previewState = reader.readString();
  if (previewState !== 'notCached' && previewState !== 'missingBlob') malformed('Unknown asset preview state');
  const canPlace = reader.readBoolean();
  const placementDisabledReason = readOptionalString(reader);
  return {
    assetId, category, displayLabel, canonicalFormatVersion, byteSize, aliases, tags, sources,
    classIds, previewState, canPlace,
    ...(placementDisabledReason ? { placementDisabledReason } : {}),
  };
}

function categoryValue(value: number): AssetExplorerCategory {
  return categories[value] ?? malformed('Unknown asset explorer category');
}

function writeOptionalString(writer: PayloadWriter, value: string | undefined): void {
  writer.writeString(value ?? '');
}

function readOptionalString(reader: PayloadReader): string | undefined {
  return reader.readString() || undefined;
}

function writeStrings(writer: PayloadWriter, values: string[], maximum: number): void {
  if (values.length > maximum) malformed('List exceeds item limit');
  writer.writeUInt32(values.length);
  values.forEach((value) => writer.writeString(value));
}

function readStrings(reader: PayloadReader, maximum: number): string[] {
  return readList(reader, maximum, () => reader.readString());
}

function readList<T>(reader: PayloadReader, maximum: number, read: () => T): T[] {
  const count = reader.readUInt32();
  if (count > maximum) malformed('List exceeds item limit');
  return Array.from({ length: count }, read);
}
