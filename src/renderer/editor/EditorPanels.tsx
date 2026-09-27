import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye';
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash';
import { PowerIcon } from '@phosphor-icons/react/dist/csr/Power';
import {
  ActionIcon, Alert, Badge, Button, Checkbox, Code, Group, Slider, Stack, Text,
} from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import type { ReactNode } from 'react';
import { useEffect, useMemo, useState } from 'react';

import type { EditorEntity, EditorLevelSettings } from '../../types/EditorRuntime.js';
import type { SceneTreeKind } from '../../types/SceneTree.js';
import { DEFAULT_SCENE_TREE_COLORS, SCENE_TREE_LABELS } from '../../utils/SceneTreeColors.ts';
import { parseSplinePointId, splinePointId } from '../../utils/SplinePoints.ts';
import { ColorPickerInput } from '../ColorPickerInput.tsx';
import { EntityProperties, MultiEntityProperties } from './EntityProperties.tsx';
import { useEditor } from './EditorContext.ts';
import {
  buildSceneEntityGroups,
  buildSkyTreeItems,
  buildTerrainTreeItems,
  entityTreeKind,
  entityTreeText,
} from './EditorPanelState.ts';
import {
  EditorEmptyState,
  EditorFilter,
  EditorPanel,
  EditorProgressState,
  EditorProperty,
  EditorPropertyGrid,
  EditorTree,
} from './EditorPrimitives.tsx';
import { SceneViewport } from './SceneViewport.tsx';

export function ViewportPanel() {
  const {
    project, keybindings, sceneTreeColors, terrain, cameraFocus, setCameraFocus, setSceneLoad, setSkyPieces,
    execute, busy, showViewportStats, showOcclusionOctants, splinePointSelection, setSplinePointSelection,
  } = useEditor();
  const levelSettingsSignature = JSON.stringify(project.levelSettings);
  const environment = useMemo(() => project.levelSettings
    ? { ...terrain?.environment, ...project.levelSettings }
    : terrain?.environment, [levelSettingsSignature, terrain?.environment]);
  return <SceneViewport
    disabled={busy}
    entities={project.entities}
    keybindings={keybindings}
    sceneTreeColors={sceneTreeColors}
    focusEntityId={cameraFocus?.entityId}
    selection={splinePointSelection.length ? splinePointSelection : project.selection}
    showStats={showViewportStats}
    showOcclusionOctants={showOcclusionOctants}
    terrain={terrain}
    environment={environment}
    onFocusHandled={() => setCameraFocus(undefined)}
    onLoadProgress={setSceneLoad}
    onSkyPiecesChange={setSkyPieces}
    onSelectionChange={(values) => {
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
    onSplinePointsCommit={(entityId, points) => execute({
      id: crypto.randomUUID(), kind: 'updateSplinePoints', entityIds: [entityId], points,
    })}
  />;
}

export function LevelSettingsPanel() {
  const { project, terrain, execute, busy, showOcclusionOctants, setShowOcclusionOctants } = useEditor();
  const editable = project.levelSettings;
  const settings = editable ? { ...terrain?.environment, ...editable } : terrain?.environment;
  if (!settings) return <EditorPanel label="Level settings">
    <EditorEmptyState message="Level settings are unavailable." />
  </EditorPanel>;
  const update = (value: Partial<EditorLevelSettings>) => {
    if (!editable) return;
    void execute({
      id: crypto.randomUUID(), kind: 'updateLevelSettings', entityIds: [],
      levelSettings: { ...editable, ...value },
    });
  };
  const rgb = (value: readonly number[]) => `rgb(${value.join(', ')})`;
  const vector = (value: readonly number[]) => value.map((item) => item.toFixed(2)).join(', ');
  const fogDistanceMax = Math.max(1000, settings.fogNearDistance, settings.fogFarDistance);
  return <EditorPanel label="Level settings">
    <Stack>
      {!editable && <Text size="xs" c="yellow">Migrate this project to edit imported level settings.</Text>}
      <Checkbox
        checked={showOcclusionOctants}
        label={`Show occlusion octants (${(terrain?.occlusionOctants.length ?? 0).toLocaleString()})`}
        onChange={(event) => setShowOcclusionOctants(event.currentTarget.checked)}
      />
      <LevelColorInput label="Background color" value={rgb(settings.backgroundColor)} disabled={!editable || busy}
        onCommit={(value) => update({ backgroundColor: value })} />
      <LevelColorInput label="Fog color" value={rgb(settings.fogColor)} disabled={!editable || busy}
        onCommit={(value) => update({ fogColor: value })} />
      <LevelSlider label="Fog near distance" value={settings.fogNearDistance} max={fogDistanceMax}
        precision={2} disabled={!editable || busy} onCommit={(value) => update({ fogNearDistance: value })} />
      <LevelSlider label="Fog far distance" value={settings.fogFarDistance} max={fogDistanceMax}
        precision={2} disabled={!editable || busy} onCommit={(value) => update({ fogFarDistance: value })} />
      <LevelSlider label="Fog near intensity" value={settings.fogNearIntensity} max={255}
        disabled={!editable || busy} onCommit={(value) => update({ fogNearIntensity: value })} />
      <LevelSlider label="Fog far intensity" value={settings.fogFarIntensity} max={255}
        disabled={!editable || busy} onCommit={(value) => update({ fogFarIntensity: value })} />
      <EditorPropertyGrid>
        <EditorProperty label="Death height">{(settings.deathHeight ?? 0).toFixed(2)}</EditorProperty>
        <EditorProperty label="Spherical world">{settings.isSphericalWorld ? 'Yes' : 'No'}</EditorProperty>
        <EditorProperty label="Sphere center"><Code>{vector(settings.sphereCenter ?? [0, 0, 0])}</Code></EditorProperty>
        <EditorProperty label="Ship position"><Code>{vector(settings.shipPosition ?? [0, 0, 0])}</Code></EditorProperty>
        <EditorProperty label="Ship rotation Z">{(settings.shipRotationZ ?? 0).toFixed(3)}</EditorProperty>
        <EditorProperty label="Ship path">{settings.shipPath ?? -1}</EditorProperty>
        <EditorProperty label="Ship camera cuboids">
          {settings.shipCameraCuboidStart ?? -1} → {settings.shipCameraCuboidEnd ?? -1}
        </EditorProperty>
        <EditorProperty label="Chunk planes">{settings.chunkPlaneCount ?? 0}</EditorProperty>
        <EditorProperty label="Core sounds">{settings.coreSoundsCount ?? 0}</EditorProperty>
      </EditorPropertyGrid>
    </Stack>
  </EditorPanel>;
}

function LevelColorInput({ label, value, disabled, onCommit }: {
  label: string;
  value: string;
  disabled: boolean;
  onCommit(value: [number, number, number]): void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return <ColorPickerInput
    label={label} value={draft} format="rgb" disabled={disabled}
    onChange={setDraft} onChangeEnd={(next) => onCommit(parseRgb(next))}
  />;
}

function LevelSlider({ label, value, max, precision = 0, disabled, onCommit }: {
  label: string;
  value: number;
  max: number;
  precision?: number;
  disabled: boolean;
  onCommit(value: number): void;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return <Stack gap={2}>
    <Group justify="space-between"><Text size="xs" fw={500}>{label}</Text><Code>{draft.toFixed(precision)}</Code></Group>
    <Slider disabled={disabled} min={0} max={max} step={precision ? 0.01 : 1} value={draft}
      thumbLabel={label} onChange={setDraft} onChangeEnd={onCommit} />
  </Stack>;
}

function parseRgb(value: string): [number, number, number] {
  const channels = value.match(/\d+/g)?.slice(0, 3).map(Number);
  return channels?.length === 3 ? channels.map((channel) => Math.min(255, channel)) as [number, number, number]
    : [0, 0, 0];
}

export function SceneTreePanel() {
  const {
    project, terrain, skyPieces, setCameraFocus, execute, busy,
    splinePointSelection, setSplinePointSelection,
  } = useEditor();
  const [filter, setFilter] = useState('');
  const model = useMemo(() => buildSceneEntityGroups(project.entities, filter), [filter, project.entities]);
  const tfrags = useMemo(() => buildTerrainTreeItems(terrain?.urls ?? [], filter), [filter, terrain]);
  const sky = useMemo(() => buildSkyTreeItems(skyPieces, filter), [filter, skyPieces]);
  const entityIds = useMemo(() => new Set(project.entities.map((entity) => entity.id)), [project.entities]);
  const nodes = useMemo<TreeNodeData[]>(() => [
    ...(tfrags.length ? [{
      value: 'render:tfrags',
      label: <SceneTreeLabel kind="tfrag">({tfrags.length})</SceneTreeLabel>,
      children: tfrags.map((item) => ({
        ...item,
        label: <SceneTreeLabel kind="tfrag" dot>{item.label.replace(/ tfrag$/i, '')}</SceneTreeLabel>,
        nodeProps: { selectable: false },
      })),
    }] : []),
    ...(sky.length ? [{
      value: 'render:sky',
      label: <SceneTreeLabel kind="sky">({sky.length})</SceneTreeLabel>,
      children: sky.map((item) => ({
        ...item,
        label: <SceneTreeLabel kind="sky" dot>{item.label.replace(/^sky /i, '')}</SceneTreeLabel>,
        nodeProps: { selectable: false },
      })),
    }] : []),
    ...model.groups.map((group) => ({
      value: `layer:${group.layer}`,
      label: <SceneTreeNode entities={group.entities} disabled={busy}>
        <SceneTreeLabel kind={groupTreeKind(group.entities)}>({group.entities.length})</SceneTreeLabel>
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
  ], [busy, model.groups, sky, tfrags]);
  const renderItemCount = tfrags.length + sky.length;
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
          selected={splinePointSelection.length ? splinePointSelection : project.selection}
          multiple
          onActivate={(value) => setCameraFocus({ entityId: parseSplinePointId(value)?.entityId ?? value })}
          onSelectionChange={(values) => {
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

function SceneTreeNode({ entities, disabled, children }: {
  entities: readonly EditorEntity[];
  disabled: boolean;
  children: ReactNode;
}) {
  const { execute } = useEditor();
  const ids = entities.map((entity) => entity.id);
  const allHidden = entities.every((entity) => entity.state.hidden);
  const allDisabled = entities.every((entity) => entity.state.disabled);
  const locked = entities.some((entity) => entity.state.locked);
  const description = entities.length === 1 ? entities[0].name : `${entities.length} objects`;
  const change = (state: { hidden?: boolean; disabled?: boolean }) => void execute({
    id: crypto.randomUUID(),
    kind: 'setEntityState',
    entityIds: ids,
    state,
  });
  return <span className="scene-tree-node">
    {children}
    <span className="scene-tree-actions">
      <ActionIcon
        aria-label={`${allHidden ? 'Show' : 'Hide'} ${description}`}
        color={allHidden ? 'gray' : 'blue'}
        disabled={disabled || locked}
        size="xs"
        title={`${allHidden ? 'Show' : 'Hide'} ${description}`}
        variant="subtle"
        onClick={(event) => {
          event.stopPropagation();
          change({ hidden: !allHidden });
        }}
      >
        {allHidden ? <EyeSlashIcon size={13} /> : <EyeIcon size={13} />}
      </ActionIcon>
      <ActionIcon
        aria-label={`${allDisabled ? 'Enable' : 'Disable'} ${description}`}
        color={allDisabled ? 'gray' : 'teal'}
        disabled={disabled || locked}
        size="xs"
        title={`${allDisabled ? 'Enable' : 'Disable'} ${description}`}
        variant="subtle"
        onClick={(event) => {
          event.stopPropagation();
          change({ disabled: !allDisabled });
        }}
      >
        <PowerIcon size={13} weight={allDisabled ? 'regular' : 'fill'} />
      </ActionIcon>
    </span>
  </span>;
}

function SceneTreeLabel({ kind, children, dot = false }: { kind: string; children: ReactNode; dot?: boolean }) {
  const normalizedKind = kind in SCENE_TREE_LABELS ? kind as SceneTreeKind : 'object';
  const label = SCENE_TREE_LABELS[normalizedKind];
  const color = `var(--forge-scene-tree-${normalizedKind}, ${DEFAULT_SCENE_TREE_COLORS[normalizedKind]})`;
  return <span className="scene-tree-label">
    {dot
      ? <span
        aria-label={`${label} item`}
        className="scene-tree-dot"
        role="img"
        style={{ backgroundColor: color }}
        title={label}
      />
      : <Badge className="scene-tree-badge" size="xs" variant="outline" style={{ borderColor: color, color }}>
        {label}
      </Badge>}
    <span className="scene-tree-label-text">{children}</span>
  </span>;
}

function groupTreeKind(entities: readonly EditorEntity[]): string {
  const kinds = new Set(entities.map(entityTreeKind));
  return kinds.size === 1 ? kinds.values().next().value ?? 'object' : 'object';
}

export function PropertiesPanel() {
  const { project, splinePointSelection } = useEditor();
  const pointEntityId = parseSplinePointId(splinePointSelection[0] ?? '')?.entityId;
  const entities = (pointEntityId ? [pointEntityId] : project.selection)
    .map((id) => project.entities.find((entity) => entity.id === id))
    .filter((entity): entity is EditorEntity => Boolean(entity));

  return <EditorPanel label="Properties">
    {entities.length === 1
      ? <EntityProperties entity={entities[0]} />
      : entities.length > 1
        ? <MultiEntityProperties entities={entities} />
        : <EditorEmptyState message="Select an object to inspect its properties." />}
  </EditorPanel>;
}

export function DiagnosticsPanel() {
  const { project, sceneLoad, busy, save } = useEditor();
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
      {project.migrationPending && <Alert color="yellow" title="Project migration pending">
        Save the project to commit its migrated format.
        <Button ml="sm" disabled={busy} onClick={() => void save()}>Save now</Button>
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
      {!sceneLoad && !project.migrationPending && !project.baseLevel.missingAssetCount && !project.diagnostics.length
        && <EditorEmptyState message="No diagnostics." />}
      <Button variant="default" onClick={() => void window.forge.revealLogs()}>Open detailed logs</Button>
    </Stack>
  </EditorPanel>;
}

export function EditorWatermark() {
  return <Text c="dimmed" className="editor-empty">Use View → Reset Layout to restore editor panels.</Text>;
}
