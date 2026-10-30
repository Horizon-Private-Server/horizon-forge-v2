import { DockviewReact, themeAbyss } from 'dockview-react';
import type { DockviewApi, DockviewReadyEvent } from 'dockview-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import type { KeybindingMap } from '../../types/Keybindings.js';
import type { AssetExplorerCategory, AssetExplorerFamily } from '../../types/AssetExplorer.js';
import type {
  EditorCommand, EditorEntity, EditorSnapshot, EditorInstancedCollisionGenerationSettings, EditorGroupTemplateRequest,
  ProjectVector4,
} from '../../types/EditorRuntime.js';
import type { SceneTreeColors } from '../../types/SceneTree.js';
import type { CollisionVisualization } from '../../types/CollisionVisualization.js';
import type { OcclusionSamplePreview, OcclusionVisibilityData } from '../../types/Occlusion.js';
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
import { applyEditorCommandResult } from '../../utils/EditorCommandResult.ts';
import { findKeybindingCommand, forgeActionForKeybinding } from '../../utils/Keybindings.ts';
import { parseSplinePointId, removeSplinePoints, splinePointId } from '../../utils/SplinePoints.ts';
import { EditorContext, type InstancedCollisionOverlay } from './EditorContext.ts';
import { BuildPanel } from './BuildPanel.tsx';
import { OcclusionPanel } from './OcclusionPanel.tsx';
import { OcclusionViewportProvider } from './OcclusionViewportContext.tsx';
import { occlusionSampleSceneKey } from './OcclusionSampleOverlay.ts';
import { AssetExplorerPanel } from './AssetExplorerPanel.tsx';
import { AssetPreviewPanel } from './AssetPreviewPanel.tsx';
import { AssetThumbnailRuntime } from './AssetThumbnailRuntime.ts';
import {
  createDefaultEditorLayout,
  decodeEditorLayout,
  panelForLayoutAction,
  showEditorPanel,
} from './EditorLayout.ts';
import {
  EditorWatermark,
  PropertiesPanel,
  SceneTreePanel,
  ViewportPanel,
} from './EditorPanels.tsx';
import { GroupsPanel } from './GroupsPanel.tsx';
import { LevelSettingsPanel } from './LevelSettingsPanel.tsx';
import { EditorStatusBar } from './EditorStatusBar.tsx';
import { ReferencesPanel } from './ReferencesPanel.tsx';
import { HudBankPanel } from './HudBankPanel.tsx';
import { FxTexturePanel } from './FxTexturePanel.tsx';
import { CustomAssetsPanel } from './CustomAssetsPanel.tsx';
import { SoundWorkspacePanel } from './SoundWorkspacePanel.tsx';
import { ImportBatchesPanel } from './ImportBatchesPanel.tsx';

import { LogPanel } from '../log/LogPanel.tsx';
import { forgeLog } from '../log/LogStore.ts';
import { useEditorLog } from '../log/UseEditorLog.ts';
import { useLogReporter } from '../log/UseLog.ts';

interface EditorWorkspaceProps {
  project: EditorSnapshot;
  keybindings: KeybindingMap;
  selectionColor: string;
  sceneTreeColors: SceneTreeColors;
  collisionVisualization: CollisionVisualization;
  hostStatus?: ForgeHostStatus;
  layoutAction?: { id: number; action: EditorLayoutAction };
  renderRevision: number;
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
  diagnostics: LogPanel,
  build: BuildPanel,
  occlusion: OcclusionPanel,
  assetExplorer: AssetExplorerPanel,
  customAssets: CustomAssetsPanel,
  assetPreview: AssetPreviewPanel,
  hudBank: HudBankPanel,
  fxTextures: FxTexturePanel,
  sounds: SoundWorkspacePanel,
  imports: ImportBatchesPanel,
};

export function EditorWorkspace({
  project,
  keybindings,
  selectionColor,
  sceneTreeColors,
  collisionVisualization,
  hostStatus,
  layoutAction,
  renderRevision,
  showViewportStats,
  onProjectChange,
}: EditorWorkspaceProps) {
  const [api, setApi] = useState<DockviewApi>();
  const [initialized, setInitialized] = useState(false);
  const reportError = useLogReporter('Editor');
  const [busy, setBusy] = useState(false);
  const [buildProgress, setBuildProgress] = useState<BuildPatchProgress>();
  const [buildResult, setBuildResult] = useState<BuildPatchResult>();
  const buildRunning = useRef(false);
  const [terrain, setTerrain] = useState<EditorTerrainSource>();
  const [sceneLoad, setSceneLoad] = useState<EditorLoadProgress>();
  useEditorLog(project, sceneLoad);
  const [sceneFilter, setSceneFilter] = useState('');
  const [activeImportBatchId, setActiveImportBatchId] = useState<string>();
  const [cameraFocus, setCameraFocus] = useState<{ entityId: string }>();
  const [splinePointSelection, setSplinePointSelection] = useState<string[]>([]);
  const [skyCompositionSelected, setSkyCompositionSelected] = useState(false);
  const splinePointClipboard = useRef<ProjectVector4[]>([]);
  const [showOcclusionOctants, setShowOcclusionOctants] = useState(false);
  const [showOcclusionOctantIds, setShowOcclusionOctantIds] = useState(false);
  const [samplePreview, setSamplePreview] = useState<{ data: OcclusionSamplePreview; sceneKey: string }>();
  const [showOcclusionSamples, setShowOcclusionSamples] = useState(false);
  const [occlusionSamplePullIn, setOcclusionSamplePullIn] = useState(2);
  const [occlusionPixelEdgeAllowance, setOcclusionPixelEdgeAllowance] = useState(false);
  const [occlusionEnabled, setOcclusionEnabled] = useState(true);
  const [occlusionFrozen, setOcclusionFrozen] = useState(false);
  const [occlusionData, setOcclusionData] = useState<{ projectId: string; value: OcclusionVisibilityData }>();
  const [occlusionVisibilityLoading, setOcclusionVisibilityLoading] = useState(false);
  const [occlusionVisibilityError, setOcclusionVisibilityError] = useState<string>();
  const [occlusionBakeRevision, setOcclusionBakeRevision] = useState(0);
  const occlusionVisibility = occlusionEnabled && occlusionData?.projectId === project.projectId ? occlusionData.value : undefined;
  const sampleSceneKey = useMemo(() => occlusionSampleSceneKey(project, renderRevision, occlusionSamplePullIn),
    [project.projectId, project.entities, project.target, project.customModels, renderRevision, occlusionSamplePullIn]);
  const occlusionSamplesStale = Boolean(samplePreview && samplePreview.sceneKey !== sampleSceneKey);
  const occlusionSamples = occlusionSamplesStale ? undefined : samplePreview?.data;
  const [showTerrain, setShowTerrain] = useState(true);
  const [showSolidCollision, setShowSolidCollision] = useState(true);
  const [showPlayerBarriers, setShowPlayerBarriers] = useState(true);
  const [showDeathHeight, setShowDeathHeight] = useState(true);
  const [mobySimulationEnabled, setMobySimulationEnabled] = useState(true);
  const [mobySimulationPaused, setMobySimulationPaused] = useState(false);
  const [assetPreview, setAssetPreview] = useState<AssetExplorerFamily>();
  const [assetThumbnails, setAssetThumbnails] = useState<AssetThumbnailRuntime | null>();
  const [instancedCollisionOverlay, setInstancedCollisionOverlay] = useState<InstancedCollisionOverlay>();
  const [renderedInstancedCollisionEntityIds, setRenderedInstancedCollisionEntityIds] = useState<Set<string>>(
    () => new Set(),
  );
  const terrainProjectId = useRef<string | undefined>(undefined);
  const projectRef = useRef(project);
  projectRef.current = project;
  const onReady = useCallback((event: DockviewReadyEvent) => setApi(event.api), []);
  const customTfragRevision = useMemo(() => project.entities
    .filter((entity) => entity.asset?.kind === 'Tfrag' && entity.tfrag?.customImportId)
    .sort((left, right) => left.tfrag!.groupIndex - right.tfrag!.groupIndex)
    .map((entity) => `${entity.id}:${entity.asset!.id}:${entity.tfrag!.sourceIndex}:${entity.tfrag!.groupIndex}:`
      + `${entity.tfrag!.sourcePosition?.x}:${entity.tfrag!.sourcePosition?.y}:${entity.tfrag!.sourcePosition?.z}`)
    .join('|'), [project.entities]);

  useEffect(() => {
    try {
      const runtime = new AssetThumbnailRuntime(project.target.game);
      setAssetThumbnails(runtime);
      return () => runtime.dispose();
    } catch {
      setAssetThumbnails(null);
    }
  }, [project.target.game]);

  useEffect(() => {
    let disposed = false;
    if (terrainProjectId.current !== project.projectId) {
      terrainProjectId.current = project.projectId;
      setTerrain(undefined);
      setSamplePreview(undefined);
      setShowOcclusionSamples(false);
      setOcclusionEnabled(true);
      setOcclusionFrozen(false);
      setOcclusionData(undefined);
      setCameraFocus(undefined);
      setAssetPreview(undefined);
      setInstancedCollisionOverlay(undefined);
      setRenderedInstancedCollisionEntityIds(new Set());
      setSplinePointSelection([]);
      setSkyCompositionSelected(false);
      setMobySimulationEnabled(true);
      setMobySimulationPaused(false);
      setSceneFilter('');
      setActiveImportBatchId(undefined);
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
  }, [customTfragRevision, project.projectId, renderRevision]);

  useEffect(() => {
    if (!occlusionEnabled) { setOcclusionFrozen(false); setOcclusionVisibilityLoading(false); setOcclusionVisibilityError(undefined); return; }
    let disposed = false;
    setOcclusionVisibilityLoading(true);
    setOcclusionVisibilityError(undefined);
    setOcclusionData(undefined);
    void window.forge.getOcclusionVisibility().then(value => {
      if (!disposed) setOcclusionData({ projectId: project.projectId, value });
    }).catch(cause => {
      if (!disposed) { setOcclusionVisibilityError(errorMessage(cause)); reportError(errorMessage(cause)); }
    }).finally(() => { if (!disposed) setOcclusionVisibilityLoading(false); });
    return () => { disposed = true; };
  }, [occlusionEnabled, project.projectId, occlusionBakeRevision]);

  useEffect(() => {
    if (!api) return;
    let disposed = false;
    let subscription: { dispose(): void } | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let pending: string | undefined;
    const save = (layout: string) => window.forge.setSetting('ui.editorLayout', layout)
      .catch((cause) => { if (!disposed) reportError(errorMessage(cause)); });

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
        if (!api.getPanel('occlusion')) { showEditorPanel(api, 'occlusion'); migratedLayout = true; }
        if (!api.getPanel('customAssets')) {
          showEditorPanel(api, 'customAssets');
          migratedLayout = true;
        }
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
      reportError(errorMessage(cause));
      setInitialized(true);
    });

    return () => {
      disposed = true;
      subscription?.dispose();
      if (timer) clearTimeout(timer);
      if (pending) void window.forge.setSetting('ui.editorLayout', pending)
        .catch((cause) => reportError(`Could not persist editor layout: ${errorMessage(cause)}`));
    };
  }, [api]);

  useEffect(() => {
    if (!api || !initialized || !layoutAction) return;
    if (layoutAction.action === 'resetLayout') createDefaultEditorLayout(api);
    else showEditorPanel(api, panelForLayoutAction(layoutAction.action));
  }, [api, initialized, layoutAction]);

  useEffect(() => window.forge.onBuildPatchProgress((progress) => {
    setBuildProgress(progress);
    forgeLog.sync('Build progress', [{ source: 'Build', severity: 'info',
      message: `${progress.phase}: ${progress.message}` }]);
  }), []);

  const buildAndPatch = useCallback(async (includedLayers: BuildLayerId[]) => {
    if (buildRunning.current) return;
    buildRunning.current = true;
    setBusy(true);
    setBuildResult(undefined);
    setBuildProgress({ phase: 'Preflight', completed: 0, total: 1, message: 'Starting build…' });
    forgeLog.sync('Build progress', []);
    forgeLog.write('Build', 'info', 'Starting build…');
    try {
      const result = await window.forge.buildAndPatchProject(includedLayers);
      setBuildResult(result);
      forgeLog.write('Build', result.succeeded ? 'success' : result.requiresWarningAcknowledgement ? 'warning' : 'error',
        [result.message, result.nextAction].filter(Boolean).join(' '));
      forgeLog.append(result.diagnostics.map((message) => ({ source: 'Build',
        severity: result.succeeded || result.requiresWarningAcknowledgement ? 'warning' : 'error', message })));
      onProjectChange(await window.forge.getEditorSnapshot());
    } catch (cause) {
      reportError(errorMessage(cause));
    } finally {
      buildRunning.current = false;
      setBusy(false);
    }
  }, [onProjectChange]);

  const inspectAsset = useCallback((family?: AssetExplorerFamily, activate = true) => {
    setAssetPreview(family);
    if (family && activate && api) showEditorPanel(api, 'assetPreview');
  }, [api]);

  const rebuildOcclusion = useCallback(async () => {
    if (buildRunning.current) return;
    buildRunning.current = true;
    setBusy(true);
    setBuildResult(undefined);
    setBuildProgress({ phase: 'Occlusion', completed: 0, total: 1, message: 'Starting occlusion rebuild…' });
    try {
      const result = await window.forge.rebuildOcclusion(occlusionSamplePullIn, occlusionPixelEdgeAllowance);
      setOcclusionBakeRevision(value => value + 1);
      const message = `Rebuilt ${result.octants.toLocaleString()} octants from ${result.origins.toLocaleString()} shared camera positions in ${(result.wallMs / 1000).toFixed(2)}s GPU worker time.`;
      setBuildProgress({ phase: 'Complete', completed: 1, total: 1, message });
      forgeLog.write('Occlusion', 'success', message);
      onProjectChange(await window.forge.getEditorSnapshot());
    } catch (cause) {
      reportError(errorMessage(cause));
    } finally {
      buildRunning.current = false;
      setBusy(false);
    }
  }, [onProjectChange, occlusionSamplePullIn, occlusionPixelEdgeAllowance]);

  const previewOcclusionSamples = useCallback(async () => {
    if (buildRunning.current) return;
    buildRunning.current = true;
    setBusy(true);
    setBuildProgress({ phase: 'Occlusion samples', completed: 0, total: 1, message: 'Qualifying occlusion samples…' });
    const signature = occlusionSampleSceneKey(projectRef.current, renderRevision, occlusionSamplePullIn);
    try {
      const data = await window.forge.previewOcclusionSamples(occlusionSamplePullIn);
      const next = await window.forge.getEditorSnapshot();
      if (occlusionSampleSceneKey(next, renderRevision, occlusionSamplePullIn) !== signature
        || occlusionSampleSceneKey(projectRef.current, renderRevision, occlusionSamplePullIn) !== signature)
        throw new Error('The scene changed while qualifying samples. Qualify samples again.');
      onProjectChange(next);
      setSamplePreview({ data, sceneKey: signature });
      setShowOcclusionOctants(true);
      setShowOcclusionSamples(true);
      const message = `Qualified ${data.origins.length.toLocaleString()} shared sample positions in ${data.cells.length.toLocaleString()} octants.`;
      setBuildProgress({ phase: 'Complete', completed: 1, total: 1, message });
      forgeLog.write('Occlusion samples', 'success', message);
    } catch (cause) {
      reportError(errorMessage(cause));
      setBuildProgress(undefined);
    } finally {
      buildRunning.current = false;
      setBusy(false);
    }
  }, [onProjectChange, renderRevision, occlusionSamplePullIn]);

  const execute = useCallback(async (command: EditorCommand) => {
    try {
      const next = applyEditorCommandResult(
        projectRef.current,
        await window.forge.executeEditorCommand(command),
      );
      projectRef.current = next;
      onProjectChange(next);
      return true;
    }
    catch (cause) {
      reportError(errorMessage(cause));
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
      reportError(`${assetKind} assets do not have an interactive preview.`);
      return;
    }
    try {
      const page = await window.forge.queryAssetExplorer({ category, search: assetId, limit: 128 });
      const item = page.items.find((candidate) => candidate.assetId === assetId);
      const family = item ? buildAssetFamilies([item], {
        game: project.target.game,
        region: project.target.region,
        revision: project.target.revision,
        level: project.baseLevel.level,
      })[0] : assetKind === 'Tie' && project.customModels.some((model) => model.assetId === assetId) ? {
        familyId: `project-custom-tie:${assetId}`,
        category: 'ties' as const,
        displayLabel: `Custom TIE ${assetId.slice(0, 12)}`,
        classId: project.customModels.find((model) => model.assetId === assetId)?.classId,
        variants: [{
          assetId,
          category: 'ties' as const,
          displayLabel: `Custom TIE ${assetId.slice(0, 12)}`,
          canonicalFormatVersion: 2,
          byteSize: 0,
          aliases: [],
          tags: ['tie', 'custom-model', 'project-attached'],
          sources: [],
          classIds: [project.customModels.find((model) => model.assetId === assetId)!.classId],
          previewState: 'notCached' as const,
          canPlace: true,
          previewRevision: project.customModels.find((model) => model.assetId === assetId)!.nativeAssetId,
        }],
        representativeAssetId: assetId,
      } : assetKind === 'Texture' && projectTextureIds(project).has(assetId) ? {
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
      reportError(errorMessage(cause));
    }
  }, [inspectAsset, project.baseLevel.level, project.customModels, project.target]);

  const selectedPreviewEntity = project.selection.length === 1
    ? project.entities.find((entity) => entity.id === project.selection[0])
    : undefined;
  const selectedCustomModel = selectedPreviewEntity?.asset?.kind === 'Tie'
    ? project.customModels.find((model) => model.assetId === selectedPreviewEntity.asset?.id)
    : undefined;
  useEffect(() => {
    inspectAsset(sceneEntityAssetFamily(project, selectedPreviewEntity), false);
  }, [inspectAsset, project.projectId, selectedCustomModel, selectedPreviewEntity?.asset?.id,
    selectedPreviewEntity?.asset?.kind, selectedPreviewEntity?.id, selectedPreviewEntity?.name, selectedPreviewEntity?.sourceClassId,
    selectedPreviewEntity?.skyShell?.sourceShellIndex]);

  const showReferences = useCallback(() => {
    if (api) showEditorPanel(api, 'references');
  }, [api]);

  const openImportBatch = useCallback((batchId: string) => {
    setActiveImportBatchId(batchId);
    if (api) showEditorPanel(api, 'imports');
  }, [api]);

  const filterSceneToImportBatch = useCallback((batchId: string) => {
    setSceneFilter(batchId);
    if (api) showEditorPanel(api, 'sceneTree');
  }, [api]);

  const removeImportBatch = useCallback(async (batchId: string, targetFingerprint: string) => {
    setBusy(true);
    try {
      onProjectChange(await window.forge.applyContentImportRemoval(batchId, targetFingerprint));
      forgeLog.write('Imports', 'success', `Removed import batch ${batchId}.`);
      return true;
    }
    catch (cause) {
      reportError(errorMessage(cause));
      return false;
    }
    finally { setBusy(false); }
  }, [onProjectChange]);

  const previewInstancedCollision = useCallback(async (
    entityId: string,
    settings?: EditorInstancedCollisionGenerationSettings,
  ) => {
    try { return await window.forge.previewInstancedCollision(entityId, settings); }
    catch (cause) {
      reportError(errorMessage(cause));
      throw cause;
    }
  }, []);

  const inspectInstancedCollisionSource = useCallback(async (entityId: string) => {
    try { return await window.forge.inspectInstancedCollisionSource(entityId); }
    catch (cause) {
      reportError(errorMessage(cause));
      throw cause;
    }
  }, []);

  const cancelInstancedCollisionPreview = useCallback(() => window.forge.cancelInstancedCollisionPreview(), []);

  const applyInstancedCollisionPreview = useCallback(async (token: string) => {
    setBusy(true);
    try {
      onProjectChange(await window.forge.applyInstancedCollisionPreview(crypto.randomUUID(), token));
      return true;
    }
    catch (cause) {
      reportError(errorMessage(cause));
      return false;
    }
    finally { setBusy(false); }
  }, [onProjectChange]);

  const previewGroupTemplate = useCallback((request: EditorGroupTemplateRequest) =>
    window.forge.previewGroupTemplate(request), []);

  const cancelGroupTemplatePreview = useCallback(() => window.forge.cancelGroupTemplatePreview(), []);

  const applyGroupTemplatePreview = useCallback(async (token: string) => {
    setBusy(true);
    try {
      onProjectChange(await window.forge.applyGroupTemplatePreview(crypto.randomUUID(), token));
      return true;
    }
    catch (cause) {
      reportError(errorMessage(cause));
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
    sceneFilter,
    setSceneFilter,
    cameraFocus,
    setCameraFocus,
    splinePointSelection,
    setSplinePointSelection,
    skyCompositionSelected,
    setSkyCompositionSelected,
    showViewportStats,
    occlusionEnabled,
    setOcclusionEnabled,
    occlusionFrozen,
    setOcclusionFrozen,
    occlusionVisibility,
    occlusionVisibilityLoading,
    occlusionVisibilityError,
    showOcclusionOctants,
    setShowOcclusionOctants,
    showOcclusionOctantIds,
    setShowOcclusionOctantIds,
    occlusionSamples,
    occlusionSamplesStale,
    showOcclusionSamples,
    setShowOcclusionSamples,
    showTerrain,
    setShowTerrain,
    showSolidCollision,
    setShowSolidCollision,
    showPlayerBarriers,
    setShowPlayerBarriers,
    showDeathHeight,
    setShowDeathHeight,
    mobySimulationEnabled,
    setMobySimulationEnabled,
    mobySimulationPaused,
    setMobySimulationPaused,
    assetPreview,
    assetThumbnails,
    inspectAsset,
    inspectReferencedAsset,
    navigateToEntity,
    activeImportBatchId,
    openImportBatch,
    filterSceneToImportBatch,
    removeImportBatch,
    showReferences,
    busy,
    hostAvailable: Boolean(hostStatus),
    buildProgress,
    buildResult,
    build: buildAndPatch,
    rebuildOcclusion,
    previewOcclusionSamples,
    occlusionSamplePullIn,
    setOcclusionSamplePullIn,
    occlusionPixelEdgeAllowance,
    setOcclusionPixelEdgeAllowance,
    cancelBuild: () => window.forge.cancelBuildAndPatch().catch((cause) => reportError(errorMessage(cause))),
    execute,
    previewInstancedCollision,
    inspectInstancedCollisionSource,
    cancelInstancedCollisionPreview,
    applyInstancedCollisionPreview,
    previewGroupTemplate,
    cancelGroupTemplatePreview,
    applyGroupTemplatePreview,
    instancedCollisionOverlay,
    setInstancedCollisionOverlay,
    renderedInstancedCollisionEntityIds,
    setInstancedCollisionRendered,
    save: async () => {
      setBusy(true);
      try {
        onProjectChange(await window.forge.saveEditorProject());
        forgeLog.write('Project', 'success', 'Project saved.');
      }
      catch (cause) { reportError(errorMessage(cause)); }
      finally { setBusy(false); }
    },
  }), [activeImportBatchId, applyGroupTemplatePreview, applyInstancedCollisionPreview, assetPreview, assetThumbnails, buildAndPatch, rebuildOcclusion, previewOcclusionSamples, occlusionSamples, occlusionSamplesStale, occlusionSamplePullIn, occlusionPixelEdgeAllowance, showOcclusionSamples, buildProgress, buildResult, busy, cameraFocus,
    cancelGroupTemplatePreview,
    cancelInstancedCollisionPreview, execute,
    filterSceneToImportBatch, hostStatus, inspectAsset, inspectReferencedAsset, navigateToEntity, openImportBatch, removeImportBatch, showReferences, keybindings,
    collisionVisualization, project, sceneFilter, sceneLoad, sceneTreeColors, showDeathHeight, showOcclusionOctants, showOcclusionOctantIds, showPlayerBarriers, showSolidCollision,
    occlusionEnabled, occlusionFrozen, occlusionVisibility, occlusionVisibilityLoading, occlusionVisibilityError,
    mobySimulationEnabled, mobySimulationPaused,
    selectionColor, showTerrain,
    showViewportStats, skyCompositionSelected,
    splinePointSelection,
    terrain, previewGroupTemplate, previewInstancedCollision, inspectInstancedCollisionSource, instancedCollisionOverlay,
    renderedInstancedCollisionEntityIds, setInstancedCollisionRendered]);

  return <EditorContext.Provider value={context}>
    <OcclusionViewportProvider projectId={project.projectId}>
      <div className="editor-workspace">
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
          onCancel={() => void window.forge.cancelBuildAndPatch().catch((cause) => reportError(errorMessage(cause)))}
          onShowLog={() => { if (api) showEditorPanel(api, 'diagnostics'); }}
        />
      </div>
    </OcclusionViewportProvider>
  </EditorContext.Provider>;
}

function referenceAssetCategory(kind: string): AssetExplorerCategory | undefined {
  const categories: Partial<Record<string, AssetExplorerCategory>> = {
    Tie: 'ties', Shrub: 'shrubs', Moby: 'mobys', Tfrag: 'tfrags', Sky: 'skyShells', Texture: 'textures',
  };
  return categories[kind];
}

function sceneEntityAssetFamily(
  project: EditorSnapshot,
  entity: EditorEntity | undefined,
): AssetExplorerFamily | undefined {
  if (!entity?.asset) return undefined;
  const category = referenceAssetCategory(entity.asset.kind);
  if (!category) return undefined;
  const customModel = entity.asset.kind === 'Tie'
    ? project.customModels.find((model) => model.assetId === entity.asset?.id)
    : undefined;
  const customTfrag = entity.tfrag?.customImportId !== undefined;
  const classId = customModel?.classId ?? entity.sourceClassId;
  const shellIndex = entity.skyShell?.sourceShellIndex;
  const displayLabel = customModel?.sourceName ?? entity.tfrag?.customSourceName ?? entity.name;
  const item = {
    assetId: entity.asset.id,
    category,
    displayLabel,
    canonicalFormatVersion: customModel ? 2 : 0,
    byteSize: 0,
    aliases: [],
    tags: customModel ? ['tie', 'custom-model', 'project-attached']
      : customTfrag ? ['tfrag', 'custom-tfrag', 'project-attached'] : ['project-attached'],
    sources: [],
    classIds: classId === undefined ? [] : [classId],
    previewState: 'notCached' as const,
    canPlace: classId !== undefined && category !== 'skyShells' && category !== 'textures',
    ...(shellIndex === undefined ? {} : { shellIndex }),
    ...(customModel ? { previewRevision: customModel.nativeAssetId }
      : customTfrag ? { previewRevision: entity.tfrag!.customImportId } : {}),
  };
  return {
    familyId: `scene:${entity.asset.id}:${shellIndex ?? ''}`,
    category,
    displayLabel,
    classId,
    variants: [item],
    representativeAssetId: item.assetId,
  };
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
