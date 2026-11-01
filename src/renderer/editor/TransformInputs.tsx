import { Group, NumberInput } from '@mantine/core';
import type { ProjectQuaternion, ProjectVector3 } from '../../types/EditorRuntime.js';

export function VectorInputs({ label, value, disabled, onChange }: {
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

export function QuaternionInputs({ value, disabled, onChange }: {
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

