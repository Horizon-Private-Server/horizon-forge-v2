import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import type { EditorEntity } from '../src/types/EditorRuntime.js';
import { SceneProjection } from '../src/renderer/editor/SceneProjection.ts';
import { LevelSettingsOverlayProjection } from '../src/renderer/editor/LevelSettingsOverlayProjection.ts';
import { ViewportPicking } from '../src/renderer/editor/ViewportPicking.ts';
import { isSceneBillboardGizmo } from '../src/renderer/editor/SceneBillboardGizmo.ts';
import { disposeObject } from '../src/utils/Scene.ts';

const bounds = { left: 20, top: 30, width: 800, height: 800 };

function entity(id: string, x = 0, y = 0, z = 0): EditorEntity {
  return {
    id, name: id, layer: 'mobys',
    transform: { position: { x, y, z }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 } },
    transformModes: ['translate', 'rotate', 'scale'],
    state: { dirty: false, hidden: false, disabled: false, locked: false, readOnly: false, invalid: false, missingAsset: false },
  };
}

function fixture() {
  const projection = new SceneProjection();
  const overlays = new LevelSettingsOverlayProjection();
  const simulation = new THREE.Group();
  const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 1_000);
  camera.position.set(0, 0, 300);
  camera.lookAt(0, 0, 0);
  const picking = new ViewportPicking(camera, projection, overlays, simulation);
  const raycaster = new THREE.Raycaster();
  raycaster.params.Line.threshold = 8;
  let hovered: string | undefined;
  let hoverUpdates = 0;
  const setHovered = projection.setHoveredEntityId.bind(projection);
  projection.setHoveredEntityId = (id) => { hovered = id; hoverUpdates++; setHovered(id); };
  const update = () => {
    camera.updateMatrixWorld(true);
    projection.updateBillboards(camera, bounds.height);
    overlays.updateForCamera(camera, bounds.height);
    projection.root.updateMatrixWorld(true);
    overlays.root.updateMatrixWorld(true);
    simulation.updateMatrixWorld(true);
  };
  const client = (point: THREE.Vector3) => {
    const ndc = point.clone().project(camera);
    return [bounds.left + (ndc.x + 1) * bounds.width / 2,
      bounds.top + (1 - ndc.y) * bounds.height / 2] as const;
  };
  const reference = (x: number, y: number) => {
    raycaster.setFromCamera(new THREE.Vector2(
      (x - bounds.left) / bounds.width * 2 - 1, -(y - bounds.top) / bounds.height * 2 + 1,
    ), camera);
    const saved = new Map<THREE.Mesh, THREE.Mesh['raycast']>();
    projection.root.traverse((object) => {
      if (!(object instanceof THREE.InstancedMesh) && !(object instanceof THREE.BatchedMesh)) return;
      saved.set(object, object.raycast);
      object.raycast = object instanceof THREE.InstancedMesh
        ? THREE.InstancedMesh.prototype.raycast : THREE.BatchedMesh.prototype.raycast;
    });
    let hits: THREE.Intersection[];
    try { hits = raycaster.intersectObjects([overlays.root, simulation, projection.root], true); }
    finally { saved.forEach((raycast, mesh) => { mesh.raycast = raycast; }); }
    const markerId = overlays.resolvePick(hits);
    const entityId = markerId ? undefined : projection.resolvePick(hits);
    return { markerId, entityId };
  };
  return {
    projection, overlays, simulation, camera, picking, update, client, reference,
    get hovered() { return hovered; }, get hoverUpdates() { return hoverUpdates; },
    dispose() { projection.dispose(); overlays.dispose(); disposeObject(simulation); },
  };
}

test('hover consumes only the latest pointer once per frame and clear/click discard pending work', (t) => {
  const f = fixture();
  t.after(() => f.dispose());
  f.projection.sync([entity('marker')]);
  f.update();
  const center = new THREE.Box3().setFromObject(f.projection.getObject('marker')!).getCenter(new THREE.Vector3());
  const [x, y] = f.client(center);
  for (let i = 0; i < 1_000; i++) f.picking.queueHover(-1_000, -1_000);
  f.picking.queueHover(x, y);
  assert.equal(f.hoverUpdates, 0);
  assert.equal(f.picking.hasPendingHover, true);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, 'marker');
  assert.equal(f.hoverUpdates, 1);
  f.picking.flushHover(bounds);
  assert.equal(f.hoverUpdates, 1);
  f.picking.queueHover(x, y);
  f.picking.clearHover();
  assert.equal(f.picking.hasPendingHover, false);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
  f.picking.queueHover(-1_000, -1_000);
  assert.deepEqual(f.picking.pick(x, y, bounds), { markerId: undefined, entityId: 'marker' });
  assert.equal(f.picking.hasPendingHover, false);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
});

test('hover skips asset triangles on marker misses and retains full occlusion when a marker is hit', (t) => {
  const f = fixture();
  t.after(() => f.dispose());
  const template = new THREE.Mesh(new THREE.BoxGeometry(100, 100, 2), new THREE.MeshBasicMaterial());
  t.after(() => disposeObject(template));
  f.projection.setAssetTemplates(new Map([['asset', template]]));
  const marker = entity('marker');
  const blocker = entity('blocker', 0, -20, 20);
  blocker.asset = { id: 'asset', kind: 'moby' };
  f.projection.sync([marker, blocker]);
  f.update();
  let triangleQueries = 0;
  f.projection.root.traverse((object) => {
    if (!(object instanceof THREE.InstancedMesh)) return;
    const original = object.raycast.bind(object);
    object.raycast = (...args) => { triangleQueries++; original(...args); };
  });
  f.picking.queueHover(-1_000, -1_000);
  f.picking.flushHover(bounds);
  assert.equal(triangleQueries, 0);
  const point = new THREE.Box3().setFromObject(f.projection.getObject('marker')!).getCenter(new THREE.Vector3());
  const [x, y] = f.client(point);
  assert.equal(f.reference(x, y).entityId, 'blocker');
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
  assert.ok(triangleQueries > 0);
  const locked = { ...blocker, state: { ...blocker.state, locked: true } };
  f.projection.sync([marker, locked]);
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, 'marker');
  f.projection.sync([{ ...marker, state: { ...marker.state, hidden: true } }, blocker]);
  triangleQueries = 0;
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
  assert.equal(triangleQueries, 0);
});

test('clicks and visible hover match the reference across mixed instances, batches, geometry and overlays', (t) => {
  const f = fixture();
  t.after(() => f.dispose());
  const template = new THREE.Mesh(new THREE.BoxGeometry(8, 8, 8), new THREE.MeshBasicMaterial());
  t.after(() => disposeObject(template));
  const templates = new Map<string, THREE.Object3D>([['asset', template]]);
  const entities = Array.from({ length: 80 }, (_, i) => {
    const value = entity(`e${i}`, (i % 10 - 5) * 28, (Math.floor(i / 10) - 4) * 28);
    if (i < 55) value.asset = { id: 'asset', kind: 'moby' };
    if (i >= 30 && i < 45) {
      value.asset = { id: 'collision', kind: 'collision' };
      value.collision = { kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: i, faceCount: 12, vertexCount: 24, types: [] };
      templates.set(`collision:solid:${i}`, template);
    }
    if (i >= 45 && i < 55) value.transform.scale.x = -1;
    if (i >= 65 && i < 70) value.geometry = { kind: 'pointLight', points: [] };
    if (i >= 70 && i < 75) {
      value.geometry = { kind: 'sphere', points: [] };
      value.transform.scale = { x: 8, y: 8, z: 8 };
    }
    if (i >= 75) value.geometry = { kind: 'spline', points: [{ x: 0, y: 0, z: 0, w: 0 }, { x: 12, y: 0, z: 0, w: 0 }] };
    if (i % 17 === 0) value.state.hidden = true;
    if (i % 13 === 0) value.state.locked = true;
    if (i % 19 === 0) value.state.disabled = true;
    return value;
  });
  f.projection.setAssetTemplates(templates);
  f.projection.setViewportSize(bounds.width, bounds.height);
  f.projection.sync(entities, ['e75']);
  f.overlays.sync({
    deathHeight: -100, isSphericalWorld: true, sphereCenter: { x: 100, y: 0, z: 30 },
    shipPosition: { x: 0, y: 0, z: 30 }, shipRotationZ: 0,
    shipPath: { sourceValue: -1 }, shipCameraCuboidStart: { sourceValue: -1 }, shipCameraCuboidEnd: { sourceValue: -1 },
  });
  f.update();
  const points = entities.flatMap((value) => {
    const object = f.projection.getObject(value.id);
    const box = object && isSceneBillboardGizmo(object)
      ? new THREE.Box3().setFromObject(object) : f.projection.getBounds(value.id);
    return box && !box.isEmpty() ? [box.getCenter(new THREE.Vector3())] : [];
  });
  f.overlays.root.traverse((object) => {
    if (isSceneBillboardGizmo(object)) points.push(new THREE.Box3().setFromObject(object).getCenter(new THREE.Vector3()));
  });
  let markers = 0;
  let hoverHits = 0;
  let assetHits = 0;
  for (let i = 0; i < 400; i++) {
    const point = points[i % points.length].clone();
    point.x += i % 5 - 2;
    const [x, y] = f.client(point);
    const expected = f.reference(x, y);
    assert.deepEqual(f.picking.pick(x, y, bounds), expected);
    const object = expected.entityId && f.projection.getObject(expected.entityId);
    const hovered = object && isSceneBillboardGizmo(object) ? expected.entityId : undefined;
    f.picking.queueHover(x, y);
    f.picking.flushHover(bounds);
    assert.equal(f.hovered, hovered);
    if (expected.markerId) markers++;
    if (hovered) hoverHits++;
    if (expected.entityId && !hovered) assetHits++;
  }
  assert.ok(markers > 0 && hoverHits > 0 && assetHits > 0);
});

test('queued hover uses current transforms, visibility, template replacement and simulation occluders', (t) => {
  const f = fixture();
  t.after(() => f.dispose());
  const marker = entity('marker');
  f.projection.sync([marker]);
  f.update();
  const point = new THREE.Box3().setFromObject(f.projection.getObject('marker')!).getCenter(new THREE.Vector3());
  const [x, y] = f.client(point);
  f.picking.queueHover(x, y);
  f.projection.previewTransforms([{ entityId: marker.id, transform: { ...marker.transform, position: { x: 150, y: 0, z: 0 } } }]);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
  f.projection.sync([marker]);
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, 'marker');
  const proxy = new THREE.Mesh(new THREE.BoxGeometry(100, 100, 2), new THREE.MeshBasicMaterial());
  proxy.position.set(0, 20, 20);
  const simulated = entity('simulated');
  simulated.asset = { id: 'not-loaded', kind: 'moby' };
  proxy.userData.forgeEntityId = simulated.id;
  f.projection.sync([marker, simulated]);
  f.projection.setSimulationHiddenEntity(simulated.id, true);
  f.projection.setSimulationPickableEntity(simulated.id, true);
  f.projection.sync([marker, simulated]);
  f.simulation.add(proxy);
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
  proxy.position.x = 150;
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, 'marker');
  f.projection.setSimulationPickableEntity(simulated.id, false);
  f.projection.setSimulationHiddenEntity(simulated.id, false);
  f.projection.sync([{ ...marker, state: { ...marker.state, locked: true } }, simulated]);
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  t.after(() => disposeObject(template));
  f.projection.setAssetTemplates(new Map([['asset', template]]));
  f.projection.sync([{ ...marker, asset: { id: 'asset', kind: 'moby' } }]);
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
  f.projection.setAssetTemplates(new Map());
  f.projection.sync([marker]);
  f.update();
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, 'marker');
  f.picking.queueHover(x, y);
  f.projection.sync([]);
  f.picking.flushHover(bounds);
  assert.equal(f.hovered, undefined);
});

test('level marker priority avoids entity queries and clears marker hover on input cancellation', (t) => {
  const f = fixture();
  t.after(() => f.dispose());
  f.overlays.sync({
    deathHeight: -100, isSphericalWorld: false, sphereCenter: { x: 0, y: 0, z: 0 },
    shipPosition: { x: 0, y: 0, z: 0 }, shipRotationZ: 0,
    shipPath: { sourceValue: -1 }, shipCameraCuboidStart: { sourceValue: -1 }, shipCameraCuboidEnd: { sourceValue: -1 },
  });
  let hovered: string | undefined;
  const setHovered = f.overlays.setHovered.bind(f.overlays);
  f.overlays.setHovered = id => { hovered = id; setHovered(id); };
  f.projection.root.raycast = () => { throw new Error('Entity query should not run for a level marker'); };
  f.update();
  const marker = f.overlays.root.children.find(isSceneBillboardGizmo)!;
  const [x, y] = f.client(new THREE.Box3().setFromObject(marker).getCenter(new THREE.Vector3()));
  const id = marker.userData.forgeEntityId as string;
  f.picking.queueHover(x, y);
  f.picking.flushHover(bounds);
  assert.equal(hovered, id);
  f.picking.clearHover();
  assert.equal(hovered, undefined);
  assert.deepEqual(f.picking.pick(x, y, bounds), { markerId: id, entityId: undefined });
});

test('projection edits keep indexed instance and batch clicks equivalent to native picking', (t) => {
  const f = fixture();
  t.after(() => f.dispose());
  const template = new THREE.Mesh(new THREE.BoxGeometry(4, 4, 4), new THREE.MeshBasicMaterial());
  t.after(() => disposeObject(template));
  const templates = new Map<string, THREE.Object3D>([['asset', template]]);
  let entities = Array.from({ length: 128 }, (_, i) => {
    const value = entity(`e${i}`, (i % 16 - 8) * 12, (Math.floor(i / 16) - 4) * 12);
    value.asset = { id: i < 64 ? 'asset' : 'collision', kind: i < 64 ? 'moby' : 'collision' };
    if (i === 63) value.transform.scale.x = -1;
    if (i >= 64) {
      value.collision = { kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: i, faceCount: 12, vertexCount: 24, types: [] };
      templates.set(`collision:solid:${i}`, template);
    }
    return value;
  });
  const compare = () => {
    f.update();
    for (const value of entities) {
      const box = f.projection.getBounds(value.id);
      if (!box || box.isEmpty()) continue;
      const [x, y] = f.client(box.getCenter(new THREE.Vector3()));
      assert.deepEqual(f.picking.pick(x, y, bounds), f.reference(x, y));
    }
  };
  f.projection.setAssetTemplates(templates);
  f.projection.sync(entities);
  compare();
  f.projection.previewTransforms([0, 80].map(i => ({ entityId: entities[i].id,
    transform: { ...entities[i].transform, position: { x: i ? 130 : -130, y: 0, z: 20 } } })));
  compare();
  f.projection.sync(entities);
  compare();
  entities = entities.filter(value => value.id !== 'e7' && value.id !== 'e80').reverse();
  entities = entities.map(value => ['e10', 'e81'].includes(value.id)
    ? { ...value, state: { ...value.state, hidden: true } } : value);
  f.projection.sync(entities, ['e1', 'e70']);
  compare();
  f.projection.sync(entities, [], undefined, true, new Set());
  compare();
  f.projection.sync(entities);
  compare();
  f.projection.setAssetTemplates(new Map());
  f.projection.sync(entities);
  compare();
  f.projection.setAssetTemplates(templates);
  f.projection.sync(entities);
  compare();
});
