import type { ForgeAction } from './ForgeApi.js';

export type EditorSelectionAction = Extract<ForgeAction,
  'copyEntities' | 'pasteEntities' | 'duplicateEntities' | 'deleteEntities'>;

export interface EditorSelectionActions {
  canCopy: boolean;
  canPaste: boolean;
  canMutate: boolean;
  handle(action: EditorSelectionAction): void;
}
