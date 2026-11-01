import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { createGraphVolume } from './fixtures/OcclusionCoverage.ts';

import { isOcclusionCoverageState } from '../dist-electron/utils/OcclusionCoverage.js';
import { decodeOcclusionCoverageSnapshot, encodeOcclusionCoverageUpdate } from '../dist-electron/main/bridge/OcclusionCoveragePayloadCodec.js';

const fixture = JSON.parse(readFileSync(new URL('./fixtures/occlusion-coverage-v1.json', import.meta.url), 'utf8'));

test('coverage payloads match shared C# vectors for boxes, cylinders, graphs and native-space transforms', () => {
  assert.equal(encodeOcclusionCoverageUpdate(fixture.projectFingerprint, fixture.state).toString('hex'), fixture.requestHex);
  assert.deepEqual(decodeOcclusionCoverageSnapshot(Buffer.from(fixture.snapshotHex, 'hex')), {
    projectFingerprint: fixture.projectFingerprint, recipeFingerprint: fixture.recipeFingerprint,
    visibilityFingerprint: fixture.visibilityFingerprint, state: fixture.state,
  });
});

test('coverage boundary rejects invalid schema, shape combinations, dimensions, IDs, ownership and non-data fields', () => {
  assert.equal(isOcclusionCoverageState(fixture.state), true);
  for (const mutate of [
    (s) => { s.schemaVersion = 2; },
    (s) => { s.sources[0].id = '00000000-0000-0000-0000-000000000000'; },
    (s) => { s.sources[0].id = s.sources[2].graph.markers[0].id; },
    (s) => { s.sources[0].cylinder = { radius: 1, height: 2 }; },
    (s) => { s.sources[0].boxSize.x = 0; },
    (s) => { s.sources[0].boxSize.x = 1e-100; },
    (s) => { s.sources[0].transform.position.z = Infinity; },
    (s) => { s.sources[0].transform.rotation.w = 2; },
    (s) => { s.sources[0].transform.scale.x = -1; },
    (s) => { s.sources[1].cylinder.radius = NaN; },
    (s) => { s.sources[2].role = 'exclude'; },
    (s) => { s.sources[2].graph.sticky = 'true'; },
    (s) => { s.sources[2].graph.stickyDistance = -1; },
    (s) => { s.sources[2].graph.stickyDistance = NaN; },
    (s) => { s.sources[2].graph.stickyDistance = Infinity; },
    (s) => { s.sources[2].graph.stickyDistance = 1000001; },
    (s) => { s.sources[2].graph.markers[0].position.x = 100; },
    (s) => { s.sources[2].graph.markers = Array(4097).fill(s.sources[2].graph.markers[0]); },
    (s) => { s.sources = Array(1025).fill(s.sources[0]); },
    (s) => { Object.assign(s.sources[0], { visible: true }); },
    (s) => { Object.assign(s.sources[0], { role: { toString: () => 'include' } }); },
  ]) {
    const state = structuredClone(fixture.state); mutate(state);
    assert.equal(isOcclusionCoverageState(state), false);
    assert.throws(() => encodeOcclusionCoverageUpdate(fixture.projectFingerprint, state));
  }
  assert.throws(() => encodeOcclusionCoverageUpdate('stale', fixture.state));
  assert.throws(() => encodeOcclusionCoverageUpdate('A'.repeat(64), fixture.state));
  assert.equal(isOcclusionCoverageState(null), false);
});

test('Sticky graph metadata matches C# bytes and retains old graph payloads', () => {
  const sticky = JSON.parse(readFileSync(new URL('./fixtures/occlusion-coverage-sticky-v1.json', import.meta.url), 'utf8'));
  assert.equal(encodeOcclusionCoverageUpdate(sticky.projectFingerprint, sticky.state).toString('hex'), sticky.requestHex);
  assert.deepEqual(decodeOcclusionCoverageSnapshot(Buffer.from(sticky.snapshotHex, 'hex')).state, sticky.state);
  const zero = structuredClone(sticky.state); zero.sources[2].graph.stickyDistance = 0;
  assert.equal(isOcclusionCoverageState(zero), true);
  assert.deepEqual(decodeOcclusionCoverageSnapshot(Buffer.from(fixture.snapshotHex, 'hex')).state, fixture.state);
});

test('box and cylinder seed graphs round-trip with inactive graphs and strict shape ownership', () => {
  const value = JSON.parse(readFileSync(new URL('./fixtures/occlusion-coverage-volume-graphs-v1.json', import.meta.url), 'utf8'));
  assert.equal(encodeOcclusionCoverageUpdate(value.projectFingerprint, value.state).toString('hex'), value.requestHex);
  assert.deepEqual(decodeOcclusionCoverageSnapshot(Buffer.from(value.snapshotHex, 'hex')).state, value.state);
  for (const change of [
    s => { s.sources[0].graph.size = s.sources[0].boxSize; },
    s => { delete s.sources[0].boxSize; },
    s => { s.sources[1].graph.markers[0].position = { x: 7, y: 7, z: 0 }; },
    s => { s.sources[1].graph.disabled = 'false'; },
    s => { s.sources[2].graph.disabled = false; },
  ]) {
    const state = structuredClone(value.state); change(state);
    assert.equal(isOcclusionCoverageState(state), false);
    assert.throws(() => encodeOcclusionCoverageUpdate(value.projectFingerprint, state));
  }
});

test('graph attachment preserves shape, seed identities and defaults; cylinder placement and resizing honor its radius', async () => {
  const { createOcclusionCoverageSource, createOcclusionCoverageMarker, setOcclusionSeedGraph, setOcclusionVolumeShape,
    clampOcclusionSeed, occlusionVolumeContains, transformOcclusionCoverageSource } = await import('../dist-electron/utils/OcclusionCoverage.js');
  const cylinder = setOcclusionSeedGraph(createOcclusionCoverageSource('cylinder', 'Tower'), true);
  assert.deepEqual(cylinder.cylinder, { radius: 4, height: 8 });
  assert.equal(cylinder.graph.size, undefined); assert.equal(cylinder.graph.sticky, true); assert.equal(cylinder.graph.stickyDistance, 8);
  const position = clampOcclusionSeed(cylinder, { x: 20, y: 20, z: 20 });
  assert.equal(occlusionVolumeContains(cylinder, position), true); assert.equal(position.z, 4);
  cylinder.graph.markers = [createOcclusionCoverageMarker('Seed 1', position)];
  const off = setOcclusionSeedGraph(cylinder, false), on = setOcclusionSeedGraph(off, true);
  assert.equal(off.graph.disabled, true); assert.deepEqual(on.graph.markers, cylinder.graph.markers);
  assert.equal(isOcclusionCoverageState({ schemaVersion: 1, primaryEnabled: false, sources: [off] }), true);
  assert.equal(transformOcclusionCoverageSource(cylinder, { ...cylinder.transform, scale: { x: .5, y: .5, z: 1 } }), undefined);
  const legacy = structuredClone(fixture.state.sources[2]);
  legacy.graph.markers = [createOcclusionCoverageMarker('Seed 1', { x: 15, y: 15, z: 0 })];
  const converted = setOcclusionVolumeShape(legacy, 'cylinder');
  assert.equal(converted.id, legacy.id); assert.deepEqual(converted.graph.markers, legacy.graph.markers);
  assert.equal(converted.graph.size, undefined); assert.equal(occlusionVolumeContains(converted, legacy.graph.markers[0].position), true);
});

test('coverage decoding bounds counts before allocation and rejects truncation, trailing bytes and invalid roles', () => {
  const valid = Buffer.from(fixture.snapshotHex, 'hex');
  for (let length = 0; length < valid.length; length++) assert.throws(() => decodeOcclusionCoverageSnapshot(valid.subarray(0, length)));
  assert.throws(() => decodeOcclusionCoverageSnapshot(Buffer.concat([valid, Buffer.of(0)])));
  const excessive = Buffer.from(valid); excessive.writeUInt32LE(0xffffffff, 141);
  assert.throws(() => decodeOcclusionCoverageSnapshot(excessive));
  const invalidBoolean = Buffer.from(valid); invalidBoolean[140] = 2;
  assert.throws(() => decodeOcclusionCoverageSnapshot(invalidBoolean));
  const role = Buffer.from(valid); role.writeUInt32LE(9, 193);
  assert.throws(() => decodeOcclusionCoverageSnapshot(role));
});

test('new and duplicated coverage sources preserve shape ownership without aliasing', async () => {
  const { createOcclusionCoverageSource, duplicateOcclusionCoverageSource } = await import('../dist-electron/utils/OcclusionCoverage.js');
  const position = { x: 16, y: 24, z: 12 };
  const box = createOcclusionCoverageSource('box', 'Room', position);
  const cylinder = createOcclusionCoverageSource('cylinder', 'Tower', position);
  assert.equal(isOcclusionCoverageState({ schemaVersion: 1, primaryEnabled: false, sources: [box, cylinder] }), true);
  assert.notEqual(box.id, cylinder.id);
  position.x = 100;
  assert.equal(box.transform.position.x, 16);
  const graph = structuredClone(fixture.state.sources[2]);
  const copy = duplicateOcclusionCoverageSource(graph);
  assert.notEqual(copy.id, graph.id);
  assert.notEqual(copy.graph.markers[0].id, graph.graph.markers[0].id);
  assert.equal(isOcclusionCoverageState({ schemaVersion: 1, primaryEnabled: true, sources: [graph, copy] }), true);
  copy.graph.markers[0].position.x = 99;
  assert.notEqual(graph.graph.markers[0].position.x, 99);
});

test('coverage preview codec matches shared C# vectors and rejects truncated, excessive or unordered results', async () => {
  const { decodeOcclusionCoveragePreview, encodeOcclusionCoveragePreviewRequest } = await import('../dist-electron/main/bridge/OcclusionCoveragePreviewPayloadCodec.js');
  const vector = JSON.parse(readFileSync(new URL('./fixtures/occlusion-coverage-preview-v1.json', import.meta.url), 'utf8'));
  const bytes = Buffer.from(vector.resultHex, 'hex');
  assert.deepEqual(decodeOcclusionCoveragePreview(bytes), vector.preview);
  assert.equal(encodeOcclusionCoveragePreviewRequest(vector.preview.projectFingerprint, '').toString('hex'), vector.requestHex);
  for (let size = 0; size < bytes.length; size++) assert.throws(() => decodeOcclusionCoveragePreview(bytes.subarray(0, size)));
  assert.throws(() => decodeOcclusionCoveragePreview(Buffer.concat([bytes, Buffer.of(0)])));
  for (const [field, value] of [['algorithmVersion', 0], ['sourceCount', 1025], ['primaryCells', 100001],
    ['candidateTests', 2000001], ['candidateTests', 0], ['cellCount', 100001], ['firstCell', 0xffffffff]]) {
    const bad = Buffer.from(bytes); bad.writeUInt32LE(value, vector.offsets[field]);
    assert.throws(() => decodeOcclusionCoveragePreview(bad), field);
  }
  const badSize = Buffer.from(bytes); badSize.writeFloatLE(NaN, vector.offsets.cellSize);
  assert.throws(() => decodeOcclusionCoveragePreview(badSize));
  const duplicate = Buffer.from(bytes);
  duplicate.copy(duplicate, vector.offsets.firstCell + 12, vector.offsets.firstCell, vector.offsets.firstCell + 12);
  assert.throws(() => decodeOcclusionCoveragePreview(duplicate));
  assert.throws(() => encodeOcclusionCoveragePreviewRequest('a'.repeat(64), 'x'.repeat(4097)));
  assert.throws(() => encodeOcclusionCoveragePreviewRequest('a'.repeat(64), '\0'));
});


test('seed graphs attach to enabled volumes and own independent, bounded seed identities', async () => {
  const { createOcclusionCoverageMarker, duplicateOcclusionCoverageSource } = await import('../dist-electron/utils/OcclusionCoverage.js');
  const graph = createGraphVolume('Interior');
  assert.equal(graph.enabled, true);
  assert.deepEqual(graph.boxSize, { x: 32, y: 32, z: 32 });
  assert.deepEqual(graph.graph, { markers: [], sticky: true, stickyDistance: 8, disabled: false });
  const position = { x: 1, y: 2, z: 3 };
  const first = createOcclusionCoverageMarker('Entrance', position);
  const second = createOcclusionCoverageMarker('Landing', first.position);
  position.x = 999; second.position.y = 10;
  assert.deepEqual(first.position, { x: 1, y: 2, z: 3 });
  assert.notEqual(first.id, second.id);
  graph.graph.markers.push(first, second);
  const copy = duplicateOcclusionCoverageSource(graph);
  const state = { schemaVersion: 1, primaryEnabled: true, sources: [graph, copy] };
  assert.equal(isOcclusionCoverageState(state), true);
  assert.equal(new Set(state.sources.flatMap(s => s.graph.markers.map(m => m.id))).size, 4);
  graph.graph.markers[0].position.x = 17;
  assert.equal(isOcclusionCoverageState(state), false);
  assert.equal(copy.graph.markers[0].position.x, 1);
});

test('graph preview extension matches C# bytes and bounds identity, ownership, inspection cells and work', async () => {
  const { decodeOcclusionCoveragePreview } = await import('../dist-electron/main/bridge/OcclusionCoveragePreviewPayloadCodec.js');
  const fixture = JSON.parse(readFileSync(new URL('./fixtures/occlusion-coverage-preview-graph-v1.json', import.meta.url), 'utf8'));
  const bytes = Buffer.from(fixture.resultHex, 'hex');
  assert.deepEqual(decodeOcclusionCoveragePreview(bytes), fixture.preview);
  for (const count of [97_519_715, 200_000_000]) {
    const supported = Buffer.from(bytes); supported.writeUInt32LE(count, fixture.offsets.queryTests);
    assert.equal(decodeOcclusionCoveragePreview(supported).graphPreview.queryTests, count);
  }
  for (let length = 0; length < bytes.length; length++) assert.throws(() => decodeOcclusionCoveragePreview(bytes.subarray(0, length)));
  assert.throws(() => decodeOcclusionCoveragePreview(Buffer.concat([bytes, Buffer.of(0)])));
  for (const [field, value] of [['extensionVersion', 2], ['algorithmVersion', 2], ['rays', 0], ['rays', 2000001],
    ['queryTests', 200000001], ['graphCount', 0], ['graphCount', 1025], ['acceptedCount', 100001],
    ['rejectedCount', 99999], ['candidateTests', 2]]) {
    const bad = Buffer.from(bytes); bad.writeUInt32LE(value, fixture.offsets[field]);
    assert.throws(() => decodeOcclusionCoveragePreview(bad), field);
  }
  const id = Buffer.from(bytes); id[fixture.offsets.graphId + 4] = 0x31;
  assert.throws(() => decodeOcclusionCoveragePreview(id));
  const scene = Buffer.from(bytes); scene[fixture.offsets.sceneFingerprint + 4] = 0x41;
  assert.throws(() => decodeOcclusionCoveragePreview(scene));
  const overlap = Buffer.from(bytes); bytes.copy(overlap, fixture.offsets.rejectedCells, fixture.offsets.acceptedCells, fixture.offsets.acceptedCells + 12);
  assert.throws(() => decodeOcclusionCoveragePreview(overlap));
  const unordered = Buffer.from(bytes); bytes.copy(unordered, fixture.offsets.acceptedCells + 12, fixture.offsets.acceptedCells, fixture.offsets.acceptedCells + 12);
  assert.throws(() => decodeOcclusionCoveragePreview(unordered));
});
