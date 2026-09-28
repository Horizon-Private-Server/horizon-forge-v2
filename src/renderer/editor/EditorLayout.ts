import type {
  DockviewApi,
  SerializedDockview,
} from 'dockview-react';
import type { EditorLayoutAction } from '../../types/ForgeApi.js';

export const EDITOR_PANELS = {
  viewport: { component: 'viewport', title: 'Viewport' },
  sceneTree: { component: 'sceneTree', title: 'Scene' },
  properties: { component: 'properties', title: 'Properties' },
  levelSettings: { component: 'levelSettings', title: 'Level Settings' },
  diagnostics: { component: 'diagnostics', title: 'Diagnostics' },
  build: { component: 'build', title: 'Build' },
  assetExplorer: { component: 'assetExplorer', title: 'Asset Explorer' },
  assetPreview: { component: 'assetPreview', title: 'Asset Preview' },
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
    id: 'build', ...EDITOR_PANELS.build,
    position: { referencePanel: 'viewport', direction: 'right' }, initialWidth: 280,
  });
  api.addPanel({
    id: 'properties', ...EDITOR_PANELS.properties,
    position: { referencePanel: 'build', direction: 'below' }, initialHeight: 280,
  });
  api.addPanel({
    id: 'assetPreview', ...EDITOR_PANELS.assetPreview,
    position: { referencePanel: 'properties', direction: 'within' },
    inactive: true,
  });
  api.addPanel({
    id: 'diagnostics', ...EDITOR_PANELS.diagnostics,
    position: { referencePanel: 'viewport', direction: 'below' }, initialHeight: 180,
  });
  api.addPanel({
    id: 'levelSettings', ...EDITOR_PANELS.levelSettings,
    position: { referencePanel: 'sceneTree', direction: 'below' }, initialHeight: 280,
  });
  api.addPanel({
    id: 'assetExplorer', ...EDITOR_PANELS.assetExplorer,
    position: { referencePanel: 'diagnostics', direction: 'within' },
  });
}

export function showEditorPanel(api: DockviewApi, id: EditorPanelId): void {
  const existing = api.getPanel(id);
  if (existing) {
    existing.api.setActive();
    return;
  }
  const definition = EDITOR_PANELS[id];
  const propertyPanel = id === 'assetPreview' ? api.getPanel('properties')
    : id === 'properties' ? api.getPanel('assetPreview') : undefined;
  if (propertyPanel) {
    api.addPanel({ id, ...definition, position: { referencePanel: propertyPanel.id, direction: 'within' } });
    return;
  }
  const build = id === 'properties' ? api.getPanel('build') : undefined;
  if (build) {
    api.addPanel({ id, ...definition, position: { referencePanel: build.id, direction: 'below' } });
    return;
  }
  const properties = id === 'build' ? api.getPanel('properties') : undefined;
  if (properties) {
    api.addPanel({ id, ...definition, position: { referencePanel: properties.id, direction: 'within' } });
    return;
  }
  const bottomPanel = id === 'assetExplorer' ? api.getPanel('diagnostics')
    : id === 'diagnostics' ? api.getPanel('assetExplorer') : undefined;
  if (bottomPanel) {
    api.addPanel({ id, ...definition, position: { referencePanel: bottomPanel.id, direction: 'within' } });
    return;
  }
  const sceneTree = id === 'levelSettings' ? api.getPanel('sceneTree') : undefined;
  if (sceneTree) {
    api.addPanel({ id, ...definition, position: { referencePanel: sceneTree.id, direction: 'below' } });
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
    showLevelSettings: 'levelSettings',
    showDiagnostics: 'diagnostics',
    showBuild: 'build',
    showAssetExplorer: 'assetExplorer',
    showAssetPreview: 'assetPreview',
  };
  return panels[action];
}

function panelDirection(id: Exclude<EditorPanelId, 'viewport'>): 'left' | 'right' | 'below' {
  if (id === 'sceneTree') return 'left';
  if (id === 'assetExplorer') return 'left';
  if (id === 'assetPreview') return 'right';
  if (id === 'properties') return 'right';
  return 'below';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
