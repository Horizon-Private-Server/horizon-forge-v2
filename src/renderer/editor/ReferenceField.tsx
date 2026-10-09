import { Badge, Button, Group, Select, Stack, Text } from '@mantine/core';

import type { EditorEntity, EditorReference } from '../../types/EditorRuntime.js';
import { editorEntityKind } from './EditorPanelState.ts';

interface ReferenceFieldProps {
  reference: Extract<EditorReference, { domain: 'entity' }>;
  candidates: readonly EditorEntity[];
  disabled: boolean;
  onChange?(entityId?: string): void;
  onNavigate?(entityId: string, action: 'select' | 'reveal' | 'focus'): void;
}

export function ReferenceField({ reference, candidates, disabled, onChange, onNavigate }: ReferenceFieldProps) {
  const target = candidates.find((entity) => entity.id === reference.targetEntityId);
  const data = candidates.map((entity) => ({
    value: entity.id,
    label: `${entity.name} · ${editorEntityKind(entity)}`,
  }));
  return <Stack className="reference-field" gap={4}>
    <Group justify="space-between" wrap="nowrap">
      <Text fw={500} size="xs">{reference.fieldKey}</Text>
      <Badge color={reference.missing ? 'red' : 'gray'} variant="light">
        {reference.missing ? 'Missing' : reference.targetKind}
      </Badge>
    </Group>
    <Select
      aria-label={`${reference.fieldKey} reference`}
      clearable={reference.nullable}
      data={data}
      disabled={disabled || !onChange}
      limit={100}
      nothingFoundMessage="No compatible targets"
      searchable
      value={reference.targetEntityId ?? null}
      onChange={(value) => onChange?.(value ?? undefined)}
    />
    <Group gap="xs">
      <Button disabled={!target} variant="default" onClick={() => target && onNavigate?.(target.id, 'select')}>
        Select
      </Button>
      <Button disabled={!target} variant="default" onClick={() => target && onNavigate?.(target.id, 'reveal')}>
        Reveal
      </Button>
      <Button disabled={!target} variant="default" onClick={() => target && onNavigate?.(target.id, 'focus')}>
        Focus
      </Button>
    </Group>
  </Stack>;
}
