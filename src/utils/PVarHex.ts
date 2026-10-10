import type { EditorMobyPVarFieldDescriptor } from '../types/EditorRuntime.js';

export const PVAR_HEX_BYTES_PER_ROW = 16;
export const PVAR_HEX_GROUP_SIZE = 4;
export const PVAR_HEX_GROUP_GAP = 2;
export const PVAR_HEX_ROW_HEIGHT = 21;

export function pvarByteIsModified(mask: Uint8Array | undefined, offset: number): boolean {
  return offset >= 0 && Boolean((mask?.[Math.floor(offset / 8)] ?? 0) & (1 << (offset % 8)));
}

export function pvarModifiedByteCount(mask: Uint8Array | undefined, byteLength: number): number {
  if (!mask || byteLength <= 0) return 0;
  let count = 0;
  const maskLength = Math.min(mask.length, Math.ceil(byteLength / 8));
  for (let index = 0; index < maskLength; index++) {
    const remainingBits = Math.min(8, byteLength - index * 8);
    let value = mask[index] & ((1 << remainingBits) - 1);
    while (value) {
      value &= value - 1;
      count += 1;
    }
  }
  return count;
}

export function flattenPVarFields(
  fields: readonly EditorMobyPVarFieldDescriptor[],
): EditorMobyPVarFieldDescriptor[] {
  const result: EditorMobyPVarFieldDescriptor[] = [];
  const pending = fields.slice().reverse();
  while (pending.length) {
    const field = pending.pop()!;
    result.push(field);
    for (let index = field.children.length - 1; index >= 0; index--) pending.push(field.children[index]);
  }
  return result;
}

export function structuredPVarFields(
  fields: readonly EditorMobyPVarFieldDescriptor[],
): EditorMobyPVarFieldDescriptor[] {
  return fields.flatMap((field) => {
    if (field.kind === 'unknown') return [];
    const children = structuredPVarFields(field.children);
    return field.kind === 'group' && children.length === 0 ? [] : [{ ...field, children }];
  });
}

export function pvarFieldsAtOffset(
  fields: readonly EditorMobyPVarFieldDescriptor[],
  offset: number,
): EditorMobyPVarFieldDescriptor[] {
  return flattenPVarFields(fields)
    .filter((field) => field.kind !== 'group' && offset >= field.offset && offset < field.offset + field.length)
    .sort((left, right) => left.length - right.length || left.path.localeCompare(right.path));
}

export function pvarHexWindow(
  byteLength: number,
  scrollTop: number,
  viewportHeight: number,
  overscan = 6,
): { startRow: number; endRow: number; rowCount: number; totalHeight: number } {
  const rowCount = Math.ceil(byteLength / PVAR_HEX_BYTES_PER_ROW);
  const firstVisible = Math.max(0, Math.floor(scrollTop / PVAR_HEX_ROW_HEIGHT));
  const visibleCount = Math.ceil(Math.max(0, viewportHeight) / PVAR_HEX_ROW_HEIGHT);
  const startRow = Math.max(0, firstVisible - overscan);
  const endRow = Math.min(rowCount, firstVisible + visibleCount + overscan);
  return { startRow, endRow, rowCount, totalHeight: rowCount * PVAR_HEX_ROW_HEIGHT };
}

export function pvarHexRowSegments(
  fields: readonly EditorMobyPVarFieldDescriptor[],
  rowOffset: number,
  rowByteCount: number,
): Array<{ field: EditorMobyPVarFieldDescriptor; start: number; width: number; containsStart: boolean }> {
  const rowEnd = rowOffset + rowByteCount;
  return fields.flatMap((field) => {
    const start = Math.max(rowOffset, field.offset);
    const end = Math.min(rowEnd, field.offset + field.length);
    if (start >= end) return [];
    const columns = Array.from({ length: end - start }, (_, index) =>
      pvarHexVisualColumn(start + index - rowOffset)).sort((left, right) => left - right);
    const intervals: Array<{ start: number; width: number }> = [];
    for (const column of columns) {
      const previous = intervals.at(-1);
      if (previous && previous.start + previous.width === column) previous.width += 2;
      else intervals.push({ start: column, width: 2 });
    }
    const fieldStartColumn = field.offset >= rowOffset && field.offset < rowEnd
      ? pvarHexVisualColumn(field.offset - rowOffset)
      : -1;
    return intervals.map((interval) => ({
      field,
      ...interval,
      containsStart: fieldStartColumn >= interval.start
        && fieldStartColumn < interval.start + interval.width,
    }));
  }).sort((left, right) => right.field.length - left.field.length
    || left.field.path.localeCompare(right.field.path));
}

function pvarHexVisualColumn(localOffset: number): number {
  const group = Math.floor(localOffset / PVAR_HEX_GROUP_SIZE);
  const index = localOffset % PVAR_HEX_GROUP_SIZE;
  return group * (PVAR_HEX_GROUP_SIZE * 2 + PVAR_HEX_GROUP_GAP)
    + (PVAR_HEX_GROUP_SIZE - index - 1) * 2;
}

export function formatPVarByteRange(field: Pick<EditorMobyPVarFieldDescriptor, 'offset' | 'length'>): string {
  const end = field.offset + Math.max(0, field.length - 1);
  const width = Math.max(4, end.toString(16).length);
  return `0x${field.offset.toString(16).padStart(width, '0')}–0x${end.toString(16).padStart(width, '0')}`;
}
