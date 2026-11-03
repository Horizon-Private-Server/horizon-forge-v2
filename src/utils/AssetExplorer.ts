import type {
  AssetExplorerCategory,
  AssetExplorerFamily,
  AssetExplorerItem,
  AssetExplorerPage,
  AssetExplorerQuery,
  AssetExplorerSource,
  AssetPreviewKind,
  AssetSourceSelection,
  AssetVariantSelection,
} from '../types/AssetExplorer.js';

const PREVIEW_KINDS: Record<AssetExplorerCategory, AssetPreviewKind> = {
  ties: 'tie', shrubs: 'shrub', mobys: 'moby', tfrags: 'tfrag', skyShells: 'sky', textures: 'texture',
};

export function assetPreviewKind(category: AssetExplorerCategory): AssetPreviewKind {
  return PREVIEW_KINDS[category];
}

export function isAssetSourceSelection(value: unknown): value is AssetSourceSelection {
  if (!value || typeof value !== 'object') return false;
  const source = value as Record<string, unknown>;
  return ['game', 'region', 'revision', 'level', 'archive'].every((key) =>
    typeof source[key] === 'string' && source[key].trim().length > 0
    && source[key].length <= (key === 'archive' ? 4096 : 128))
    && Number.isInteger(source.sourceIndex) && Number(source.sourceIndex) >= 0
    && Number(source.sourceIndex) <= 0x7fffffff;
}

export function assetSourceSelection(source: AssetExplorerSource): AssetSourceSelection {
  const { game, region, revision, level, archive, sourceIndex } = source;
  return { game, region, revision, level, archive, sourceIndex };
}

export function assetVariantKey(selection: AssetVariantSelection): string {
  return JSON.stringify([selection.assetId, selection.source && assetExplorerSourceKey(selection.source)]);
}

export function assetFamilyVariantOptions(family: AssetExplorerFamily): AssetVariantSelection[] {
  return family.variants.flatMap((item) => {
    const sources = family.category === 'mobys' ? item.sources.filter((source) =>
      `${item.category}\0${source.game}\0${source.region}\0${source.revision}\0${family.classId}` === family.familyId) : [];
    return sources.length ? sources.map((source) => ({ assetId: item.assetId, source: assetSourceSelection(source) }))
      : [{ assetId: item.assetId }];
  });
}

export function resolveAssetFamilySelection(
  family: AssetExplorerFamily, selected?: AssetVariantSelection,
  options: readonly AssetVariantSelection[] = assetFamilyVariantOptions(family),
): AssetVariantSelection {
  const selectedKey = selected && assetVariantKey(selected);
  const preferredKey = assetVariantKey({ assetId: family.representativeAssetId, source: family.representativeSource });
  let preferred: AssetVariantSelection | undefined;
  let representative: AssetVariantSelection | undefined;
  for (const option of options) {
    const key = assetVariantKey(option);
    if (key === selectedKey || !selected && key === preferredKey) return option;
    if (key === preferredKey) preferred ??= option;
    if (option.assetId === family.representativeAssetId) representative ??= option;
  }
  return preferred ?? representative ?? options[0];
}

export function assetExplorerSourceKey(source: AssetExplorerSource): string {
  const texture = source.textureUse;
  return JSON.stringify([
    source.game, source.region, source.revision, source.level, source.archive, source.sourceIndex,
    texture?.ownerKind, texture?.ownerClassId, texture?.role, texture?.slot, texture?.restorable,
    source.mobySounds?.bankAssetId,
  ]);
}

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
  filters?: Pick<AssetExplorerQuery, 'game' | 'level' | 'region' | 'revision'>,
): AssetExplorerFamily[] {
  const families = new Map<string, AssetExplorerFamily>();
  for (const original of items) {
    const item = filters ? { ...original, sources: original.sources.filter((source) =>
      (!filters.game || source.game === filters.game) && (!filters.level || source.level === filters.level)
      && (!filters.region || source.region === filters.region) && (!filters.revision || source.revision === filters.revision)) } : original;
    if (original.sources.length && !item.sources.length) continue;
    if (!item.classIds.length) {
      addVariant(families, `asset:${item.assetId}:${item.shellIndex ?? ''}`, item, undefined, item.displayLabel);
      continue;
    }
    const targets = new Set(item.sources.map((source) =>
      `${source.game}\0${source.region}\0${source.revision}`));
    if (targets.size === 0 && item.tags.includes('project-attached'))
      targets.add(`${target.game}\0${target.region}\0${target.revision}`);
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
    const representative = family.variants.find((item) => item.assetId === family.representativeAssetId)!;
    const sources = representative.sources.filter((source) =>
      `${family.category}\0${source.game}\0${source.region}\0${source.revision}\0${family.classId}` === family.familyId);
    const matchesTarget = (source: AssetExplorerSource) => source.game === target.game
      && source.region === target.region && source.revision === target.revision;
    const soundPriority = (source: AssetExplorerSource) => !source.mobySounds?.slots.length ? 2
      : source.mobySounds.slots.some((slot) => slot.nativeProgramIndex >= 0 && !slot.soundAssetId) ? 1 : 0;
    const source = sources.find((value) => matchesTarget(value) && value.level === level)
      ?? sources.find(matchesTarget) ?? [...sources].sort((left, right) => soundPriority(left) - soundPriority(right)
        || left.level.localeCompare(right.level) || left.archive.localeCompare(right.archive) || left.sourceIndex - right.sourceIndex)[0];
    if (family.category === 'mobys' && source) family.representativeSource = assetSourceSelection(source);
  }
  return [...families.values()].sort((left, right) =>
    (left.classId ?? Number.MAX_SAFE_INTEGER) - (right.classId ?? Number.MAX_SAFE_INTEGER)
    || left.familyId.localeCompare(right.familyId));
}

export function resolveAssetFamilyVariant(family: AssetExplorerFamily, selectedAssetId?: string): AssetExplorerItem {
  return family.variants.find((variant) => variant.assetId === selectedAssetId)
    ?? family.variants.find((variant) => variant.assetId === family.representativeAssetId)
    ?? family.variants[0];
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
  const prefix = ({
    ties: 'tie', shrubs: 'shrub', mobys: 'moby', tfrags: 'tfrag', skyShells: 'sky', textures: 'texture',
  })[category];
  return `${prefix}:0x${classId.toString(16).toUpperCase().padStart(4, '0')}`;
}
