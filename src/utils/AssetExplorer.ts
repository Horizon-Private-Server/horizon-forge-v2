import type {
  AssetExplorerFamily,
  AssetExplorerItem,
  AssetExplorerPage,
  AssetExplorerQuery,
} from '../types/AssetExplorer.js';

export function assetExplorerFilterCount(
  query: Pick<AssetExplorerQuery, 'game' | 'level' | 'region' | 'revision' | 'tags'>,
): number {
  return [query.game, query.level, query.region, query.revision].filter(Boolean).length + (query.tags?.length ?? 0);
}

export function assetExplorerQueryKey(
  category: AssetExplorerQuery['category'],
  search: string,
  query: Pick<AssetExplorerQuery, 'game' | 'level' | 'region' | 'revision' | 'tags'>,
): string {
  return JSON.stringify([category, search, query.game, query.level, query.region, query.revision, query.tags ?? []]);
}

export async function retainAssetExplorerPageDepth(
  firstPage: AssetExplorerPage,
  minimumItemCount: number,
  loadNext: (cursor: string) => Promise<AssetExplorerPage | undefined>,
): Promise<AssetExplorerPage> {
  const items = [...firstPage.items];
  let page = firstPage;
  while (page.nextCursor && items.length < minimumItemCount) {
    const next = await loadNext(page.nextCursor);
    if (!next) break;
    items.push(...next.items);
    page = next;
  }
  return { ...page, items };
}

export function isStaleAssetExplorerCursor(reason: unknown): boolean {
  const message = reason instanceof Error ? reason.message : String(reason);
  return message.toLocaleLowerCase().includes('asset catalog changed; restart the query');
}

export function buildAssetFamilies(
  items: readonly AssetExplorerItem[],
  target: { game: string; region: string; revision: string; level: number },
): AssetExplorerFamily[] {
  const families = new Map<string, AssetExplorerFamily>();
  for (const item of items) {
    if (!item.classIds.length) {
      addVariant(families, `asset:${item.assetId}:${item.shellIndex ?? ''}`, item, undefined, item.displayLabel);
      continue;
    }
    const targets = new Set(item.sources.map((source) =>
      `${source.game}\0${source.region}\0${source.revision}`));
    for (const classId of item.classIds) {
      for (const sourceTarget of targets) {
        const familyId = `${item.category}\0${sourceTarget}\0${classId}`;
        addVariant(families, familyId, item, classId, classLabel(item.category, classId));
      }
    }
  }
  const level = `level${String(target.level).padStart(2, '0')}`;
  for (const family of families.values()) {
    family.variants.sort((left, right) => left.assetId.localeCompare(right.assetId));
    family.representativeAssetId = family.variants.find((variant) => variant.sources.some((source) =>
      source.game === target.game && source.region === target.region && source.revision === target.revision
      && source.level === level))?.assetId
      ?? family.variants.find((variant) => variant.sources.some((source) =>
        source.game === target.game && source.region === target.region && source.revision === target.revision))?.assetId
      ?? family.variants[0].assetId;
  }
  return [...families.values()].sort((left, right) =>
    (left.classId ?? Number.MAX_SAFE_INTEGER) - (right.classId ?? Number.MAX_SAFE_INTEGER)
    || left.familyId.localeCompare(right.familyId));
}

function addVariant(
  families: Map<string, AssetExplorerFamily>,
  familyId: string,
  item: AssetExplorerItem,
  classId: number | undefined,
  displayLabel: string,
): void {
  const family = families.get(familyId);
  if (family) {
    if (!family.variants.some((variant) => variant.assetId === item.assetId)) family.variants.push(item);
    return;
  }
  families.set(familyId, {
    familyId, category: item.category, displayLabel, classId, variants: [item], representativeAssetId: item.assetId,
  });
}

function classLabel(category: AssetExplorerItem['category'], classId: number): string {
  const prefix = ({ ties: 'tie', shrubs: 'shrub', mobys: 'moby', skyShells: 'sky', textures: 'texture' })[category];
  return `${prefix}:0x${classId.toString(16).toUpperCase().padStart(4, '0')}`;
}
