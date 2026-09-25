import { DockviewReact, themeAbyss } from 'dockview-react';
import type { DockviewApi, DockviewReadyEvent } from 'dockview-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type { EditorSnapshot } from '../../types/EditorRuntime.js';
import type { EditorCommand } from '../../types/EditorRuntime.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
import type {
  BuildPatchProgress,
  BuildPatchResult,
  BuildLayerId,
  EditorLayoutAction,
  EditorLoadProgress,
  EditorTerrainSource,
  ForgeHostStatus,
} from '../../types/ForgeApi.js';
import { errorMessage } from '../../utils/Errors.ts';
import { EditorContext } from './EditorContext.ts';
import { BuildPanel } from './BuildPanel.tsx';
import {
  createDefaultEditorLayout,
  decodeEditorLayout,
  panelForLayoutAction,
  showEditorPanel,
} from './EditorLayout.ts';
import {
  DiagnosticsPanel,
  EditorWatermark,
  LevelSettingsPanel,
  PropertiesPanel,
  SceneTreePanel,
  ViewportPanel,
} from './EditorPanels.tsx';
import { EditorErrorState } from './EditorPrimitives.tsx';
import { EditorStatusBar } from './EditorStatusBar.tsx';

interface EditorWorkspaceProps {
  project: EditorSnapshot;
  keybindings: KeybindingMap;
  sceneTreeColors: SceneTreeColors;
  hostStatus?: ForgeHostStatus;
  layoutAction?: { id: number; action: EditorLayoutAction };
  showViewportStats: boolean;
  onProjectChange(project: EditorSnapshot): void;
}

const components = {
  viewport: ViewportPanel,
  sceneTree: SceneTreePanel,
  properties: PropertiesPanel,
  levelSettings: LevelSettingsPanel,
  diagnostics: DiagnosticsPanel,
  build: BuildPanel,
};

export function EditorWorkspace({
  project,
  keybindings,
  sceneTreeColors,
  hostStatus,
  layoutAction,
  showViewportStats,
  onProjectChange,
}: EditorWorkspaceProps) {
  const [api, setApi] = useState<DockviewApi>();
  const [initialized, setInitialized] = useState(false);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [buildProgress, setBuildProgress] = useState<BuildPatchProgress>();
  const [buildResult, setBuildResult] = useState<BuildPatchResult>();
  const buildRunning = useRef(false);
  const [terrain, setTerrain] = useState<EditorTerrainSource>();
  const [sceneLoad, setSceneLoad] = useState<EditorLoadProgress>();
  const [skyPieces, setSkyPieces] = useState<string[]>([]);
  const [cameraFocus, setCameraFocus] = useState<{ entityId: string }>();
  const [showOcclusionOctants, setShowOcclusionOctants] = useState(false);
  const onReady = useCallback((event: DockviewReadyEvent) => setApi(event.api), []);

  useEffect(() => {
    let disposed = false;
    setTerrain(undefined);
    setSkyPieces([]);
    setCameraFocus(undefined);
    setSceneLoad({ status: 'loading', label: 'Preparing UYA render package…', completed: 0, total: 1 });
    const stopProgress = window.forge.onEditorTerrainProgress((progress) => {
      if (!disposed) setSceneLoad({ status: 'loading', label: 'Preparing UYA render package…', ...progress });
    });
    void window.forge.getEditorTerrain().then((source) => {
      if (disposed) return;
      stopProgress();
      setSceneLoad({
        status: 'loading', label: 'Loading terrain, sky, and entity meshes…', completed: 0,
        total: source.urls.length + source.assets.length + (source.skyUrl ? 1 : 0),
      });
      setTerrain(source);
    }).catch((cause) => {
      if (!disposed) {
        stopProgress();
        setSceneLoad({ status: 'error', label: errorMessage(cause), completed: 0, total: 1 });
      }
    });
    return () => {
      disposed = true;
      stopProgress();
      void window.forge.cancelEditorTerrain();
    };
  }, [project.projectId]);

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
      let migratedLayout = false;
      try {
        if (layout) api.fromJSON(layout);
        else createDefaultEditorLayout(api);
        if (!api.getPanel('levelSettings')) showEditorPanel(api, 'levelSettings');
        if (!api.getPanel('build')) showEditorPanel(api, 'build');
        const levelSettings = api.getPanel('levelSettings');
        const properties = api.getPanel('properties');
        const sceneTree = api.getPanel('sceneTree');
        if (levelSettings && properties && sceneTree && levelSettings.group === properties.group) {
          levelSettings.api.moveTo({ group: sceneTree.group, position: 'bottom' });
          migratedLayout = true;
        }
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
      if (migratedLayout) void save(JSON.stringify(api.toJSON()));
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
    else showEditorPanel(api, panelForLayoutAction(layoutAction.action));
  }, [api, initialized, layoutAction]);

  useEffect(() => window.forge.onBuildPatchProgress(setBuildProgress), []);

  const buildAndPatch = useCallback(async (includedLayers: BuildLayerId[]) => {
    if (buildRunning.current) return;
    buildRunning.current = true;
    setBusy(true);
    setError(undefined);
    setBuildResult(undefined);
    setBuildProgress({ phase: 'Preflight', completed: 0, total: 1, message: 'Starting build…' });
    try {
      const result = await window.forge.buildAndPatchProject(includedLayers);
      setBuildResult(result);
      if (!result.succeeded) {
        setError([result.message, ...result.diagnostics, result.nextAction].filter(Boolean).join(' '));
      }
      onProjectChange(await window.forge.getEditorSnapshot());
    } catch (cause) {
      setError(errorMessage(cause));
    } finally {
      buildRunning.current = false;
      setBusy(false);
    }
  }, [onProjectChange]);

  const context = useMemo(() => ({
    project,
    keybindings,
    sceneTreeColors,
    terrain,
    sceneLoad,
    setSceneLoad,
    skyPieces,
    setSkyPieces,
    cameraFocus,
    setCameraFocus,
    showViewportStats,
    showOcclusionOctants,
    setShowOcclusionOctants,
    busy,
    hostAvailable: Boolean(hostStatus),
    buildProgress,
    buildResult,
    build: buildAndPatch,
    cancelBuild: () => window.forge.cancelBuildAndPatch(),
    execute: async (command: EditorCommand) => {
      setError(undefined);
      try {
        onProjectChange(await window.forge.executeEditorCommand(command));
        return true;
      }
      catch (cause) {
        setError(errorMessage(cause));
        return false;
      }
    },
    save: async () => {
      setBusy(true);
      setError(undefined);
      try { onProjectChange(await window.forge.saveEditorProject()); }
      catch (cause) { setError(errorMessage(cause)); }
      finally { setBusy(false); }
    },
  }), [buildAndPatch, buildProgress, buildResult, busy, cameraFocus, hostStatus, keybindings, onProjectChange,
    project, sceneLoad, sceneTreeColors, showOcclusionOctants, showViewportStats, skyPieces, terrain]);

  return <EditorContext.Provider value={context}>
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
      <EditorStatusBar
        hostStatus={hostStatus}
        busy={busy}
        buildProgress={buildProgress}
        buildResult={buildResult}
        onCancel={() => void window.forge.cancelBuildAndPatch()}
      />
    </div>
  </EditorContext.Provider>;
}
