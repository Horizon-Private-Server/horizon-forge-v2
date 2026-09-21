import { Group, SegmentedControl } from '@mantine/core';

import type { EditorTransformMode, EditorTransformSpace } from './TransformTool.ts';

interface ViewportToolbarProps {
  mode: EditorTransformMode;
  space: EditorTransformSpace;
  onModeChange(value: EditorTransformMode): void;
  onSpaceChange(value: EditorTransformSpace): void;
}

export function ViewportToolbar({
  mode,
  space,
  onModeChange,
  onSpaceChange,
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
  </Group>;
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
