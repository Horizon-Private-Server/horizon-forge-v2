import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import test from 'node:test';
import { setImmediate } from 'node:timers/promises';

import { forgeLog } from '../src/renderer/log/LogStore.ts';

// Exercise the project-open callback with only React's hook registration replaced.
const reactUrl = `data:text/javascript,${encodeURIComponent(`
  export const useCallback = callback => callback;
  export const useState = value => [value, () => {}];
  export const useEffect = () => {};
  export const useSyncExternalStore = () => {};
`)}`;
const hooks = registerHooks({
  resolve(specifier, context, next) {
    return specifier === 'react' ? { url: reactUrl, shortCircuit: true } : next(specifier, context);
  },
});
let useForgeActions;
try {
  ({ useForgeActions } = await import('../src/renderer/hooks/UseForgeActions.ts'));
} finally { hooks.deregister(); }

test('opening a level clears old history only after the open succeeds', async () => {
  const previousWindow = globalThis.window;
  let resolveOpen;
  const opened = new Promise((resolve) => { resolveOpen = resolve; });
  globalThis.window = { forge: { openEditorProject: () => opened } };
  forgeLog.clear();
  forgeLog.write('Old level', 'warning', 'Old level diagnostic.');
  const history = forgeLog.getSnapshot();
  try {
    const { openProject } = useForgeActions(() => {}, () => {}, () => {});
    openProject('/new-level');
    assert.equal(forgeLog.getSnapshot(), history, 'keep history while opening');
    resolveOpen({ projectName: 'New level' });
    await setImmediate();
    assert.deepEqual(forgeLog.getSnapshot().map((entry) => entry.message), ['Opened New level.']);
    assert.deepEqual(forgeLog.getUnreadSnapshot(), forgeLog.getSnapshot());

    globalThis.window.forge.openEditorProject = async () => { throw new Error('Level is unavailable.'); };
    openProject('/missing-level');
    await setImmediate();
    assert.deepEqual(forgeLog.getSnapshot().map((entry) => entry.message), [
      'Opened New level.', 'Level is unavailable.',
    ], 'a failed open preserves the current history and records the error');
  } finally {
    globalThis.window = previousWindow;
    forgeLog.clear();
  }
});
