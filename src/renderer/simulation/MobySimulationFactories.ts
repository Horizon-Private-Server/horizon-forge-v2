import { MobySimulationRegistry } from './MobySimulationRegistry.ts';
import { createUyaOceanSimulation, UYA_OCEAN_CLASS_ID } from './uya/UyaOceanSimulation.ts';

export function createMobySimulationRegistry(): MobySimulationRegistry {
  const registry = new MobySimulationRegistry();
  registry.register('UYA', UYA_OCEAN_CLASS_ID, createUyaOceanSimulation);
  return registry;
}
