import type {
  EditorEntity,
  EditorFx,
  EditorReference,
} from '../../types/EditorRuntime.js';
import type {
  MobySimulationInvalidation,
  MobySimulationProjectSnapshot,
} from '../../types/MobySimulation.js';

export function buildMobySimulationSnapshot(
  projectId: string,
  revision: number,
  game: string,
  entities: readonly EditorEntity[],
  references: readonly EditorReference[],
  supportsClass: (classId: number) => boolean,
): MobySimulationProjectSnapshot {
  const referencesByOwner = new Map<string, EditorReference[]>();
  for (const reference of references) {
    const owners = referencesByOwner.get(reference.ownerEntityId);
    if (owners) owners.push(reference);
    else referencesByOwner.set(reference.ownerEntityId, [reference]);
  }
  const simulationEntities = entities
    .filter((entity) => entity.layer === 'mobys' && entity.sourceClassId !== undefined
      && supportsClass(entity.sourceClassId))
    .map((entity) => ({
      id: entity.id,
      classId: entity.sourceClassId!,
      transform: {
        position: { ...entity.transform.position },
        rotation: { ...entity.transform.rotation },
        scale: { ...entity.transform.scale },
      },
      pvar: entity.mobyPVar?.rawData?.slice(),
      pvarFields: flattenPVarFields(entity.mobyPVar?.fields ?? []),
      references: (referencesByOwner.get(entity.id) ?? []).map(cloneReference),
      hidden: entity.state.hidden,
      disabled: entity.state.disabled,
      assetId: entity.asset?.id,
    }));
  const referenceTargetIds = new Set(simulationEntities.flatMap((entity) => [
    ...entity.pvarFields.flatMap((field) => field.targetEntityId ? [field.targetEntityId] : []),
    ...entity.references.flatMap((reference) => reference.domain === 'entity' && reference.targetEntityId
      ? [reference.targetEntityId] : []),
  ]));
  const referenceTargets = entities
    .filter((entity) => entity.geometry && referenceTargetIds.has(entity.id))
    .map((entity) => ({
      id: entity.id,
      kind: entity.geometry!.kind,
      transform: {
        position: { ...entity.transform.position },
        rotation: { ...entity.transform.rotation },
        scale: { ...entity.transform.scale },
      },
      hidden: entity.state.hidden,
      disabled: entity.state.disabled,
    }));
  return {
    projectId,
    revision,
    game,
    entities: simulationEntities,
    referenceTargets,
  };
}

export function diffMobySimulationSnapshots(
  previous: MobySimulationProjectSnapshot,
  next: MobySimulationProjectSnapshot,
): MobySimulationInvalidation[] {
  const prior = new Map(previous.entities.map((entity) => [entity.id, entity]));
  const current = new Map(next.entities.map((entity) => [entity.id, entity]));
  const changes = new Map<MobySimulationInvalidation['kind'], Set<string>>();
  const changed = (kind: MobySimulationInvalidation['kind'], entityId: string) => {
    const ids = changes.get(kind);
    if (ids) ids.add(entityId);
    else changes.set(kind, new Set([entityId]));
  };
  for (const entityId of new Set([...prior.keys(), ...current.keys()])) {
    const before = prior.get(entityId);
    const after = current.get(entityId);
    if (!before || !after || before.classId !== after.classId) {
      changed('visibility', entityId);
      continue;
    }
    if (!sameTransform(before.transform, after.transform)) changed('transform', entityId);
    if (!sameBytes(before.pvar, after.pvar)
      || JSON.stringify(before.pvarFields) !== JSON.stringify(after.pvarFields)) changed('pvar', entityId);
    if (JSON.stringify(before.references) !== JSON.stringify(after.references)) changed('reference', entityId);
    if (before.assetId !== after.assetId) changed('asset', entityId);
    if (before.hidden !== after.hidden || before.disabled !== after.disabled) changed('visibility', entityId);
  }
  return [...changes].map(([kind, entityIds]) => ({ kind, entityIds: [...entityIds] }));
}

export function mobySimulationFxSignature(fx?: EditorFx): string {
  if (!fx) return '';
  return JSON.stringify([
    fx.sourceTextures.map((texture) => [texture.sourceIndex, texture.effectiveTexture?.id ?? texture.sourceTexture?.id]),
    fx.additions.map((texture) => texture.texture.id),
  ]);
}

export function resolveMobySimulationFxAssetId(fx: EditorFx | undefined, fxId: number): string | undefined {
  const source = fx?.sourceTextures.find((texture) => texture.sourceIndex === fxId);
  if (source) return source.effectiveTexture?.id ?? source.sourceTexture?.id;
  const appendStart = fx?.sourceTextures.reduce((maximum, texture) => Math.max(maximum, texture.sourceIndex + 1), 0) ?? 0;
  return fx?.additions[fxId - appendStart]?.texture.id;
}

function cloneReference(reference: EditorReference): EditorReference {
  return { ...reference };
}

function flattenPVarFields(
  fields: readonly NonNullable<EditorEntity['mobyPVar']>['fields'][number][],
): MobySimulationProjectSnapshot['entities'][number]['pvarFields'] {
  return fields.flatMap((field) => [
    {
      path: field.path,
      invalid: field.invalid,
      value: field.value ? structuredClone(field.value) : undefined,
      targetEntityId: field.reference?.targetEntityId,
    },
    ...flattenPVarFields(field.children),
  ]);
}

function sameTransform(
  left: MobySimulationProjectSnapshot['entities'][number]['transform'],
  right: MobySimulationProjectSnapshot['entities'][number]['transform'],
): boolean {
  return left.position.x === right.position.x && left.position.y === right.position.y
    && left.position.z === right.position.z && left.rotation.x === right.rotation.x
    && left.rotation.y === right.rotation.y && left.rotation.z === right.rotation.z
    && left.rotation.w === right.rotation.w && left.scale.x === right.scale.x
    && left.scale.y === right.scale.y && left.scale.z === right.scale.z;
}

function sameBytes(left?: Readonly<Uint8Array>, right?: Readonly<Uint8Array>): boolean {
  if (!left || !right) return left === right;
  return left.length === right.length && left.every((value, index) => value === right[index]);
}
