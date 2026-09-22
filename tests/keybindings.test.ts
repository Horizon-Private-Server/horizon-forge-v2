import assert from 'node:assert/strict';
import test from 'node:test';

import {
  bindingFromKeyInput,
  findKeybindingCommand,
  findKeybindingConflict,
  forgeActionForKeybinding,
  isKeybindingOverrides,
  parseKeybindingOverrides,
  resolveKeybindings,
  transformModeForKeybinding,
} from '../src/utils/Keybindings.ts';

test('keybindings resolve, capture, match contexts, and reject conflicts', () => {
  const overrides = parseKeybindingOverrides(JSON.stringify({
    'scene.translate': 'KeyG',
    'scene.rotate': null,
    unknown: 'KeyU',
  }));
  const bindings = resolveKeybindings(overrides);
  assert.equal(bindings['scene.translate'], 'KeyG');
  assert.equal(bindings['scene.rotate'], null);
  assert.equal(bindings['scene.scale'], 'Digit4');
  assert.equal(bindingFromKeyInput({
    code: 'KeyG', ctrlKey: false, metaKey: false, altKey: false, shiftKey: true,
  }), 'Shift+KeyG');
  assert.equal(findKeybindingCommand(bindings, {
    code: 'KeyG', ctrlKey: false, metaKey: false, altKey: false, shiftKey: false,
  }, 'viewport'), 'scene.translate');
  assert.equal(findKeybindingCommand(bindings, {
    code: 'KeyG', ctrlKey: false, metaKey: false, altKey: false, shiftKey: false,
  }, 'global'), undefined);
  assert.equal(findKeybindingConflict(bindings, 'scene.select', 'KeyG')?.id, 'scene.translate');
  assert.equal(forgeActionForKeybinding('edit.copy'), 'copyEntities');
  assert.equal(transformModeForKeybinding('scene.translate'), 'translate');
  assert.equal(isKeybindingOverrides('{"edit.delete":"KeyX"}'), true);
  assert.equal(isKeybindingOverrides('{"unknown":"KeyX"}'), false);
});
