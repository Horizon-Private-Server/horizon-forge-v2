import type { EditorTieCollisionCandidate } from '../types/EditorRuntime.js';

export function tieCollisionWorstOctantBytes(candidate: EditorTieCollisionCandidate): number {
  return Math.max(0, ...candidate.octants.map((octant) => octant.encodedByteCount));
}

export function recommendTieCollisionCandidate(
  candidates: EditorTieCollisionCandidate[],
  maximumDeviation: number,
): EditorTieCollisionCandidate | undefined {
  return candidates.filter((candidate) => candidate.hardViolationCount === 0
    && (candidate.combinedAnalysis?.hardViolationCount ?? 0) === 0
    && !candidate.combinedAnalysis?.error
    && candidate.maximumDeviation <= maximumDeviation)
    .sort((left, right) => tieCollisionWorstOctantBytes(left) - tieCollisionWorstOctantBytes(right)
      || left.encodedByteCount - right.encodedByteCount)[0];
}
