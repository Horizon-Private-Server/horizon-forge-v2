import type { SoundProgramEntry } from '../types/SoundWorkspace.js';

export function soundSlotsForMoby(programs: readonly SoundProgramEntry[], classId: number) {
  const slots = new Map<string, { slot: number; program: SoundProgramEntry }>();
  for (const program of programs) {
    for (const dependent of program.mobyDependents) {
      if (dependent.classId !== classId) continue;
      for (const slot of dependent.remapSlots) {
        slots.set(`${slot}:${program.nativeIndex}`, { slot, program });
      }
    }
  }
  return [...slots.values()].sort((left, right) =>
    left.slot - right.slot || left.program.nativeIndex - right.program.nativeIndex);
}
