import type {
  ContentImportPlan, ContentImportReferenceChoice,
} from '../types/ContentImport.js';

export async function createContentImportQualificationRecord(
  plan: ContentImportPlan,
  referenceChoices: readonly ContentImportReferenceChoice[],
): Promise<string> {
  const selectedFamilies = plan.families.filter((family) => family.selected).map((family) => ({
    family: family.family,
    mode: family.mode,
    addCount: family.add.length,
    reuseCount: family.reuse.length,
    remapCount: family.remap.length,
    removeCount: family.remove.length,
    protectedCount: family.protect.length,
    estimatedBytes: family.estimatedBytes,
  }));
  const choices = [...referenceChoices].sort((left, right) =>
    left.ownerSourceIndex - right.ownerSourceIndex
      || left.fieldKey.localeCompare(right.fieldKey)
      || left.sourceValue - right.sourceValue);
  const record = {
    schemaVersion: 1,
    kind: 'forge-content-import-plan',
    planSchemaVersion: plan.schemaVersion,
    capabilitySchemaVersion: plan.capabilitySchemaVersion,
    batchId: plan.batchId,
    source: {
      game: plan.game,
      region: plan.region,
      revision: plan.revision,
      fingerprint: plan.sourceFingerprint,
      level: plan.sourceLevel,
      levelSha256: plan.sourceLevelHash,
    },
    target: {
      projectId: plan.targetProjectId,
      preImportFingerprint: plan.targetFingerprint,
      level: plan.targetLevel,
    },
    estimatedBytes: plan.estimatedBytes,
    selectedFamilies,
    referenceChoices: choices,
    warnings: plan.warnings,
    blockers: plan.blockers,
  };
  const bytes = new TextEncoder().encode(JSON.stringify(record));
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  const recordSha256 = [...new Uint8Array(digest)]
    .map((value) => value.toString(16).padStart(2, '0')).join('');
  return JSON.stringify({ recordSha256, ...record }, null, 2);
}
