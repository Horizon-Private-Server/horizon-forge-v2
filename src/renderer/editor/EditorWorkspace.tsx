import { DockviewReact, themeAbyss } from 'dockview-react';
import type { DockviewApi, DockviewReadyEvent } from 'dockview-react';
import { useCallback, useEffect, useState } from 'react';

import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import type { EditorLayoutAction } from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';
import { EditorContext } from './EditorContext.ts';
import {
  createDefaultEditorLayout,
  decodeEditorLayout,
  showEditorPanel,
  type EditorPanelId,
} from './EditorLayout.ts';
import {
  DiagnosticsPanel,
  EditorWatermark,
  PropertiesPanel,
  SceneTreePanel,
  ViewportPanel,
} from './EditorPanels.tsx';
import { EditorErrorState } from './EditorPrimitives.tsx';

interface EditorWorkspaceProps {
  project: EditorSnapshot;
  layoutAction?: { id: number; action: EditorLayoutAction };
}

const components = {
  viewport: ViewportPanel,
  sceneTree: SceneTreePanel,
  properties: PropertiesPanel,
  diagnostics: DiagnosticsPanel,
};

export function EditorWorkspace({ project, layoutAction }: EditorWorkspaceProps) {
  const [api, setApi] = useState<DockviewApi>();
  const [initialized, setInitialized] = useState(false);
  const [error, setError] = useState<string>();
  const onReady = useCallback((event: DockviewReadyEvent) => setApi(event.api), []);

  useEffect(() => {
    if (!api) return;
    let disposed = false;
    let subscription: { dispose(): void } | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let pending: string | undefined;
    const save = (layout: string) => window.forge.setSetting('ui.editorLayout', layout)
      .catch((cause) => { if (!disposed) setError(errorMessage(cause)); });

    void window.forge.getSettings().then((settings) => {
      if (disposed) return;
      const stored = settings.entries.find((entry) => entry.key === 'ui.editorLayout')?.value;
      const layout = typeof stored === 'string' ? decodeEditorLayout(stored) : undefined;
      try {
        if (layout) api.fromJSON(layout);
        else createDefaultEditorLayout(api);
      } catch {
        createDefaultEditorLayout(api);
      }
      subscription = api.onDidLayoutChange(() => {
        pending = JSON.stringify(api.toJSON());
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => {
          timer = undefined;
          const value = pending;
          pending = undefined;
          if (value) void save(value);
        }, 250);
      });
      setInitialized(true);
    }).catch((cause) => {
      if (disposed) return;
      createDefaultEditorLayout(api);
      setError(errorMessage(cause));
      setInitialized(true);
    });

    return () => {
      disposed = true;
      subscription?.dispose();
      if (timer) clearTimeout(timer);
      if (pending) void window.forge.setSetting('ui.editorLayout', pending)
        .catch((cause) => console.error('Could not persist editor layout', cause));
    };
  }, [api]);

  useEffect(() => {
    if (!api || !initialized || !layoutAction) return;
    if (layoutAction.action === 'resetLayout') createDefaultEditorLayout(api);
    else showEditorPanel(api, panelForAction(layoutAction.action));
  }, [api, initialized, layoutAction]);

  return <EditorContext.Provider value={project}>
    <div className="editor-workspace">
      {error && <div className="editor-layout-error">
        <EditorErrorState message={error} onClose={() => setError(undefined)} />
      </div>}
      <DockviewReact
        components={components}
        keyboardNavigation
        onReady={onReady}
        theme={themeAbyss}
        watermarkComponent={EditorWatermark}
      />
    </div>
  </EditorContext.Provider>;
}

function panelForAction(action: Exclude<EditorLayoutAction, 'resetLayout'>): EditorPanelId {
  const panels: Record<Exclude<EditorLayoutAction, 'resetLayout'>, EditorPanelId> = {
    showViewport: 'viewport',
    showSceneTree: 'sceneTree',
    showProperties: 'properties',
    showDiagnostics: 'diagnostics',
  };
  return panels[action];
}
