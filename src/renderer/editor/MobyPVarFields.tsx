import { Badge, Code, Group, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';

import type {
  EditorEntity, EditorMobyPVarFieldDescriptor, EditorMobyPVarValue,
} from '../../types/EditorRuntime.js';
import { formatPVarByteRange } from '../../utils/PVarHex.ts';
import { compatibleReferenceTargets } from '../../utils/EditorReferences.ts';
import { MobyPVarControl } from './MobyPVarControls.tsx';
import { ReferenceField } from './ReferenceField.tsx';

export function MobyPVarFields({ fields, entity, entities, disabled, searching, onCommit, onNavigate, onShowRaw }: {
  fields: readonly EditorMobyPVarFieldDescriptor[];
  entity: EditorEntity;
  entities: EditorEntity[];
  disabled: boolean;
  searching: boolean;
  onCommit(field: EditorMobyPVarFieldDescriptor, value: EditorMobyPVarValue): Promise<boolean>;
  onNavigate(entityId: string, action: 'select' | 'reveal' | 'focus'): Promise<void>;
  onShowRaw(fieldPath: string): void;
}) {
  return <Stack gap="xs">{fields.map((field) => <MobyPVarField key={field.path} field={field}
    entity={entity} entities={entities} disabled={disabled} searching={searching}
    onCommit={onCommit} onNavigate={onNavigate} onShowRaw={onShowRaw} />)}</Stack>;
}

function MobyPVarField({ field, entity, entities, disabled, searching, onCommit, onNavigate, onShowRaw }: {
  field: EditorMobyPVarFieldDescriptor;
  entity: EditorEntity;
  entities: EditorEntity[];
  disabled: boolean;
  searching: boolean;
  onCommit(field: EditorMobyPVarFieldDescriptor, value: EditorMobyPVarValue): Promise<boolean>;
  onNavigate(entityId: string, action: 'select' | 'reveal' | 'focus'): Promise<void>;
  onShowRaw(fieldPath: string): void;
}) {
  const heading = <Group gap="xs" wrap="wrap">
    <Text fw={500} size="xs">{field.label}</Text>
    <Code>{formatPVarByteRange(field)}</Code>
    {field.invalid && <Badge color="red" variant="light">Invalid</Badge>}
  </Group>;
  if (field.kind === 'group') return <details className="pvar-field-group" open={searching || undefined}>
    <summary>{heading}</summary>
    <Stack gap="xs" ml="sm" mt="xs">
      {field.help && <Text c="dimmed" size="xs">{field.help}</Text>}
      {field.children.map((child) => <MobyPVarField key={child.path} field={child} entity={entity}
        entities={entities} disabled={disabled} searching={searching} onCommit={onCommit}
        onNavigate={onNavigate} onShowRaw={onShowRaw} />)}
    </Stack>
  </details>;

  return <div id={pvarFieldElementId(field.path)} tabIndex={-1} className="pvar-field">
    <div className="pvar-field-label">
      <Text fw={500} size="xs">{field.label}</Text>
      <Tooltip label={`${field.path} · Show in raw bytes`}>
        <UnstyledButton className="pvar-field-range"
          aria-label={`Show ${field.label} in raw bytes`} onClick={() => onShowRaw(field.path)}>
          <Code>{formatPVarByteRange(field)}</Code>
        </UnstyledButton>
      </Tooltip>
      {field.invalid && <Badge color="red" variant="light">Invalid</Badge>}
    </div>
    <div className="pvar-field-data">
      {field.kind === 'reference' && field.reference
        ? <MobyPVarReference field={field} entity={entity} entities={entities} disabled={disabled}
          onCommit={onCommit} onNavigate={onNavigate} />
        : <MobyPVarControl field={field} disabled={disabled} onCommit={(value) => onCommit(field, value)} />}
      {field.help && <Text c="dimmed" size="xs">{field.help}</Text>}
    </div>
  </div>;
}

function MobyPVarReference({ field, entity, entities, disabled, onCommit, onNavigate }: {
  field: EditorMobyPVarFieldDescriptor;
  entity: EditorEntity;
  entities: EditorEntity[];
  disabled: boolean;
  onCommit(field: EditorMobyPVarFieldDescriptor, value: EditorMobyPVarValue): Promise<boolean>;
  onNavigate(entityId: string, action: 'select' | 'reveal' | 'focus'): Promise<void>;
}) {
  const reference = field.reference!;
  const shared = {
    ownerEntityId: entity.id,
    fieldKey: field.path,
    domain: 'entity' as const,
    targetKind: reference.targetKind,
    targetEntityId: reference.targetEntityId,
    nullable: reference.nullable,
    sourceValue: reference.sourceValue,
    missing: reference.missing,
  };
  return <ReferenceField
    reference={shared}
    candidates={compatibleReferenceTargets(shared, entities)}
    disabled={disabled || !field.editable}
    showFieldKey={false}
    onChange={(target) => void onCommit(field, target === undefined
      ? { kind: 'reference' }
      : { kind: 'reference', value: target })}
    onNavigate={(target, action) => void onNavigate(target, action)}
  />;
}

export function pvarFieldElementId(path: string): string {
  return `pvar-field-${encodeURIComponent(path)}`;
}
