import { Button, Checkbox, Group, Menu, NumberInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { useState } from 'react';
import type { OcclusionCoverageSource, OcclusionCoverageState } from '../../types/OcclusionCoverage.js';
import { hasOcclusionSeedGraph, isOcclusionCoverageState, occlusionVolumeSize, setOcclusionSeedGraph, setOcclusionVolumeShape } from '../../utils/OcclusionCoverage.ts';
import { VectorInputs } from './TransformInputs.tsx';

export function OcclusionCoverageSourceEditor({ source, state, disabled, canDuplicate, cylinderGraphsAvailable, onApply, onDuplicate, onDelete }: {
  source: OcclusionCoverageSource;
  state: OcclusionCoverageState;
  disabled: boolean;
  canDuplicate: boolean;
  cylinderGraphsAvailable: boolean;
  onApply(source: OcclusionCoverageSource): void;
  onDuplicate(): void;
  onDelete(): void;
}) {
  const [draft, setDraft] = useState(() => structuredClone(source));
  const valid = isOcclusionCoverageState({ ...state,
    sources: state.sources.map(s => s.id === source.id ? draft : s) });
  const changed = JSON.stringify(draft) !== JSON.stringify(source);
  return <Stack gap="xs">
    <TextInput label="Volume name" value={draft.name} maxLength={256} disabled={disabled}
      onChange={event => setDraft({ ...draft, name: event.currentTarget.value })} />
    <Checkbox label="Use when generating octants" checked={draft.enabled} disabled={disabled}
      onChange={event => setDraft({ ...draft, enabled: event.currentTarget.checked })} />
    <Select label="Volume shape" value={draft.cylinder ? 'cylinder' : 'box'} disabled={disabled}
      data={[{ value: 'box', label: 'Box' }, { value: 'cylinder', label: 'Cylinder', disabled: hasOcclusionSeedGraph(draft) && !cylinderGraphsAvailable }]}
      onChange={shape => { if (shape === 'box' || shape === 'cylinder') setDraft(setOcclusionVolumeShape(draft, shape)); }} />
    {!hasOcclusionSeedGraph(draft) && <Select label="Coverage role" value={draft.role} disabled={disabled}
      data={[{ value: 'include', label: 'Include intersecting cells' }, { value: 'exclude', label: 'Exclude fully contained cells' }]}
      onChange={role => { if (role === 'include' || role === 'exclude') setDraft({ ...draft, role }); }} />}
    <Checkbox label="Use seed graph" checked={hasOcclusionSeedGraph(draft)}
      description="Applies volume edits immediately. Add seeds below to filter octants; turning this off keeps them."
      disabled={disabled || !valid || draft.role === 'exclude' || Boolean(draft.cylinder && !cylinderGraphsAvailable && !hasOcclusionSeedGraph(draft))}
      onChange={event => onApply(setOcclusionSeedGraph(draft, event.currentTarget.checked))} />
    {draft.cylinder && !cylinderGraphsAvailable && <Text size="xs" c="dimmed">Seed graphs on cylinders are unavailable in this build.</Text>}
    {draft.role === 'exclude' && <Text size="xs" c="dimmed">Exclusion volumes remove cells using their shape.</Text>}
    {!draft.cylinder && <VectorInputs label="Size" value={occlusionVolumeSize(draft)} disabled={disabled}
      onChange={boxSize => setDraft(draft.boxSize ? { ...draft, boxSize } : { ...draft, graph: { ...draft.graph!, size: boxSize } })} />}
    {draft.cylinder && <Group grow>
      {(['radius', 'height'] as const).map(key => <NumberInput key={key} label={key === 'radius' ? 'Radius' : 'Height'}
        value={draft.cylinder![key]} disabled={disabled} min={0.000001} max={1_000_000}
        onChange={value => setDraft({ ...draft, cylinder: { ...draft.cylinder!, [key]: typeof value === 'number' ? value : Number.NaN } })} />)}
    </Group>}
    {draft.graph && <>
      <Group justify="space-between" align="center">
        <Checkbox label="Sticky" checked={draft.graph.sticky ?? false} disabled={disabled || draft.graph.disabled}
          onChange={event => setDraft({ ...draft, graph: { ...draft.graph!, sticky: event.currentTarget.checked } })} />
        <NumberInput label="Sticky distance" w={120}
          value={draft.graph.stickyDistance ?? 12} min={0} max={1_000_000} disabled={disabled || draft.graph.disabled || !draft.graph.sticky}
          onChange={value => setDraft({ ...draft, graph: { ...draft.graph!, stickyDistance: typeof value === 'number' ? value : Number.NaN } })} />
      </Group>
      <Text size="xs" c="dimmed">Keeps octants near TIE/tfrag surfaces. Apply edits before generating.</Text>
    </>}
    {!valid && <Text size="xs" role="alert">Enter a name, finite values and positive dimensions. Seeds must remain inside the volume, including its circular radius.</Text>}
    <Group gap="xs">
      <Button disabled={disabled || !valid || !changed} onClick={() => onApply(draft)}>Apply changes</Button>
      <Button variant="subtle" disabled={disabled || !changed} onClick={() => setDraft(structuredClone(source))}>Discard edits</Button>
      <Menu position="bottom-end" withinPortal>
        <Menu.Target><Button variant="default" disabled={disabled} aria-label="Volume actions">Actions</Button></Menu.Target>
        <Menu.Dropdown>
          <Menu.Item disabled={!canDuplicate} onClick={onDuplicate}>Duplicate volume</Menu.Item>
          <Menu.Item color="red" onClick={onDelete}>Delete volume</Menu.Item>
        </Menu.Dropdown>
      </Menu>
    </Group>
  </Stack>;
}
