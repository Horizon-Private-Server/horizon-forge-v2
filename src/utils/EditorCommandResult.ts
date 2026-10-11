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
