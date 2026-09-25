import type { EditorEntity } from '../../types/EditorRuntime.js';

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
