import { Alert, Button, Checkbox, Code, Group, NumberInput, Stack, Text, TextInput } from '@mantine/core';
import type { TreeNodeData } from '@mantine/core';
import { useLayoutEffect, useMemo, useState } from 'react';

import type { EditorEntity, ProjectQuaternion, ProjectTransform, ProjectVector3 } from '../../types/EditorRuntime.js';
import { useEditor } from './EditorContext.ts';
import { buildSceneEntityGroups, buildSkyTreeItems, buildTerrainTreeItems, entityStateLabel } from './EditorPanelState.ts';
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
    project, keybindings, terrain, cameraFocus, setCameraFocus, setSceneLoad, setSkyPieces,
    execute, busy, showViewportStats,
  } = useEditor();
  return <SceneViewport
    disabled={busy}
    entities={project.entities}
    keybindings={keybindings}
    focusEntityId={cameraFocus?.entityId}
    selection={project.selection}
    showStats={showViewportStats}
    terrain={terrain}
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

export function SceneTreePanel() {
  const { project, terrain, skyPieces, setCameraFocus, execute, busy } = useEditor();
  const [filter, setFilter] = useState('');
  const model = useMemo(() => buildSceneEntityGroups(project.entities, filter), [filter, project.entities]);
  const tfrags = useMemo(() => buildTerrainTreeItems(terrain?.urls ?? [], filter), [filter, terrain]);
  const sky = useMemo(() => buildSkyTreeItems(skyPieces, filter), [filter, skyPieces]);
  const entityIds = useMemo(() => new Set(project.entities.map((entity) => entity.id)), [project.entities]);
  const selected = project.entities.filter((entity) => project.selection.includes(entity.id));
  const nodes: TreeNodeData[] = [
    ...(tfrags.length ? [{
      value: 'render:tfrags',
      label: `tfrags (${tfrags.length})`,
      children: tfrags.map((item) => ({ ...item, nodeProps: { selectable: false } })),
    }] : []),
    ...(sky.length ? [{
      value: 'render:sky',
      label: `sky (${sky.length})`,
      children: sky.map((item) => ({ ...item, nodeProps: { selectable: false } })),
    }] : []),
    ...model.groups.map((group) => ({
    value: `layer:${group.layer}`,
    label: `${group.layer} (${group.entities.length})`,
    children: group.entities.map((entity) => ({ value: entity.id, label: entityStateLabel(entity) })),
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
      {selected.length > 0 && <EntityStateControls entities={selected} disabled={busy} />}
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
      {model.shown + renderItemCount < matched && <Text c="yellow" size="xs">
        Showing {model.shown + renderItemCount} of {matched} matches. Refine the filter to see the remainder.
      </Text>}
    </Stack>
  </EditorPanel>;
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
  const locked = entity.state.locked;
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
    <EntityStateControls entities={[entity]} disabled={busy} />
    {locked && <Text size="xs" c="yellow">Unlock this entity to edit its properties.</Text>}
    <TransformEditor entity={entity} disabled={busy || locked} />
    <EditorPropertyGrid>
      <EditorProperty label="Entity ID"><Code>{entity.id}</Code></EditorProperty>
      <EditorProperty label="Type">{entity.asset?.kind ?? 'model-less moby'}</EditorProperty>
      <EditorProperty label="Asset">{entity.asset ? <Code>{entity.asset.id}</Code> : 'None'}</EditorProperty>
      <EditorProperty label="Source">{entity.provenance
        ? `${entity.provenance.game} level ${entity.provenance.level}, ${entity.provenance.section} #${entity.provenance.sourceIndex}`
        : 'Project-created'}</EditorProperty>
    </EditorPropertyGrid>
  </Stack>;
}

function MultiEntityProperties({ entities }: { entities: EditorEntity[] }) {
  const { execute, busy } = useEditor();
  const locked = entities.some((entity) => entity.state.locked);
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
    <EntityStateControls entities={entities} disabled={busy} />
    {locked && <Text size="xs" c="yellow">Unlock all selected entities before changing their shared layer.</Text>}
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
