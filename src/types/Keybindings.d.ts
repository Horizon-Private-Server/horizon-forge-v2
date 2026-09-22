export type KeybindingCommandId =
  | 'project.new' | 'project.open' | 'project.save'
  | 'edit.undo' | 'edit.redo' | 'edit.duplicate' | 'edit.delete' | 'edit.copy' | 'edit.paste'
  | 'app.settings'
  | 'scene.select' | 'scene.translate' | 'scene.rotate' | 'scene.scale' | 'scene.snapToGround';

export type KeybindingContext = 'global' | 'viewport';

export interface KeybindingDefinition {
  id: KeybindingCommandId;
  label: string;
  group: string;
  context: KeybindingContext;
  defaultBinding: string;
}

export type KeybindingOverrides = Partial<Record<KeybindingCommandId, string | null>>;
export type KeybindingMap = Record<KeybindingCommandId, string | null>;
