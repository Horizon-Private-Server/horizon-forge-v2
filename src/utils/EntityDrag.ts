export const ENTITY_DRAG_MIME = 'application/x-horizon-forge-entity';
let activeEntityId: string | undefined;

export function writeEntityDrag(data: DataTransfer, entityId: string): void {
  activeEntityId = entityId;
  data.effectAllowed = 'copy';
  data.setData(ENTITY_DRAG_MIME, entityId);
  data.setData('text/plain', entityId);
}

export function readEntityDrag(data: DataTransfer): string | undefined {
  return data.getData(ENTITY_DRAG_MIME) || data.getData('text/plain') || activeEntityId;
}

export function clearEntityDrag(): void {
  activeEntityId = undefined;
}
