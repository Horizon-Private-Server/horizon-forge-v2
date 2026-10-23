import type { MobySimulationFactory } from '../../types/MobySimulation.js';

export class MobySimulationRegistry {
  private readonly factories = new Map<string, MobySimulationFactory>();

  register(game: string, classId: number, factory: MobySimulationFactory): () => void {
    const key = simulationKey(game, classId);
    if (this.factories.has(key))
      throw new Error(`A simulator is already registered for ${game} class 0x${classId.toString(16)}.`);
    this.factories.set(key, factory);
    return () => {
      if (this.factories.get(key) === factory) this.factories.delete(key);
    };
  }

  resolve(game: string, classId: number): MobySimulationFactory | undefined {
    return this.factories.get(simulationKey(game, classId));
  }

  get size(): number {
    return this.factories.size;
  }
}

function simulationKey(game: string, classId: number): string {
  if (!Number.isSafeInteger(classId) || classId < 0)
    throw new Error(`Invalid Moby simulator class ID ${classId}.`);
  return `${game.trim().toLowerCase()}:${classId}`;
}
