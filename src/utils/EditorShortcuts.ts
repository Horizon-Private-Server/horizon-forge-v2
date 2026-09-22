interface KeyInput {
  type: string;
  key: string;
  isAutoRepeat: boolean;
  control: boolean;
  meta: boolean;
  alt: boolean;
  shift: boolean;
}

export function editorClipboardShortcut(input: KeyInput): 'copyEntities' | 'pasteEntities' | undefined {
  if (input.type !== 'keyDown' || input.isAutoRepeat || input.alt || input.shift || (!input.control && !input.meta))
    return undefined;
  if (input.key.toLowerCase() === 'c') return 'copyEntities';
  if (input.key.toLowerCase() === 'v') return 'pasteEntities';
  return undefined;
}
