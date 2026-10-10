import { TrashIcon } from '@phosphor-icons/react/dist/csr/Trash';
import { ActionIcon, Group, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { useState } from 'react';

import type { EditorEntity, EditorReference } from '../../types/EditorRuntime.js';
import { clearEntityDrag, ENTITY_DRAG_MIME, readEntityDrag } from '../../utils/EntityDrag.ts';
import { editorEntityDisplayName } from '../../utils/EntityDisplay.ts';
import { compatibleReferenceDropTarget, editorEntityKind } from '../../utils/EditorReferences.ts';

interface ReferenceFieldProps {
  reference: Extract<EditorReference, { domain: 'entity' }>;
  candidates: readonly EditorEntity[];
  disabled: boolean;
  showFieldKey?: boolean;
  onChange?(entityId?: string): void;
  onNavigate?(entityId: string, action: 'select' | 'reveal' | 'focus'): void;
}

export function ReferenceField({
  reference, candidates, disabled, showFieldKey = true, onChange, onNavigate,
}: ReferenceFieldProps) {
  const [dropping, setDropping] = useState(false);
  const target = candidates.find((entity) => entity.id === reference.targetEntityId);
  const canChange = !disabled && onChange !== undefined;
  const label = target
    ? `${editorEntityDisplayName(target)} · ${editorEntityKind(target)}`
    : reference.missing ? `Missing reference (${reference.sourceValue ?? '?'})` : 'None';
  const dropTarget = (data: DataTransfer) => compatibleReferenceDropTarget(readEntityDrag(data), candidates);

  return <Group
    className="reference-field"
    gap={4}
    wrap="nowrap"
    onDragEnter={(event) => {
      if (!canChange || !event.dataTransfer.types.includes(ENTITY_DRAG_MIME)) return;
      event.preventDefault();
      setDropping(true);
    }}
    onDragLeave={(event) => {
      if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropping(false);
    }}
    onDragOver={(event) => {
      if (!canChange || !event.dataTransfer.types.includes(ENTITY_DRAG_MIME)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
      setDropping(true);
    }}
    onDrop={(event) => {
      event.preventDefault();
      event.stopPropagation();
      setDropping(false);
      const entityId = dropTarget(event.dataTransfer);
      clearEntityDrag();
      if (canChange && entityId) onChange(entityId);
    }}
  >
    {showFieldKey && <Text className="reference-field-key" fw={500} size="xs">{reference.fieldKey}</Text>}
    <Tooltip label={target
      ? 'Double-click to focus; drop to replace'
      : 'Drop a compatible entity here'}>
      <UnstyledButton
        aria-invalid={reference.missing || undefined}
        aria-label={`${reference.fieldKey} reference: ${label}`}
        className="reference-field-target"
        data-dropping={dropping || undefined}
        data-missing={reference.missing || undefined}
        disabled={!target && !canChange}
        onClick={(event) => event.detail === 0 && target && onNavigate?.(target.id, 'focus')}
        onDoubleClick={() => target && onNavigate?.(target.id, 'focus')}
      >
        <Text c={reference.missing ? 'red' : target ? undefined : 'dimmed'} size="xs" truncate>{label}</Text>
      </UnstyledButton>
    </Tooltip>
    {reference.nullable && <Tooltip label="Clear reference">
      <ActionIcon
        aria-label={`Clear ${reference.fieldKey} reference`}
        disabled={!canChange || (!reference.targetEntityId && !reference.missing)}
        size="sm"
        variant="subtle"
        onClick={() => onChange?.()}
      >
        <TrashIcon size={13} />
      </ActionIcon>
    </Tooltip>}
  </Group>;
}
