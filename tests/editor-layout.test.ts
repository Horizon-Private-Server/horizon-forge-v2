import assert from 'node:assert/strict';
import test from 'node:test';

import { createDefaultEditorLayout, decodeEditorLayout, showEditorPanel } from '../src/renderer/editor/EditorLayout.ts';

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

test('default editor layout matches the established three-column workspace', () => {
  const panels: Array<Record<string, unknown>> = [];
  createDefaultEditorLayout({
    width: 2560,
    height: 1285,
    clear: () => panels.splice(0),
    addPanel: (panel: Record<string, unknown>) => panels.push(panel),
  } as never);
  assert.deepEqual(panels.map((panel) => panel.id), [
    'sceneTree', 'viewport', 'build', 'levelSettings', 'diagnostics', 'groups',
    'properties', 'hudBank', 'fxTextures', 'assetPreview', 'references', 'assetExplorer',
  ]);
  assert.deepEqual(panels.find((panel) => panel.id === 'viewport'), {
    id: 'viewport', component: 'viewport', title: 'Viewport', initialWidth: 2194,
    position: { referencePanel: 'sceneTree', direction: 'right' },
  });
  assert.deepEqual(panels.find((panel) => panel.id === 'build')?.position,
    { referencePanel: 'viewport', direction: 'right' });
  assert.equal(panels.find((panel) => panel.id === 'build')?.initialWidth, 536);
  assert.deepEqual(panels.find((panel) => panel.id === 'levelSettings')?.position,
    { referencePanel: 'sceneTree', direction: 'below' });
  assert.equal(panels.find((panel) => panel.id === 'levelSettings')?.initialHeight, 544);
  assert.deepEqual(panels.find((panel) => panel.id === 'diagnostics')?.position,
    { referencePanel: 'viewport', direction: 'below' });
  assert.equal(panels.find((panel) => panel.id === 'diagnostics')?.initialHeight, 485);
  assert.deepEqual(panels.find((panel) => panel.id === 'groups')?.position,
    { referencePanel: 'viewport', direction: 'left' });
  assert.equal(panels.find((panel) => panel.id === 'groups')?.initialWidth, 283);
  assert.deepEqual(panels.find((panel) => panel.id === 'properties')?.position,
    { referencePanel: 'build', direction: 'below' });
  assert.equal(panels.find((panel) => panel.id === 'properties')?.initialHeight, 669);
  assert.deepEqual(panels.find((panel) => panel.id === 'assetExplorer')?.position,
    { referencePanel: 'diagnostics', direction: 'within' });
});

test('reopened Asset Explorer rejoins the Diagnostics group', () => {
  let added: Record<string, unknown> | undefined;
  showEditorPanel({
    getPanel: (id: string) => id === 'diagnostics' ? { id } : undefined,
    addPanel: (panel: Record<string, unknown>) => { added = panel; },
  } as never, 'assetExplorer');
  assert.deepEqual(added?.position, { referencePanel: 'diagnostics', direction: 'within' });
});

test('reopened Asset Preview rejoins the Properties group', () => {
  let added: Record<string, unknown> | undefined;
  showEditorPanel({
    getPanel: (id: string) => id === 'properties' ? { id } : undefined,
    addPanel: (panel: Record<string, unknown>) => { added = panel; },
  } as never, 'assetPreview');
  assert.deepEqual(added?.position, { referencePanel: 'properties', direction: 'within' });
});

test('reopened References rejoins the Properties group', () => {
  let added: Record<string, unknown> | undefined;
  showEditorPanel({
    getPanel: (id: string) => id === 'properties' ? { id } : undefined,
    addPanel: (panel: Record<string, unknown>) => { added = panel; },
  } as never, 'references');
  assert.deepEqual(added?.position, { referencePanel: 'properties', direction: 'within' });
});

test('reopened HUD Bank rejoins the main viewport group', () => {
  let added: Record<string, unknown> | undefined;
  showEditorPanel({
    getPanel: (id: string) => id === 'viewport' ? { id } : undefined,
    addPanel: (panel: Record<string, unknown>) => { added = panel; },
  } as never, 'hudBank');
  assert.deepEqual(added?.position, { referencePanel: 'viewport', direction: 'within' });
});

test('reopened Groups panel sits beside Scene for cross-tree drag and drop', () => {
  let added: Record<string, unknown> | undefined;
  showEditorPanel({
    getPanel: (id: string) => id === 'sceneTree' ? { id } : undefined,
    addPanel: (panel: Record<string, unknown>) => { added = panel; },
  } as never, 'groups');
  assert.deepEqual(added?.position, { referencePanel: 'sceneTree', direction: 'right' });
});

test('reopened Scene panel sits beside Groups for cross-tree drag and drop', () => {
  let added: Record<string, unknown> | undefined;
  showEditorPanel({
    getPanel: (id: string) => id === 'groups' ? { id } : undefined,
    addPanel: (panel: Record<string, unknown>) => { added = panel; },
  } as never, 'sceneTree');
  assert.deepEqual(added?.position, { referencePanel: 'groups', direction: 'left' });
});
