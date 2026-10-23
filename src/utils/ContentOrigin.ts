import type { EditorContentOrigin, EditorEntity } from '../types/EditorRuntime.js';

export function contentOriginLabel(origin: EditorContentOrigin | undefined, fallbackLevel?: number): string {
  if (origin?.kind === 'base') return 'Base map';
  if (origin?.kind !== 'imported') return 'Project-created';
  return `Imported from level ${origin.sourceLevel ?? fallbackLevel ?? 'unknown'}`;
}

export function entityOriginSearchText(entity: EditorEntity): string {
  if (entity.origin?.kind !== 'imported') return entity.origin?.kind ?? '';
  const level = entity.origin.sourceLevel ?? entity.provenance?.level;
  return `imported donor${level === undefined ? '' : ` level ${level} l${level}`} ${entity.origin.importBatchId ?? ''}`;
}
