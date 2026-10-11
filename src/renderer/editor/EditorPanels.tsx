import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye';
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash';
import {
  ActionIcon, Alert, Button, Group, Stack, Text,
} from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import { useMemo, useState } from 'react';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import { createAssetPlacementCommand, createSkyShellAddCommand } from '../../utils/AssetPlacement.ts';
import { formatCollisionType } from '../../utils/CollisionFormat.ts';
import { selectedMissingGroupMemberCount } from '../../utils/SceneGroups.ts';
import {
  buildSceneEntityGroups,
  buildTerrainTreeItems,
  entityTreeKind,
  entityTreeText,
} from '../../utils/SceneSelection.ts';
import { parseSplinePointId, splinePointId } from '../../utils/SplinePoints.ts';
import { EntityProperties, MultiEntityProperties } from './EntityProperties.tsx';
import { useEditor } from './EditorContext.ts';
import {
  EditorEmptyState,
  EditorFilter,
  EditorPanel,
  EditorProgressState,
  EditorTree,
} from './EditorPrimitives.tsx';
import { SceneViewport } from './SceneViewport.tsx';
import { groupTreeKind, SceneTreeLabel, SceneTreeNode } from './SceneTreeNodes.tsx';
import { SkyCompositionProperties } from './SkyCompositionProperties.tsx';

export function ViewportPanel() {
  const {
    project, keybindings, selectionColor, sceneTreeColors, collisionVisualization, terrain, cameraFocus, setCameraFocus, setSceneLoad,
    execute, busy, showViewportStats, showOcclusionOctants, splinePointSelection, setSplinePointSelection,
    setSkyCompositionSelected, showSolidCollision, setShowSolidCollision,
    showPlayerBarriers, setShowPlayerBarriers, showDeathHeight, setShowDeathHeight, showTerrain,
    instancedCollisionOverlay,
  } = useEditor();
  const appearance = project.levelSettings && {
    backgroundColor: project.levelSettings.backgroundColor,
    fogColor: project.levelSettings.fogColor,
    fogNearDistance: project.levelSettings.fogNearDistance,
    fogFarDistance: project.levelSettings.fogFarDistance,
    fogNearIntensity: project.levelSettings.fogNearIntensity,
    fogFarIntensity: project.levelSettings.fogFarIntensity,
  };
  const appearanceSignature = JSON.stringify(appearance);
  const environment = useMemo(() => appearance
    ? { ...terrain?.environment, ...appearance }
    : terrain?.environment, [appearanceSignature, terrain?.environment]);
  const missingSelectionMembers = useMemo(() => selectedMissingGroupMemberCount(
    project.selection,
    project.entities,
    project.groups,
    project.mapGroups,
    project.references,
  ), [project.entities, project.groups, project.mapGroups, project.references, project.selection]);
  return <SceneViewport
    disabled={busy}
    entities={project.entities}
    keybindings={keybindings}
    selectionColor={selectionColor}
    sceneTreeColors={sceneTreeColors}
    collisionVisualization={collisionVisualization}
    focusEntityId={cameraFocus?.entityId}
    selection={splinePointSelection.length ? splinePointSelection : project.selection}
    missingSelectionMembers={splinePointSelection.length ? 0 : missingSelectionMembers}
    showStats={showViewportStats}
    showOcclusionOctants={showOcclusionOctants}
    showTerrain={showTerrain}
    showSolidCollision={showSolidCollision}
    showPlayerBarriers={showPlayerBarriers}
    showDeathHeight={showDeathHeight}
    instancedCollisionOverlay={instancedCollisionOverlay}
    terrain={terrain}
    environment={environment}
    levelSceneSettings={project.levelSettings?.scene}
    onFocusHandled={() => setCameraFocus(undefined)}
    onLoadProgress={setSceneLoad}
    onSolidCollisionVisibilityChange={setShowSolidCollision}
    onPlayerBarrierVisibilityChange={setShowPlayerBarriers}
    onDeathHeightVisibilityChange={setShowDeathHeight}
    onSelectionChange={(values) => {
      setSkyCompositionSelected(false);
      const points = values.filter((value) => parseSplinePointId(value));
      if (points.length) {
        const entityId = parseSplinePointId(points.at(-1)!)!.entityId;
        setSplinePointSelection(points.filter((value) => parseSplinePointId(value)?.entityId === entityId));
        if (project.selection.length) void execute({ id: crypto.randomUUID(), kind: 'setSelection', entityIds: [] });
      } else {
        setSplinePointSelection([]);
        void execute({ id: crypto.randomUUID(), kind: 'setSelection', entityIds: values });
      }
    }}
    onTransformsCommit={(transforms) => execute({
      id: crypto.randomUUID(), kind: 'updateTransforms',
      entityIds: transforms.map((value) => value.entityId), transforms,
    })}
    onLevelPositionCommit={(field, value) => {
      const levelSettings = project.levelSettings;
      if (!levelSettings?.scene) return Promise.resolve(false);
      return execute({
        id: crypto.randomUUID(), kind: 'updateLevelSettings', entityIds: [],
        levelSettings: {
          ...levelSettings,
          scene: { ...levelSettings.scene, [field]: value },
        },
      });
    }}
    onSplinePointsCommit={(entityId, points) => execute({
      id: crypto.randomUUID(), kind: 'updateSplinePoints', entityIds: [entityId], points,
    })}
    onAssetDrop={(asset, position) => execute(asset.kind === 'sky'
      ? createSkyShellAddCommand(asset)
      : createAssetPlacementCommand(asset, position!))}
  />;
}

export function SceneTreePanel() {
  const {
    project, terrain, setCameraFocus, execute, busy,
    splinePointSelection, setSplinePointSelection,
    skyCompositionSelected, setSkyCompositionSelected,
    showTerrain, setShowTerrain,
  } = useEditor();
  const [filter, setFilter] = useState('');
  const model = useMemo(() => buildSceneEntityGroups(
    project.entities.filter((entity) => !entity.skyShell && !entity.collision
      && entity.geometry?.kind !== 'area'), filter,
  ), [filter, project.entities]);
  const tfrags = useMemo(() => buildTerrainTreeItems(terrain?.urls ?? [], filter), [filter, terrain]);
  const sky = useMemo(() => {
    const query = filter.trim().toLocaleLowerCase();
    return project.entities.filter((entity) => entity.skyShell
      && (!query || `${entity.name} ${entity.layer} sky shell ${entity.skyShell.order + 1}`
        .toLocaleLowerCase().includes(query)))
      .sort((left, right) => left.skyShell!.order - right.skyShell!.order);
  }, [filter, project.entities]);
  const collision = useMemo(() => {
    const query = filter.trim().toLocaleLowerCase();
    return project.entities.filter((entity) => entity.collision
      && (!query || collisionTreeText(entity, project.target.game).toLocaleLowerCase().includes(query)));
  }, [filter, project.entities, project.target.game]);
  const entityIds = useMemo(() => new Set(project.entities.map((entity) => entity.id)), [project.entities]);
  const nodes = useMemo<TreeNodeData[]>(() => [
    ...(tfrags.length ? [{
      value: 'render:tfrags',
      label: <span className="scene-tree-node">
        <SceneTreeLabel kind="tfrag">Terrain ({tfrags.length})</SceneTreeLabel>
        <span className="scene-tree-actions">
          <ActionIcon
            aria-label={`${showTerrain ? 'Hide' : 'Show'} terrain`}
            color={showTerrain ? 'blue' : 'gray'}
            disabled={busy}
            size="xs"
            title={`${showTerrain ? 'Hide' : 'Show'} terrain`}
            variant="subtle"
            onClick={(event) => {
              event.stopPropagation();
              setShowTerrain(!showTerrain);
            }}
          >
            {showTerrain ? <EyeIcon size={13} /> : <EyeSlashIcon size={13} />}
          </ActionIcon>
        </span>
      </span>,
      children: tfrags.map((item) => ({
        ...item,
        label: <SceneTreeLabel kind="tfrag" dot>{item.label.replace(/ tfrag$/i, '')}</SceneTreeLabel>,
        nodeProps: { selectable: false },
      })),
    }] : []),
    ...(sky.length ? [{
      value: 'render:sky',
      label: <SceneTreeNode entities={sky} disabled={busy}>
        <SceneTreeLabel kind="sky">Sky ({sky.length})</SceneTreeLabel>
      </SceneTreeNode>,
      nodeProps: { selectable: true },
      children: sky.map((entity) => ({
        value: entity.id,
        label: <SceneTreeNode entities={[entity]} disabled={busy}>
          <SceneTreeLabel kind="sky" dot>#{entity.skyShell!.order + 1} · {entity.name}</SceneTreeLabel>
        </SceneTreeNode>,
        nodeProps: { selectable: true },
      })),
    }] : []),
    ...(collision.length ? [{
      value: 'render:collision',
      label: <SceneTreeNode entities={collision} disabled={busy}>
        <SceneTreeLabel kind="collision">Collision ({collision.length})</SceneTreeLabel>
      </SceneTreeNode>,
      children: [...new Set(collision.map((entity) => entity.collision!.sourcePayloadIndex))]
        .sort((left, right) => left - right)
        .map((payloadIndex) => {
          const payload = collision.filter((entity) => entity.collision!.sourcePayloadIndex === payloadIndex);
          return {
            value: `render:collision:${payloadIndex}`,
            label: <SceneTreeNode entities={payload} disabled={busy}>
              <SceneTreeLabel kind="collision">{payloadIndex === 0 ? 'Primary' : `Chunk ${payloadIndex}`}</SceneTreeLabel>
            </SceneTreeNode>,
            children: (['solid', 'playerBarrier'] as const).flatMap((kind) => {
              const pieces = payload.filter((entity) => entity.collision!.kind === kind);
              return pieces.length ? [{
                value: `render:collision:${payloadIndex}:${kind}`,
                label: <SceneTreeNode entities={pieces} disabled={busy}>
                  <SceneTreeLabel kind={kind === 'solid' ? 'collision' : 'playerBarrier'}>
                    {kind === 'solid' ? 'Solid collision' : 'Player barriers'} ({pieces.length})
                  </SceneTreeLabel>
                </SceneTreeNode>,
                children: pieces.map((entity) => ({
                  value: entity.id,
                  label: <SceneTreeNode entities={[entity]} disabled={busy}>
                    <SceneTreeLabel kind={kind === 'solid' ? 'collision' : 'playerBarrier'} dot>
                      {collisionTreeText(entity, project.target.game)}
                    </SceneTreeLabel>
                  </SceneTreeNode>,
                  nodeProps: { selectable: true },
                })),
              }] : [];
            }),
          };
        }),
    }] : []),
    ...model.groups.map((group) => ({
      value: `layer:${group.layer}`,
      label: <SceneTreeNode entities={group.entities} disabled={busy}>
        <SceneTreeLabel kind={groupTreeKind(group.entities)}>{group.layer} ({group.entities.length})</SceneTreeLabel>
      </SceneTreeNode>,
      children: group.entities.map((entity) => ({
        value: entity.id,
        label: <SceneTreeNode entities={[entity]} disabled={busy}>
          <SceneTreeLabel kind={entityTreeKind(entity)} dot>{entityTreeText(entity)}</SceneTreeLabel>
        </SceneTreeNode>,
        nodeProps: { selectable: true },
        children: entity.geometry?.kind === 'spline' || entity.geometry?.kind === 'grindPath'
          ? entity.geometry.points.map((point, index) => ({
            value: splinePointId(entity.id, index),
            label: <SceneTreeLabel kind={entityTreeKind(entity)} dot>
              Point {index} · {point.x.toFixed(2)}, {point.y.toFixed(2)}, {point.z.toFixed(2)}
            </SceneTreeLabel>,
          }))
          : undefined,
      })),
    })),
  ], [busy, collision, model.groups, setShowTerrain, showTerrain, sky, tfrags]);
  const renderItemCount = tfrags.length + sky.length + collision.length;
  const matched = model.matched + renderItemCount;

  return <EditorPanel label="Scene hierarchy">
    <Stack>
      <Group justify="space-between">
        <Text size="xs" c={project.isDirty ? 'yellow' : 'dimmed'}>{project.isDirty ? 'Unsaved changes' : 'Saved'}</Text>
        <Text size="xs" c="dimmed">{matched} matches</Text>
      </Group>
      <EditorFilter label="Filter scene hierarchy" value={filter} onChange={setFilter} />
      {nodes.length
        ? <EditorTree
          label="Scene hierarchy"
          nodes={nodes}
          draggableValues={entityIds}
          selected={skyCompositionSelected
            ? ['render:sky']
            : splinePointSelection.length ? splinePointSelection : project.selection}
          multiple
          onActivate={(value) => {
            if (value !== 'render:sky') setCameraFocus({ entityId: parseSplinePointId(value)?.entityId ?? value });
          }}
          onSelectionChange={(values) => {
            if (values.includes('render:sky')) {
              setSkyCompositionSelected(true);
              setSplinePointSelection([]);
              if (project.selection.length) void execute({
                id: crypto.randomUUID(), kind: 'setSelection', entityIds: [],
              });
              return;
            }
            setSkyCompositionSelected(false);
            const points = values.map(parseSplinePointId).filter((value) => value !== undefined);
            if (points.length) {
              const entityId = points.at(-1)!.entityId;
              setSplinePointSelection(values.filter((value) => parseSplinePointId(value)?.entityId === entityId));
              if (project.selection.length) void execute({
                id: crypto.randomUUID(), kind: 'setSelection', entityIds: [],
              });
              return;
            }
            setSplinePointSelection([]);
            void execute({
              id: crypto.randomUUID(), kind: 'setSelection', entityIds: values.filter((value) => entityIds.has(value)),
            });
          }}
        />
        : <EditorEmptyState message="No matching scene objects." />}
    </Stack>
  </EditorPanel>;
}

function collisionTreeText(entity: EditorEntity, targetGame: string): string {
  const piece = entity.collision!;
  const types = piece.types.length === 0
    ? 'Barrier'
    : piece.types.length === 1
      ? formatCollisionType(piece.types[0].rawType, targetGame)
      : `${piece.types.length} mixed raw types`;
  return `#${piece.sourcePieceIndex} · ${piece.faceCount} face${piece.faceCount === 1 ? '' : 's'} · ${types}`;
}

export function PropertiesPanel() {
  const { project, splinePointSelection, skyCompositionSelected } = useEditor();
  const pointEntityId = parseSplinePointId(splinePointSelection[0] ?? '')?.entityId;
  const entities = (pointEntityId ? [pointEntityId] : project.selection)
    .map((id) => project.entities.find((entity) => entity.id === id))
    .filter((entity): entity is EditorEntity => Boolean(entity));

  return <EditorPanel label="Properties">
    {skyCompositionSelected
      ? <SkyCompositionProperties />
      : entities.length === 1
      ? <EntityProperties entity={entities[0]} />
      : entities.length > 1
        ? <MultiEntityProperties entities={entities} />
        : <EditorEmptyState message="Select an object to inspect its properties." />}
  </EditorPanel>;
}

export function DiagnosticsPanel() {
  const { project, sceneLoad } = useEditor();
  return <EditorPanel label="Diagnostics">
    <Stack>
      {sceneLoad?.status === 'loading' && <EditorProgressState
        label={sceneLoad.label}
        completed={sceneLoad.completed}
        total={sceneLoad.total}
      />}
      {sceneLoad?.status === 'error' && <Alert color="red" title="Scene loading failed">
        {sceneLoad.label}
      </Alert>}
      {project.baseLevel.missingAssetCount > 0 && <Alert color="yellow" title="Base assets missing">
        {project.baseLevel.missingAssetCount} source instances were unavailable when this project was created.
        Re-import the source catalog and recreate the project to include them.
      </Alert>}
      {project.diagnostics.map((diagnostic) => <Alert
        color={diagnostic.severity === 'error' ? 'red' : diagnostic.severity === 'warning' ? 'yellow' : 'blue'}
        title={diagnostic.code}
        key={`${diagnostic.code}:${diagnostic.message}`}
      >{diagnostic.message}</Alert>)}
      {!sceneLoad && !project.baseLevel.missingAssetCount && !project.diagnostics.length
        && <EditorEmptyState message="No diagnostics." />}
      <Button variant="default" onClick={() => void window.forge.revealLogs()}>Open detailed logs</Button>
    </Stack>
  </EditorPanel>;
}

export function EditorWatermark() {
  return <Text c="dimmed" className="editor-empty">Use View → Reset Layout to restore editor panels.</Text>;
}
