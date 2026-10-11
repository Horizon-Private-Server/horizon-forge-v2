import type {
  EditorEntity, EditorEntityKind, EditorLevelReference, EditorReference,
} from '../types/EditorRuntime.js';
import { editorEntityKind } from './EditorReferences.ts';

export function levelReferenceCandidates(
  entities: readonly EditorEntity[],
  targetKind: 'spline' | 'cuboid',
  section: 'gameplay/core/splines' | 'gameplay/core/cuboids',
): EditorEntity[] {
  return entities.filter((entity) => entity.geometry?.kind === targetKind
      && entity.provenance?.game === 'UYA'
      && entity.provenance.section === section
      && Number.isInteger(entity.provenance.sourceIndex)
      && entity.provenance.sourceIndex >= 0)
    .sort((left, right) => left.provenance!.sourceIndex - right.provenance!.sourceIndex
      || left.id.localeCompare(right.id));
}

export function levelReferenceState(
  ownerEntityId: string,
  fieldKey: string,
  targetKind: 'spline' | 'cuboid',
  value: EditorLevelReference,
  candidates: readonly EditorEntity[],
  entities: readonly EditorEntity[],
): {
  reference: Extract<EditorReference, { domain: 'entity' }>;
  diagnostic?: string;
} {
  const candidate = candidates.find((entity) => entity.id === value.entityId);
  const target = value.entityId ? entities.find((entity) => entity.id === value.entityId) : undefined;
  let diagnostic: string | undefined;
  if (value.entityId && !target) diagnostic = 'The referenced entity is missing.';
  else if (target && editorEntityKind(target) !== targetKind)
    diagnostic = `The referenced entity is ${kindLabel(editorEntityKind(target))}; expected ${kindLabel(targetKind)}.`;
  else if (target && !candidate)
    diagnostic = `The referenced ${kindLabel(targetKind)} is not backed by the required UYA source section.`;
  else if (!value.entityId && value.sourceValue >= 0)
    diagnostic = `Source index ${value.sourceValue} could not be resolved.`;
  return {
    reference: {
      ownerEntityId,
      domain: 'entity',
      fieldKey,
      nullable: true,
      targetKind,
      targetEntityId: value.entityId,
      sourceValue: value.sourceValue,
      missing: !candidate && (Boolean(value.entityId) || value.sourceValue >= 0),
      datasetSource: 'UYA level settings',
    },
    diagnostic,
  };
}

export function levelReferenceValue(
  entityId: string | undefined,
  candidates: readonly EditorEntity[],
): EditorLevelReference | undefined {
  if (!entityId) return { sourceValue: -1 };
  const target = candidates.find((entity) => entity.id === entityId);
  return target ? { sourceValue: target.provenance!.sourceIndex, entityId: target.id } : undefined;
}

function kindLabel(kind: EditorEntityKind): string {
  return kind === 'grindPath' ? 'grind path' : kind.replace(/([A-Z])/g, ' $1').toLocaleLowerCase();
}
