import type { EditorEntity } from '../../types/EditorRuntime.js';
import type { AssetExplorerFamily, AssetExplorerItem, AssetExplorerQuery } from '../../types/AssetExplorer.js';

export interface SceneEntityGroup {
  layer: string;
  entities: EditorEntity[];
}

export interface AssetGridWindow {
  columns: number;
  cardWidth: number;
  startIndex: number;
  endIndex: number;
  totalHeight: number;
}

export function assetExplorerFilterCount(
  query: Pick<AssetExplorerQuery, 'game' | 'level' | 'region' | 'revision' | 'tags'>,
): number {
  return [query.game, query.level, query.region, query.revision].filter(Boolean).length + (query.tags?.length ?? 0);
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

export function assetGridWindow(
  itemCount: number,
  width: number,
  height: number,
  scrollTop: number,
): AssetGridWindow {
  const gap = 8;
  const rowHeight = 218;
  const columns = Math.max(1, Math.floor((Math.max(160, width) + gap) / 168));
  const rows = Math.ceil(itemCount / columns);
  const startRow = Math.max(0, Math.floor(Math.max(0, scrollTop) / rowHeight) - 2);
  const endRow = Math.min(rows, Math.ceil((Math.max(0, scrollTop) + Math.max(1, height)) / rowHeight) + 2);
  return {
    columns,
    cardWidth: Math.max(1, (Math.max(160, width) - gap * (columns - 1)) / columns),
    startIndex: Math.min(itemCount, startRow * columns),
    endIndex: Math.min(itemCount, endRow * columns),
    totalHeight: Math.max(0, rows * rowHeight - gap),
  };
}

export function buildTerrainTreeItems(urls: readonly string[], filter: string): { value: string; label: string }[] {
  const query = filter.trim().toLocaleLowerCase();
  return urls.map((value) => {
    const path = decodeURIComponent(new URL(value).pathname);
    const chunk = path.match(/\/chunks\/chunk([^/]+)\/tfrag\.gltf$/i)?.[1];
    return { value, label: chunk ? `Chunk ${chunk} tfrag` : 'Primary tfrag' };
  }).filter((item) => !query || `${item.label} tfrags terrain`.toLocaleLowerCase().includes(query));
}

export function buildSkyTreeItems(names: readonly string[], filter: string): { value: string; label: string }[] {
  const query = filter.trim().toLocaleLowerCase();
  return names.map((name, index) => {
    const shell = name.match(/^skybox_shell_(\d+)$/i)?.[1];
    return { value: `render:sky:${index}`, label: shell ? `Sky shell ${Number(shell)}` : name };
  }).filter((item) => !query || `${item.label} sky`.toLocaleLowerCase().includes(query));
}

export function nextTreeSelection(
  selected: readonly string[],
  value: string,
  orderedValues: readonly string[],
  anchor: string | undefined,
  modifiers: { toggle: boolean; range: boolean },
): { selected: string[]; anchor: string } {
  if (modifiers.range && anchor) {
    const anchorIndex = orderedValues.indexOf(anchor);
    const valueIndex = orderedValues.indexOf(value);
    if (anchorIndex >= 0 && valueIndex >= 0) {
      const range = orderedValues.slice(Math.min(anchorIndex, valueIndex), Math.max(anchorIndex, valueIndex) + 1);
      return { selected: modifiers.toggle ? [...new Set([...selected, ...range])] : range, anchor };
    }
  }
  if (modifiers.toggle) {
    return {
      selected: selected.includes(value) ? selected.filter((candidate) => candidate !== value) : [...selected, value],
      anchor: value,
    };
  }
  return { selected: [value], anchor: value };
}

export function nextViewportSelection(
  selected: readonly string[],
  value: string | undefined,
  modifiers: { toggle: boolean; add: boolean },
): string[] {
  if (!value) return modifiers.toggle || modifiers.add ? [...selected] : [];
  if (modifiers.toggle) {
    return selected.includes(value) ? selected.filter((candidate) => candidate !== value) : [...selected, value];
  }
  if (modifiers.add) return selected.includes(value) ? [...selected] : [...selected, value];
  return [value];
}

export function buildSceneEntityGroups(
  entities: readonly EditorEntity[],
  filter: string,
): { groups: SceneEntityGroup[]; matched: number } {
  const query = filter.trim().toLocaleLowerCase();
  const groups = new Map<string, EditorEntity[]>();
  for (const entity of entities) {
    if (query && !`${entity.name} ${entity.layer} ${entity.asset?.kind ?? ''} ${entity.geometry?.kind ?? ''}`
      .toLocaleLowerCase().includes(query)) continue;
    const group = groups.get(entity.layer);
    if (group) group.push(entity);
    else groups.set(entity.layer, [entity]);
  }
  const ordered = [...groups].sort(([left], [right]) => left.localeCompare(right));
  return {
    groups: ordered.map(([layer, values]) => ({ layer, entities: values })),
    matched: ordered.reduce((total, [, values]) => total + values.length, 0),
  };
}

export function entityStateLabel(entity: EditorEntity): string {
  const states = [
    entity.state.dirty && 'dirty',
    entity.state.hidden && 'hidden',
    entity.state.disabled && 'disabled',
    entity.state.locked && 'locked',
    entity.state.readOnly && 'read-only',
    entity.state.invalid && 'invalid',
    entity.state.missingAsset && 'missing asset',
  ].filter(Boolean);
  return states.length ? `${entity.name} [${states.join(', ')}]` : entity.name;
}

export function entityTreeKind(entity: EditorEntity): string {
  if (entity.geometry) return entity.geometry.kind;
  if (entity.asset?.kind) return entity.asset.kind.toLocaleLowerCase();
  return entity.layer.toLocaleLowerCase().replace(/s$/, '');
}

export function entityTreeText(entity: EditorEntity): string {
  const label = entityStateLabel(entity);
  const prefix = `${entityTreeKind(entity)} `;
  if (!label.toLocaleLowerCase().startsWith(prefix)) return label;
  const remainder = label.slice(prefix.length);
  return /^0x[\da-f]+ #\d+(?:\s|$)/i.test(remainder) ? remainder : label;
}
