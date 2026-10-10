export function parseRgb(value: string): [number, number, number] {
  const channels = value.match(/\d+/g)?.slice(0, 3).map(Number);
  return channels?.length === 3
    ? channels.map((channel) => Math.min(255, channel)) as [number, number, number]
    : [0, 0, 0];
}
