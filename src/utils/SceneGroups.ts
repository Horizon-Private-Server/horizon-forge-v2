import type { EditorEntity, EditorGroup, EditorMapGroup, EditorReference } from '../types/EditorRuntime.js';
import { editorEntityDisplayName } from './EntityDisplay.ts';

type EditorEntityReference = Extract<EditorReference, { domain: 'entity' }>;

export interface SceneSemanticMember {
  entityId: string;
  entity?: EditorEntity;
  missing: boolean;
}

export interface SceneSemanticGroup {
  id: string;
  name: string;
  members: SceneSemanticMember[];
}

export interface SceneAreaGroup {
  area: EditorEntity;
  members: SceneSemanticMember[];
}

const SCENE_GROUP_PAGE_SIZE = 250;

export function buildSceneSemanticGroups(
  groups: readonly EditorGroup[],
  entities: readonly EditorEntity[],
  filter: string,
): { groups: SceneSemanticGroup[]; matched: number } {
  const query = filter.trim().toLocaleLowerCase();
  const entitiesById = new Map(entities.map((entity) => [entity.id, entity]));
  const values = groups.map((group) => {
    const groupMatches = !query || group.name.toLocaleLowerCase().includes(query);
    const members = group.members.map((entityId): SceneSemanticMember => {
      const entity = entitiesById.get(entityId);
      return { entityId, entity, missing: !entity };
    }).filter((member) => groupMatches || semanticMemberMatches(member, query));
    return { id: group.id, name: group.name, members };
  }).filter((group) => group.members.length > 0 || !query || group.name.toLocaleLowerCase().includes(query));
  return { groups: values, matched: values.reduce((total, group) => total + group.members.length, 0) };
}

export function buildSceneAreaGroups(
  entities: readonly EditorEntity[],
  references: readonly EditorReference[],
  filter: string,
): { groups: SceneAreaGroup[]; matched: number } {
  const query = filter.trim().toLocaleLowerCase();
  const entitiesById = new Map(entities.map((entity) => [entity.id, entity]));
  const referencesByArea = new Map<string, EditorEntityReference[]>();
  for (const reference of references) {
    if (reference.domain !== 'entity' || entitiesById.get(reference.ownerEntityId)?.geometry?.kind !== 'area') continue;
    const values = referencesByArea.get(reference.ownerEntityId);
    if (values) values.push(reference);
    else referencesByArea.set(reference.ownerEntityId, [reference]);
  }
  const groups = entities.filter((entity) => entity.geometry?.kind === 'area').flatMap((area) => {
    const areaMatches = !query || semanticEntityMatches(area, query);
    const seen = new Set<string>();
    const members = (referencesByArea.get(area.id) ?? []).flatMap((reference): SceneSemanticMember[] => {
      const entityId = reference.targetEntityId
        ?? `reference:${reference.fieldKey}:${reference.sourceValue ?? 'unknown'}`;
      if (seen.has(entityId)) return [];
      seen.add(entityId);
      const entity = reference.targetEntityId ? entitiesById.get(reference.targetEntityId) : undefined;
      const member = { entityId, entity, missing: reference.missing || !entity };
      return areaMatches || semanticMemberMatches(member, query) ? [member] : [];
    });
    return areaMatches || members.length > 0 ? [{ area, members }] : [];
  });
  return {
    groups,
    matched: groups.length + groups.reduce((total, group) => total + group.members.length, 0),
  };
}

export function sceneGroupValue(groupId: string): string {
  return `group:${groupId}`;
}

export function sceneGroupMemberValue(groupId: string, entityId: string): string {
  return `${sceneGroupValue(groupId)}:member:${entityId}`;
}

export function sceneGroupMemberIds(groups: readonly SceneSemanticGroup[]): Map<string, string> {
  const values = new Map<string, string>();
  for (const group of groups)
    for (const member of group.members)
      if (member.entity) values.set(sceneGroupMemberValue(group.id, member.entityId), member.entityId);
  return values;
}

export function sceneGroupDropTargets(groups: readonly SceneSemanticGroup[]): Map<string, string> {
  const values = new Map<string, string>();
  for (const group of groups) {
    const target = sceneGroupValue(group.id);
    values.set(target, target);
    for (const member of group.members) values.set(
      member.entity
        ? sceneGroupMemberValue(group.id, member.entityId)
        : `missing:${group.id}:${member.entityId}`,
      target,
    );
  }
  return values;
}

export function sceneGroupSelectedValues(
  selection: readonly string[],
  memberIds: ReadonlyMap<string, string>,
  groups: readonly SceneSemanticGroup[] = [],
): string[] {
  const selected = new Set(selection);
  const memberValues = [...memberIds].filter(([, entityId]) => selected.has(entityId)).map(([value]) => value);
  const groupValues = groups.flatMap((group) => {
    const known = group.members.filter((member) => member.entity).map((member) => member.entityId);
    return known.length > 0 && known.every((entityId) => selected.has(entityId))
      ? [sceneGroupValue(group.id)] : [];
  });
  return [...memberValues, ...groupValues];
}

export function sceneGroupSelection(
  values: readonly string[],
  groups: readonly SceneSemanticGroup[],
  memberIds: ReadonlyMap<string, string>,
  changedValue?: string,
): string[] {
  const groupByValue = new Map(groups.map((group) => [sceneGroupValue(group.id), group]));
  const selected = new Set(values.flatMap((value) => {
    const group = groupByValue.get(value);
    if (group) return group.members.filter((member) => member.entity).map((member) => member.entityId);
    const entityId = memberIds.get(value);
    return entityId ? [entityId] : [];
  }));
  const changedEntityId = changedValue ? memberIds.get(changedValue) : undefined;
  if (changedEntityId && changedValue !== undefined && !values.includes(changedValue))
    selected.delete(changedEntityId);
  const changedGroup = changedValue ? groupByValue.get(changedValue) : undefined;
  if (changedGroup && changedValue !== undefined && !values.includes(changedValue))
    changedGroup.members.forEach((member) => selected.delete(member.entityId));
  return [...selected];
}

export function selectedMissingGroupMemberCount(
  selection: readonly string[],
  entities: readonly EditorEntity[],
  groups: readonly EditorGroup[],
  mapGroups: readonly EditorMapGroup[],
  references: readonly EditorReference[] = [],
): number {
  if (selection.length === 0) return 0;
  const selected = new Set(selection);
  const known = new Set(entities.map((entity) => entity.id));
  const missing = new Set<string>();
  for (const group of groups) {
    const presentMembers = group.members.filter((member) => known.has(member));
    if (presentMembers.length > 0 && presentMembers.every((member) => selected.has(member)))
      group.missingMembers.forEach((member) => missing.add(`custom:${member}`));
  }
  for (const group of mapGroups) {
    if (group.members.length > 0 && group.members.every((member) => selected.has(member)))
      group.missingSourceIndices.forEach((member) => missing.add(`map:${group.kind}:${group.sourceIndex}:${member}`));
  }
  for (const group of buildSceneAreaGroups(entities, references, '').groups) {
    const presentMembers = group.members.filter((member) => member.entity);
    if (presentMembers.length > 0 && presentMembers.every((member) => selected.has(member.entityId)))
      group.members.filter((member) => member.missing)
        .forEach((member) => missing.add(`area:${group.area.id}:${member.entityId}`));
  }
  return missing.size;
}

export function sceneGroupPage<T>(values: readonly T[], requestedPage: number): {
  values: readonly T[];
  page: number;
  pageCount: number;
  start: number;
  end: number;
} {
  const pageCount = Math.max(1, Math.ceil(values.length / SCENE_GROUP_PAGE_SIZE));
  const page = Math.max(0, Math.min(requestedPage, pageCount - 1));
  const start = page * SCENE_GROUP_PAGE_SIZE;
  const end = Math.min(values.length, start + SCENE_GROUP_PAGE_SIZE);
  return { values: values.slice(start, end), page, pageCount, start, end };
}

function semanticMemberMatches(member: SceneSemanticMember, query: string): boolean {
  if (!member.entity) return `${member.entityId} missing invalid`.toLocaleLowerCase().includes(query);
  return semanticEntityMatches(member.entity, query);
}

function semanticEntityMatches(entity: EditorEntity, query: string): boolean {
  const states = [
    entity.state.dirty && 'dirty',
    entity.state.hidden && 'hidden',
    entity.state.disabled && 'disabled',
    entity.state.locked && 'locked',
    entity.state.readOnly && 'read-only',
    entity.state.invalid && 'invalid',
    entity.state.missingAsset && 'missing asset',
  ].filter(Boolean).join(' ');
  return `${editorEntityDisplayName(entity)} ${entity.name} ${entity.sourceClassName ?? ''} ${entity.layer} ${entity.id} ${states}`
    .toLocaleLowerCase().includes(query);
}
