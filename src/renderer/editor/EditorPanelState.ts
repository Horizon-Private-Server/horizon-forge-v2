import type { EditorEntity, EditorEntityKind, EditorFx, EditorHud, EditorReference } from '../../types/EditorRuntime.js';
import type {
  AssetExplorerFamily,
  AssetExplorerItem,
  AssetExplorerPage,
  AssetExplorerQuery,
} from '../../types/AssetExplorer.js';

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

export interface ReferenceIndex {
  incoming: ReadonlyMap<string, readonly EditorReference[]>;
  outgoing: ReadonlyMap<string, readonly EditorReference[]>;
}

export function buildReferenceIndex(references: readonly EditorReference[]): ReferenceIndex {
  const incoming = new Map<string, EditorReference[]>();
  const outgoing = new Map<string, EditorReference[]>();
  for (const reference of references) {
    append(outgoing, reference.ownerEntityId, reference);
    if (reference.domain === 'entity' && reference.targetEntityId)
      append(incoming, reference.targetEntityId, reference);
  }
  return { incoming, outgoing };
}

export function referencePage<T>(values: readonly T[], page: number, size = 100): {
  values: readonly T[];
  page: number;
  pageCount: number;
} {
  const pageCount = Math.max(1, Math.ceil(values.length / size));
  const boundedPage = Math.max(0, Math.min(page, pageCount - 1));
  return { values: values.slice(boundedPage * size, (boundedPage + 1) * size), page: boundedPage, pageCount };
}

export function editorEntityKind(entity: EditorEntity): EditorEntityKind {
  if (entity.geometry) return entity.geometry.kind;
  if (entity.collision) return 'collision';
  if (entity.skyShell) return 'skyShell';
  if (entity.asset?.kind === 'Moby') return 'moby';
  if (entity.asset?.kind === 'Tie') return 'tie';
  if (entity.asset?.kind === 'Shrub') return 'shrub';
  if (entity.asset?.kind === 'Tfrag') return 'tfrag';
  return 'entity';
}

export function compatibleReferenceTargets(
  reference: Extract<EditorReference, { domain: 'entity' }>,
  entities: readonly EditorEntity[],
): EditorEntity[] {
  return entities.filter((entity) => reference.fieldKey === 'collision.attachment'
    ? entity.asset?.kind === 'Tie' || entity.asset?.kind === 'Shrub'
    : reference.targetKind === 'entity' || editorEntityKind(entity) === reference.targetKind);
}

export function referenceNavigationTarget(
  reference: EditorReference,
  direction: 'incoming' | 'outgoing',
): { domain: 'entity' | 'asset'; id?: string; kind?: string } {
  if (direction === 'incoming') return { domain: 'entity', id: reference.ownerEntityId };
  return reference.domain === 'entity'
    ? { domain: 'entity', id: reference.targetEntityId, kind: reference.targetKind }
    : { domain: 'asset', id: reference.targetAssetId, kind: reference.targetKind };
}

function append(map: Map<string, EditorReference[]>, key: string, value: EditorReference): void {
  const values = map.get(key);
  if (values) values.push(value);
  else map.set(key, [value]);
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
  return virtualGridWindow(itemCount, width, height, scrollTop, 168, 218);
}

export function virtualGridWindow(
  itemCount: number,
  width: number,
  height: number,
  scrollTop: number,
  minimumCardWidth: number,
  rowHeight: number,
): AssetGridWindow {
  const gap = 8;
  const columns = Math.max(1, Math.floor((Math.max(minimumCardWidth, width) + gap) / minimumCardWidth));
  const rows = Math.ceil(itemCount / columns);
  const startRow = Math.max(0, Math.floor(Math.max(0, scrollTop) / rowHeight) - 2);
  const endRow = Math.min(rows, Math.ceil((Math.max(0, scrollTop) + Math.max(1, height)) / rowHeight) + 2);
  return {
    columns,
    cardWidth: Math.max(1, (Math.max(minimumCardWidth, width) - gap * (columns - 1)) / columns),
    startIndex: Math.min(itemCount, startRow * columns),
    endIndex: Math.min(itemCount, endRow * columns),
    totalHeight: Math.max(0, rows * rowHeight - gap),
  };
}

export function buildHudBankItems(
  hud: EditorHud,
  search: string,
  filter: 'all' | 'changed' | 'issues',
) {
  const items = [
    ...hud.sourceIcons.flatMap((icon) => icon.frames.map((frame) => ({
      key: `source:${icon.sourceIconIndex}:${frame.sourceFrameIndex}`,
      kind: 'frame' as const,
      spriteId: icon.spriteId,
      sourceIconIndex: icon.sourceIconIndex,
      sourceFrameIndex: frame.sourceFrameIndex,
      sourcePaletteIndex: frame.sourcePaletteIndex,
      sourceTextureIndex: frame.sourceTextureIndex,
      paletteBankIndex: frame.paletteBankIndex,
      textureBankIndex: frame.textureBankIndex,
      width: frame.width,
      height: frame.height,
      sourceAssetId: frame.sourceTexture?.id,
      previewAssetId: frame.effectiveTexture?.id ?? frame.sourceTexture?.id,
      state: frame.diagnostic || !frame.sourceTexture || !frame.effectiveTexture
        ? 'invalid' as const
        : frame.effectiveTexture.id !== frame.sourceTexture.id ? 'override' as const : 'source' as const,
      diagnostic: frame.diagnostic ?? (!frame.sourceTexture || !frame.effectiveTexture
        ? 'Texture dependency is missing.' : undefined),
    }))),
    ...hud.additions.map((addition, index) => ({
      key: `addition:${addition.spriteId}:${index}`,
      kind: 'addition' as const,
      spriteId: addition.spriteId,
      sourceIconIndex: undefined,
      sourceFrameIndex: undefined,
      sourcePaletteIndex: undefined,
      sourceTextureIndex: undefined,
      paletteBankIndex: addition.bankIndex,
      textureBankIndex: addition.bankIndex,
      width: addition.width,
      height: addition.height,
      sourceAssetId: undefined,
      previewAssetId: addition.texture.id,
      state: 'new' as const,
      diagnostic: undefined,
    })),
  ];
  const query = search.trim().toLocaleLowerCase();
  return items.filter((item) => {
    if (filter === 'changed' && item.state !== 'override' && item.state !== 'new') return false;
    if (filter === 'issues' && item.state !== 'invalid') return false;
    return !query || [
      formatHudSpriteId(item.spriteId), item.kind, item.state,
      item.sourceIconIndex, item.sourceFrameIndex, item.sourcePaletteIndex, item.sourceTextureIndex,
      item.paletteBankIndex, item.textureBankIndex, item.previewAssetId, item.diagnostic,
    ].some((value) => String(value ?? '').toLocaleLowerCase().includes(query));
  });
}

export function buildFxTextureItems(
  fx: EditorFx,
  search: string,
  filter: 'all' | 'changed' | 'issues',
) {
  const items = [
    ...fx.sourceTextures.map((texture) => ({
      key: `source:${texture.sourceIndex}`,
      kind: 'source' as const,
      index: texture.sourceIndex,
      label: texture.label,
      width: texture.width,
      height: texture.height,
      paletteOffset: texture.paletteOffset,
      pixelOffset: texture.pixelOffset,
      isSwizzled: texture.isSwizzled,
      sourceAssetId: texture.sourceTexture?.id,
      previewAssetId: texture.effectiveTexture?.id ?? texture.sourceTexture?.id,
      state: texture.diagnostic || !texture.sourceTexture || !texture.effectiveTexture
        ? 'invalid' as const
        : texture.effectiveTexture.id !== texture.sourceTexture.id ? 'override' as const : 'source' as const,
      diagnostic: texture.diagnostic ?? (!texture.sourceTexture || !texture.effectiveTexture
        ? 'Texture dependency is missing.' : undefined),
    })),
    ...fx.additions.map((addition, offset) => ({
      key: `addition:${fx.sourceTextures.length + offset}`,
      kind: 'addition' as const,
      index: fx.sourceTextures.length + offset,
      label: `FX_TEXTURE_${fx.sourceTextures.length + offset}`,
      width: addition.width,
      height: addition.height,
      paletteOffset: undefined,
      pixelOffset: undefined,
      isSwizzled: false,
      sourceAssetId: undefined,
      previewAssetId: addition.texture.id,
      state: 'new' as const,
      diagnostic: undefined,
    })),
  ];
  const query = search.trim().toLocaleLowerCase();
  return items.filter((item) => {
    if (filter === 'changed' && item.state !== 'override' && item.state !== 'new') return false;
    if (filter === 'issues' && item.state !== 'invalid') return false;
    return !query || [
      item.index, item.label, item.kind, item.state, item.previewAssetId, item.diagnostic,
    ].some((value) => String(value ?? '').toLocaleLowerCase().includes(query));
  });
}

export function formatHudSpriteId(value: number): string {
  return value.toString(16).toUpperCase().padStart(4, '0');
}

export function hudThumbnailDimensions(width: number, height: number): { width: number; height: number } {
  if (width <= 0 || height <= 0) return { width: 1, height: 1 };
  const scale = Math.min(2, 300 / width, 150 / height);
  return { width: Math.round(width * scale), height: Math.round(height * scale) };
}

export function nextHudSpriteId(hud: EditorHud, minimum: number, maximum: number): number | undefined {
  const occupied = new Set([
    ...hud.sourceIcons.map((icon) => icon.spriteId),
    ...hud.additions.map((addition) => addition.spriteId),
  ]);
  for (let value = minimum; value <= maximum; value += 1) if (!occupied.has(value)) return value;
  return undefined;
}

export function validateHudPng(bytes: Uint8Array): {
  valid: boolean;
  width: number;
  height: number;
  diagnostic?: string;
} {
  return validateTexturePng(bytes, 1_024, 'HUD');
}

export function validateFxPng(bytes: Uint8Array): {
  valid: boolean;
  width: number;
  height: number;
  diagnostic?: string;
} {
  return validateTexturePng(bytes, 4_096, 'FX');
}

function validateTexturePng(bytes: Uint8Array, maximumDimension: number, family: string): {
  valid: boolean;
  width: number;
  height: number;
  diagnostic?: string;
} {
  if (bytes.byteLength === 0 || bytes.byteLength > 16 * 1024 * 1024)
    return { valid: false, width: 0, height: 0, diagnostic: 'PNG must be no larger than 16 MiB.' };
  if (bytes.byteLength < 24 || ![0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]
    .every((value, index) => bytes[index] === value))
    return { valid: false, width: 0, height: 0, diagnostic: 'Choose a valid PNG image.' };
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const width = view.getUint32(16);
  const height = view.getUint32(20);
  if (width === 0 || height === 0 || width > maximumDimension || height > maximumDimension
    || (width & (width - 1)) !== 0 || (height & (height - 1)) !== 0)
    return {
      valid: false, width, height,
      diagnostic: `${family} dimensions must be powers of two no larger than ${maximumDimension} × ${maximumDimension}.`,
    };
  return { valid: true, width, height };
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
