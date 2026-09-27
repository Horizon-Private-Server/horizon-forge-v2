import { ArrowDownIcon } from '@phosphor-icons/react/dist/csr/ArrowDown';
import { ArrowUpIcon } from '@phosphor-icons/react/dist/csr/ArrowUp';
import { DotsSixVerticalIcon } from '@phosphor-icons/react/dist/csr/DotsSixVertical';
import { PlusIcon } from '@phosphor-icons/react/dist/csr/Plus';
import { TrashIcon } from '@phosphor-icons/react/dist/csr/Trash';
import { ActionIcon, Button, Fieldset, Group, NumberInput, Stack, Table, Text } from '@mantine/core';
import { memo, useCallback, useLayoutEffect, useMemo, useState } from 'react';
import type { DragEvent } from 'react';

import type { EditorEntity, ProjectVector4 } from '../../types/EditorRuntime.js';
import { parseSplinePointId, removeSplinePoints } from '../../utils/SplinePoints.ts';
import { useEditor } from './EditorContext.ts';

const AXES = ['x', 'y', 'z', 'w'] as const;
const ROW_HEIGHT = 31;
const VISIBLE_ROWS = 20;
const OVERSCAN = 5;

export function SplineProperties({ entity, disabled }: { entity: EditorEntity; disabled: boolean }) {
  const { execute, splinePointSelection, setSplinePointSelection } = useEditor();
  const source = entity.geometry?.points ?? [];
  const signature = useMemo(() => JSON.stringify(source), [source]);
  const [points, setPoints] = useState<ProjectVector4[]>(() => clonePoints(source));
  const [dirty, setDirty] = useState(false);
  const [scrollTop, setScrollTop] = useState(0);
  useLayoutEffect(() => {
    setPoints(clonePoints(source));
    setDirty(false);
  }, [entity.id, signature]);
  const selected = useMemo(() => new Set(splinePointSelection.flatMap((value) => {
    const point = parseSplinePointId(value);
    return point?.entityId === entity.id ? [point.index] : [];
  })), [entity.id, splinePointSelection]);
  const change = useCallback((update: (current: ProjectVector4[]) => ProjectVector4[]) => {
    setPoints(update);
    setDirty(true);
  }, []);
  const update = useCallback((index: number, axis: keyof ProjectVector4, value: string | number) => change((current) =>
    current.map((point, pointIndex) => pointIndex === index
      ? { ...point, [axis]: typeof value === 'number' ? value : Number.NaN }
      : point)), [change]);
  const move = useCallback((index: number, offset: number) => change((current) => {
    const next = [...current];
    [next[index], next[index + offset]] = [next[index + offset], next[index]];
    return next;
  }), [change]);
  const moveTo = useCallback((from: number, target: number) => {
    if (from === target) return;
    change((current) => {
      const next = [...current];
      const [point] = next.splice(from, 1);
      next.splice(from < target ? target - 1 : target, 0, point);
      return next;
    });
  }, [change]);
  const remove = useCallback((index: number) => change((current) =>
    current.filter((_, pointIndex) => pointIndex !== index)), [change]);
  const valid = useMemo(() => points.every((point) => Object.values(point).every(Number.isFinite)), [points]);
  const start = Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN);
  const end = Math.min(points.length, start + VISIBLE_ROWS + OVERSCAN * 2);

  return <Fieldset legend={`${entity.geometry?.kind === 'grindPath' ? 'Grind path' : 'Spline'} · ${points.length} points`}>
    <Stack gap={6}>
      <Group justify="space-between" gap="xs">
        <Text size="xs" c="dimmed">Only visible rows are rendered.</Text>
        <Group gap={4}>
          {selected.size > 0 && <Button size="compact-xs" color="red" variant="subtle"
            leftSection={<TrashIcon size={13} />} disabled={disabled}
            onClick={() => {
              const next = removeSplinePoints(source, selected);
              void execute({
                id: crypto.randomUUID(), kind: 'updateSplinePoints', entityIds: [entity.id], points: next,
              }).then(async (committed) => {
                if (!committed) return;
                await execute({ id: crypto.randomUUID(), kind: 'setSelection', entityIds: [entity.id] });
                setSplinePointSelection([]);
              });
            }}>
            Delete {selected.size}
          </Button>}
          <Button size="compact-xs" variant="default" leftSection={<PlusIcon size={13} />} disabled={disabled}
            onClick={() => change((current) => [
              ...current, { ...(current.at(-1) ?? { x: 0, y: 0, z: 0, w: 0 }) },
            ])}>
            Add
          </Button>
        </Group>
      </Group>
      <div className="spline-point-table" onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}>
        <Table horizontalSpacing={3} verticalSpacing={0} withRowBorders={false} stickyHeader>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>#</Table.Th>
              {AXES.map((axis) => <Table.Th key={axis}>{axis.toUpperCase()}</Table.Th>)}
              <Table.Th aria-label="Point actions" />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {start > 0 && <Table.Tr aria-hidden="true"><Table.Td colSpan={6} h={start * ROW_HEIGHT} /></Table.Tr>}
            {points.slice(start, end).map((point, offset) => {
              const index = start + offset;
              return <SplinePointRow key={index} index={index} point={point} count={points.length}
                selected={selected.has(index)} disabled={disabled} onUpdate={update} onMove={move}
                onMoveTo={moveTo} onRemove={remove} />;
            })}
            {end < points.length
              && <Table.Tr aria-hidden="true"><Table.Td colSpan={6} h={(points.length - end) * ROW_HEIGHT} /></Table.Tr>}
          </Table.Tbody>
        </Table>
      </div>
      <Text size="xs" c="dimmed">
        W is gameplay metadata: camera paths may use marker values; other systems replace it with segment length.
      </Text>
      <Button size="compact-xs" disabled={disabled || !valid || !dirty}
        onClick={() => void execute({
          id: crypto.randomUUID(), kind: 'updateSplinePoints', entityIds: [entity.id], points,
        })}>
        Apply points
      </Button>
      {!valid && <Text size="xs" c="red">Point values must be finite.</Text>}
    </Stack>
  </Fieldset>;
}

const SplinePointRow = memo(function SplinePointRow({
  index, point, count, selected, disabled, onUpdate, onMove, onMoveTo, onRemove,
}: {
  index: number;
  point: ProjectVector4;
  count: number;
  selected: boolean;
  disabled: boolean;
  onUpdate(index: number, axis: keyof ProjectVector4, value: string | number): void;
  onMove(index: number, offset: number): void;
  onMoveTo(from: number, target: number): void;
  onRemove(index: number): void;
}) {
  const drop = (event: DragEvent) => {
    event.preventDefault();
    const source = Number(event.dataTransfer.getData('text/plain'));
    if (Number.isInteger(source)) onMoveTo(source, index);
  };
  return <Table.Tr data-selected={selected || undefined} onDragOver={(event) => event.preventDefault()} onDrop={drop}>
    <Table.Td><Text size="xs" c="dimmed">{index}</Text></Table.Td>
    {AXES.map((axis) => <Table.Td key={axis}>
      <NumberInput
        aria-label={`Point ${index} ${axis.toUpperCase()}`}
        value={point[axis]}
        disabled={disabled}
        hideControls
        size="xs"
        onChange={(value) => onUpdate(index, axis, value)}
      />
    </Table.Td>)}
    <Table.Td>
      <Group gap={1} wrap="nowrap">
        <ActionIcon draggable size="xs" variant="subtle" disabled={disabled} aria-label={`Drag point ${index}`}
          onDragStart={(event) => {
            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData('text/plain', String(index));
          }}>
          <DotsSixVerticalIcon size={13} />
        </ActionIcon>
        <ActionIcon size="xs" variant="subtle" disabled={disabled || index === 0}
          aria-label={`Move point ${index} up`} onClick={() => onMove(index, -1)}>
          <ArrowUpIcon size={12} />
        </ActionIcon>
        <ActionIcon size="xs" variant="subtle" disabled={disabled || index === count - 1}
          aria-label={`Move point ${index} down`} onClick={() => onMove(index, 1)}>
          <ArrowDownIcon size={12} />
        </ActionIcon>
        <ActionIcon size="xs" color="red" variant="subtle" disabled={disabled}
          aria-label={`Delete point ${index}`} onClick={() => onRemove(index)}>
          <TrashIcon size={12} />
        </ActionIcon>
      </Group>
    </Table.Td>
  </Table.Tr>;
});

function clonePoints(points: readonly ProjectVector4[]): ProjectVector4[] {
  return points.map((point) => ({ ...point }));
}
