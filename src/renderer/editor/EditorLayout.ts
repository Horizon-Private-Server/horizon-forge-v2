import type {
  DockviewApi,
  SerializedDockview,
} from 'dockview-react';
import type { EditorLayoutAction } from '../../types/ForgeApi.js';

export const EDITOR_PANELS = {
  viewport: { component: 'viewport', title: 'Viewport' },
  sceneTree: { component: 'sceneTree', title: 'Scene' },
  properties: { component: 'properties', title: 'Properties' },
  diagnostics: { component: 'diagnostics', title: 'Diagnostics' },
  build: { component: 'build', title: 'Build' },
} as const;

export type EditorPanelId = keyof typeof EDITOR_PANELS;

export function decodeEditorLayout(value: string): SerializedDockview | undefined {
  if (!value || value.length > 1024 * 1024) return undefined;
  try {
    const layout: unknown = JSON.parse(value);
    if (!isRecord(layout) || !isRecord(layout.grid) || !isRecord(layout.panels)) return undefined;
    for (const [id, panel] of Object.entries(layout.panels)) {
      if (!isEditorPanelId(id) || !isRecord(panel)
        || panel.id !== id || panel.contentComponent !== EDITOR_PANELS[id].component) return undefined;
    }
    return layout as unknown as SerializedDockview;
  } catch {
    return undefined;
  }
}

export function createDefaultEditorLayout(api: DockviewApi): void {
  api.clear();
  api.addPanel({ id: 'viewport', ...EDITOR_PANELS.viewport });
  api.addPanel({
    id: 'sceneTree', ...EDITOR_PANELS.sceneTree,
    position: { referencePanel: 'viewport', direction: 'left' }, initialWidth: 240,
  });
  api.addPanel({
    id: 'properties', ...EDITOR_PANELS.properties,
    position: { referencePanel: 'viewport', direction: 'right' }, initialWidth: 280,
  });
  api.addPanel({
    id: 'diagnostics', ...EDITOR_PANELS.diagnostics,
    position: { referencePanel: 'viewport', direction: 'below' }, initialHeight: 180,
  });
  api.addPanel({
    id: 'build', ...EDITOR_PANELS.build,
    position: { referencePanel: 'properties', direction: 'within' },
  });
}

export function showEditorPanel(api: DockviewApi, id: EditorPanelId): void {
  const existing = api.getPanel(id);
  if (existing) {
    existing.api.setActive();
    return;
  }
  const definition = EDITOR_PANELS[id];
  const properties = id === 'build' ? api.getPanel('properties') : undefined;
  if (properties) {
    api.addPanel({ id, ...definition, position: { referencePanel: properties.id, direction: 'within' } });
    return;
  }
  api.addPanel(api.getPanel('viewport') && id !== 'viewport'
    ? { id, ...definition, position: { referencePanel: 'viewport', direction: panelDirection(id) } }
    : { id, ...definition });
}

export function isEditorPanelId(value: string): value is EditorPanelId {
  return Object.hasOwn(EDITOR_PANELS, value);
}

export function panelForLayoutAction(action: Exclude<EditorLayoutAction, 'resetLayout'>): EditorPanelId {
  const panels: Record<Exclude<EditorLayoutAction, 'resetLayout'>, EditorPanelId> = {
    showViewport: 'viewport',
    showSceneTree: 'sceneTree',
    showProperties: 'properties',
    showDiagnostics: 'diagnostics',
    showBuild: 'build',
  };
  return panels[action];
}

function panelDirection(id: Exclude<EditorPanelId, 'viewport'>): 'left' | 'right' | 'below' {
  if (id === 'sceneTree') return 'left';
  if (id === 'properties') return 'right';
  return 'below';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
