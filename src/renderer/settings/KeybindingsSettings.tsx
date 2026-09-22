import { Alert, Button, Group, Stack, Text, TextInput } from '@mantine/core';
import { useMemo, useState } from 'react';

import type { KeybindingCommandId, KeybindingMap, KeybindingOverrides } from '../../types/Keybindings.js';
import {
  bindingFromKeyInput,
  findKeybindingConflict,
  formatKeybinding,
  KEYBINDING_COMMANDS,
} from '../../utils/Keybindings.ts';
import { errorMessage } from '../../utils/Errors.ts';

interface KeybindingsSettingsProps {
  overrides: KeybindingOverrides;
  bindings: KeybindingMap;
  onChange(overrides: KeybindingOverrides): void;
}

export function KeybindingsSettings({ overrides, bindings, onChange }: KeybindingsSettingsProps) {
  const [filter, setFilter] = useState('');
  const [capturing, setCapturing] = useState<KeybindingCommandId>();
  const [error, setError] = useState<string>();
  const commands = useMemo(() => {
    const query = filter.trim().toLowerCase();
    return KEYBINDING_COMMANDS.filter((command) => !query
      || `${command.group} ${command.label} ${formatKeybinding(bindings[command.id])}`.toLowerCase().includes(query));
  }, [bindings, filter]);

  const save = (next: KeybindingOverrides) => {
    setError(undefined);
    void window.forge.setSetting('keybindings.overrides', JSON.stringify(next))
      .then(() => onChange(next))
      .catch((cause: unknown) => setError(errorMessage(cause)));
  };
  const setBinding = (id: KeybindingCommandId, binding: string | null) => {
    const conflict = binding ? findKeybindingConflict(bindings, id, binding) : undefined;
    if (conflict) {
      setError(`${formatKeybinding(binding)} is already assigned to ${conflict.label}.`);
      return;
    }
    save({ ...overrides, [id]: binding });
    setCapturing(undefined);
  };
  const reset = (id: KeybindingCommandId) => {
    const next = { ...overrides };
    delete next[id];
    save(next);
  };

  return <Stack>
    <Group justify="space-between" wrap="nowrap">
      <TextInput aria-label="Search keybindings" placeholder="Search commands or keys" value={filter}
        onChange={(event) => setFilter(event.currentTarget.value)} style={{ flex: 1 }} />
      <Button variant="default" disabled={Object.keys(overrides).length === 0} onClick={() => save({})}>Reset all</Button>
    </Group>
    <Text c="dimmed" size="xs">Select a binding, then press the replacement key combination. Escape cancels capture.</Text>
    {error && <Alert color="red" withCloseButton onClose={() => setError(undefined)}>{error}</Alert>}
    <div className="keybinding-list" role="list" aria-label="Keybindings">
      {commands.map((command) => <div className="keybinding-row" role="listitem" key={command.id}>
        <div>
          <Text size="sm">{command.label}</Text>
          <Text c="dimmed" size="xs">{command.group} · Default {formatKeybinding(command.defaultBinding)}</Text>
        </div>
        <Button
          aria-label={`Change ${command.label} binding`}
          color={capturing === command.id ? 'cyan' : undefined}
          variant={capturing === command.id ? 'filled' : 'default'}
          onClick={() => { setError(undefined); setCapturing(command.id); }}
          onKeyDown={(event) => {
            if (capturing !== command.id) return;
            event.preventDefault();
            event.stopPropagation();
            if (event.code === 'Escape') {
              setCapturing(undefined);
              return;
            }
            const binding = bindingFromKeyInput(event);
            if (binding) setBinding(command.id, binding);
          }}
        >{capturing === command.id ? 'Press keys…' : formatKeybinding(bindings[command.id])}</Button>
        <Button variant="subtle" disabled={bindings[command.id] === null}
          onClick={() => setBinding(command.id, null)}>Clear</Button>
        <Button variant="subtle" disabled={!Object.hasOwn(overrides, command.id)}
          onClick={() => reset(command.id)}>Reset</Button>
      </div>)}
      {!commands.length && <Text c="dimmed" p="sm" size="sm">No matching commands.</Text>}
    </div>
  </Stack>;
}
