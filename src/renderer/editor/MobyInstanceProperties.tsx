import { Checkbox, Code, Fieldset, NumberInput, Stack, Text } from '@mantine/core';
import type { KeyboardEvent } from 'react';
import { useEffect, useState } from 'react';

import type {
  EditorEntity, EditorMobyPropertyDescriptor, EditorMobyPropertyValue,
} from '../../types/EditorRuntime.js';
import { parseRgb } from '../../utils/Color.ts';
import { ColorPickerInput } from '../ColorPickerInput.tsx';
import { useEditor } from './EditorContext.ts';
import { compatibleMobyProperties } from '../../utils/MobyFields.ts';
import { EditorProperty, EditorPropertyGrid } from './EditorPrimitives.tsx';

export function MobyInstanceProperties({ entities, disabled }: {
  entities: EditorEntity[];
  disabled: boolean;
}) {
  const { execute } = useEditor();
  const descriptors = compatibleMobyProperties(entities);
  const hasMobyProperties = entities.every((entity) => entity.mobyProperties?.length);
  if (!hasMobyProperties) return null;
  if (!descriptors.length) return <Fieldset legend="Moby instance">
    <Text c="dimmed" size="xs">Select mobys with the same OClass to edit shared instance properties.</Text>
  </Fieldset>;

  const commit = async (descriptor: EditorMobyPropertyDescriptor, value: EditorMobyPropertyValue) => execute({
    id: crypto.randomUUID(),
    kind: 'setMobyInstanceProperty',
    entityIds: entities.map((entity) => entity.id),
    property: {
      fieldKey: descriptor.key,
      expectedClassId: entities[0].sourceClassId!,
      value,
    },
  });

  return <Fieldset legend={entities.length === 1 ? 'Moby instance' : 'Shared moby instance'}>
    <EditorPropertyGrid>
      {descriptors.map((descriptor) => <EditorProperty key={descriptor.key} label={descriptor.label}>
        <MobyPropertyControl
          descriptor={descriptor}
          disabled={disabled}
          values={entities.map((entity) => entity.mobyProperties!
            .find((candidate) => candidate.key === descriptor.key)!.value)}
          onCommit={(value) => commit(descriptor, value)}
        />
      </EditorProperty>)}
    </EditorPropertyGrid>
    {entities.length > 1 && <Text c="dimmed" mt="xs" size="xs">
      Changes apply to all {entities.length} selected mobys as one undoable edit.
    </Text>}
  </Fieldset>;
}

function MobyPropertyControl({ descriptor, values, disabled, onCommit }: {
  descriptor: EditorMobyPropertyDescriptor;
  values: EditorMobyPropertyValue[];
  disabled: boolean;
  onCommit(value: EditorMobyPropertyValue): Promise<boolean>;
}) {
  const mixed = values.some((value) => !sameMobyPropertyValue(value, values[0]));
  if (!descriptor.editable) return <Stack gap={2}>
    <Code>{mixed ? 'Multiple values' : formatValue(descriptor.key, values[0])}</Code>
    {descriptor.help && <Text c="dimmed" size="xs">{descriptor.help}</Text>}
    <Text c="dimmed" size="xs">{descriptor.readOnlyReason ?? 'This field is read-only.'}</Text>
  </Stack>;
  switch (values[0].kind) {
    case 'integer':
    case 'float':
      return <MobyNumberInput descriptor={descriptor} mixed={mixed} value={values[0]}
        disabled={disabled} onCommit={onCommit} />;
    case 'boolean':
      return <MobyBooleanInput label={descriptor.label} mixed={mixed} value={values[0].value}
        disabled={disabled} help={descriptor.help}
        onCommit={(value) => onCommit({ kind: 'boolean', value })} />;
    case 'color':
      return <MobyColorInput label={descriptor.label} mixed={mixed} value={values[0].value}
        disabled={disabled} help={descriptor.help}
        onCommit={(value) => onCommit({ kind: 'color', value })} />;
  }
}

function sameMobyPropertyValue(left: EditorMobyPropertyValue, right: EditorMobyPropertyValue): boolean {
  if (left.kind !== right.kind) return false;
  if (left.kind === 'color' && right.kind === 'color')
    return left.value.every((value, index) => value === right.value[index]);
  return left.value === right.value;
}

function MobyNumberInput({ descriptor, value, mixed, disabled, onCommit }: {
  descriptor: EditorMobyPropertyDescriptor;
  value: Extract<EditorMobyPropertyValue, { kind: 'integer' | 'float' }>;
  mixed: boolean;
  disabled: boolean;
  onCommit(value: EditorMobyPropertyValue): Promise<boolean>;
}) {
  const [draft, setDraft] = useState<number | string>(mixed ? '' : value.value);
  const [error, setError] = useState<string>();
  const source = `${value.kind}:${value.value}:${mixed}`;
  useEffect(() => {
    setDraft(mixed ? '' : value.value);
    setError(undefined);
  }, [source]);
  const minimum = value.kind === 'integer' ? descriptor.integerMinimum : descriptor.floatMinimum;
  const maximum = value.kind === 'integer' ? descriptor.integerMaximum : descriptor.floatMaximum;
  const commit = async () => {
    if (draft === '') return;
    const message = validateNumber(draft, value.kind, minimum, maximum);
    if (message) {
      setError(message);
      return;
    }
    if (typeof draft !== 'number') return;
    if (!mixed && draft === value.value) return;
    if (!await onCommit({ kind: value.kind, value: draft }))
      setError('The value was rejected. Review the valid range and retry.');
  };
  return <Stack gap={2}>
    <NumberInput
      aria-label={descriptor.label}
      allowDecimal={value.kind === 'float'}
      disabled={disabled}
      error={error}
      hideControls
      max={maximum}
      min={minimum}
      placeholder={mixed ? 'Multiple values' : undefined}
      size="xs"
      suffix={descriptor.unit ? ` ${descriptor.unit}` : undefined}
      value={draft}
      onBlur={() => void commit()}
      onChange={(next) => {
        setDraft(next);
        setError(validateNumber(next, value.kind, minimum, maximum));
      }}
      onKeyDown={(event: KeyboardEvent<HTMLInputElement>) => {
        if (event.key === 'Enter') event.currentTarget.blur();
        if (event.key === 'Escape') {
          setDraft(mixed ? '' : value.value);
          setError(undefined);
        }
      }}
    />
    {descriptor.help && <Text c="dimmed" size="xs">{descriptor.help}</Text>}
  </Stack>;
}

function MobyBooleanInput({ label, value, mixed, disabled, help, onCommit }: {
  label: string;
  value: boolean;
  mixed: boolean;
  disabled: boolean;
  help?: string;
  onCommit(value: boolean): Promise<boolean>;
}) {
  const [error, setError] = useState<string>();
  return <Stack gap={2}>
    <Checkbox
      aria-label={label}
      checked={!mixed && value}
      disabled={disabled}
      indeterminate={mixed}
      label={mixed ? 'Multiple values' : value ? 'Yes' : 'No'}
      onChange={(event) => void onCommit(event.currentTarget.checked).then((accepted) => {
        setError(accepted ? undefined : 'The value was rejected. Retry after reviewing the project diagnostics.');
      })}
    />
    {help && <Text c="dimmed" size="xs">{help}</Text>}
    {error && <Text c="red" role="alert" size="xs">{error}</Text>}
  </Stack>;
}

function MobyColorInput({ label, value, mixed, disabled, help, onCommit }: {
  label: string;
  value: [number, number, number];
  mixed: boolean;
  disabled: boolean;
  help?: string;
  onCommit(value: [number, number, number]): Promise<boolean>;
}) {
  const source = `rgb(${value.join(', ')})`;
  const [draft, setDraft] = useState(source);
  const [error, setError] = useState<string>();
  useEffect(() => {
    setDraft(source);
    setError(undefined);
  }, [source, mixed]);
  return <Stack gap={2}>
    {mixed && <Text c="dimmed" size="xs">Multiple values; choosing a color replaces all.</Text>}
    <ColorPickerInput
      label={`${label} RGB`}
      value={draft}
      format="rgb"
      disabled={disabled}
      onChange={setDraft}
      onChangeEnd={(next) => void onCommit(parseRgb(next)).then((accepted) => {
        setError(accepted ? undefined : 'The color was rejected. Retry after reviewing the project diagnostics.');
      })}
    />
    {help && <Text c="dimmed" size="xs">{help}</Text>}
    {error && <Text c="red" role="alert" size="xs">{error}</Text>}
  </Stack>;
}

function validateNumber(
  value: number | string,
  kind: 'integer' | 'float',
  minimum?: number,
  maximum?: number,
): string | undefined {
  if (typeof value !== 'number' || !Number.isFinite(value)) return 'Enter a finite number.';
  if (kind === 'integer' && !Number.isInteger(value)) return 'Enter a whole number.';
  if (minimum !== undefined && value < minimum) return `Value must be at least ${minimum}.`;
  if (maximum !== undefined && value > maximum) return `Value must be at most ${maximum}.`;
  return undefined;
}

function formatValue(key: string, value: EditorMobyPropertyValue): string {
  switch (value.kind) {
    case 'integer': return key === 'instance.classId'
      ? `0x${value.value.toString(16).toUpperCase().padStart(4, '0')} (${value.value})`
      : value.value.toString();
    case 'float': return value.value.toString();
    case 'boolean': return value.value ? 'Yes' : 'No';
    case 'color': return `rgb(${value.value.join(', ')})`;
  }
}
