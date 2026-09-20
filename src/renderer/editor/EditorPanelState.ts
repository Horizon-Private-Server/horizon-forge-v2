import type { EditorEntity } from '../../types/EditorRuntime.js';

export const MAX_VISIBLE_TREE_ENTITIES = 1_000;

export interface SceneEntityGroup {
  layer: string;
  entities: EditorEntity[];
}

export function buildTerrainTreeItems(urls: readonly string[], filter: string): { value: string; label: string }[] {
  const query = filter.trim().toLocaleLowerCase();
  return urls.map((value) => {
    const path = decodeURIComponent(new URL(value).pathname);
    const chunk = path.match(/\/chunks\/chunk([^/]+)\/tfrag\.gltf$/i)?.[1];
    return { value, label: chunk ? `Chunk ${chunk} tfrag` : 'Primary tfrag' };
  }).filter((item) => !query || `${item.label} tfrags terrain`.toLocaleLowerCase().includes(query));
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
  limit = MAX_VISIBLE_TREE_ENTITIES,
): { groups: SceneEntityGroup[]; matched: number; shown: number } {
  const query = filter.trim().toLocaleLowerCase();
  const groups = new Map<string, EditorEntity[]>();
  let matched = 0;
  let shown = 0;
  for (const entity of entities) {
    if (query && !`${entity.name} ${entity.layer} ${entity.asset?.kind ?? ''}`.toLocaleLowerCase().includes(query)) continue;
    matched += 1;
    if (shown >= limit) continue;
    const group = groups.get(entity.layer);
    if (group) group.push(entity);
    else groups.set(entity.layer, [entity]);
    shown += 1;
  }
  return {
    groups: [...groups].sort(([left], [right]) => left.localeCompare(right))
      .map(([layer, values]) => ({ layer, entities: values })),
    matched,
    shown,
  };
}

export function entityStateLabel(entity: EditorEntity): string {
  const states = [
    entity.state.dirty && 'dirty',
    entity.state.hidden && 'hidden',
    entity.state.disabled && 'disabled',
    entity.state.locked && 'locked',
    entity.state.invalid && 'invalid',
    entity.state.missingAsset && 'missing asset',
  ].filter(Boolean);
  return states.length ? `${entity.name} [${states.join(', ')}]` : entity.name;
}
