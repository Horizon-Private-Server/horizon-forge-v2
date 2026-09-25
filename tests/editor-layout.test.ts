import assert from 'node:assert/strict';
import test from 'node:test';

import { createDefaultEditorLayout, decodeEditorLayout } from '../src/renderer/editor/EditorLayout.ts';

function layout(panels: Record<string, unknown>): string {
  return JSON.stringify({
    grid: { root: { type: 'branch', data: [] }, height: 800, width: 1200, orientation: 'HORIZONTAL' },
    panels,
  });
}

test('editor layout restore accepts hidden panels and rejects stale or malformed panels', () => {
  const hiddenPanels = layout({
    viewport: { id: 'viewport', contentComponent: 'viewport', title: 'Viewport' },
  });
  assert.ok(decodeEditorLayout(hiddenPanels));
  assert.equal(decodeEditorLayout(layout({ removed: { id: 'removed', contentComponent: 'removed' } })), undefined);
  assert.equal(decodeEditorLayout(layout({ viewport: { id: 'viewport', contentComponent: 'renamed' } })), undefined);
  assert.equal(decodeEditorLayout(layout({ toString: { id: 'toString' } })), undefined);
  assert.equal(decodeEditorLayout('{broken'), undefined);
  assert.equal(decodeEditorLayout('x'.repeat(1024 * 1024 + 1)), undefined);
});

test('default editor layout places properties below the right-hand build panel', () => {
  const panels: Array<Record<string, unknown>> = [];
  createDefaultEditorLayout({
    clear: () => panels.splice(0),
    addPanel: (panel: Record<string, unknown>) => panels.push(panel),
  } as never);
  assert.deepEqual(panels.find((panel) => panel.id === 'build')?.position,
    { referencePanel: 'viewport', direction: 'right' });
  assert.deepEqual(panels.find((panel) => panel.id === 'properties')?.position,
    { referencePanel: 'build', direction: 'below' });
});
