import { Badge, Button, Code, Group, Stack, Text } from '@mantine/core';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties, FocusEvent, MouseEvent } from 'react';

import type { EditorEntity, EditorMobyPVarFieldDescriptor } from '../../types/EditorRuntime.js';
import {
  flattenPVarFields, formatPVarByteRange, PVAR_HEX_BYTES_PER_ROW, PVAR_HEX_GROUP_SIZE,
  PVAR_HEX_ROW_HEIGHT, pvarByteIsModified, pvarHexRowSegments, pvarHexWindow,
  pvarModifiedByteCount,
} from '../../utils/PVarHex.ts';

const VIEWPORT_HEIGHT = 320;
const HIGHLIGHT_COLORS = [
  '255 214 102', '255 153 153', '145 213 255', '183 235 143',
  '255 173 210', '179 127 235', '135 232 222', '255 187 150',
  '105 192 255', '186 230 55', '255 112 67', '151 95 228',
];

interface HexTooltipState {
  field: EditorMobyPVarFieldDescriptor;
  x: number;
  y: number;
}

export function MobyPVarHexView({ data, modifiedByteMask, fields, entities, focusedPath, onShowField }: {
  data: Uint8Array;
  modifiedByteMask?: Uint8Array;
  fields: readonly EditorMobyPVarFieldDescriptor[];
  entities: readonly EditorEntity[];
  focusedPath?: string;
  onShowField(fieldPath: string): void;
}) {
  const shell = useRef<HTMLDivElement>(null);
  const viewport = useRef<HTMLDivElement>(null);
  const leaves = useMemo(() => flattenPVarFields(fields)
    .filter((field) => field.kind !== 'group' && field.kind !== 'unknown'), [fields]);
  const fieldColors = useMemo(() => new Map(leaves.map((field, index) => [
    field.path, HIGHLIGHT_COLORS[index % HIGHLIGHT_COLORS.length],
  ])), [leaves]);
  const fieldsByPath = useMemo(() => new Map(leaves.map((field) => [field.path, field])), [leaves]);
  const entitiesById = useMemo(() => new Map(entities.map((entity) => [entity.id, entity])), [entities]);
  const referenceDetails = useMemo(() => new Map(leaves.flatMap((field) => {
    const detail = referenceDetail(field, entitiesById);
    return detail ? [[field.path, detail] as const] : [];
  })), [entitiesById, leaves]);
  const [scrollTop, setScrollTop] = useState(0);
  const [activePath, setActivePath] = useState(focusedPath);
  const [tooltip, setTooltip] = useState<HexTooltipState>();
  const activeField = activePath ? fieldsByPath.get(activePath) : undefined;
  const activeReferenceDetail = activeField ? referenceDetails.get(activeField.path) : undefined;
  const window = pvarHexWindow(data.length, scrollTop, VIEWPORT_HEIGHT);
  const addressWidth = Math.max(4, Math.max(0, data.length - 1).toString(16).length);
  const modifiedByteCount = useMemo(() =>
    pvarModifiedByteCount(modifiedByteMask, data.length), [data.length, modifiedByteMask]);

  useEffect(() => {
    if (!focusedPath) return;
    const field = fieldsByPath.get(focusedPath);
    if (!field) return;
    setActivePath(focusedPath);
    viewport.current?.scrollTo({ top: Math.floor(field.offset / PVAR_HEX_BYTES_PER_ROW) * PVAR_HEX_ROW_HEIGHT });
  }, [fieldsByPath, focusedPath]);

  const showTooltip = (
    event: MouseEvent<HTMLButtonElement> | FocusEvent<HTMLButtonElement>,
    field: EditorMobyPVarFieldDescriptor,
  ) => {
    const shellBounds = shell.current?.getBoundingClientRect();
    if (!shellBounds) return;
    const bounds = event.currentTarget.getBoundingClientRect();
    setActivePath(field.path);
    setTooltip({
      field,
      x: bounds.left + bounds.width / 2 - shellBounds.left,
      y: bounds.top - shellBounds.top,
    });
  };

  return <Stack gap="xs">
    <Group justify="space-between" gap="xs" wrap="wrap">
      <Text c="dimmed" size="xs">Current project bytes · little-endian groups · read-only</Text>
      <Group gap="xs">
        {modifiedByteCount > 0 && <Badge color="orange" variant="light">• {modifiedByteCount} modified</Badge>}
        <Code>{data.length.toLocaleString()} bytes</Code>
      </Group>
    </Group>
    {activeField && <Group className="pvar-hex-selection" justify="space-between" gap="xs" wrap="nowrap">
      <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
        <span className="pvar-hex-swatch"
          style={{ '--pvar-highlight': fieldColors.get(activeField.path) } as CSSProperties} />
        <Text size="xs" truncate="end">
          <strong>{activeField.label}</strong> · {formatPVarByteRange(activeField)}
          {activeReferenceDetail && ` · ${activeReferenceDetail}`}
        </Text>
      </Group>
      <Button size="compact-xs" variant="subtle" onClick={() => onShowField(activeField.path)}>Show field</Button>
    </Group>}
    <div className="pvar-hex-shell" ref={shell}>
      <div className="pvar-hex" ref={viewport} role="region" aria-label="Raw PVar bytes" tabIndex={0}
        onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}>
        <div className="pvar-hex-content" style={{ height: window.totalHeight }}>
          {Array.from({ length: window.endRow - window.startRow }, (_, rowOffset) => {
            const row = window.startRow + rowOffset;
            const offset = row * PVAR_HEX_BYTES_PER_ROW;
            return <HexRow key={row} row={row} top={row * PVAR_HEX_ROW_HEIGHT} offset={offset}
              addressWidth={addressWidth} data={data} fields={leaves} fieldColors={fieldColors}
              modifiedByteMask={modifiedByteMask} referenceDetails={referenceDetails}
              activePath={activePath} onActivate={setActivePath} onShowTooltip={showTooltip}
              onHideTooltip={() => setTooltip(undefined)} />;
          })}
        </div>
      </div>
      {tooltip && <div className="pvar-hex-tooltip" role="tooltip" style={{ left: tooltip.x, top: tooltip.y }}>
        <strong>{tooltip.field.label}</strong>
        <span>{tooltip.field.path}</span>
        <span>{formatPVarByteRange(tooltip.field)}</span>
        {referenceDetails.get(tooltip.field.path) && <span>{referenceDetails.get(tooltip.field.path)}</span>}
      </div>}
    </div>
    <Text c="dimmed" size="xs">
      Colors identify schema fields; uncolored bytes are unmapped. Modified bytes are orange and marked •.
      {' '}Tab moves between field starts, not every byte.
    </Text>
  </Stack>;
}

function HexRow({ row, top, offset, addressWidth, data, fields, fieldColors, modifiedByteMask, referenceDetails,
  activePath, onActivate, onShowTooltip, onHideTooltip }: {
  row: number;
  top: number;
  offset: number;
  addressWidth: number;
  data: Uint8Array;
  fields: readonly EditorMobyPVarFieldDescriptor[];
  fieldColors: ReadonlyMap<string, string>;
  modifiedByteMask?: Uint8Array;
  referenceDetails: ReadonlyMap<string, string>;
  activePath?: string;
  onActivate(path: string): void;
  onShowTooltip(
    event: MouseEvent<HTMLButtonElement> | FocusEvent<HTMLButtonElement>,
    field: EditorMobyPVarFieldDescriptor,
  ): void;
  onHideTooltip(): void;
}) {
  const byteCount = Math.min(PVAR_HEX_BYTES_PER_ROW, data.length - offset);
  const bytes = Array.from({ length: byteCount }, (_, index) => data[offset + index]);
  const groups = Array.from({ length: PVAR_HEX_BYTES_PER_ROW / PVAR_HEX_GROUP_SIZE }, (_, groupIndex) => {
    const start = groupIndex * PVAR_HEX_GROUP_SIZE;
    return Array.from({ length: PVAR_HEX_GROUP_SIZE }, (_, index) => ({
      value: bytes[start + index],
      modified: pvarByteIsModified(modifiedByteMask, offset + start + index),
    })).reverse();
  });
  const segments = pvarHexRowSegments(fields, offset, byteCount);

  return <div className="pvar-hex-row" data-even={row % 2 === 0 || undefined} style={{ top }}>
    <span className="pvar-hex-address">0x{offset.toString(16).toUpperCase().padStart(addressWidth, '0')}</span>
    <span className="pvar-hex-values">
      <span className="pvar-hex-highlights">{segments.map((segment, index) => <button
        key={`${segment.field.path}:${segment.start}:${index}`}
        type="button"
        className="pvar-hex-highlight"
        data-active={segment.field.path === activePath || undefined}
        aria-label={[segment.field.label, formatPVarByteRange(segment.field),
          referenceDetails.get(segment.field.path)].filter(Boolean).join(', ')}
        tabIndex={segment.containsStart ? 0 : -1}
        style={{
          left: `calc(${segment.start}ch - 0.28ch)`,
          width: `calc(${segment.width}ch + 0.56ch)`,
          '--pvar-highlight': fieldColors.get(segment.field.path),
        } as CSSProperties}
        onClick={() => onActivate(segment.field.path)}
        onMouseEnter={(event) => onShowTooltip(event, segment.field)}
        onMouseLeave={onHideTooltip}
        onFocus={(event) => onShowTooltip(event, segment.field)}
        onBlur={onHideTooltip}
      />)}</span>
      <span className="pvar-hex-text" aria-hidden="true">{groups.map((group, groupIndex) => <span
        className="pvar-hex-group" key={groupIndex}>{group.map((byte, index) => <span
          className="pvar-hex-byte" data-modified={byte.modified || undefined} key={index}>
          {byte.value === undefined ? '  ' : hex(byte.value)}
        </span>)}</span>)}</span>
    </span>
    <span className="pvar-hex-ascii" aria-hidden="true">|{bytes.map((value) => value >= 0x20 && value <= 0x7e
      ? String.fromCharCode(value) : '.').join('').padEnd(PVAR_HEX_BYTES_PER_ROW, ' ')}|</span>
  </div>;
}

function hex(value: number): string {
  return value.toString(16).padStart(2, '0').toUpperCase();
}

function referenceDetail(
  field: EditorMobyPVarFieldDescriptor,
  entities: ReadonlyMap<string, EditorEntity>,
): string | undefined {
  if (field.kind !== 'reference' || !field.reference) return undefined;
  const target = field.reference.targetEntityId
    ? entities.get(field.reference.targetEntityId)
    : undefined;
  const targetLabel = target?.name
    ?? (field.reference.missing ? `Missing ${field.reference.targetKind}` : 'None');
  return `Encoded ${field.reference.sourceValue} → ${targetLabel}`;
}
