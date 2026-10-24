export interface VirtualGridWindow {
  columns: number;
  cardWidth: number;
  startIndex: number;
  endIndex: number;
  totalHeight: number;
}

export function assetGridWindow(
  itemCount: number,
  width: number,
  height: number,
  scrollTop: number,
): VirtualGridWindow {
  return virtualGridWindow(itemCount, width, height, scrollTop, 168, 218);
}

export function assetGridItemVisible(index: number, columns: number, height: number, scrollTop: number): boolean {
  const top = Math.floor(index / columns) * 218;
  return height > 0 && top < scrollTop + height && top + 210 > scrollTop;
}

export function virtualGridWindow(
  itemCount: number,
  width: number,
  height: number,
  scrollTop: number,
  minimumCardWidth: number,
  rowHeight: number,
  maximumColumns = Number.POSITIVE_INFINITY,
): VirtualGridWindow {
  const gap = 8;
  const columns = Math.min(
    Math.max(1, Math.floor(maximumColumns)),
    Math.max(1, Math.floor((Math.max(minimumCardWidth, width) + gap) / minimumCardWidth)),
  );
  const rows = Math.ceil(itemCount / columns);
  const startRow = Math.max(0, Math.floor(Math.max(0, scrollTop) / rowHeight) - 2);
  const endRow = Math.min(rows, Math.ceil((Math.max(0, scrollTop) + Math.max(1, height)) / rowHeight) + 2);
  return {
    columns,
    cardWidth: Math.max(1, (Math.max(minimumCardWidth, width) - gap * (columns - 1)) / columns),
    startIndex: Math.min(itemCount, startRow * columns),
    endIndex: Math.min(itemCount, endRow * columns),
    totalHeight: Math.max(0, rows * rowHeight - gap),
  };
}
