import { Button, Group, NumberInput, Popover, SegmentedControl, Select, Stack, Switch, Text } from '@mantine/core';

import type { EditorSnapSource, EditorSnapTarget } from '../../types/EditorViewport.js';
import type { MobySimulationStats } from '../../types/MobySimulation.js';
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
  showSolidCollision: boolean;
  showPlayerBarriers: boolean;
  showDeathHeight: boolean;
  mobySimulationEnabled: boolean;
  mobySimulationPaused: boolean;
  mobySimulationStats?: MobySimulationStats;
  simulationControlsDisabled: boolean;
  onModeChange(value: EditorTransformMode): void;
  onSpaceChange(value: EditorTransformSpace): void;
  onSnapSourceChange(value: EditorSnapSource): void;
  onSnapTargetChange(value: EditorSnapTarget): void;
  onSnapEnabledChange(value: boolean): void;
  onTranslationSnapChange(value: number): void;
  onRotationSnapChange(value: number): void;
  onScaleSnapChange(value: number): void;
  onSolidCollisionVisibilityChange(value: boolean): void;
  onPlayerBarrierVisibilityChange(value: boolean): void;
  onDeathHeightVisibilityChange(value: boolean): void;
  onMobySimulationEnabledChange(value: boolean): void;
  onMobySimulationPausedChange(value: boolean): void;
  onMobySimulationReset(): void;
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
  showSolidCollision,
  showPlayerBarriers,
  showDeathHeight,
  mobySimulationEnabled,
  mobySimulationPaused,
  mobySimulationStats,
  simulationControlsDisabled,
  onModeChange,
  onSpaceChange,
  onSnapSourceChange,
  onSnapTargetChange,
  onSnapEnabledChange,
  onTranslationSnapChange,
  onRotationSnapChange,
  onScaleSnapChange,
  onSolidCollisionVisibilityChange,
  onPlayerBarrierVisibilityChange,
  onDeathHeightVisibilityChange,
  onMobySimulationEnabledChange,
  onMobySimulationPausedChange,
  onMobySimulationReset,
}: ViewportToolbarProps) {
  return <Group aria-label="Viewport tools" className="scene-toolbar" gap="xs" role="group">
    <SegmentedControl
      aria-label="Transform mode"
      value={mode}
      data={TRANSFORM_MODES}
      onChange={(value) => onModeChange(value as EditorTransformMode)}
    />
    <SegmentedControl
      aria-label="Transform orientation"
      value={space}
      data={TRANSFORM_SPACES}
      onChange={(value) => onSpaceChange(value as EditorTransformSpace)}
    />
    <Popover position="bottom-end" width={190} withArrow>
      <Popover.Target>
        <Button className="scene-toolbar-snap" size="compact-xs" variant={snapEnabled ? 'filled' : 'default'}>
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
    <Popover position="bottom-end" width={210} withArrow>
      <Popover.Target>
        <Button size="compact-xs" variant="default">Visibility</Button>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Switch checked={showSolidCollision} label="Solid collision"
            onChange={(event) => onSolidCollisionVisibilityChange(event.currentTarget.checked)} />
          <Switch checked={showPlayerBarriers} label="Player barriers"
            onChange={(event) => onPlayerBarrierVisibilityChange(event.currentTarget.checked)} />
          <Switch checked={showDeathHeight} label="Death height"
            onChange={(event) => onDeathHeightVisibilityChange(event.currentTarget.checked)} />
          <Text c="dimmed" size="xs">Viewport-only; does not change the project or build.</Text>
        </Stack>
      </Popover.Dropdown>
    </Popover>
    <Popover position="bottom-end" width={230} withArrow>
      <Popover.Target>
        <Button
          aria-label="Moby simulation preview controls"
          disabled={simulationControlsDisabled}
          size="compact-xs"
          variant={mobySimulationEnabled ? 'filled' : 'default'}
        >
          Simulation {mobySimulationEnabled ? (mobySimulationPaused ? 'paused' : 'on') : 'off'}
        </Button>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Switch
            checked={mobySimulationEnabled}
            disabled={simulationControlsDisabled}
            label="Enable Moby simulation"
            onChange={(event) => onMobySimulationEnabledChange(event.currentTarget.checked)}
          />
          <Group gap="xs" grow>
            <Button
              disabled={!mobySimulationEnabled || simulationControlsDisabled}
              onClick={() => onMobySimulationPausedChange(!mobySimulationPaused)}
              size="compact-xs"
              variant="default"
            >
              {mobySimulationPaused ? 'Resume' : 'Pause'}
            </Button>
            <Button
              disabled={!mobySimulationEnabled || simulationControlsDisabled}
              onClick={onMobySimulationReset}
              size="compact-xs"
              variant="default"
            >Reset</Button>
          </Group>
          <Text c="dimmed" size="xs">
            Preview only; never changes project or build data.
            {mobySimulationStats && ` ${mobySimulationStats.activeSimulators} active · ${mobySimulationStats.fixedTicks} ticks.`}
          </Text>
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
