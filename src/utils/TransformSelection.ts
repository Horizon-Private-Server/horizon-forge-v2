import type { EditorEntity } from '../types/EditorRuntime.js';

export type TransformSelectionMode = 'translate' | 'rotate' | 'scale';

export interface TransformSelectionEligibility {
  entities: EditorEntity[];
  message?: string;
}

const MODE_LABELS: Record<TransformSelectionMode, { action: string; capability: string }> = {
  translate: { action: 'move', capability: 'moving' },
  rotate: { action: 'rotate', capability: 'rotation' },
  scale: { action: 'scale', capability: 'scaling' },
};

export function inspectTransformSelection(
  entities: readonly EditorEntity[],
  selection: readonly string[],
  mode: TransformSelectionMode,
  missingMemberCount = 0,
): TransformSelectionEligibility {
  const ids = [...new Set(selection)];
  const entitiesById = new Map(entities.map((entity) => [entity.id, entity]));
  const selected = ids.flatMap((id) => {
    const entity = entitiesById.get(id);
    return entity ? [entity] : [];
  });
  if (ids.length === 0 && missingMemberCount === 0) return { entities: [] };

  const counts = new Map<string, number>();
  const add = (label: string, count = 1) => counts.set(label, (counts.get(label) ?? 0) + count);
  const missingIds = ids.length - selected.length;
  if (missingIds + missingMemberCount > 0) add('missing', missingIds + missingMemberCount);

  for (const entity of selected) {
    if (entity.state.locked) add('locked');
    if (entity.state.readOnly) add('read-only');
    if (entity.state.hidden) add('hidden');
    if (entity.state.disabled) add('disabled');
    if (entity.state.invalid) add('invalid');
    if (entity.state.missingAsset) add('missing asset');
    if (entity.collision?.attachment && ids.length > 1) add('collision-linked');
    if (!entity.transformModes.includes(mode)) add(`without ${MODE_LABELS[mode].capability} support`);
  }

  if (counts.size === 0) return { entities: selected };
  const reasons = [...counts].map(([label, count]) => `${count} ${label}`).join(', ');
  return {
    entities: [],
    message: `Cannot ${MODE_LABELS[mode].action} the whole selection: ${reasons}.`,
  };
}
