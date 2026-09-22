import { Button, Group, NumberInput, Popover, SegmentedControl, Select, Stack, Switch, Text } from '@mantine/core';

import type { EditorSnapSource, EditorSnapTarget } from '../../types/EditorViewport.js';
import type { EditorTransformMode, EditorTransformSpace } from './TransformTool.ts';

interface ViewportToolbarProps {
  mode: EditorTransformMode;
  space: EditorTransformSpace;
  snapSource: EditorSnapSource;
  snapTarget: EditorSnapTarget;
  snapEnabled: boolean;
  translationSnap: number;
  rotationSnap: number;
  scaleSnap: number;
  onModeChange(value: EditorTransformMode): void;
  onSpaceChange(value: EditorTransformSpace): void;
  onSnapSourceChange(value: EditorSnapSource): void;
  onSnapTargetChange(value: EditorSnapTarget): void;
  onSnapEnabledChange(value: boolean): void;
  onTranslationSnapChange(value: number): void;
  onRotationSnapChange(value: number): void;
  onScaleSnapChange(value: number): void;
}

export function ViewportToolbar({
  mode,
  space,
  snapSource,
  snapTarget,
  snapEnabled,
  translationSnap,
  rotationSnap,
  scaleSnap,
  onModeChange,
  onSpaceChange,
  onSnapSourceChange,
  onSnapTargetChange,
  onSnapEnabledChange,
  onTranslationSnapChange,
  onRotationSnapChange,
  onScaleSnapChange,
}: ViewportToolbarProps) {
  return <Group aria-label="Viewport tools" className="scene-toolbar" gap="xs" role="group">
    <SegmentedControl
      aria-label="Transform mode"
      size="xs"
      value={mode}
      data={TRANSFORM_MODES}
      onChange={(value) => onModeChange(value as EditorTransformMode)}
    />
    <SegmentedControl
      aria-label="Transform orientation"
      size="xs"
      value={space}
      data={TRANSFORM_SPACES}
      onChange={(value) => onSpaceChange(value as EditorTransformSpace)}
    />
    <Popover position="bottom-end" width={190} withArrow>
      <Popover.Target>
        <Button size="compact-xs" variant={snapEnabled ? 'filled' : 'default'}>
          Snap {snapEnabled ? 'on' : 'off'}
        </Button>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Switch
            checked={snapEnabled}
            label="Enable snapping"
            onChange={(event) => onSnapEnabledChange(event.currentTarget.checked)}
          />
          <Select label="Source point" data={SNAP_SOURCES} value={snapSource}
            onChange={(value) => value && onSnapSourceChange(value as EditorSnapSource)} />
          <Select label="Move target" data={SNAP_TARGETS} value={snapTarget}
            onChange={(value) => value && onSnapTargetChange(value as EditorSnapTarget)} />
          <NumberInput label="Move increment" min={0.001} step={0.5} value={translationSnap}
            onChange={(value) => onTranslationSnapChange(positiveNumber(value, translationSnap))} />
          <NumberInput label="Rotation degrees" min={0.1} step={5} value={rotationSnap}
            onChange={(value) => onRotationSnapChange(positiveNumber(value, rotationSnap))} />
          <NumberInput label="Scale increment" min={0.001} step={0.05} value={scaleSnap}
            onChange={(value) => onScaleSnapChange(positiveNumber(value, scaleSnap))} />
          <Text c="dimmed" size="xs">Hold Ctrl to temporarily invert snapping.</Text>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  </Group>;
}

function positiveNumber(value: string | number, fallback: number): number {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : fallback;
}

const TRANSFORM_MODES = [
  { value: 'select', label: 'Select' },
  { value: 'translate', label: 'Move' },
  { value: 'rotate', label: 'Rotate' },
  { value: 'scale', label: 'Scale' },
];

const TRANSFORM_SPACES = [
  { value: 'world', label: 'World' },
  { value: 'local', label: 'Local' },
];

const SNAP_SOURCES = [
  { value: 'center', label: 'Selection center' },
  { value: 'origin', label: 'Active object origin' },
  { value: 'vertex', label: 'Nearest mesh vertex' },
];

const SNAP_TARGETS = [
  { value: 'grid', label: 'Grid increment' },
  { value: 'center', label: 'Object center' },
  { value: 'vertex', label: 'Mesh vertex' },
  { value: 'surface', label: 'Visible surface' },
];
