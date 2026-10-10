import type { EditorEntity, EditorEntityKind, EditorReference } from '../types/EditorRuntime.js';

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
  const kind = entity.asset?.kind.toLocaleLowerCase();
  const layer = entity.layer.toLocaleLowerCase();
  if (layer === 'mobys' || kind === 'moby') return 'moby';
  if (layer === 'ties' || kind === 'tie') return 'tie';
  if (layer === 'shrubs' || kind === 'shrub') return 'shrub';
  if (layer === 'tfrags' || kind === 'tfrag') return 'tfrag';
  return 'entity';
}

export function compatibleReferenceTargets(
  reference: Extract<EditorReference, { domain: 'entity' }>,
  entities: readonly EditorEntity[],
): EditorEntity[] {
  return entities.filter((entity) => reference.fieldKey === 'collision.attachment'
    ? editorEntityKind(entity) === 'tie' || editorEntityKind(entity) === 'shrub'
    : reference.targetKind === 'entity' || editorEntityKind(entity) === reference.targetKind);
}

export function compatibleReferenceDropTarget(
  entityId: string | undefined,
  candidates: readonly EditorEntity[],
): string | undefined {
  return entityId !== undefined && candidates.some((entity) => entity.id === entityId) ? entityId : undefined;
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
