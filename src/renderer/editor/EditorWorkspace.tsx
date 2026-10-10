import { DockviewReact, themeAbyss } from 'dockview-react';
import type { DockviewApi, DockviewReadyEvent } from 'dockview-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type { AssetExplorerCategory, AssetExplorerFamily } from '../../types/AssetExplorer.js';
import type {
  EditorCommand, EditorSnapshot, EditorInstancedCollisionGenerationSettings, ProjectVector4,
} from '../../types/EditorRuntime.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
import type { CollisionVisualization } from '../../types/CollisionVisualization.js';
import type {
  BuildPatchProgress,
  BuildPatchResult,
  BuildLayerId,
  EditorLayoutAction,
  EditorLoadProgress,
  EditorTerrainSource,
  ForgeHostStatus,
} from '../../types/ForgeApi.js';
import { buildAssetFamilies } from '../../utils/AssetExplorer.ts';
import { isTextInput } from '../../utils/Dom.ts';
import { errorMessage } from '../../utils/Errors.ts';
import { findKeybindingCommand, forgeActionForKeybinding } from '../../utils/Keybindings.ts';
import { parseSplinePointId, removeSplinePoints, splinePointId } from '../../utils/SplinePoints.ts';
import { EditorContext, type InstancedCollisionOverlay } from './EditorContext.ts';
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
import { GroupsPanel } from './GroupsPanel.tsx';
import { EditorErrorState } from './EditorPrimitives.tsx';
import { EditorStatusBar } from './EditorStatusBar.tsx';
import { ReferencesPanel } from './ReferencesPanel.tsx';
import { HudBankPanel } from './HudBankPanel.tsx';
import { FxTexturePanel } from './FxTexturePanel.tsx';

interface EditorWorkspaceProps {
  project: EditorSnapshot;
  keybindings: KeybindingMap;
  selectionColor: string;
  sceneTreeColors: SceneTreeColors;
  collisionVisualization: CollisionVisualization;
  hostStatus?: ForgeHostStatus;
  layoutAction?: { id: number; action: EditorLayoutAction };
  showViewportStats: boolean;
  onProjectChange(project: EditorSnapshot): void;
}

const components = {
  viewport: ViewportPanel,
  sceneTree: SceneTreePanel,
  groups: GroupsPanel,
  properties: PropertiesPanel,
  references: ReferencesPanel,
  levelSettings: LevelSettingsPanel,
  diagnostics: DiagnosticsPanel,
  build: BuildPanel,
  assetExplorer: AssetExplorerPanel,
  assetPreview: AssetPreviewPanel,
  hudBank: HudBankPanel,
  fxTextures: FxTexturePanel,
};

export function EditorWorkspace({
  project,
  keybindings,
  selectionColor,
  sceneTreeColors,
  collisionVisualization,
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
  const [cameraFocus, setCameraFocus] = useState<{ entityId: string }>();
  const [splinePointSelection, setSplinePointSelection] = useState<string[]>([]);
  const [skyCompositionSelected, setSkyCompositionSelected] = useState(false);
  const splinePointClipboard = useRef<ProjectVector4[]>([]);
  const [showOcclusionOctants, setShowOcclusionOctants] = useState(false);
  const [showTerrain, setShowTerrain] = useState(true);
  const [showSolidCollision, setShowSolidCollision] = useState(true);
  const [showPlayerBarriers, setShowPlayerBarriers] = useState(true);
  const [assetPreview, setAssetPreview] = useState<AssetExplorerFamily>();
  const [instancedCollisionOverlay, setInstancedCollisionOverlay] = useState<InstancedCollisionOverlay>();
  const [renderedInstancedCollisionEntityIds, setRenderedInstancedCollisionEntityIds] = useState<Set<string>>(
    () => new Set(),
  );
  const terrainProjectId = useRef<string | undefined>(undefined);
  const onReady = useCallback((event: DockviewReadyEvent) => setApi(event.api), []);

  useEffect(() => {
    let disposed = false;
    if (terrainProjectId.current !== project.projectId) {
      terrainProjectId.current = project.projectId;
      setTerrain(undefined);
      setCameraFocus(undefined);
      setAssetPreview(undefined);
      setInstancedCollisionOverlay(undefined);
      setRenderedInstancedCollisionEntityIds(new Set());
      setSplinePointSelection([]);
      setSkyCompositionSelected(false);
      splinePointClipboard.current = [];
    }
    setSceneLoad({ status: 'loading', label: 'Preparing render package…', completed: 0, total: 1 });
    const stopProgress = window.forge.onEditorTerrainProgress((progress) => {
      if (!disposed) setSceneLoad({ status: 'loading', label: 'Preparing render package…', ...progress });
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
        if (!api.getPanel('groups')) showEditorPanel(api, 'groups');
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

  const inspectAsset = useCallback((family: AssetExplorerFamily, activate = true) => {
    setAssetPreview(family);
    if (activate && api) showEditorPanel(api, 'assetPreview');
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

  const navigateToEntity = useCallback(async (
    entityId: string,
    action: 'select' | 'reveal' | 'focus',
  ) => {
    setSplinePointSelection([]);
    setSkyCompositionSelected(false);
    if (!await execute({ id: crypto.randomUUID(), kind: 'setSelection', entityIds: [entityId] })) return;
    if (action !== 'select' && api) showEditorPanel(api, 'sceneTree');
    if (action === 'focus') setCameraFocus({ entityId });
  }, [api, execute]);

  const inspectReferencedAsset = useCallback(async (assetId: string, assetKind: string) => {
    const category = referenceAssetCategory(assetKind);
    if (!category) {
      setError(`${assetKind} assets do not have an interactive preview.`);
      return;
    }
    setError(undefined);
    try {
      const page = await window.forge.queryAssetExplorer({ category, search: assetId, limit: 128 });
      const item = page.items.find((candidate) => candidate.assetId === assetId);
      const family = item ? buildAssetFamilies([item], {
        game: project.target.game,
        region: project.target.region,
        revision: project.target.revision,
        level: project.baseLevel.level,
      })[0] : assetKind === 'Texture' && projectTextureIds(project).has(assetId) ? {
        familyId: `project-texture:${assetId}`,
        category: 'textures' as const,
        displayLabel: `Project texture ${assetId.slice(0, 12)}`,
        variants: [{
          assetId,
          category: 'textures' as const,
          displayLabel: `Project texture ${assetId.slice(0, 12)}`,
          canonicalFormatVersion: 1,
          byteSize: 0,
          aliases: [],
          tags: ['texture', 'project-attached'],
          sources: [],
          classIds: [],
          previewState: 'notCached' as const,
          canPlace: false,
          placementDisabledReason: 'Project-attached texture.',
        }],
        representativeAssetId: assetId,
      } : undefined;
      if (!family) throw new Error(`Asset ${assetId} has no previewable catalog entry.`);
      inspectAsset(family);
    } catch (cause) {
      setError(errorMessage(cause));
    }
  }, [inspectAsset, project.baseLevel.level, project.target]);

  const showReferences = useCallback(() => {
    if (api) showEditorPanel(api, 'references');
  }, [api]);

  const previewInstancedCollision = useCallback(async (
    entityId: string,
    settings?: EditorInstancedCollisionGenerationSettings,
  ) => {
    setError(undefined);
    try { return await window.forge.previewInstancedCollision(entityId, settings); }
    catch (cause) {
      setError(errorMessage(cause));
      throw cause;
    }
  }, []);

  const inspectInstancedCollisionSource = useCallback(async (entityId: string) => {
    setError(undefined);
    try { return await window.forge.inspectInstancedCollisionSource(entityId); }
    catch (cause) {
      setError(errorMessage(cause));
      throw cause;
    }
  }, []);

  const cancelInstancedCollisionPreview = useCallback(() => window.forge.cancelInstancedCollisionPreview(), []);

  const applyInstancedCollisionPreview = useCallback(async (token: string) => {
    setBusy(true);
    setError(undefined);
    try {
      onProjectChange(await window.forge.applyInstancedCollisionPreview(crypto.randomUUID(), token));
      return true;
    }
    catch (cause) {
      setError(errorMessage(cause));
      return false;
    }
    finally { setBusy(false); }
  }, [onProjectChange]);

  const setInstancedCollisionRendered = useCallback((entityId: string, rendered: boolean) => {
    setRenderedInstancedCollisionEntityIds((current) => {
      if (current.has(entityId) === rendered) return current;
      const next = new Set(current);
      if (rendered) next.add(entityId);
      else next.delete(entityId);
      return next;
    });
  }, []);

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
    selectionColor,
    sceneTreeColors,
    collisionVisualization,
    terrain,
    sceneLoad,
    setSceneLoad,
    cameraFocus,
    setCameraFocus,
    splinePointSelection,
    setSplinePointSelection,
    skyCompositionSelected,
    setSkyCompositionSelected,
    showViewportStats,
    showOcclusionOctants,
    setShowOcclusionOctants,
    showTerrain,
    setShowTerrain,
    showSolidCollision,
    setShowSolidCollision,
    showPlayerBarriers,
    setShowPlayerBarriers,
    assetPreview,
    inspectAsset,
    inspectReferencedAsset,
    navigateToEntity,
    showReferences,
    busy,
    hostAvailable: Boolean(hostStatus),
    buildProgress,
    buildResult,
    build: buildAndPatch,
    cancelBuild: () => window.forge.cancelBuildAndPatch(),
    execute,
    previewInstancedCollision,
    inspectInstancedCollisionSource,
    cancelInstancedCollisionPreview,
    applyInstancedCollisionPreview,
    instancedCollisionOverlay,
    setInstancedCollisionOverlay,
    renderedInstancedCollisionEntityIds,
    setInstancedCollisionRendered,
    save: async () => {
      setBusy(true);
      setError(undefined);
      try { onProjectChange(await window.forge.saveEditorProject()); }
      catch (cause) { setError(errorMessage(cause)); }
      finally { setBusy(false); }
    },
  }), [applyInstancedCollisionPreview, assetPreview, buildAndPatch, buildProgress, buildResult, busy, cameraFocus,
    cancelInstancedCollisionPreview, execute,
    hostStatus, inspectAsset, inspectReferencedAsset, navigateToEntity, showReferences, keybindings,
    collisionVisualization, project, sceneLoad, sceneTreeColors, showOcclusionOctants, showPlayerBarriers, showSolidCollision,
    selectionColor, showTerrain,
    showViewportStats, skyCompositionSelected,
    splinePointSelection,
    terrain, previewInstancedCollision, inspectInstancedCollisionSource, instancedCollisionOverlay,
    renderedInstancedCollisionEntityIds, setInstancedCollisionRendered]);

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

function referenceAssetCategory(kind: string): AssetExplorerCategory | undefined {
  const categories: Partial<Record<string, AssetExplorerCategory>> = {
    Tie: 'ties', Shrub: 'shrubs', Moby: 'mobys', Sky: 'skyShells', Texture: 'textures',
  };
  return categories[kind];
}

function projectTextureIds(project: EditorSnapshot): Set<string> {
  return new Set([
    ...(project.hud?.sourceIcons.flatMap((icon) => icon.frames.flatMap((frame) => [
      frame.sourceTexture?.id, frame.effectiveTexture?.id,
    ])) ?? []),
    ...(project.hud?.additions.map((addition) => addition.texture.id) ?? []),
    ...(project.fx?.sourceTextures.flatMap((texture) => [
      texture.sourceTexture?.id, texture.effectiveTexture?.id,
    ]) ?? []),
    ...(project.fx?.additions.map((addition) => addition.texture.id) ?? []),
  ].filter((value): value is string => value !== undefined));
}
