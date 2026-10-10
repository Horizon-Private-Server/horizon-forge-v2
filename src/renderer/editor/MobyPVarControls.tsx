import { Checkbox, Code, Group, NumberInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { useEffect, useState } from 'react';

import type { EditorMobyPVarFieldDescriptor, EditorMobyPVarValue } from '../../types/EditorRuntime.js';

export function MobyPVarControl({ field, disabled, onCommit }: {
  field: EditorMobyPVarFieldDescriptor;
  disabled: boolean;
  onCommit(value: EditorMobyPVarValue): Promise<boolean>;
}) {
  const [error, setError] = useState<string>();
  const value = field.value;
  const apply = async (next: EditorMobyPVarValue) => {
    const accepted = await onCommit(next);
    setError(accepted ? undefined : 'The edit was rejected; refresh the schema and retry.');
    return accepted;
  };
  if (!field.editable || !value) return <>
    <Code style={{ overflowWrap: 'anywhere' }}>{value ? formatPVarValue(value) : 'Container'}</Code>
    {(field.kind === 'unknown' || field.kind === 'bytes')
      && <Text c="dimmed" size="xs">Preserved, read-only bytes.</Text>}
  </>;

  let control;
  switch (value.kind) {
    case 'integer': control = field.kind === 'choice'
      ? <Select aria-label={field.label} data={(field.options ?? []).map((option) => ({
        value: option.value, label: `${option.label} (${option.value})`,
      }))} disabled={disabled} value={value.value}
      onChange={(next) => next && void apply({ kind: 'integer', value: next })} />
      : <IntegerInput field={field} disabled={disabled} value={value.value}
        onCommit={(next) => apply({ kind: 'integer', value: next })} />;
      break;
    case 'float': control = <FloatInput label={field.label} disabled={disabled} value={value.value}
      min={field.minimum === undefined ? undefined : Number(field.minimum)}
      max={field.maximum === undefined ? undefined : Number(field.maximum)}
      onCommit={(next) => apply({ kind: 'float', value: next })} />;
      break;
    case 'boolean': control = <Checkbox aria-label={field.label} checked={value.value} disabled={disabled}
      label={value.value ? 'Yes' : 'No'} onChange={(event) => void apply({
        kind: 'boolean', value: event.currentTarget.checked,
      })} />;
      break;
    case 'color': control = <ArrayInput label={field.label} values={value.value} disabled={disabled}
      integer onCommit={(next) => apply({ kind: 'color', value: next })} />;
      break;
    case 'vector': control = <ArrayInput label={field.label} values={value.value} disabled={disabled}
      onCommit={(next) => apply({ kind: 'vector', value: next })} />;
      break;
    default: control = <Code>{formatPVarValue(value)}</Code>;
  }
  return <Stack gap={2}>{control}{field.kind === 'flags' && field.options?.length
    ? <Text c="dimmed" size="xs">
      Flags: {field.options.map((option) => `${option.label}=${option.value}`).join(', ')}
    </Text>
    : null}{error && <Text c="red" role="alert" size="xs">{error}</Text>}</Stack>;
}

function FloatInput({ label, value, min, max, disabled, onCommit }: {
  label: string;
  value: number;
  min?: number;
  max?: number;
  disabled: boolean;
  onCommit(value: number): Promise<boolean>;
}) {
  const [draft, setDraft] = useState<number | string>(value);
  useEffect(() => setDraft(value), [value]);
  const valid = typeof draft === 'number' && Number.isFinite(draft)
    && (min === undefined || draft >= min) && (max === undefined || draft <= max);
  return <NumberInput aria-label={label} disabled={disabled} value={draft} min={min} max={max}
    error={!valid ? 'Enter a finite number in range.' : undefined}
    onChange={setDraft} onBlur={() => valid && draft !== value && void onCommit(draft as number).then((accepted) => {
      if (!accepted) setDraft(value);
    })} />;
}

function IntegerInput({ field, value, disabled, onCommit }: {
  field: EditorMobyPVarFieldDescriptor;
  value: string;
  disabled: boolean;
  onCommit(value: string): Promise<boolean>;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const valid = /^-?\d+$/.test(draft) && withinIntegerRange(draft, field.minimum, field.maximum);
  return <TextInput aria-label={field.label} disabled={disabled}
    error={!valid ? 'Enter an integer in range.' : undefined}
    value={draft} onChange={(event) => setDraft(event.currentTarget.value)}
    onBlur={() => valid && draft !== value && void onCommit(draft).then((accepted) => {
      if (!accepted) setDraft(value);
    })} />;
}

function ArrayInput({ label, values, disabled, integer = false, onCommit }: {
  label: string;
  values: number[];
  disabled: boolean;
  integer?: boolean;
  onCommit(values: number[]): Promise<boolean>;
}) {
  const [draft, setDraft] = useState(values);
  useEffect(() => setDraft(values), [values]);
  return <Group grow wrap="nowrap">{draft.map((value, index) => <NumberInput key={index}
    aria-label={`${label} ${index + 1}`} allowDecimal={!integer} disabled={disabled} value={value}
    min={integer ? 0 : undefined} max={integer ? 255 : undefined}
    onChange={(next) => setDraft(draft.map((candidate, candidateIndex) => candidateIndex === index
      ? typeof next === 'number' ? next : Number.NaN : candidate))}
    onBlur={() => draft.every((candidate) => Number.isFinite(candidate)
      && (!integer || Number.isInteger(candidate) && candidate >= 0 && candidate <= 255))
      && !sameNumbers(draft, values) && void onCommit(draft).then((accepted) => {
        if (!accepted) setDraft(values);
      })} />)}</Group>;
}

function withinIntegerRange(value: string, minimum?: string, maximum?: string): boolean {
  try {
    const parsed = BigInt(value);
    return (minimum === undefined || parsed >= BigInt(minimum))
      && (maximum === undefined || parsed <= BigInt(maximum));
  } catch { return false; }
}

function sameNumbers(left: readonly number[], right: readonly number[]): boolean {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

function formatPVarValue(value: EditorMobyPVarValue): string {
  switch (value.kind) {
    case 'integer': return value.value;
    case 'float': return value.value.toString();
    case 'boolean': return value.value ? 'Yes' : 'No';
    case 'color': return value.value.join(', ');
    case 'vector': return value.value.join(', ');
    case 'bytes': return value.value;
    case 'reference': return value.value ?? 'None';
  }
}
