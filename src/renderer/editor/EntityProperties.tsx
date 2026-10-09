import { Button, Code, Fieldset, Group, NumberInput, Stack, Text } from '@mantine/core';
import type { ReactNode } from 'react';
import { useLayoutEffect, useMemo, useState } from 'react';

import type {
  EditorEntity, EditorReference, ProjectQuaternion, ProjectTransform, ProjectVector3,
} from '../../types/EditorRuntime.js';
import { formatCollisionType } from '../../utils/CollisionFormat.ts';
import { useEditor } from './EditorContext.ts';
import { EditorProperty, EditorPropertyGrid } from './EditorPrimitives.tsx';
import { EntityStateControls, EntityTextEditor } from './EntityPropertyControls.tsx';
import { SkyShellProperties } from './SkyShellProperties.tsx';
import { SplineProperties } from './SplineProperties.tsx';
import { InstancedCollisionProperties } from './InstancedCollisionProperties.tsx';
import { compatibleReferenceTargets } from './EditorPanelState.ts';
import { ReferenceField } from './ReferenceField.tsx';

export function EntityProperties({ entity }: { entity: EditorEntity }) {
  const { busy, execute, navigateToEntity, project, showReferences } = useEditor();
  const references = useMemo(() => project.references.filter(
    (reference): reference is Extract<EditorReference, { domain: 'entity' }> =>
      reference.ownerEntityId === entity.id && reference.domain === 'entity',
  ), [entity.id, project.references]);
  const entityById = useMemo(() => new Map(project.entities.map((value) => [value.id, value])), [project.entities]);
  if (entity.skyShell) return <SkyShellProperties entity={entity} />;
  const disabled = busy || entity.state.locked || entity.state.readOnly;
  const fields = references.slice(0, 50);
  return <BaseEntityProperties entity={entity} disabled={disabled}>
    {entity.collision && <CollisionProperties entity={entity} />}
    {(entity.asset?.kind === 'Tie' || entity.asset?.kind === 'Shrub')
      && <InstancedCollisionProperties entity={entity} disabled={disabled} />}
    {(entity.geometry?.kind === 'spline' || entity.geometry?.kind === 'grindPath')
      && <SplineProperties entity={entity} disabled={disabled} />}
    {fields.length > 0 && <Fieldset legend="References">
      <Stack gap="sm">
        {fields.map((reference) => <ReferenceField
          key={`${reference.fieldKey}:${reference.sourceValue ?? ''}`}
          candidates={disabled
            ? currentReferenceTarget(reference.targetEntityId, entityById)
            : compatibleReferenceTargets(reference, project.entities)}
          disabled={disabled}
          reference={reference}
          onChange={(targetEntityId) => void execute({
            id: crypto.randomUUID(),
            kind: 'setEntityReference',
            entityIds: [entity.id],
            reference: { fieldKey: reference.fieldKey, sourceValue: reference.sourceValue, targetEntityId },
          })}
          onNavigate={(entityId, action) => void navigateToEntity(entityId, action)}
        />)}
        <Text c="dimmed" size="xs">Only understood references are shown.</Text>
        {references.length > fields.length && <Text size="xs">
          Showing {fields.length} of {references.length}; open References for the complete paged list.
        </Text>}
        <Button variant="default" onClick={showReferences}>Open References</Button>
      </Stack>
    </Fieldset>}
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
          disabled={disabled || entity.collision !== undefined}
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
        disabled={busy || locked || entities.some((entity) => entity.collision)}
        onApply={(text) => execute({ id: crypto.randomUUID(), kind: 'setEntityLayer', entityIds: ids, text })}
      />
      <EntityStateControls entities={entities} disabled={busy || readOnly} />
      {readOnly
        ? <Text size="xs" c="yellow">One or more selected source types have no native writer yet.</Text>
        : locked && <Text size="xs" c="yellow">Unlock all selected entities before changing their shared layer.</Text>}
    </Stack>
  </Fieldset>;
}

function CollisionProperties({ entity }: { entity: EditorEntity }) {
  const { project } = useEditor();
  const collision = entity.collision!;
  const attachedInstance = collision.attachment
    ? project.entities.find((candidate) => candidate.id === collision.attachment!.parentEntityId)
    : undefined;
  return <Fieldset legend="Collision">
    <EditorPropertyGrid>
      <EditorProperty label="Layer">{collision.kind === 'solid' ? 'Solid collision' : 'Player barrier'}</EditorProperty>
      <EditorProperty label="Piece"><Code>#{collision.sourcePieceIndex}</Code></EditorProperty>
      <EditorProperty label="Geometry">{collision.faceCount} faces · {collision.vertexCount} vertices</EditorProperty>
      {collision.attachment && <EditorProperty label="Follows instance">{attachedInstance?.name
        ?? <Code>{collision.attachment.parentEntityId}</Code>}</EditorProperty>}
      <EditorProperty label="Types">{collision.types.length
        ? collision.types.map((value) =>
          `${formatCollisionType(value.rawType, project.target.game)} (${value.count})`).join(', ')
        : 'Player-only barrier'}</EditorProperty>
    </EditorPropertyGrid>
  </Fieldset>;
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

function currentReferenceTarget(
  entityId: string | undefined,
  entities: ReadonlyMap<string, EditorEntity>,
): EditorEntity[] {
  const entity = entityId ? entities.get(entityId) : undefined;
  return entity ? [entity] : [];
}
