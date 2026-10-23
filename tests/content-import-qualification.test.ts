import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';

import type {
  ContentImportPlan, ContentImportReferenceChoice,
} from '../src/types/ContentImport.js';
import { createContentImportQualificationRecord } from '../src/utils/ContentImportQualification.ts';

const plan: ContentImportPlan = {
  schemaVersion: 3,
  capabilitySchemaVersion: 1,
  batchId: 'import-11111111111111111111111111111111',
  game: 'UYA',
  region: 'NTSC-U',
  revision: '1.00',
  sourceFingerprint: 'a'.repeat(32),
  sourceLevelHash: 'b'.repeat(64),
  sourceLevel: 40,
  targetProjectId: '11111111-1111-1111-1111-111111111111',
  targetFingerprint: 'c'.repeat(64),
  targetLevel: 41,
  estimatedBytes: 1234,
  families: [{
    family: 'ties', label: 'TIEs', supportedMode: 'addAndReplace',
    mode: 'add', sourceState: 'present', selected: true,
    defaultSelected: true, selectable: true,
    add: ['entity:one'], reuse: ['asset:shared'], remap: [], remove: [], protect: [],
    dependencyReasons: [], conflicts: [], estimatedBytes: 1234, warnings: [], blockers: [],
  }, {
    family: 'mobys', label: 'Mobys', supportedMode: 'addAndReplace',
    mode: 'add', sourceState: 'present', selected: false,
    defaultSelected: false, selectable: true,
    add: [], reuse: [], remap: [], remove: [], protect: [], dependencyReasons: [],
    conflicts: [], estimatedBytes: 0, warnings: [], blockers: [],
  }],
  warnings: ['review'],
  blockers: [],
  referenceRequirements: [],
};

test('content import qualification records are deterministic, compact, and path-free', async () => {
  const choices: ContentImportReferenceChoice[] = [
    { ownerSourceIndex: 2, fieldKey: 'target', sourceValue: 8, action: 'clear' },
    {
      ownerSourceIndex: 1, fieldKey: 'trigger.cuboid', sourceValue: 4, action: 'map',
      targetEntityId: '22222222-2222-2222-2222-222222222222',
    },
  ];
  const first = await createContentImportQualificationRecord(plan, choices);
  const repeated = await createContentImportQualificationRecord(plan, [...choices].reverse());
  assert.equal(first, repeated);

  const record = JSON.parse(first) as Record<string, unknown>;
  const { recordSha256, ...recordBody } = record;
  assert.equal(recordSha256, createHash('sha256').update(JSON.stringify(recordBody)).digest('hex'));
  assert.equal(record.batchId, plan.batchId);
  assert.deepEqual(record.selectedFamilies, [{
    family: 'ties', mode: 'add', addCount: 1, reuseCount: 1, remapCount: 0,
    removeCount: 0, protectedCount: 0, estimatedBytes: 1234,
  }]);
  assert.equal(first.includes('/source.iso'), false);
  assert.equal(first.includes('/project'), false);
});
