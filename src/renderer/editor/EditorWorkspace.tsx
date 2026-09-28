import { DockviewReact, themeAbyss } from 'dockview-react';
import type { DockviewApi, DockviewReadyEvent } from 'dockview-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type { AssetExplorerFamily } from '../../types/AssetExplorer.js';
import type { EditorCommand, EditorSnapshot, ProjectVector4 } from '../../types/EditorRuntime.js';
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
import { isTextInput } from '../../utils/Dom.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { findKeybindingCommand, forgeActionForKeybinding } from '../../utils/Keybindings.ts';
import { parseSplinePointId, removeSplinePoints, splinePointId } from '../../utils/SplinePoints.ts';
import { EditorContext } from './EditorContext.ts';
import { BuildPanel } from './BuildPanel.tsx';
import { AssetExplorerPanel } from './AssetExplorerPanel.tsx';
import { AssetPreviewPanel } from './AssetPreviewPanel.tsx';
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
  assetExplorer: AssetExplorerPanel,
  assetPreview: AssetPreviewPanel,
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
  const [splinePointSelection, setSplinePointSelection] = useState<string[]>([]);
  const splinePointClipboard = useRef<ProjectVector4[]>([]);
  const [showOcclusionOctants, setShowOcclusionOctants] = useState(false);
  const [assetPreview, setAssetPreview] = useState<AssetExplorerFamily>();
  const onReady = useCallback((event: DockviewReadyEvent) => setApi(event.api), []);

  useEffect(() => {
    let disposed = false;
    setTerrain(undefined);
    setSkyPieces([]);
    setCameraFocus(undefined);
    setAssetPreview(undefined);
    setSplinePointSelection([]);
    splinePointClipboard.current = [];
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

  const inspectAsset = useCallback((family: AssetExplorerFamily) => {
    setAssetPreview(family);
    if (api) showEditorPanel(api, 'assetPreview');
  }, [api]);

  const execute = useCallback(async (command: EditorCommand) => {
    setError(undefined);
    try {
      onProjectChange(await window.forge.executeEditorCommand(command));
      return true;
    }
    catch (cause) {
      setError(errorMessage(cause));
      return false;
    }
  }, [onProjectChange]);

  useEffect(() => {
    if (!splinePointSelection.length) return;
    const keyDown = (event: KeyboardEvent) => {
      if (event.repeat || isTextInput(event.target) || document.querySelector('[role="dialog"]')) return;
      const action = forgeActionForKeybinding(findKeybindingCommand(keybindings, event, 'global'));
      if (!['copyEntities', 'pasteEntities', 'deleteEntities'].includes(action ?? '')) return;
      const references = splinePointSelection.map(parseSplinePointId).filter((value) => value !== undefined);
      const entityId = references.at(-1)?.entityId;
      const entity = project.entities.find((value) => value.id === entityId
        && (value.geometry?.kind === 'spline' || value.geometry?.kind === 'grindPath'));
      if (!entity || action !== 'copyEntities' && (entity.state.locked || entity.state.readOnly)) return;
      const selected = new Set(references.filter((value) => value.entityId === entity.id).map((value) => value.index));
      if (action === 'copyEntities' && window.getSelection()?.toString()) return;
      if (action === 'pasteEntities' && splinePointClipboard.current.length === 0) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      if (action === 'copyEntities') {
        splinePointClipboard.current = entity.geometry!.points
          .filter((_, index) => selected.has(index))
          .map((point) => ({ ...point }));
        return;
      }
      if (action === 'pasteEntities') {
        const insertAt = Math.max(...selected) + 1;
        const copied = splinePointClipboard.current.map((point) => ({ ...point }));
        const points = [...entity.geometry!.points.slice(0, insertAt), ...copied,
          ...entity.geometry!.points.slice(insertAt)];
        void execute({
          id: crypto.randomUUID(), kind: 'updateSplinePoints', entityIds: [entity.id], points,
        }).then((committed) => {
          if (committed) setSplinePointSelection(copied.map((_, index) => splinePointId(entity.id, insertAt + index)));
        });
        return;
      }
      void execute({
        id: crypto.randomUUID(), kind: 'updateSplinePoints', entityIds: [entity.id],
        points: removeSplinePoints(entity.geometry!.points, selected),
      }).then(async (committed) => {
        if (!committed) return;
        if (await execute({ id: crypto.randomUUID(), kind: 'setSelection', entityIds: [entity.id] }))
          setSplinePointSelection([]);
      });
    };
    window.addEventListener('keydown', keyDown, true);
    return () => window.removeEventListener('keydown', keyDown, true);
  }, [execute, keybindings, project.entities, splinePointSelection]);

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
    splinePointSelection,
    setSplinePointSelection,
    showViewportStats,
    showOcclusionOctants,
    setShowOcclusionOctants,
    assetPreview,
    inspectAsset,
    busy,
    hostAvailable: Boolean(hostStatus),
    buildProgress,
    buildResult,
    build: buildAndPatch,
    cancelBuild: () => window.forge.cancelBuildAndPatch(),
    execute,
    save: async () => {
      setBusy(true);
      setError(undefined);
      try { onProjectChange(await window.forge.saveEditorProject()); }
      catch (cause) { setError(errorMessage(cause)); }
      finally { setBusy(false); }
    },
  }), [assetPreview, buildAndPatch, buildProgress, buildResult, busy, cameraFocus, execute, hostStatus, inspectAsset, keybindings,
    project, sceneLoad, sceneTreeColors, showOcclusionOctants, showViewportStats, skyPieces, splinePointSelection,
    terrain]);

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
