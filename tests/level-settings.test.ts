import assert from 'node:assert/strict';
import test from 'node:test';

import type { EditorEntity } from '../src/types/EditorRuntime.js';
import {
  levelReferenceCandidates, levelReferenceState, levelReferenceValue,
} from '../src/utils/LevelSettings.ts';

function geometryEntity(
  id: string,
  kind: 'spline' | 'cuboid',
  section: string,
  sourceIndex: number,
): EditorEntity {
  return {
    id,
    name: `${kind} ${sourceIndex}`,
    layer: `${kind}s`,
    transform: {
      position: { x: 0, y: 0, z: 0 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
    provenance: { game: 'UYA', level: 5, section, sourceIndex },
    geometry: { kind, points: [] },
    transformModes: ['translate'],
    state: {
      dirty: false, hidden: false, disabled: false, locked: false,
      readOnly: false, invalid: false, missingAsset: false,
    },
  };
}

test('level reference candidates include only deterministic source-backed targets', () => {
  const later = geometryEntity('later', 'spline', 'gameplay/core/splines', 8);
  const earlier = geometryEntity('earlier', 'spline', 'gameplay/core/splines', 2);
  const wrongSection = geometryEntity('wrong-section', 'spline', 'gameplay/core/grind_splines', 0);
  const wrongKind = geometryEntity('wrong-kind', 'cuboid', 'gameplay/core/cuboids', 0);
  const candidates = levelReferenceCandidates(
    [later, wrongSection, wrongKind, earlier], 'spline', 'gameplay/core/splines',
  );
  assert.deepEqual(candidates.map((entity) => entity.id), ['earlier', 'later']);
  assert.deepEqual(levelReferenceValue('later', candidates), { sourceValue: 8, entityId: 'later' });
  assert.deepEqual(levelReferenceValue(undefined, candidates), { sourceValue: -1 });
  assert.equal(levelReferenceValue('wrong-section', candidates), undefined);
});

test('level reference state distinguishes null, unresolved, and wrong-kind values', () => {
  const spline = geometryEntity('spline', 'spline', 'gameplay/core/splines', 2);
  const cuboid = geometryEntity('cuboid', 'cuboid', 'gameplay/core/cuboids', 3);
  const candidates = [spline];
  const none = levelReferenceState('project', 'level.shipPath', 'spline',
    { sourceValue: -1 }, candidates, [spline, cuboid]);
  assert.equal(none.reference.missing, false);
  assert.equal(none.diagnostic, undefined);

  const unresolved = levelReferenceState('project', 'level.shipPath', 'spline',
    { sourceValue: 7 }, candidates, [spline, cuboid]);
  assert.equal(unresolved.reference.missing, true);
  assert.match(unresolved.diagnostic!, /Source index 7/);

  const wrongKind = levelReferenceState('project', 'level.shipPath', 'spline',
    { sourceValue: 3, entityId: 'cuboid' }, candidates, [spline, cuboid]);
  assert.equal(wrongKind.reference.missing, true);
  assert.match(wrongKind.diagnostic!, /expected spline/);
});
