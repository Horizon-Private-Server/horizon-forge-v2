import { Button, Checkbox, Group, TextInput } from '@mantine/core';
import { useLayoutEffect, useState } from 'react';

import type { EditorEntity } from '../../types/EditorRuntime.js';
import { useEditor } from './EditorContext.ts';

export function EntityTextEditor({ identity, label, value, placeholder, disabled, onApply }: {
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

export function EntityStateControls({ entities, disabled }: { entities: EditorEntity[]; disabled: boolean }) {
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
