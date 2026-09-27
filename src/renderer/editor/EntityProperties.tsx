import {
  Button, Checkbox, Code, Fieldset, Group, NumberInput, Stack, Text, TextInput,
} from '@mantine/core';
import type { ReactNode } from 'react';
import { useLayoutEffect, useState } from 'react';

import type {
  EditorEntity, ProjectQuaternion, ProjectTransform, ProjectVector3,
} from '../../types/EditorRuntime.js';
import { useEditor } from './EditorContext.ts';
import { EditorProperty, EditorPropertyGrid } from './EditorPrimitives.tsx';
import { SplineProperties } from './SplineProperties.tsx';

export function EntityProperties({ entity }: { entity: EditorEntity }) {
  const { busy } = useEditor();
  const disabled = busy || entity.state.locked || entity.state.readOnly;
  return <BaseEntityProperties entity={entity} disabled={disabled}>
    {(entity.geometry?.kind === 'spline' || entity.geometry?.kind === 'grindPath')
      && <SplineProperties entity={entity} disabled={disabled} />}
  </BaseEntityProperties>;
}

function BaseEntityProperties({ entity, disabled, children }: {
  entity: EditorEntity;
  disabled: boolean;
  children?: ReactNode;
}) {
  const { execute, busy } = useEditor();
  return <Stack gap="sm">
    <Fieldset legend="General">
      <Stack gap="xs">
        <EntityTextEditor
          identity={entity.id}
          label="Name"
          value={entity.name}
          disabled={disabled}
          onApply={(text) => execute({ id: crypto.randomUUID(), kind: 'renameEntity', entityIds: [entity.id], text })}
        />
        <EntityTextEditor
          identity={entity.id}
          label="Layer"
          value={entity.layer}
          disabled={disabled}
          onApply={(text) => execute({ id: crypto.randomUUID(), kind: 'setEntityLayer', entityIds: [entity.id], text })}
        />
        <EntityStateControls entities={[entity]} disabled={busy || entity.state.readOnly} />
        {entity.state.readOnly
          ? <Text size="xs" c="yellow">This decoded source type has no native writer yet.</Text>
          : entity.state.locked && <Text size="xs" c="yellow">Unlock this entity to edit its properties.</Text>}
      </Stack>
    </Fieldset>
    <Fieldset legend="Transform"><TransformEditor entity={entity} disabled={disabled} /></Fieldset>
    {children}
    <Fieldset legend="Source">
      <EditorPropertyGrid>
        <EditorProperty label="Entity ID"><Code>{entity.id}</Code></EditorProperty>
        <EditorProperty label="Type">{entity.geometry?.kind ?? entity.asset?.kind ?? 'model-less moby'}</EditorProperty>
        <EditorProperty label="Asset">{entity.asset ? <Code>{entity.asset.id}</Code> : 'None'}</EditorProperty>
        <EditorProperty label="Source">{entity.provenance
          ? `${entity.provenance.game} level ${entity.provenance.level}, ${entity.provenance.section} #${entity.provenance.sourceIndex}`
          : 'Project-created'}</EditorProperty>
      </EditorPropertyGrid>
    </Fieldset>
  </Stack>;
}

export function MultiEntityProperties({ entities }: { entities: EditorEntity[] }) {
  const { execute, busy } = useEditor();
  const readOnly = entities.some((entity) => entity.state.readOnly);
  const locked = readOnly || entities.some((entity) => entity.state.locked);
  const layer = entities.every((entity) => entity.layer === entities[0].layer) ? entities[0].layer : '';
  const ids = entities.map((entity) => entity.id);
  return <Fieldset legend={`${entities.length} selected`}>
    <Stack gap="xs">
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
        ? <Text size="xs" c="yellow">One or more selected source types have no native writer yet.</Text>
        : locked && <Text size="xs" c="yellow">Unlock all selected entities before changing their shared layer.</Text>}
    </Stack>
  </Fieldset>;
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
    <TextInput label={label} value={text} placeholder={placeholder} error={error} disabled={disabled}
      onChange={(event) => setText(event.currentTarget.value)} style={{ flex: 1 }} />
    <Button size="xs" disabled={disabled || Boolean(error) || trimmed === value}
      onClick={() => void onApply(trimmed)}>Apply</Button>
  </Group>;
}

function EntityStateControls({ entities, disabled }: { entities: EditorEntity[]; disabled: boolean }) {
  const { execute } = useEditor();
  const ids = entities.map((entity) => entity.id);
  return <Group>{(['hidden', 'disabled', 'locked'] as const).map((key) => {
    const enabled = entities.filter((entity) => entity.state[key]).length;
    return <Checkbox
      key={key}
      label={key[0].toUpperCase() + key.slice(1)}
      checked={enabled === entities.length}
      indeterminate={enabled > 0 && enabled < entities.length}
      disabled={disabled}
      onChange={(event) => void execute({
        id: crypto.randomUUID(), kind: 'setEntityState', entityIds: ids,
        state: { [key]: event.currentTarget.checked },
      })}
    />;
  })}</Group>;
}

function TransformEditor({ entity, disabled }: { entity: EditorEntity; disabled: boolean }) {
  const { execute } = useEditor();
  const [transform, setTransform] = useState<ProjectTransform>(() => cloneTransform(entity.transform));
  const source = JSON.stringify(entity.transform);
  useLayoutEffect(() => setTransform(cloneTransform(entity.transform)), [entity.id, source]);
  const values = [...Object.values(transform.position), ...Object.values(transform.rotation),
    ...Object.values(transform.scale)];
  const valid = values.every(Number.isFinite)
    && Object.values(transform.scale).every((value) => value !== 0)
    && Object.values(transform.rotation).some((value) => value !== 0);
  return <Stack gap="xs">
    <VectorInputs label="Position" value={transform.position}
      disabled={disabled || !entity.transformModes.includes('translate')}
      onChange={(position) => setTransform({ ...transform, position })} />
    <QuaternionInputs value={transform.rotation}
      disabled={disabled || !entity.transformModes.includes('rotate')}
      onChange={(rotation) => setTransform({ ...transform, rotation })} />
    <VectorInputs label="Scale" value={transform.scale}
      disabled={disabled || !entity.transformModes.includes('scale')}
      onChange={(scale) => setTransform({ ...transform, scale })} />
    <Button size="xs" disabled={disabled || !valid || JSON.stringify(transform) === source}
      onClick={() => void execute({
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
    {(['x', 'y', 'z'] as const).map((axis) => <NumberInput key={axis} label={`${label} ${axis.toUpperCase()}`}
      value={value[axis]} disabled={disabled}
      onChange={(next) => onChange({ ...value, [axis]: typeof next === 'number' ? next : Number.NaN })} />)}
  </Group>;
}

function QuaternionInputs({ value, disabled, onChange }: {
  value: ProjectQuaternion;
  disabled: boolean;
  onChange(value: ProjectQuaternion): void;
}) {
  return <Group grow wrap="nowrap">
    {(['x', 'y', 'z', 'w'] as const).map((axis) => <NumberInput key={axis} label={`Rotation ${axis.toUpperCase()}`}
      value={value[axis]} disabled={disabled}
      onChange={(next) => onChange({ ...value, [axis]: typeof next === 'number' ? next : Number.NaN })} />)}
  </Group>;
}

function cloneTransform(value: ProjectTransform): ProjectTransform {
  return { position: { ...value.position }, rotation: { ...value.rotation }, scale: { ...value.scale } };
}
