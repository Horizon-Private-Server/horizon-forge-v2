import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye';
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash';
import { PowerIcon } from '@phosphor-icons/react/dist/csr/Power';
import { ArrowDownIcon } from '@phosphor-icons/react/dist/csr/ArrowDown';
import { ArrowUpIcon } from '@phosphor-icons/react/dist/csr/ArrowUp';
import { PlusIcon } from '@phosphor-icons/react/dist/csr/Plus';
import { TrashIcon } from '@phosphor-icons/react/dist/csr/Trash';
import {
  ActionIcon, Alert, Badge, Button, Checkbox, Code, Group, NumberInput, Slider, Stack, Text, TextInput,
} from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import type { ReactNode } from 'react';
import { useEffect, useLayoutEffect, useMemo, useState } from 'react';

import type {
  EditorEntity, EditorLevelSettings, ProjectQuaternion, ProjectTransform, ProjectVector3, ProjectVector4,
} from '../../types/EditorRuntime.js';
import type { SceneTreeKind } from '../../types/SceneTree.js';
import { DEFAULT_SCENE_TREE_COLORS, SCENE_TREE_LABELS } from '../../utils/SceneTreeColors.ts';
import { ColorPickerInput } from '../ColorPickerInput.tsx';
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
    execute, busy, showViewportStats, showOcclusionOctants,
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
    selection={project.selection}
    showStats={showViewportStats}
    showOcclusionOctants={showOcclusionOctants}
    terrain={terrain}
    environment={environment}
    onFocusHandled={() => setCameraFocus(undefined)}
    onLoadProgress={setSceneLoad}
    onSkyPiecesChange={setSkyPieces}
    onSelectionChange={(entityIds) => void execute({
      id: crypto.randomUUID(), kind: 'setSelection', entityIds,
    })}
    onTransformsCommit={(transforms) => execute({
      id: crypto.randomUUID(), kind: 'updateTransforms',
      entityIds: transforms.map((value) => value.entityId), transforms,
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
  const { project, terrain, skyPieces, setCameraFocus, execute, busy } = useEditor();
  const [filter, setFilter] = useState('');
  const model = useMemo(() => buildSceneEntityGroups(project.entities, filter), [filter, project.entities]);
  const tfrags = useMemo(() => buildTerrainTreeItems(terrain?.urls ?? [], filter), [filter, terrain]);
  const sky = useMemo(() => buildSkyTreeItems(skyPieces, filter), [filter, skyPieces]);
  const entityIds = useMemo(() => new Set(project.entities.map((entity) => entity.id)), [project.entities]);
  const nodes: TreeNodeData[] = [
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
      })),
    })),
  ];
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
          selected={project.selection}
          multiple
          onActivate={(entityId) => setCameraFocus({ entityId })}
          onSelectionChange={(values) => void execute({
            id: crypto.randomUUID(),
            kind: 'setSelection',
            entityIds: values.filter((value) => entityIds.has(value)),
          })}
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
  const { project } = useEditor();
  const entities = project.selection
    .map((id) => project.entities.find((entity) => entity.id === id))
    .filter((entity): entity is EditorEntity => Boolean(entity));

  return <EditorPanel label="Properties">
    {entities.length === 1
      ? <SingleEntityProperties entity={entities[0]} />
      : entities.length > 1
        ? <MultiEntityProperties entities={entities} />
        : <EditorEmptyState message="Select an object to inspect its properties." />}
  </EditorPanel>;
}

function SingleEntityProperties({ entity }: { entity: EditorEntity }) {
  const { execute, busy } = useEditor();
  const locked = entity.state.locked || entity.state.readOnly;
  return <Stack>
    <EntityTextEditor
      identity={entity.id}
      label="Name"
      value={entity.name}
      disabled={busy || locked}
      onApply={(text) => execute({ id: crypto.randomUUID(), kind: 'renameEntity', entityIds: [entity.id], text })}
    />
    <EntityTextEditor
      identity={entity.id}
      label="Layer"
      value={entity.layer}
      disabled={busy || locked}
      onApply={(text) => execute({ id: crypto.randomUUID(), kind: 'setEntityLayer', entityIds: [entity.id], text })}
    />
    <EntityStateControls entities={[entity]} disabled={busy || entity.state.readOnly} />
    {entity.state.readOnly
      ? <Text size="xs" c="yellow">This decoded source geometry is read-only until its native writer is available.</Text>
      : locked && <Text size="xs" c="yellow">Unlock this entity to edit its properties.</Text>}
    <TransformEditor entity={entity} disabled={busy || locked} />
    {entity.geometry?.kind === 'spline'
      && <SplinePointsEditor entity={entity} disabled={busy || locked} />}
    <EditorPropertyGrid>
      <EditorProperty label="Entity ID"><Code>{entity.id}</Code></EditorProperty>
      <EditorProperty label="Type">{entity.geometry?.kind ?? entity.asset?.kind ?? 'model-less moby'}</EditorProperty>
      <EditorProperty label="Asset">{entity.asset ? <Code>{entity.asset.id}</Code> : 'None'}</EditorProperty>
      <EditorProperty label="Source">{entity.provenance
        ? `${entity.provenance.game} level ${entity.provenance.level}, ${entity.provenance.section} #${entity.provenance.sourceIndex}`
        : 'Project-created'}</EditorProperty>
    </EditorPropertyGrid>
  </Stack>;
}

function MultiEntityProperties({ entities }: { entities: EditorEntity[] }) {
  const { execute, busy } = useEditor();
  const readOnly = entities.some((entity) => entity.state.readOnly);
  const locked = readOnly || entities.some((entity) => entity.state.locked);
  const layer = entities.every((entity) => entity.layer === entities[0].layer) ? entities[0].layer : '';
  const ids = entities.map((entity) => entity.id);
  return <Stack>
    <Text size="sm">{entities.length} objects selected</Text>
    <EntityTextEditor
      identity={ids.join()}
      label="Shared layer"
      value={layer}
      placeholder={layer ? undefined : 'Multiple values'}
      disabled={busy || locked}
      onApply={(text) => execute({ id: crypto.randomUUID(), kind: 'setEntityLayer', entityIds: ids, text })}
    />
    <EntityStateControls entities={entities} disabled={busy || readOnly} />
    {readOnly
      ? <Text size="xs" c="yellow">Decoded source geometry is read-only until its native writer is available.</Text>
      : locked && <Text size="xs" c="yellow">Unlock all selected entities before changing their shared layer.</Text>}
  </Stack>;
}

function EntityTextEditor({ identity, label, value, placeholder, disabled, onApply }: {
  identity: string;
  label: string;
  value: string;
  placeholder?: string;
  disabled: boolean;
  onApply(value: string): Promise<unknown>;
}) {
  const [text, setText] = useState(value);
  useLayoutEffect(() => setText(value), [identity, value]);
  const trimmed = text.trim();
  const error = !trimmed ? `${label} is required` : trimmed.length > 256 ? `${label} is too long` : undefined;
  return <Group align="flex-end" wrap="nowrap">
    <TextInput
      label={label}
      value={text}
      placeholder={placeholder}
      error={error}
      disabled={disabled}
      onChange={(event) => setText(event.currentTarget.value)}
      style={{ flex: 1 }}
    />
    <Button disabled={disabled || Boolean(error) || trimmed === value} onClick={() => void onApply(trimmed)}>Apply</Button>
  </Group>;
}

function EntityStateControls({ entities, disabled }: { entities: EditorEntity[]; disabled: boolean }) {
  const { execute } = useEditor();
  const ids = entities.map((entity) => entity.id);
  const control = (key: 'hidden' | 'disabled' | 'locked', label: string) => {
    const enabled = entities.filter((entity) => entity.state[key]).length;
    return <Checkbox
      key={key}
      label={label}
      checked={enabled === entities.length}
      indeterminate={enabled > 0 && enabled < entities.length}
      disabled={disabled}
      onChange={(event) => void execute({
        id: crypto.randomUUID(),
        kind: 'setEntityState',
        entityIds: ids,
        state: { [key]: event.currentTarget.checked },
      })}
    />;
  };
  return <Group>{control('hidden', 'Hidden')}{control('disabled', 'Disabled')}{control('locked', 'Locked')}</Group>;
}

function TransformEditor({ entity, disabled }: { entity: EditorEntity; disabled: boolean }) {
  const { execute } = useEditor();
  const [transform, setTransform] = useState<ProjectTransform>(() => cloneTransform(entity.transform));
  const sourceTransform = JSON.stringify(entity.transform);
  useLayoutEffect(() => setTransform(cloneTransform(entity.transform)), [entity.id, sourceTransform]);
  const values = [
    ...Object.values(transform.position),
    ...Object.values(transform.rotation),
    ...Object.values(transform.scale),
  ];
  const valid = values.every(Number.isFinite)
    && Object.values(transform.scale).every((value) => value !== 0)
    && Object.values(transform.rotation).some((value) => value !== 0);
  return <Stack>
    <VectorInputs label="Position" value={transform.position} disabled={disabled}
      onChange={(position) => setTransform({ ...transform, position })} />
    <QuaternionInputs value={transform.rotation} disabled={disabled}
      onChange={(rotation) => setTransform({ ...transform, rotation })} />
    <VectorInputs label="Scale" value={transform.scale} disabled={disabled}
      onChange={(scale) => setTransform({ ...transform, scale })} />
    <Button disabled={disabled || !valid} onClick={() => void execute({
      id: crypto.randomUUID(), kind: 'updateTransform', entityIds: [entity.id], transform,
    })}>Apply transform</Button>
    {!valid && <Text size="xs" c="red">Values must be finite; scale and quaternion cannot be zero.</Text>}
  </Stack>;
}

function SplinePointsEditor({ entity, disabled }: { entity: EditorEntity; disabled: boolean }) {
  const { execute } = useEditor();
  const source = entity.geometry?.points ?? [];
  const signature = JSON.stringify(source);
  const [points, setPoints] = useState<ProjectVector4[]>(() => source.map((point) => ({ ...point })));
  useLayoutEffect(() => setPoints(source.map((point) => ({ ...point }))), [entity.id, signature]);
  const update = (index: number, axis: keyof ProjectVector4, value: string | number) => setPoints(points.map(
    (point, pointIndex) => pointIndex === index
      ? { ...point, [axis]: typeof value === 'number' ? value : Number.NaN }
      : point,
  ));
  const move = (index: number, offset: number) => {
    const next = [...points];
    [next[index], next[index + offset]] = [next[index + offset], next[index]];
    setPoints(next);
  };
  const valid = points.every((point) => Object.values(point).every(Number.isFinite));
  return <Stack gap="xs">
    <Group justify="space-between">
      <Text size="sm" fw={500}>Spline points ({points.length})</Text>
      <Button size="xs" variant="default" leftSection={<PlusIcon size={14} />} disabled={disabled}
        onClick={() => setPoints([...points, { ...(points.at(-1) ?? { x: 0, y: 0, z: 0, w: 0 }) }])}>
        Add point
      </Button>
    </Group>
    {points.map((point, index) => <Group key={index} align="flex-end" wrap="nowrap">
      {(['x', 'y', 'z', 'w'] as const).map((axis) => <NumberInput
        key={axis}
        label={`${index} ${axis.toUpperCase()}`}
        value={point[axis]}
        disabled={disabled}
        onChange={(value) => update(index, axis, value)}
        style={{ minWidth: 0 }}
      />)}
      <ActionIcon variant="default" disabled={disabled || index === 0} aria-label={`Move point ${index} up`}
        onClick={() => move(index, -1)}><ArrowUpIcon size={16} /></ActionIcon>
      <ActionIcon variant="default" disabled={disabled || index === points.length - 1}
        aria-label={`Move point ${index} down`} onClick={() => move(index, 1)}>
        <ArrowDownIcon size={16} />
      </ActionIcon>
      <ActionIcon color="red" variant="subtle" disabled={disabled} aria-label={`Delete point ${index}`}
        onClick={() => setPoints(points.filter((_, pointIndex) => pointIndex !== index))}>
        <TrashIcon size={16} />
      </ActionIcon>
    </Group>)}
    <Text size="xs" c="dimmed">
      W is gameplay metadata: some camera paths use positive marker values, while other systems replace it with segment length.
    </Text>
    <Button disabled={disabled || !valid || JSON.stringify(points) === signature} onClick={() => void execute({
      id: crypto.randomUUID(), kind: 'updateSplinePoints', entityIds: [entity.id], points,
    })}>Apply spline points</Button>
    {!valid && <Text size="xs" c="red">Point values must be finite.</Text>}
  </Stack>;
}

function VectorInputs({ label, value, disabled, onChange }: {
  label: string;
  value: ProjectVector3;
  disabled: boolean;
  onChange(value: ProjectVector3): void;
}) {
  return <Group grow wrap="nowrap">
    {(['x', 'y', 'z'] as const).map((axis) => <NumberInput
      key={axis}
      label={`${label} ${axis.toUpperCase()}`}
      value={value[axis]}
      disabled={disabled}
      onChange={(next) => onChange({ ...value, [axis]: typeof next === 'number' ? next : Number.NaN })}
    />)}
  </Group>;
}

function QuaternionInputs({ value, disabled, onChange }: {
  value: ProjectQuaternion;
  disabled: boolean;
  onChange(value: ProjectQuaternion): void;
}) {
  return <Group grow wrap="nowrap">
    {(['x', 'y', 'z', 'w'] as const).map((axis) => <NumberInput
      key={axis}
      label={`Rotation ${axis.toUpperCase()}`}
      value={value[axis]}
      disabled={disabled}
      onChange={(next) => onChange({ ...value, [axis]: typeof next === 'number' ? next : Number.NaN })}
    />)}
  </Group>;
}

function cloneTransform(value: ProjectTransform): ProjectTransform {
  return {
    position: { ...value.position },
    rotation: { ...value.rotation },
    scale: { ...value.scale },
  };
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
