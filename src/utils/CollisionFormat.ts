export function formatCollisionType(rawType: number, targetGame: string): string {
  const raw = (Math.trunc(rawType) & 0xff).toString(16).padStart(2, '0').toUpperCase();
  return targetGame.toUpperCase() === 'UYA'
    ? `Sound 0x${raw[0]} · Type 0x${raw[1]} · Raw 0x${raw}`
    : `Raw 0x${raw}`;
}
