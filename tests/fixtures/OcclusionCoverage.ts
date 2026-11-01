import type { ProjectVector3 } from '../../src/types/EditorRuntime.js';
import { createOcclusionCoverageSource, setOcclusionSeedGraph } from '../../src/utils/OcclusionCoverage.ts';

export function createGraphVolume(name: string, position?: ProjectVector3) {
  const volume = createOcclusionCoverageSource('box', name, position);
  volume.boxSize = { x: 32, y: 32, z: 32 };
  return setOcclusionSeedGraph(volume, true);
}
