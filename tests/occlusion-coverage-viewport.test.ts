import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import type { OcclusionCoverageAuthoring, OcclusionCoverageSource, OcclusionCoverageState } from '../src/types/OcclusionCoverage.js';
import type { ProjectTransform } from '../src/types/EditorRuntime.js';
import { createOcclusionCoverageMarker, createOcclusionCoverageSource, setOcclusionSeedGraph, transformOcclusionCoverageSource } from '../src/utils/OcclusionCoverage.ts';
import { OcclusionOctantOverlay } from '../src/renderer/editor/OcclusionOctantOverlay.ts';
import { OcclusionCoverageOverlay } from '../src/renderer/editor/OcclusionCoverageOverlay.ts';
import { TransformTool } from '../src/renderer/editor/TransformTool.ts';
import { TransformTargetSession } from '../src/renderer/editor/TransformTargetSession.ts';
import { cameraPlacementPosition } from '../src/utils/Scene.ts';
import { projectPositionToLocal } from '../src/utils/Transforms.ts';
import { createGraphVolume } from './fixtures/OcclusionCoverage.ts';

function authoring(sources: OcclusionCoverageSource[]): OcclusionCoverageAuthoring {
  return { snapshot: { projectFingerprint: 'a'.repeat(64), recipeFingerprint: 'b'.repeat(64), visibilityFingerprint: 'c'.repeat(64),
    state: { schemaVersion: 1, primaryEnabled: false, sources } }, available: true, loading: false, updating: false,
    selectedId: sources[0]?.id, select() {}, showVolumes: true, setShowVolumes() {}, hiddenSources: new Set(),
    setSourceVisible() {}, focus() {}, focusHandled() {}, commit: async () => true,
    placementPosition: () => undefined, setPlacementProvider() {}, previewAvailable: true, previewing: false, previewStale: false, startPreview: async () => true,
    cancelPreview() {}, showPreview: false, setShowPreview() {}, setGraphInspection() {} };
}

function transform(source: OcclusionCoverageSource, scale = { x: 1, y: 1, z: 1 }): ProjectTransform {
  return { ...structuredClone(source.transform), scale };
}

test('new coverage placement follows the camera direction and seeds convert into rotated graph coordinates', () => {
  const camera = new THREE.PerspectiveCamera(); camera.position.set(100, 40, -200); camera.lookAt(100, 40, -300);
  assert.deepEqual(cameraPlacementPosition(camera), { x: 100, y: 216, z: 40 });
  assert.deepEqual(cameraPlacementPosition(camera, 48), { x: 100, y: 248, z: 40 });
  assert.equal(cameraPlacementPosition(camera, Infinity), undefined);
  assert.equal(cameraPlacementPosition(camera, -1), undefined);
  const graph = createGraphVolume('Rotated', { x: 10, y: 20, z: 30 });
  graph.transform.rotation = { x: 0, y: 0, z: Math.SQRT1_2, w: Math.SQRT1_2 };
  const local = projectPositionToLocal(graph.transform, { x: 6, y: 23, z: 35 });
  for (const [axis, expected] of [['x', 3], ['y', 4], ['z', 5]] as const) assert.ok(Math.abs(local[axis] - expected) < 1e-6);
});

test('coverage helpers map native positions/dimensions/rotations to scene space and stay independent of contribution', () => {
  const box = createOcclusionCoverageSource('box', 'Room', { x: 10, y: 20, z: 30 });
  box.boxSize = { x: 8, y: 12, z: 16 }; box.enabled = false;
  const overlay = new OcclusionCoverageOverlay(), state = authoring([box]);
  overlay.sync(state);
  const bounds = overlay.getBounds(box.id)!;
  assert.ok(bounds.min.distanceTo(new THREE.Vector3(6, 22, -26)) < 1e-8);
  assert.ok(bounds.max.distanceTo(new THREE.Vector3(14, 38, -14)) < 1e-8);
  const ray = new THREE.Raycaster(new THREE.Vector3(10, 30, 100), new THREE.Vector3(0, 0, -1));
  assert.equal(overlay.pick(ray), box.id, 'disabled contribution remains editable/visible');
  state.hiddenSources = new Set([box.id]); overlay.sync(state);
  assert.equal(overlay.pick(ray), undefined);
  assert.equal(overlay.target(), undefined);
  assert.equal(state.snapshot!.state.sources[0].enabled, false, 'hiding does not enable contribution');
  state.hiddenSources = new Set(); state.showVolumes = false; overlay.sync(state);
  assert.equal(overlay.pick(ray), undefined);
  state.showVolumes = true;
  const q = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 0, 1), Math.PI / 2);
  box.transform.rotation = { x: q.x, y: q.y, z: q.z, w: q.w }; overlay.sync(state);
  assert.ok(overlay.getBounds(box.id)!.getSize(new THREE.Vector3()).distanceTo(new THREE.Vector3(12, 16, 8)) < 1e-6);
  overlay.dispose(); assert.equal(overlay.root.children.length, 0);
});

test('cylinder scale handles preserve circular radius, explicit dimensions and positive bounded shapes', () => {
  const cylinder = createOcclusionCoverageSource('cylinder', 'Tower');
  const radialX = transformOcclusionCoverageSource(cylinder, transform(cylinder, { x: 2, y: 1, z: 1 }), 'X')!;
  const radialZ = transformOcclusionCoverageSource(cylinder, transform(cylinder, { x: 1, y: 3, z: 1 }), 'Z')!;
  const height = transformOcclusionCoverageSource(cylinder, transform(cylinder, { x: 1, y: 1, z: 4 }), 'Y')!;
  assert.deepEqual(radialX.cylinder, { radius: 8, height: 8 });
  assert.deepEqual(radialZ.cylinder, { radius: 12, height: 8 });
  assert.deepEqual(height.cylinder, { radius: 4, height: 32 });
  assert.deepEqual(radialX.transform.scale, { x: 1, y: 1, z: 1 });
  assert.deepEqual(cylinder.cylinder, { radius: 4, height: 8 }, 'the starting shape remains unchanged');
  assert.equal(transformOcclusionCoverageSource(cylinder, transform(cylinder, { x: -1, y: 1, z: 1 }), 'X'), undefined);
  assert.equal(transformOcclusionCoverageSource(cylinder, transform(cylinder, { x: 1, y: 1, z: 0 }), 'Y'), undefined);
  assert.equal(transformOcclusionCoverageSource(cylinder, transform(cylinder, { x: 1e9, y: 1, z: 1 }), 'X'), undefined);
  const box = createOcclusionCoverageSource('box', 'Box');
  assert.deepEqual(transformOcclusionCoverageSource(box, transform(box, { x: 2, y: 3, z: 4 }))!.boxSize, { x: 16, y: 24, z: 32 });
});

test('the shared target session previews locally, commits once, cancels and discards stale or invalid drags', () => {
  const source = createOcclusionCoverageSource('box', 'Room');
  const changes: ProjectTransform[] = []; let resets = 0;
  const session = new TransformTargetSession();
  const target = { key: 'v1', transform: source.transform, vertices: () => [],
    preview: (value?: ProjectTransform) => { if (!value) resets++; return !value || value.position.x < 100; },
    commit: (value: ProjectTransform) => { changes.push(value); } };
  session.sync(target); session.begin(); session.proxy.position.x += 5;
  session.update('X'); assert.equal(changes.length, 0);
  session.finish(); session.finish(); assert.equal(changes.length, 1);
  assert.equal(changes[0].position.x, source.transform.position.x + 5);
  session.sync(target); session.begin(); session.proxy.position.x += 20; session.update('X');
  assert.equal(session.cancel(), true); session.update('X'); session.finish(); assert.equal(changes.length, 1);
  session.sync(target); session.begin(); session.proxy.position.x += 30; session.update('X');
  session.sync({ ...target, key: 'v2' }); session.finish(); assert.equal(changes.length, 1);
  session.begin(); session.proxy.position.x = 1000; session.update('X'); session.finish();
  assert.equal(changes.length, 1, 'an invalid candidate cannot commit');
  assert.ok(resets >= 3);
});

test('coverage gizmos rotate/resize through the real coordinate conversion and commit the captured recipe', async () => {
  const source = createOcclusionCoverageSource('cylinder', 'Tower', { x: 10, y: 20, z: 30 });
  source.transform.rotation = { x: 0, y: 0, z: Math.SQRT1_2, w: Math.SQRT1_2 };
  const state = authoring([source]), overlay = new OcclusionCoverageOverlay();
  let committed: OcclusionCoverageState | undefined, fingerprint: string | undefined;
  state.commit = async (value, expected) => { committed = value; fingerprint = expected; return true; };
  overlay.sync(state); const session = new TransformTargetSession(); session.sync(overlay.target()); session.begin();
  session.proxy.scale.z = 2; session.update('Z');
  assert.equal(committed, undefined);
  assert.deepEqual(source.cylinder, { radius: 4, height: 8 });
  assert.ok(overlay.getBounds(source.id)!.getSize(new THREE.Vector3()).distanceTo(new THREE.Vector3(16, 8, 16)) < 1e-5);
  session.finish(); await Promise.resolve();
  assert.equal(fingerprint, state.snapshot!.projectFingerprint);
  assert.ok(Math.abs(committed!.sources[0].cylinder!.radius - 8) < 1e-6);
  assert.equal(committed!.sources[0].transform.scale.x, 1);
  const oldTarget = overlay.target()!;
  state.snapshot = { ...state.snapshot!, projectFingerprint: 'c'.repeat(64), state: { ...state.snapshot!.state,
    sources: [{ ...source, transform: { ...source.transform, position: { x: 100, y: 200, z: 300 } } }] } };
  overlay.sync(state);
  assert.equal(oldTarget.preview(transform(source)), false, 'an old drag cannot overwrite a refreshed helper');
  assert.ok(overlay.getBounds(source.id)!.getCenter(new THREE.Vector3()).distanceTo(new THREE.Vector3(100, 300, -200)) < 1e-8);
  overlay.dispose();
});

test('moving a cylinder after attaching its seed graph preserves filtering, settings and seeds', async () => {
  const cylinder = createOcclusionCoverageSource('cylinder', 'Tower');
  const state = authoring([cylinder]), overlay = new OcclusionCoverageOverlay();
  overlay.sync(state);
  const graph = setOcclusionSeedGraph(cylinder, true);
  graph.graph!.markers.push(createOcclusionCoverageMarker('Seed 1', { x: 1, y: 0, z: 0 }));
  state.snapshot = { ...state.snapshot!, projectFingerprint: 'd'.repeat(64), state: { ...state.snapshot!.state, sources: [graph] } };
  let committed: OcclusionCoverageState | undefined;
  state.commit = async (value, fingerprint) => {
    assert.equal(fingerprint, state.snapshot!.projectFingerprint);
    committed = value;
    return true;
  };
  overlay.sync(state);
  const session = new TransformTargetSession(); session.sync(overlay.target()); session.begin();
  session.proxy.position.x += 8; session.update('X'); session.finish();
  await Promise.resolve();
  assert.equal(committed!.sources[0].transform.position.x, cylinder.transform.position.x + 8);
  assert.deepEqual(committed!.sources[0].graph, graph.graph);
  assert.equal(committed!.sources[0].graph!.disabled, false);
  assert.equal(cylinder.graph, undefined, 'the original pre-attachment object stays unchanged');
  overlay.dispose();
});

test('preview cells use bounded instancing, correct axes and no pick targets; resources dispose on replacement', () => {
  const source = createOcclusionCoverageSource('box', 'Room'), state = authoring([source]);
  state.showPreview = true;
  state.preview = { projectFingerprint: 'a'.repeat(64), recipeFingerprint: 'b'.repeat(64), algorithmVersion: 2,
    cellSize: 4, primaryFingerprint: 'c'.repeat(64), coordinateFingerprint: 'd'.repeat(64), primaryCells: 0,
    candidateTests: 2, sources: [], cells: [{ x: 1, y: 2, z: 3 }, { x: 3, y: 4, z: 5 }] };
  const overlay = new OcclusionCoverageOverlay(); overlay.sync(state);
  const mesh = overlay.octants.children.flatMap(child => child.children).find(child => child instanceof THREE.InstancedMesh) as THREE.InstancedMesh;
  assert.equal(mesh.count, 2); const matrix = new THREE.Matrix4(); mesh.getMatrixAt(0, matrix);
  assert.deepEqual(new THREE.Vector3().setFromMatrixPosition(matrix).toArray(), [6, 14, -10]);
  let geometryDisposed = 0, materialDisposed = 0, instanceDisposed = 0;
  mesh.geometry.addEventListener('dispose', () => geometryDisposed++);
  (mesh.material as THREE.Material).addEventListener('dispose', () => materialDisposed++);
  mesh.addEventListener('dispose', () => instanceDisposed++);
  const hits: THREE.Intersection[] = []; mesh.raycast(new THREE.Raycaster(), hits); assert.equal(hits.length, 0);
  state.preview = undefined; overlay.sync(state);
  assert.deepEqual([geometryDisposed, materialDisposed, instanceDisposed], [1, 1, 1]); overlay.dispose();
});

class CanvasStub {
  style = { touchAction: '' };
  ownerDocument = { pointerLockElement: null };
  listeners = new Map<string, { listener: EventListener; capture: boolean }[]>();
  addEventListener(type: string, listener: EventListener, options?: boolean) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), { listener, capture: options === true }]);
  }
  removeEventListener(type: string, listener: EventListener) {
    this.listeners.set(type, (this.listeners.get(type) ?? []).filter(item => item.listener !== listener));
  }
  dispatch(type: string) {
    const event = Object.assign(new Event(type), { pointerId: 1, button: 0, clientX: 50, clientY: 50 });
    const listeners = [...(this.listeners.get(type) ?? [])];
    for (const capture of [true, false]) for (const item of listeners) if (item.capture === capture) item.listener(event);
  }
  getBoundingClientRect() { return { left: 0, top: 0, width: 100, height: 100 }; }
  releasePointerCapture() { this.dispatch('lostpointercapture'); }
}

test('the real transform tool commits normal releases once, cancels capture loss/Escape, and preserves grid settings', async () => {
  const canvas = new CanvasStub(), scene = new THREE.Scene(), camera = new THREE.PerspectiveCamera();
  let commits = 0, entityCommits = 0, resets = 0;
  const source = createOcclusionCoverageSource('box', 'Room');
  const tool = new TransformTool(camera, canvas as unknown as HTMLElement, scene, {
    previewTransforms() {}, previewSpline() {}, availability() {}, commit() { entityCommits++; },
    commitSplinePoints() {}, collectSnapVertices: () => [], resolveSnapTarget: () => undefined,
  });
  tool.setMode('translate');
  tool.setSnapping(true, 4, 15, .25, 'center', 'grid');
  tool.setExternalTarget({ key: 'source:v1', transform: source.transform, vertices: () => [],
    preview: value => { if (!value) resets++; return true; }, commit: () => { commits++; } });
  assert.equal(tool.controls.translationSnap, 4);
  assert.equal(tool.controls.scaleSnap, .25);
  const begin = () => {
    tool.controls.dragging = true; tool.controls.axis = 'X';
    tool.controls.dispatchEvent({ type: 'mouseDown', mode: 'translate' });
    tool.controls.object!.position.x += 5;
    tool.controls.dispatchEvent({ type: 'objectChange' });
  };
  begin(); canvas.dispatch('pointerup'); await Promise.resolve();
  assert.equal(commits, 1, 'normal pointer release must not be mistaken for lost capture cancellation');
  assert.equal(tool.isInteracting, false);
  begin(); canvas.dispatch('lostpointercapture'); canvas.dispatch('pointerup'); await Promise.resolve();
  assert.equal(commits, 1); assert.equal(tool.isInteracting, false);
  begin(); assert.equal(tool.cancel(), true); canvas.dispatch('pointerup'); await Promise.resolve();
  assert.equal(commits, 1);
  begin(); canvas.dispatch('pointercancel'); canvas.dispatch('pointerup'); await Promise.resolve();
  assert.equal(commits, 1);
  begin(); tool.setEnabled(false); canvas.dispatch('pointerup'); await Promise.resolve();
  assert.equal(commits, 1, 'an intervening operation cancels its local drag');
  assert.equal(entityCommits, 0, 'coverage never dispatches gameplay transform commands');
  assert.ok(resets >= 4);
  tool.dispose(); assert.equal(scene.children.length, 0);
  assert.ok([...canvas.listeners.values()].every(list => list.length === 0));
});

test('graph seeds transform in their owning graph, preview without mutation and reject leaving the region', async () => {
  const graph = createGraphVolume('Interior', { x: 20, y: 30, z: 40 });
  graph.transform.rotation = { x: 0, y: 0, z: Math.SQRT1_2, w: Math.SQRT1_2 };
  const marker = { id: crypto.randomUUID(), name: 'Entrance', position: { x: 2, y: 3, z: 4 } };
  graph.graph!.markers.push(marker);
  const other = createGraphVolume('Other');
  other.graph!.markers.push({ id: crypto.randomUUID(), name: 'Other seed', position: { x: 0, y: 0, z: 0 } });
  const state = authoring([graph, other]); state.selectedMarkerId = marker.id;
  const overlay = new OcclusionCoverageOverlay(); let committed: OcclusionCoverageState | undefined, commits = 0;
  state.commit = async value => { committed = value; commits++; return true; };
  overlay.sync(state);
  const target = overlay.target()!;
  assert.equal(target.translateOnly, true);
  const position = target.transform.position;
  assert.ok(new THREE.Vector3(position.x, position.y, position.z).distanceTo(new THREE.Vector3(17, 32, 44)) < 1e-6);
  assert.ok(overlay.getBounds(graph.id, undefined, marker.id)!.getCenter(new THREE.Vector3()).distanceTo(new THREE.Vector3(17, 44, -32)) < 1e-6);
  const session = new TransformTargetSession(); session.sync(target); session.begin();
  session.proxy.position.x += 5; session.update('X');
  assert.equal(commits, 0); assert.deepEqual(marker.position, { x: 2, y: 3, z: 4 });
  assert.ok(overlay.getBounds(graph.id, undefined, marker.id)!.getCenter(new THREE.Vector3()).distanceTo(new THREE.Vector3(22, 44, -32)) < 1e-5);
  session.finish(); await Promise.resolve(); assert.equal(commits, 1);
  const updated = committed!.sources[0].graph!.markers[0].position;
  assert.ok(Math.abs(updated.x - 2) < 1e-6 && Math.abs(updated.y + 2) < 1e-6 && Math.abs(updated.z - 4) < 1e-6);
  assert.deepEqual(committed!.sources[1], other, 'moving a seed cannot edit another graph');
  assert.deepEqual(committed!.sources[0].transform, graph.transform, 'moving a seed cannot move its graph');
  session.sync(target); session.begin(); session.proxy.position.x += 1000; session.update('X'); session.finish();
  assert.equal(commits, 1, 'an out-of-region drag cannot commit');
  session.sync(target); session.begin(); session.proxy.position.z += 2; session.update('Z'); session.cancel(); session.finish();
  assert.equal(commits, 1);
  const resized = transformOcclusionCoverageSource(graph, transform(graph, { x: .1, y: 1, z: 1 }));
  assert.equal(resized, undefined, 'shrinking the region past a seed is rejected rather than clamped');
  const moved = transform(graph); moved.position.x += 10;
  assert.deepEqual(transformOcclusionCoverageSource(graph, moved)!.graph!.markers, graph.graph!.markers);
  state.selectedMarkerId = other.graph!.markers[0].id; overlay.sync(state);
  assert.equal(overlay.target(), undefined, 'marker selection requires explicit ownership');
  overlay.dispose();
});

test('graph seed picking keeps an eight-pixel target at different depths and excludes hidden or clipped seeds', () => {
  const graph = createGraphVolume('Graph', { x: 0, y: 0, z: 0 });
  const marker = { id: crypto.randomUUID(), name: 'Center', position: { x: 0, y: 0, z: 0 } };
  graph.graph!.markers.push(marker);
  const state = authoring([graph]), overlay = new OcclusionCoverageOverlay(); overlay.sync(state);
  const camera = new THREE.PerspectiveCamera(60, 1, .1, 10000);
  for (const depth of [10, 1000]) {
    camera.position.set(0, 0, depth); camera.lookAt(0, 0, 0); camera.updateMatrixWorld(true);
    assert.deepEqual(overlay.pickMarker(new THREE.Vector2(7 * 2 / 800, 0), camera, 800, 800), { sourceId: graph.id, markerId: marker.id });
    assert.equal(overlay.pickMarker(new THREE.Vector2(9 * 2 / 800, 0), camera, 800, 800), undefined);
  }
  state.hiddenSources = new Set([graph.id]); overlay.sync(state);
  assert.equal(overlay.pickMarker(new THREE.Vector2(), camera, 800, 800), undefined);
  state.hiddenSources = new Set(); overlay.sync(state);
  camera.position.set(0, 0, -10); camera.lookAt(0, 0, -20); camera.updateMatrixWorld(true);
  assert.equal(overlay.pickMarker(new THREE.Vector2(), camera, 800, 800), undefined);
  overlay.dispose();
});

test('marker-only targets detach Rotate/Scale and explain how to transform the graph', () => {
  const canvas = new CanvasStub(), scene = new THREE.Scene(); let notice: string | undefined;
  const tool = new TransformTool(new THREE.PerspectiveCamera(), canvas as unknown as HTMLElement, scene, {
    previewTransforms() {}, previewSpline() {}, availability: value => { notice = value; }, commit() {},
    commitSplinePoints() {}, collectSnapVertices: () => [], resolveSnapTarget: () => undefined,
  });
  tool.setExternalTarget({ key: 'seed', translateOnly: true, transform: createGraphVolume('Graph').transform,
    preview: () => true, commit() {}, vertices: () => [] });
  for (const mode of ['rotate', 'scale'] as const) {
    tool.setMode(mode); assert.equal(tool.controls.object, undefined); assert.match(notice!, /Move tool/);
  }
  tool.setMode('translate'); assert.ok(tool.controls.object); assert.equal(notice, undefined);
  tool.dispose();
});

test('graph inspection displays only the selected graph before composition, replaces buffers and marks stale data', () => {
  const graph = createGraphVolume('Rooms'), other = createGraphVolume('Other');
  const state = authoring([graph, other]); state.showPreview = true;
  const accepted = [{ x: 1, y: 2, z: 3 }], rejected = [{ x: 4, y: 5, z: 6 }];
  state.preview = { projectFingerprint: 'a'.repeat(64), recipeFingerprint: 'b'.repeat(64), algorithmVersion: 3, cellSize: 4,
    primaryFingerprint: 'c'.repeat(64), coordinateFingerprint: 'd'.repeat(64), primaryCells: 0, candidateTests: 4,
    sources: [{ id: graph.id, cells: 1 }], cells: [...accepted, ...rejected],
    graphPreview: { sceneFingerprint: 'e'.repeat(64), rays: 18, queryTests: 22, sources: [{ id: graph.id, accepted, rejected }] } };
  const original = JSON.stringify(state.snapshot), overlay = new OcclusionCoverageOverlay();
  const mesh = () => overlay.octants.children.flatMap(c => c.children).find(c => c instanceof THREE.InstancedMesh) as THREE.InstancedMesh | undefined;
  overlay.sync(state); const effective = mesh()!; assert.equal(effective.count, 2);
  let disposed = 0; effective.addEventListener('dispose', () => disposed++);
  state.graphInspection = 'accepted'; overlay.sync(state);
  assert.equal(disposed, 1); assert.equal(mesh()!.count, 1);
  const matrix = new THREE.Matrix4(); mesh()!.getMatrixAt(0, matrix);
  assert.deepEqual(new THREE.Vector3().setFromMatrixPosition(matrix).toArray(), [6, 14, -10]);
  state.graphInspection = 'rejected'; overlay.sync(state); mesh()!.getMatrixAt(0, matrix);
  assert.deepEqual(new THREE.Vector3().setFromMatrixPosition(matrix).toArray(), [18, 26, -22]);
  const current = mesh()!; state.previewStale = true; overlay.sync(state);
  assert.equal(mesh(), current); assert.equal((current.material as THREE.MeshBasicMaterial).color.getHexString(), 'ffb34d');
  state.selectedId = other.id; overlay.sync(state); assert.equal(mesh(), undefined, 'inspection cannot silently show another graph');
  state.graphInspection = undefined; overlay.sync(state); assert.equal(mesh()!.count, 2);
  assert.equal(JSON.stringify(state.snapshot), original); overlay.dispose();
});

test('Primary and generated octants share the original fill, edges, depth and configured color', () => {
  const state = authoring([]), cells = [{ x: 1, y: 2, z: 3 }, { x: 3, y: 4, z: 5 }];
  state.showPreview = true;
  state.preview = { projectFingerprint: 'a'.repeat(64), recipeFingerprint: 'b'.repeat(64), algorithmVersion: 2,
    cellSize: 4, primaryFingerprint: 'c'.repeat(64), coordinateFingerprint: 'd'.repeat(64), primaryCells: 0,
    candidateTests: 2, sources: [], cells };
  const primary = new OcclusionOctantOverlay(cells, '#22ff00'), coverage = new OcclusionCoverageOverlay();
  coverage.sync(state, '#22ff00');
  const preview = coverage.octants.children[0];
  assert.ok(preview instanceof OcclusionOctantOverlay);
  assert.equal(coverage.root.children.includes(coverage.octants), false, 'octants belong in the main scene, separate from helpers drawn through geometry');
  const [fill, lines] = preview.children as [THREE.InstancedMesh<THREE.BoxGeometry, THREE.MeshBasicMaterial>, THREE.LineSegments<THREE.EdgesGeometry, THREE.LineBasicMaterial>];
  const [originalFill, originalLines] = primary.children as typeof preview.children;
  assert.equal(fill.material.wireframe, false);
  assert.equal(fill.material.opacity, .08); assert.equal(lines.material.opacity, .55);
  assert.equal(fill.material.depthTest, true); assert.equal(lines.material.depthTest, true);
  assert.equal(fill.material.depthWrite, false);
  assert.equal(fill.material.toneMapped, true); assert.equal(lines.material.toneMapped, true);
  assert.equal(fill.material.color.getHexString(), '22ff00'); assert.equal(lines.material.color.getHexString(), '22ff00');
  assert.deepEqual(fill.instanceMatrix.array, (originalFill as THREE.InstancedMesh).instanceMatrix.array);
  assert.deepEqual(lines.geometry.getAttribute('position').array, (originalLines as THREE.LineSegments).geometry.getAttribute('position').array);
  const positions = lines.geometry.getAttribute('position');
  assert.equal(positions.count, 24 * cells.length, 'each cube has exactly twelve edges');
  for (let i = 0; i < positions.count; i += 2) {
    const delta = new THREE.Vector3().fromBufferAttribute(positions, i).sub(new THREE.Vector3().fromBufferAttribute(positions, i + 1));
    assert.equal(delta.length(), 4);
    assert.equal(delta.toArray().filter(value => value !== 0).length, 1, 'there are no diagonal triangle edges');
  }
  const hits: THREE.Intersection[] = [];
  fill.raycast(new THREE.Raycaster(), hits); lines.raycast(new THREE.Raycaster(), hits); assert.equal(hits.length, 0);
  coverage.sync(state, '#123456');
  assert.equal(coverage.octants.children[0], preview, 'color changes reuse geometry');
  assert.equal(fill.material.color.getHexString(), '123456'); assert.equal(lines.material.color.getHexString(), '123456');
  let edgeGeometryDisposed = 0, edgeMaterialDisposed = 0;
  lines.geometry.addEventListener('dispose', () => edgeGeometryDisposed++);
  lines.material.addEventListener('dispose', () => edgeMaterialDisposed++);
  coverage.dispose(); primary.dispose();
  assert.equal(edgeGeometryDisposed, 1); assert.equal(edgeMaterialDisposed, 1);
  assert.equal(coverage.octants.children.length, 0);
});
