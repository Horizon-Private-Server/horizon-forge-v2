import type { EditorCommandResult, EditorSnapshot } from '../types/EditorRuntime.js';

export function applyEditorCommandResult(
  snapshot: EditorSnapshot,
  result: EditorCommandResult,
): EditorSnapshot {
  if (result.kind === 'snapshot') return result.snapshot;
  const status = {
    isDirty: result.isDirty,
    canUndo: result.canUndo,
    canRedo: result.canRedo,
    lastEventSequence: result.lastEventSequence,
  };
  if (result.kind === 'levelSettings') return { ...snapshot, ...status, levelSettings: result.levelSettings };
  if (result.kind === 'selection') {
    const known = new Set(snapshot.entities.map((entity) => entity.id));
    const patches = new Map(result.mobyPVars.map((patch) => [patch.entityId, patch.mobyPVar]));
    if (new Set(result.selection).size !== result.selection.length
      || patches.size !== result.mobyPVars.length
      || result.selection.some((id) => !known.has(id))
      || result.mobyPVars.some((patch) => !known.has(patch.entityId)))
      throw new TypeError('Editor selection patch contains duplicate or unknown entities');
    let changed = false;
    const updatedEntities = patches.size ? snapshot.entities.map((entity) => {
      if (!patches.has(entity.id) || patches.get(entity.id) === entity.mobyPVar) return entity;
      changed = true;
      const updated = { ...entity, mobyPVar: patches.get(entity.id) };
      if (!updated.mobyPVar) delete updated.mobyPVar;
      return updated;
    }) : snapshot.entities;
    const entities = changed ? updatedEntities : snapshot.entities;
    return { ...snapshot, ...status, entities, selection: result.selection };
  }

  const transforms = new Map(result.transforms.map((value) => [value.entityId, value]));
  let applied = 0;
  const entities = snapshot.entities.map((entity) => {
    const update = transforms.get(entity.id);
    if (!update) return entity;
    applied++;
    return {
      ...entity,
      transform: update.transform,
      state: { ...entity.state, dirty: update.dirty },
    };
  });
  if (applied !== transforms.size) throw new TypeError('Editor transform patch references an unknown entity');
  return { ...snapshot, ...status, entities };
}
