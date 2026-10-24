import assert from 'node:assert/strict';
import test from 'node:test';

import type { EditorCommandResult, EditorMobyPVarDescriptor, EditorSnapshot } from '../src/types/EditorRuntime.js';
import { applyEditorCommandResult } from '../src/utils/EditorCommandResult.ts';

const transform = (x: number) => ({
  position: { x, y: 0, z: 0 },
  rotation: { x: 0, y: 0, z: 0, w: 1 },
  scale: { x: 1, y: 1, z: 1 },
});

function snapshot(): EditorSnapshot {
  return {
    entities: [
      { id: 'a', transform: transform(0), state: { dirty: false } },
      { id: 'b', transform: transform(1), state: { dirty: false } },
    ],
    levelSettings: {
      backgroundColor: [0, 0, 0], fogColor: [0, 0, 0],
      fogNearDistance: 0, fogFarDistance: 1, fogNearIntensity: 0, fogFarIntensity: 1,
    },
    isDirty: false,
    canUndo: false,
    canRedo: false,
    lastEventSequence: 1,
  } as EditorSnapshot;
}

test('transform command patches preserve unaffected editor entities', () => {
  const before = snapshot();
  const after = applyEditorCommandResult(before, {
    kind: 'transforms',
    transforms: [{ entityId: 'a', transform: transform(2), dirty: true }],
    isDirty: true,
    canUndo: true,
    canRedo: false,
    lastEventSequence: 2,
  });

  assert.equal(after.entities[0].transform.position.x, 2);
  assert.equal(after.entities[0].state.dirty, true);
  assert.equal(after.entities[1], before.entities[1]);
  assert.equal(after.canUndo, true);
  assert.equal(after.lastEventSequence, 2);
});

test('level settings patches preserve the entity collection', () => {
  const before = snapshot();
  const levelSettings = { ...before.levelSettings!, fogFarDistance: 50 };
  const after = applyEditorCommandResult(before, {
    kind: 'levelSettings', levelSettings, isDirty: true, canUndo: true, canRedo: false, lastEventSequence: 2,
  });

  assert.equal(after.entities, before.entities);
  assert.equal(after.levelSettings, levelSettings);
});

test('selection patches preserve geometry and update selected and deselected PVars', () => {
  const before = snapshot();
  const pvar = { fields: [], rawData: new Uint8Array([1, 2]) } as unknown as EditorMobyPVarDescriptor;
  before.entities[0].mobyPVar = pvar;
  const patch = {
    kind: 'selection', selection: ['b'], mobyPVars: [{ entityId: 'a', mobyPVar: { ...pvar, rawData: undefined } }],
    isDirty: false, canUndo: true, canRedo: false, lastEventSequence: 2,
  } satisfies EditorCommandResult;
  const after = applyEditorCommandResult(before, patch);
  assert.deepEqual(after.selection, ['b']);
  assert.equal(after.entities[0].transform, before.entities[0].transform);
  assert.equal(after.entities[0].state, before.entities[0].state);
  assert.equal(after.entities[0].mobyPVar?.rawData, undefined);
  assert.equal(before.entities[0].mobyPVar.rawData, pvar.rawData);
  assert.equal(after.entities[1], before.entities[1]);
  assert.equal(after.canUndo, true);
  const cleared = applyEditorCommandResult(after, { ...patch, selection: [], mobyPVars: [] });
  assert.equal(cleared.entities, after.entities);
  assert.deepEqual(cleared.selection, []);
  const removed = applyEditorCommandResult(after, { ...patch, mobyPVars: [{ entityId: 'a' }] });
  assert.equal(Object.hasOwn(removed.entities[0], 'mobyPVar'), false);
  for (const invalid of [
    { selection: ['unknown'] }, { selection: ['b', 'b'] },
    { mobyPVars: [{ entityId: 'unknown' }] },
    { mobyPVars: [{ entityId: 'a' }, { entityId: 'a' }] },
  ]) assert.throws(() => applyEditorCommandResult(before, { ...patch, ...invalid }), TypeError);
});
