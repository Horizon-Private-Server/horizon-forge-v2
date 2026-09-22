export function formatBytes(value?: number): string {
  if (value === undefined) return 'unknown';
  const gib = value / 1024 ** 3;
  return gib >= 0.1 ? `${gib.toFixed(2)} GiB` : `${(value / 1024 ** 2).toFixed(1)} MiB`;
}

export function fileName(value: string): string {
  return value.split(/[\\/]/).filter(Boolean).at(-1) ?? value;
}
