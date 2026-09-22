import { useEffect } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type { ForgeAction } from '../../types/ForgeApi.js';
import { isTextInput } from '../../utils/Dom.ts';
import { findKeybindingCommand, forgeActionForKeybinding } from '../../utils/Keybindings.ts';

export function useKeyboardContext(keybindings: KeybindingMap, onAction: (action: ForgeAction) => void): void {
  useEffect(() => {
    const update = () => window.forge.setEditorTextInputActive(isTextInput(document.activeElement));
    const updateAfterFocus = () => queueMicrotask(update);
    document.addEventListener('focusin', updateAfterFocus);
    document.addEventListener('focusout', updateAfterFocus);
    update();
    return () => {
      document.removeEventListener('focusin', updateAfterFocus);
      document.removeEventListener('focusout', updateAfterFocus);
      window.forge.setEditorTextInputActive(false);
    };
  }, []);

  useEffect(() => {
    const keyDown = (event: KeyboardEvent) => {
      if (isTextInput(event.target) || document.querySelector('[role="dialog"]')) return;
      const action = forgeActionForKeybinding(findKeybindingCommand(keybindings, event, 'global'));
      if (!action) return;
      event.preventDefault();
      onAction(action);
    };
    window.addEventListener('keydown', keyDown, true);
    return () => window.removeEventListener('keydown', keyDown, true);
  }, [keybindings, onAction]);
}
