import assert from 'node:assert/strict';
import test from 'node:test';

import type { EditorEntity, EditorGroup, EditorMapGroup, EditorReference } from '../src/types/EditorRuntime.js';
import {
  collisionTypeId, collisionTypeIdOptions, defaultCollisionType, formatCollisionType,
  formatUyaCollisionTypeId, packCollisionType, soundTypeId,
} from '../src/utils/CollisionFormat.ts';
import { editorEntityDisplayName } from '../src/utils/EntityDisplay.ts';
import {
  buildSceneAreaGroups, buildSceneSemanticGroups, sceneGroupDropTargets, sceneGroupMemberIds, sceneGroupMemberValue,
  sceneGroupPage, sceneGroupSelectedValues, sceneGroupSelection, sceneGroupValue,
  selectedMissingGroupMemberCount,
} from '../src/utils/SceneGroups.ts';
import {
  buildSceneEntityGroups, buildSkyTreeItems, buildTerrainTreeItems, entityStateLabel,
  entityTreeKind, entityTreeText, nextTreeSelection, nextViewportSelection,
} from '../src/utils/SceneSelection.ts';
import { parseSplinePointId, removeSplinePoints, splinePointId } from '../src/utils/SplinePoints.ts';

function entity(index: number): EditorEntity {
  return {
    id: String(index),
    name: `Moby ${index}`,
    layer: index % 2 ? 'mobys' : 'gameplay',
    transform: {
      position: { x: 0, y: 0, z: 0 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
    asset: { id: 'asset', kind: 'moby' },
    transformModes: ['translate', 'rotate', 'scale'],
    state: {
      dirty: index === 42,
      hidden: false,
      disabled: false,
      locked: index === 42,
      readOnly: false,
      invalid: false,
      missingAsset: index === 42,
    },
  };
}

test('scene tree grouping filters, labels states, and returns every result', () => {
  const entities = Array.from({ length: 25_000 }, (_, index) => entity(index));
  const started = performance.now();
  const all = buildSceneEntityGroups(entities, '');
  const elapsed = performance.now() - started;
  assert.equal(all.matched, 25_000);
  assert.equal(all.groups.reduce((total, group) => total + group.entities.length, 0), 25_000);
  assert.equal(all.groups.length, 2);
  assert.ok(elapsed < 1_000, `large tree model took ${elapsed.toFixed(1)} ms`);

  const filtered = buildSceneEntityGroups(entities, 'moby 42');
  assert.ok(filtered.matched > 0);
  assert.ok(filtered.groups.flatMap((group) => group.entities).every((value) => value.name.includes('42')));
  assert.equal(entityStateLabel(entity(42)), 'Moby 42 [dirty, locked, missing asset]');
  assert.equal(entityTreeKind(entity(42)), 'moby');
  assert.equal(entityTreeKind({ ...entity(42), asset: undefined, geometry: { kind: 'cuboid', points: [] } }), 'cuboid');
  assert.equal(entityTreeText({ ...entity(42), name: 'Moby 0x002A #42' }),
    '0x002A #42 [dirty, locked, missing asset]');
  const named = { ...entity(42), name: 'Moby 0x002A #42', sourceClassName: 'Blitz Gun Reticule' };
  assert.equal(editorEntityDisplayName(named), 'Blitz Gun Reticule #42');
  assert.equal(entityTreeText(named), 'Blitz Gun Reticule #42 [dirty, locked, missing asset]');
  assert.equal(editorEntityDisplayName({ ...named, name: 'moby:0x002A' }), 'Blitz Gun Reticule');
  assert.equal(editorEntityDisplayName({ ...named, name: 'Custom spawn' }), 'Custom spawn');
  assert.equal(buildSceneEntityGroups([named], 'blitz gun').matched, 1);
  assert.equal(entityTreeText(entity(42)), 'Moby 42 [dirty, locked, missing asset]');
  assert.equal(entityTreeText({ ...entity(42), name: 'Custom name' }), 'Custom name [dirty, locked, missing asset]');
});

test('scene tree keeps every populated layer visible', () => {
  const entities = ['mobys', 'ties', 'shrubs'].flatMap((layer, group) =>
    Array.from({ length: 1_000 }, (_, index) => ({ ...entity(group * 1_000 + index), layer })));
  const model = buildSceneEntityGroups(entities, '');

  assert.deepEqual(model.groups.map((group) => group.layer), ['mobys', 'shrubs', 'ties']);
  assert.equal(model.groups.reduce((total, group) => total + group.entities.length, 0), 3_000);
  assert.ok(model.groups.every((group) => group.entities.length > 0));
});

test('semantic group tree preserves order, duplicate membership, and missing members', () => {
  const entities = [entity(1), entity(2), entity(3)];
  const groups: EditorGroup[] = [
    { id: 'first', name: 'Spawn team', members: ['1', 'missing', '2'], missingMembers: ['missing'] },
    { id: 'second', name: 'Secondary', members: ['2'], missingMembers: [] },
  ];
  const model = buildSceneSemanticGroups(groups, entities, '');
  assert.deepEqual(model.groups.map((group) => group.name), ['Spawn team', 'Secondary']);
  assert.deepEqual(model.groups[0].members.map((member) => [member.entityId, member.missing]), [
    ['1', false], ['missing', true], ['2', false],
  ]);
  assert.deepEqual(model.groups[1].members.map((member) => member.entityId), ['2']);

  assert.deepEqual(buildSceneSemanticGroups(groups, entities, 'spawn team').groups[0].members
    .map((member) => member.entityId), ['1', 'missing', '2']);
  assert.deepEqual(buildSceneSemanticGroups(groups, entities, 'moby 2').groups
    .map((group) => [group.name, group.members.map((member) => member.entityId)]), [
    ['Spawn team', ['2']], ['Secondary', ['2']],
  ]);
  assert.equal(buildSceneSemanticGroups(groups, entities, 'missing').groups[0].members[0].missing, true);

  const memberIds = sceneGroupMemberIds(model.groups);
  const dropTargets = sceneGroupDropTargets(model.groups);
  const firstValue = sceneGroupMemberValue('first', '2');
  const secondValue = sceneGroupMemberValue('second', '2');
  assert.equal(dropTargets.get(sceneGroupValue('first')), sceneGroupValue('first'));
  assert.equal(dropTargets.get(firstValue), sceneGroupValue('first'));
  assert.equal(dropTargets.get('missing:first:missing'), sceneGroupValue('first'));
  assert.deepEqual(sceneGroupSelectedValues(['2'], memberIds, model.groups), [
    firstValue, secondValue, sceneGroupValue('second'),
  ]);
  assert.deepEqual(sceneGroupSelection([sceneGroupValue('first')], model.groups, memberIds), ['1', '2']);
  assert.deepEqual(sceneGroupSelection([secondValue], model.groups, memberIds), ['2']);
  assert.deepEqual(sceneGroupSelection([firstValue], model.groups, memberIds, secondValue), [],
    'deselecting one duplicate occurrence clears the authoritative entity selection');
  const selectedGroup = sceneGroupSelectedValues(['1', '2'], memberIds, model.groups);
  assert.ok(selectedGroup.includes(sceneGroupValue('first')));
  assert.deepEqual(sceneGroupSelection(
    selectedGroup.filter((value) => value !== sceneGroupValue('first')),
    model.groups,
    memberIds,
    sceneGroupValue('first'),
  ), [], 'deselecting a group clears all of its authoritative members');
});

test('areas become baked semantic groups of their referenced geometry', () => {
  const area = {
    ...entity(10), name: 'Area #10', asset: undefined, geometry: { kind: 'area', points: [] },
  } as EditorEntity;
  const spline = {
    ...entity(11), name: 'Spline #11', asset: undefined, geometry: { kind: 'spline', points: [] },
  } as EditorEntity;
  const references: EditorReference[] = [{
    ownerEntityId: area.id,
    fieldKey: 'geometry.area.splines',
    nullable: false,
    sourceValue: 3,
    missing: false,
    domain: 'entity',
    targetKind: 'spline',
    targetEntityId: spline.id,
  }, {
    ownerEntityId: area.id,
    fieldKey: 'geometry.area.cuboids',
    nullable: false,
    sourceValue: 7,
    missing: true,
    domain: 'entity',
    targetKind: 'cuboid',
  }];

  const model = buildSceneAreaGroups([area, spline], references, '');
  assert.equal(model.groups.length, 1);
  assert.equal(model.groups[0].area.id, area.id);
  assert.deepEqual(model.groups[0].members.map((member) => [member.entity?.id, member.missing]), [
    [spline.id, false], [undefined, true],
  ]);
  assert.equal(buildSceneAreaGroups([area, spline], references, 'spline #11').groups[0].members.length, 1);
  assert.equal(buildSceneAreaGroups([area, spline], references, 'area #10').groups[0].members.length, 2);
  assert.equal(buildSceneAreaGroups([area, spline], references, 'not present').groups.length, 0);
  assert.equal(selectedMissingGroupMemberCount([spline.id], [area, spline], [], [], references), 1,
    'selecting all resolved area children preserves unresolved-member transform safety');
});

test('selected incomplete groups report unresolved transform members', () => {
  const entities = [entity(1), entity(2), entity(3)];
  const groups: EditorGroup[] = [{
    id: 'custom', name: 'Incomplete', members: ['1', 'missing', '2'], missingMembers: ['missing'],
  }];
  const mapGroups: EditorMapGroup[] = [{
    kind: 'moby', sourceIndex: 4, members: ['2', '3'], missingSourceIndices: [9],
  }];

  assert.equal(selectedMissingGroupMemberCount(['1'], entities, groups, mapGroups), 0);
  assert.equal(selectedMissingGroupMemberCount(['1', '2'], entities, groups, mapGroups), 1);
  assert.equal(selectedMissingGroupMemberCount(['1', '2', '3'], entities, groups, mapGroups), 2);
});

test('semantic group pages bound rendered members for representative large projects', () => {
  const entities = Array.from({ length: 25_000 }, (_, index) => entity(index));
  const groups: EditorGroup[] = Array.from({ length: 25 }, (_, groupIndex) => ({
    id: `group-${groupIndex}`,
    name: `Group ${groupIndex}`,
    members: Array.from({ length: 1_000 }, (_, offset) => String(groupIndex * 1_000 + offset)),
    missingMembers: [],
  }));
  const started = performance.now();
  const model = buildSceneSemanticGroups(groups, entities, '');
  const elapsed = performance.now() - started;
  assert.ok(elapsed < 1_000, `large semantic tree model took ${elapsed.toFixed(1)} ms`);
  assert.equal(model.matched, 25_000);
  assert.equal(model.groups.length, 25);
  assert.ok(model.groups.reduce((count, group) => count + sceneGroupPage(group.members, 0).values.length, 0)
    <= groups.length * 250);
  assert.equal(sceneGroupPage(model.groups[0].members, 0).values.length, 250);
  const last = sceneGroupPage(model.groups[0].members, 99);
  assert.equal(last.page, 3);
  assert.equal(last.values.length, 250);
});

test('terrain package sections become filterable scene-tree items', () => {
  const urls = [
    'forge-asset://key/assets/tfrag/tfrag.gltf',
    'forge-asset://key/assets/tfrag/chunks/chunk2/tfrag.gltf',
  ];
  assert.deepEqual(buildTerrainTreeItems(urls, ''), [
    { value: urls[0], label: 'Primary tfrag' },
    { value: urls[1], label: 'Chunk 2 tfrag' },
  ]);
  assert.deepEqual(buildTerrainTreeItems(urls, 'chunk 2'), [
    { value: urls[1], label: 'Chunk 2 tfrag' },
  ]);
});

test('sky meshes become filterable scene-tree items', () => {
  assert.deepEqual(buildSkyTreeItems(['skybox_shell_00', 'clouds'], ''), [
    { value: 'render:sky:0', label: 'Sky shell 0' },
    { value: 'render:sky:1', label: 'clouds' },
  ]);
  assert.deepEqual(buildSkyTreeItems(['skybox_shell_00', 'clouds'], 'shell'), [
    { value: 'render:sky:0', label: 'Sky shell 0' },
  ]);
});

test('tree selection follows desktop replace, toggle, and range conventions', () => {
  const values = ['a', 'b', 'c', 'd'];
  let state = nextTreeSelection(['a'], 'b', values, 'a', { toggle: false, range: false });
  assert.deepEqual(state, { selected: ['b'], anchor: 'b' });
  state = nextTreeSelection(state.selected, 'd', values, state.anchor, { toggle: true, range: false });
  assert.deepEqual(state, { selected: ['b', 'd'], anchor: 'd' });
  state = nextTreeSelection(state.selected, 'b', values, state.anchor, { toggle: true, range: false });
  assert.deepEqual(state, { selected: ['d'], anchor: 'b' });
  state = nextTreeSelection(state.selected, 'd', values, state.anchor, { toggle: false, range: true });
  assert.deepEqual(state, { selected: ['b', 'c', 'd'], anchor: 'b' });
});

test('viewport selection replaces, adds, toggles, and clears predictably', () => {
  assert.deepEqual(nextViewportSelection(['a'], 'b', { toggle: false, add: false }), ['b']);
  assert.deepEqual(nextViewportSelection(['a'], 'b', { toggle: false, add: true }), ['a', 'b']);
  assert.deepEqual(nextViewportSelection(['a', 'b'], 'a', { toggle: true, add: false }), ['b']);
  assert.deepEqual(nextViewportSelection(['a'], undefined, { toggle: false, add: false }), []);
  assert.deepEqual(nextViewportSelection(['a'], undefined, { toggle: true, add: false }), ['a']);
});

test('spline point tree IDs round-trip without constraining entity IDs', () => {
  const value = splinePointId('entity:with:colons', 42);
  assert.deepEqual(parseSplinePointId(value), { entityId: 'entity:with:colons', index: 42 });
  assert.equal(parseSplinePointId('entity'), undefined);
  assert.deepEqual(removeSplinePoints([
    { x: 0, y: 0, z: 0, w: 0 },
    { x: 1, y: 1, z: 1, w: 1 },
    { x: 2, y: 2, z: 2, w: 2 },
  ], new Set([0, 2])), [{ x: 1, y: 1, z: 1, w: 1 }]);
});

test('collision type formatting keeps game-specific nibble semantics behind target dispatch', () => {
  assert.equal(formatCollisionType(0xa7, 'UYA'), 'Sound 0xA · Type 0x7 · Grind rail · Raw 0xA7');
  assert.equal(formatCollisionType(0xa7, 'GC'), 'Raw 0xA7');
  assert.deepEqual(
    [2, 4, 5, 6, 7, 8, 9, 10, 12, 14, 15].map(formatUyaCollisionTypeId),
    [
      '0x2 · Magnetic', '0x4 · Grind rail', '0x5 · Normal', '0x6 · Normal',
      '0x7 · Grind rail', '0x8 · Slide off', '0x9 · Normal (No ledge grab)',
      '0xA · Magnetic', '0xC · Slide off (No ledge grab)',
      '0xE · Normal (Water trail)', '0xF · Normal',
    ],
  );
  assert.equal(soundTypeId(0xa7, 'UYA'), 0xa);
  assert.equal(collisionTypeId(0xa7, 'UYA'), 0x7);
  assert.equal(packCollisionType(0x7, 0xa, 'UYA'), 0xa7);
  assert.equal(defaultCollisionType('UYA'), 0x0f);
  assert.equal(collisionTypeIdOptions('UYA').length, 16);
});
