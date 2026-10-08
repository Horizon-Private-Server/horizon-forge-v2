import type { EditorInstancedCollisionCandidate } from '../types/EditorRuntime.js';

export function instancedCollisionWorstOctantBytes(candidate: EditorInstancedCollisionCandidate): number {
  return Math.max(0, ...candidate.octants.map((octant) => octant.encodedByteCount));
}

export function recommendInstancedCollisionCandidate(
  candidates: EditorInstancedCollisionCandidate[],
  maximumDeviation: number,
): EditorInstancedCollisionCandidate | undefined {
  return candidates.filter((candidate) => candidate.hardViolationCount === 0
    && (candidate.combinedAnalysis?.hardViolationCount ?? 0) === 0
    && !candidate.combinedAnalysis?.error
    && candidate.maximumDeviation <= maximumDeviation)
    .sort((left, right) => instancedCollisionWorstOctantBytes(left) - instancedCollisionWorstOctantBytes(right)
      || left.encodedByteCount - right.encodedByteCount)[0];
}
