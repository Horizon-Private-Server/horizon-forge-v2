import { UYA_COLLISION_TYPE_LABELS } from './UyaCollisionVisualization.ts';

const UYA_COLLISION_ID_OPTIONS = Array.from({ length: 16 }, (_, value) => ({
  value: String(value), label: formatUyaCollisionTypeId(value),
}));
const UYA_SOUND_ID_OPTIONS = Array.from({ length: 16 }, (_, value) => ({
  value: String(value), label: formatUyaSoundTypeId(value),
}));
const RAW_COLLISION_ID_OPTIONS = Array.from({ length: 256 }, (_, value) => ({
  value: String(value), label: `0x${value.toString(16).padStart(2, '0').toUpperCase()}`,
}));
const NO_SOUND_ID_OPTIONS = [{ value: '0', label: 'None' }];

export function formatCollisionType(rawType: number, targetGame: string): string {
  const raw = (Math.trunc(rawType) & 0xff).toString(16).padStart(2, '0').toUpperCase();
  return targetGame.toUpperCase() === 'UYA'
    ? `${formatUyaSoundTypeId(uyaSoundTypeId(rawType))} · Type ${formatUyaCollisionTypeId(uyaCollisionTypeId(rawType))} · Raw 0x${raw}`
    : `Raw 0x${raw}`;
}

export function collisionTypeIdOptions(targetGame: string): Array<{ value: string; label: string }> {
  return targetGame.toUpperCase() === 'UYA' ? UYA_COLLISION_ID_OPTIONS : RAW_COLLISION_ID_OPTIONS;
}

export function soundTypeIdOptions(targetGame: string): Array<{ value: string; label: string }> {
  return targetGame.toUpperCase() === 'UYA' ? UYA_SOUND_ID_OPTIONS : NO_SOUND_ID_OPTIONS;
}

export function collisionTypeId(rawType: number, targetGame: string): number {
  return targetGame.toUpperCase() === 'UYA'
    ? uyaCollisionTypeId(rawType)
    : Math.trunc(rawType) & 0xff;
}

export function soundTypeId(rawType: number, targetGame: string): number {
  return targetGame.toUpperCase() === 'UYA' ? uyaSoundTypeId(rawType) : 0;
}

export function packCollisionType(
  collisionId: number,
  soundId: number,
  targetGame: string,
): number {
  return targetGame.toUpperCase() === 'UYA'
    ? packUyaCollisionType(collisionId, soundId)
    : Math.trunc(collisionId) & 0xff;
}

export function defaultCollisionType(targetGame: string): number {
  return targetGame.toUpperCase() === 'UYA' ? 0x0f : 0;
}

export function formatUyaCollisionTypeId(value: number): string {
  const id = Math.trunc(value) & 0x0f;
  return `0x${id.toString(16).toUpperCase()} · ${UYA_COLLISION_TYPE_LABELS[id]}`;
}

export function formatUyaSoundTypeId(value: number): string {
  return `Sound 0x${(Math.trunc(value) & 0x0f).toString(16).toUpperCase()}`;
}

export function uyaCollisionTypeId(rawType: number): number {
  return Math.trunc(rawType) & 0x0f;
}

export function uyaSoundTypeId(rawType: number): number {
  return (Math.trunc(rawType) >> 4) & 0x0f;
}

export function packUyaCollisionType(collisionTypeId: number, soundTypeId: number): number {
  return ((Math.trunc(soundTypeId) & 0x0f) << 4) | (Math.trunc(collisionTypeId) & 0x0f);
}
