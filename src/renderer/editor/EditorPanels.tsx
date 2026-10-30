import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye';
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash';
import {
  ActionIcon, Button, Group, Stack, Text,
} from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import { useMemo, useState } from 'react';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import {
  createAssetPlacementCommand, createSkyShellAddCommand, createTfragPlacementCommand,
} from '../../utils/AssetPlacement.ts';
import { formatCollisionType } from '../../utils/CollisionFormat.ts';
import { selectedMissingGroupMemberCount } from '../../utils/SceneGroups.ts';
import {
  buildSceneEntityGroups,
  buildTerrainTreeItems,
  entityTreeKind,
  entityTreeText,
  sceneEntityMatchesFilter,
} from '../../utils/SceneSelection.ts';
import { parseSplinePointId, splinePointId } from '../../utils/SplinePoints.ts';
import { EntityProperties, MultiEntityProperties } from './EntityProperties.tsx';
import { useEditor } from './EditorContext.ts';
import { useOcclusionViewport } from './OcclusionViewportContext.tsx';
import {
  EditorEmptyState,
  EditorFilter,
  EditorPanel,
  EditorTree,
} from './EditorPrimitives.tsx';
import { SceneViewport } from './SceneViewport.tsx';
import { groupTreeKind, SceneTreeLabel, SceneTreeNode } from './SceneTreeNodes.tsx';
import { SkyCompositionProperties } from './SkyCompositionProperties.tsx';

export function ViewportPanel() {
  const {
    project, keybindings, selectionColor, sceneTreeColors, collisionVisualization, terrain, cameraFocus, setCameraFocus, setSceneLoad,
    execute, busy, showViewportStats, showOcclusionOctants, showOcclusionOctantIds, occlusionSamples, showOcclusionSamples, splinePointSelection, setSplinePointSelection,
    occlusionEnabled, occlusionFrozen, occlusionVisibility,
    setSkyCompositionSelected, showSolidCollision, setShowSolidCollision,
    showPlayerBarriers, setShowPlayerBarriers, showDeathHeight, setShowDeathHeight, showTerrain,
    instancedCollisionOverlay, mobySimulationEnabled, setMobySimulationEnabled,
    mobySimulationPaused, setMobySimulationPaused,
  } = useEditor();
  const { state: occlusionViewportState, setState: setOcclusionViewportState } = useOcclusionViewport();
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
    projectId={project.projectId}
    projectRevision={project.lastEventSequence}
    game={project.target.game}
    references={project.references}
    fx={project.fx}
    keybindings={keybindings}
    selectionColor={selectionColor}
    sceneTreeColors={sceneTreeColors}
    collisionVisualization={collisionVisualization}
    focusEntityId={cameraFocus?.entityId}
    selection={splinePointSelection.length ? splinePointSelection : project.selection}
    missingSelectionMembers={splinePointSelection.length ? 0 : missingSelectionMembers}
    showStats={showViewportStats}
    showOcclusionOctants={showOcclusionOctants}
    showOcclusionOctantIds={showOcclusionOctantIds}
    occlusionSamples={showOcclusionSamples ? occlusionSamples : undefined}
    occlusionEnabled={occlusionEnabled}
    occlusionFrozen={occlusionFrozen}
    frozenOcclusionOctant={occlusionEnabled && occlusionFrozen ? occlusionViewportState.octant : undefined}
    occlusionVisibility={occlusionVisibility}
    onOcclusionStateChange={setOcclusionViewportState}
    showTerrain={showTerrain}
    showSolidCollision={showSolidCollision}
    showPlayerBarriers={showPlayerBarriers}
    showDeathHeight={showDeathHeight}
    mobySimulationEnabled={mobySimulationEnabled}
    mobySimulationPaused={mobySimulationPaused}
    instancedCollisionOverlay={instancedCollisionOverlay}
    terrain={terrain}
    environment={environment}
    levelSceneSettings={project.levelSettings?.scene}
    onFocusHandled={() => setCameraFocus(undefined)}
    onLoadProgress={setSceneLoad}
    onSolidCollisionVisibilityChange={setShowSolidCollision}
    onPlayerBarrierVisibilityChange={setShowPlayerBarriers}
    onDeathHeightVisibilityChange={setShowDeathHeight}
    onMobySimulationEnabledChange={setMobySimulationEnabled}
    onMobySimulationPausedChange={setMobySimulationPaused}
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
    onAssetDrop={(asset, position) => {
      const command = asset.kind === 'sky'
        ? createSkyShellAddCommand(asset)
        : asset.kind === 'tfrag'
          ? createTfragPlacementCommand(asset, position!, project.entities)
          : createAssetPlacementCommand(asset, position!);
      return command ? execute(command) : Promise.resolve(false);
    }}
  />;
}

export function SceneTreePanel() {
  const {
    project, terrain, setCameraFocus, execute, busy,
    splinePointSelection, setSplinePointSelection,
    skyCompositionSelected, setSkyCompositionSelected,
    showTerrain, setShowTerrain,
    sceneFilter: filter, setSceneFilter: setFilter,
  } = useEditor();
  const model = useMemo(() => buildSceneEntityGroups(
    project.entities.filter((entity) => !entity.skyShell && !entity.collision
      && !entity.tfrag && entity.geometry?.kind !== 'area'), filter,
  ), [filter, project.entities]);
  const tfragEntities = useMemo(() => {
    return project.entities.filter((entity) => entity.tfrag
      && sceneEntityMatchesFilter(entity, filter,
        `${entity.tfrag!.sourceKind} ${entity.tfrag!.sourceIndex} group ${entity.tfrag!.groupIndex} tfrag terrain`));
  }, [filter, project.entities]);
  const customTfragParents = useMemo(() => {
    const parents = new Map<string, EditorEntity[]>();
    for (const entity of project.entities) {
      const importId = entity.tfrag?.customImportId;
      if (!importId) continue;
      const key = customTfragParentValue(importId);
      const pieces = parents.get(key);
      if (pieces) pieces.push(entity);
      else parents.set(key, [entity]);
    }
    parents.forEach((pieces) => pieces.sort((left, right) =>
      left.tfrag!.groupIndex - right.tfrag!.groupIndex));
    return parents;
  }, [project.entities]);
  const sceneTreeSelection = useMemo(() => {
    const selected = new Set(project.selection);
    const values = [...project.selection];
    for (const [parent, pieces] of customTfragParents) {
      if (!pieces.length || !pieces.every((piece) => selected.has(piece.id))) continue;
      const members = new Set(pieces.map((piece) => piece.id));
      values.splice(0, values.length, ...values.filter((value) => !members.has(value)), parent);
    }
    return values;
  }, [customTfragParents, project.selection]);
  const legacyTfrags = useMemo(() => tfragEntities.length || project.entities.some((entity) => entity.tfrag)
    ? []
    : buildTerrainTreeItems(terrain?.urls ?? [], filter), [filter, project.entities, terrain, tfragEntities.length]);
  const sky = useMemo(() => {
    return project.entities.filter((entity) => entity.skyShell
      && sceneEntityMatchesFilter(entity, filter, `sky shell ${entity.skyShell.order + 1}`))
      .sort((left, right) => left.skyShell!.order - right.skyShell!.order);
  }, [filter, project.entities]);
  const collision = useMemo(() => {
    return project.entities.filter((entity) => entity.collision
      && sceneEntityMatchesFilter(entity, filter, collisionTreeText(entity, project.target.game)));
  }, [filter, project.entities, project.target.game]);
  const entityIds = useMemo(() => new Set(project.entities.map((entity) => entity.id)), [project.entities]);
  const nodes = useMemo<TreeNodeData[]>(() => [
    ...(tfragEntities.length || legacyTfrags.length ? [{
      value: 'render:tfrags',
      label: <span className="scene-tree-node">
        <SceneTreeLabel kind="tfrag">Terrain ({tfragEntities.length || legacyTfrags.length})</SceneTreeLabel>
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
      children: tfragEntities.length
        ? [...new Set(tfragEntities.map((entity) => entity.tfrag!.sourceIndex))]
          .sort((left, right) => left - right)
          .map((sourceIndex) => {
            const pieces = tfragEntities.filter((entity) => entity.tfrag!.sourceIndex === sourceIndex);
            const nativePieces = pieces.filter((entity) => !entity.tfrag!.customImportId);
            const importIds = [...new Set(pieces.map((entity) => entity.tfrag!.customImportId)
              .filter((value): value is string => value !== undefined))];
            return {
              value: `render:tfrags:${sourceIndex}`,
              label: <SceneTreeNode entities={pieces} disabled={busy}>
                <SceneTreeLabel kind="tfrag">
                  {sourceIndex === 0 ? 'Primary' : `Chunk ${sourceIndex}`} ({pieces.length})
                </SceneTreeLabel>
              </SceneTreeNode>,
              children: [
                ...nativePieces.map((entity) => ({
                  value: entity.id,
                  label: <SceneTreeNode entities={[entity]} disabled={busy}>
                    <SceneTreeLabel kind="tfrag" dot>Group #{entity.tfrag!.groupIndex}</SceneTreeLabel>
                  </SceneTreeNode>,
                  nodeProps: { selectable: true },
                })),
                ...importIds.map((importId) => {
                  const parent = customTfragParentValue(importId);
                  const importedPieces = customTfragParents.get(parent) ?? [];
                  const visiblePieces = pieces.filter((entity) => entity.tfrag!.customImportId === importId);
                  return {
                    value: parent,
                    label: <SceneTreeNode entities={importedPieces} disabled={busy}>
                      <SceneTreeLabel kind="tfrag">
                        {importedPieces[0]?.tfrag?.customSourceName ?? 'Imported terrain'} ({importedPieces.length})
                      </SceneTreeLabel>
                    </SceneTreeNode>,
                    nodeProps: { selectable: true },
                    children: visiblePieces.map((entity) => ({
                      value: entity.id,
                      label: <SceneTreeNode entities={[entity]} disabled={busy}>
                        <SceneTreeLabel kind="tfrag" dot>Group #{entity.tfrag!.groupIndex}</SceneTreeLabel>
                      </SceneTreeNode>,
                      nodeProps: { selectable: true },
                    })),
                  };
                }),
              ],
            };
          })
        : legacyTfrags.map((item) => ({
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
  ], [busy, collision, customTfragParents, legacyTfrags, model.groups, setShowTerrain, showTerrain, sky, tfragEntities]);
  const renderItemCount = tfragEntities.length + legacyTfrags.length + sky.length + collision.length;
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
            : splinePointSelection.length ? splinePointSelection : sceneTreeSelection}
          multiple
          onActivate={(value) => {
            const parentPiece = customTfragParents.get(value)?.[0];
            if (value !== 'render:sky')
              setCameraFocus({ entityId: parentPiece?.id ?? parseSplinePointId(value)?.entityId ?? value });
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
            const entityValues = values.flatMap((value) =>
              customTfragParents.get(value)?.map((entity) => entity.id) ?? [value]);
            void execute({
              id: crypto.randomUUID(), kind: 'setSelection',
              entityIds: [...new Set(entityValues.filter((value) => entityIds.has(value)))],
            });
          }}
        />
        : <EditorEmptyState message="No matching scene objects." />}
    </Stack>
  </EditorPanel>;
}

function customTfragParentValue(importId: string): string {
  return `render:tfrag-import:${importId}`;
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

export function EditorWatermark() {
  return <Text c="dimmed" className="editor-empty">Use View → Reset Layout to restore editor panels.</Text>;
}
