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

export function virtualGridWindow(
  itemCount: number,
  width: number,
  height: number,
  scrollTop: number,
  minimumCardWidth: number,
  rowHeight: number,
): VirtualGridWindow {
  const gap = 8;
  const columns = Math.max(1, Math.floor((Math.max(minimumCardWidth, width) + gap) / minimumCardWidth));
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
