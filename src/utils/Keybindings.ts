import type {
  KeybindingCommandId,
  KeybindingContext,
  KeybindingDefinition,
  KeybindingMap,
  KeybindingOverrides,
} from '../types/Keybindings.js';
import type { ForgeAction } from '../types/ForgeApi.js';

interface KeyInput {
  code: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
  repeat?: boolean;
}

export const KEYBINDING_COMMANDS: readonly KeybindingDefinition[] = [
  { id: 'project.new', label: 'New project', group: 'Project', context: 'global', defaultBinding: 'CtrlOrMeta+KeyN' },
  { id: 'project.open', label: 'Open project', group: 'Project', context: 'global', defaultBinding: 'CtrlOrMeta+KeyO' },
  { id: 'project.save', label: 'Save project', group: 'Project', context: 'global', defaultBinding: 'CtrlOrMeta+KeyS' },
  { id: 'edit.undo', label: 'Undo', group: 'Edit', context: 'global', defaultBinding: 'CtrlOrMeta+KeyZ' },
  { id: 'edit.redo', label: 'Redo', group: 'Edit', context: 'global', defaultBinding: 'CtrlOrMeta+KeyY' },
  { id: 'edit.duplicate', label: 'Duplicate selection', group: 'Edit', context: 'global', defaultBinding: 'CtrlOrMeta+KeyD' },
  { id: 'edit.delete', label: 'Delete selection', group: 'Edit', context: 'global', defaultBinding: 'Delete' },
  { id: 'edit.copy', label: 'Copy selection', group: 'Edit', context: 'global', defaultBinding: 'CtrlOrMeta+KeyC' },
  { id: 'edit.paste', label: 'Paste', group: 'Edit', context: 'global', defaultBinding: 'CtrlOrMeta+KeyV' },
  { id: 'app.settings', label: 'Open settings', group: 'Application', context: 'global', defaultBinding: 'CtrlOrMeta+Comma' },
  { id: 'scene.select', label: 'Select mode', group: 'Viewport', context: 'viewport', defaultBinding: 'Digit1' },
  { id: 'scene.translate', label: 'Move mode', group: 'Viewport', context: 'viewport', defaultBinding: 'Digit2' },
  { id: 'scene.rotate', label: 'Rotate mode', group: 'Viewport', context: 'viewport', defaultBinding: 'Digit3' },
  { id: 'scene.scale', label: 'Scale mode', group: 'Viewport', context: 'viewport', defaultBinding: 'Digit4' },
  { id: 'scene.snapToGround', label: 'Snap selection to ground', group: 'Viewport', context: 'viewport', defaultBinding: 'PageDown' },
];

const commandIds = new Set(KEYBINDING_COMMANDS.map((command) => command.id));
const modifierCodes = new Set(['ControlLeft', 'ControlRight', 'ShiftLeft', 'ShiftRight', 'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight']);

export function parseKeybindingOverrides(value: unknown): KeybindingOverrides {
  if (typeof value !== 'string') return {};
  try {
    const parsed: unknown = JSON.parse(value);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
    return Object.fromEntries(Object.entries(parsed).filter(([id, binding]) =>
      commandIds.has(id as KeybindingCommandId) && (binding === null || isKeybinding(binding)))) as KeybindingOverrides;
  } catch {
    return {};
  }
}

export function isKeybindingOverrides(value: unknown): value is string {
  if (typeof value !== 'string' || value.length > 16_384) return false;
  try {
    const parsed: unknown = JSON.parse(value);
    return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
      && Object.entries(parsed).every(([id, binding]) =>
        commandIds.has(id as KeybindingCommandId) && (binding === null || isKeybinding(binding)));
  } catch {
    return false;
  }
}

export function resolveKeybindings(overrides: KeybindingOverrides): KeybindingMap {
  return Object.fromEntries(KEYBINDING_COMMANDS.map((command) => [
    command.id,
    Object.hasOwn(overrides, command.id) ? overrides[command.id] ?? null : command.defaultBinding,
  ])) as KeybindingMap;
}

export function bindingFromKeyInput(input: KeyInput): string | undefined {
  if (!input.code || modifierCodes.has(input.code)) return undefined;
  return [
    input.ctrlKey || input.metaKey ? 'CtrlOrMeta' : undefined,
    input.altKey ? 'Alt' : undefined,
    input.shiftKey ? 'Shift' : undefined,
    input.code,
  ].filter(Boolean).join('+');
}

export function findKeybindingCommand(
  bindings: KeybindingMap,
  input: KeyInput,
  context: KeybindingContext,
): KeybindingCommandId | undefined {
  if (input.repeat) return undefined;
  const binding = bindingFromKeyInput(input);
  return KEYBINDING_COMMANDS.find((command) => command.context === context && bindings[command.id] === binding)?.id;
}

export function findKeybindingConflict(
  bindings: KeybindingMap,
  commandId: KeybindingCommandId,
  binding: string,
): KeybindingDefinition | undefined {
  return KEYBINDING_COMMANDS.find((command) => command.id !== commandId && bindings[command.id] === binding);
}

export function forgeActionForKeybinding(command?: KeybindingCommandId): ForgeAction | undefined {
  return command ? GLOBAL_COMMAND_ACTIONS[command] : undefined;
}

export function transformModeForKeybinding(command?: KeybindingCommandId):
  'select' | 'translate' | 'rotate' | 'scale' | undefined {
  if (command === 'scene.select') return 'select';
  if (command === 'scene.translate') return 'translate';
  if (command === 'scene.rotate') return 'rotate';
  if (command === 'scene.scale') return 'scale';
  return undefined;
}

export function formatKeybinding(binding: string | null): string {
  if (!binding) return 'Unbound';
  const labels: Record<string, string> = {
    CtrlOrMeta: 'Ctrl/Cmd', KeyN: 'N', KeyO: 'O', KeyS: 'S', KeyZ: 'Z', KeyY: 'Y', KeyD: 'D',
    KeyC: 'C', KeyV: 'V', Comma: ',', Digit1: '1', Digit2: '2', Digit3: '3', Digit4: '4',
    PageDown: 'Page Down', Delete: 'Delete', Space: 'Space', Escape: 'Esc',
  };
  return binding.split('+').map((part) => labels[part] ?? part.replace(/^(Key|Digit)/, '')).join('+');
}

function isKeybinding(value: unknown): value is string {
  if (typeof value !== 'string' || !value || value.length > 128) return false;
  const parts = value.split('+');
  const code = parts.at(-1)!;
  return !modifierCodes.has(code) && /^[A-Za-z][A-Za-z0-9]*$/.test(code)
    && parts.slice(0, -1).every((part) => ['CtrlOrMeta', 'Alt', 'Shift'].includes(part))
    && new Set(parts).size === parts.length;
}

const GLOBAL_COMMAND_ACTIONS: Partial<Record<KeybindingCommandId, ForgeAction>> = {
  'project.new': 'newProject',
  'project.open': 'openProject',
  'project.save': 'saveProject',
  'edit.undo': 'undoEditor',
  'edit.redo': 'redoEditor',
  'edit.duplicate': 'duplicateEntities',
  'edit.delete': 'deleteEntities',
  'edit.copy': 'copyEntities',
  'edit.paste': 'pasteEntities',
  'app.settings': 'settings',
};
