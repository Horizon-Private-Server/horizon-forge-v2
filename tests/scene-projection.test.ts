import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import { Line2 } from 'three/addons/lines/Line2.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';
import { LineSegments2 } from 'three/addons/lines/LineSegments2.js';

import { SceneProjection } from '../src/renderer/editor/SceneProjection.ts';
import { SceneGeometryBatch } from '../src/renderer/editor/SceneGeometryBatch.ts';
import { addTfragTemplates, tfragSourceIndex } from '../src/renderer/editor/TfragTemplates.ts';
import { SceneBillboardResources } from '../src/renderer/editor/SceneBillboardGizmo.ts';
import {
  LevelSettingsOverlayProjection, levelSettingsPositionField,
} from '../src/renderer/editor/LevelSettingsOverlayProjection.ts';
import { constrainTfragScale, verticalDragScale } from '../src/renderer/editor/TransformTool.ts';
import {
  expandLinkedCollisionTransforms, indexLinkedCollisionTransforms, inspectTransformSelection,
} from '../src/utils/TransformSelection.ts';
import { AssetPreviewScheduler, AssetThumbnailRuntime, collectSkyShellObjects, disposeThumbnailModel } from '../src/renderer/editor/AssetThumbnailRuntime.ts';
import {
  buildGroundPlacement, createGroundSurfaceRaycast,
} from '../src/renderer/editor/ScenePlacement.ts';
import { resolvePointerSnapTarget, VertexSnapIndex } from '../src/renderer/editor/SceneSnapping.ts';
import type { EditorEntity } from '../src/types/EditorRuntime.js';
import { DEFAULT_SCENE_TREE_COLORS } from '../src/utils/SceneTreeColors.ts';
import { DEFAULT_UYA_COLLISION_VISUALIZATION } from '../src/utils/UyaCollisionVisualization.ts';
import {
  applyCollisionFaceTypes,
  collisionFaceIdFromIntersection,
  collisionRawTypeFromIntersection,
  createInstancedCollisionWireframeOverlay,
  interpolatePointerSegment,
  shouldOrbitWhileCollisionPainting,
} from '../src/renderer/editor/CollisionPainting.ts';
import {
  applySceneEnvironment,
  disposeObject,
  frameCameraOnObject,
  framePs2Positions,
  positionCameraAtPreferredMoby,
  rotateCamera,
  updateCameraFlight,
  updateCameraMovement,
} from '../src/utils/Scene.ts';
import { configureSkybox, skyEyeFromBounds } from '../src/utils/SkyboxScene.ts';
import { configureCollisionMaterials } from '../src/utils/CollisionMaterials.ts';
import {
  configurePs2AssetPreview,
  configurePs2MaterialAlpha,
  configurePs2MaterialFog,
  createPs2OpaquePassMaterial,
} from '../src/utils/Ps2Materials.ts';
import {
  createSelectionOverrideMaterial,
  INSTANCE_SELECTION_MARKER,
} from '../src/utils/SelectionMaterials.ts';
import { projectTransformToSceneMatrix, sceneMatrixToProjectTransform } from '../src/utils/Transforms.ts';
import { splinePointId, transformSplinePoints } from '../src/utils/SplinePoints.ts';
import type { OrbitControls } from 'three/addons/controls/OrbitControls.js';

function entity(id: string, x = 0, asset = true, state: Partial<EditorEntity['state']> = {}): EditorEntity {
  return {
    id,
    name: `Entity ${id}`,
    layer: 'mobys',
    transform: {
      position: { x, y: 2, z: 3 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
    asset: asset ? { id: 'asset', kind: 'moby' } : undefined,
    transformModes: ['translate', 'rotate', 'scale'],
    state: {
      dirty: false, hidden: false, disabled: false, locked: false, readOnly: false,
      invalid: false, missingAsset: false, ...state,
    },
  };
}

function textureAlpha(
  image: { data: Uint8Array; width: number; height: number },
  x: number,
  y: number,
): number {
  const pixelX = Math.max(0, Math.min(image.width - 1, Math.floor((x + 1) * image.width / 2)));
  const pixelY = Math.max(0, Math.min(image.height - 1, Math.floor((y + 1) * image.height / 2)));
  return image.data[(pixelY * image.width + pixelX) * 4 + 3];
}

function textureRgb(
  image: { data: Uint8Array; width: number; height: number },
  x: number,
  y: number,
): number[] {
  const pixelX = Math.max(0, Math.min(image.width - 1, Math.floor((x + 1) * image.width / 2)));
  const pixelY = Math.max(0, Math.min(image.height - 1, Math.floor((y + 1) * image.height / 2)));
  const offset = (pixelY * image.width + pixelX) * 4;
  return Array.from(image.data.slice(offset, offset + 3));
}

test('vertical gizmo drags provide stable scale control', () => {
  assert.equal(verticalDragScale(1, 200, null), 2);
  assert.equal(verticalDragScale(1, -200, null), 0.5);
  assert.equal(verticalDragScale(1, 30, 0.1), 1.1);
  const transform = entity('tfrag-scale').transform;
  transform.scale = { x: 2, y: 2.000001, z: 1.999999 };
  constrainTfragScale(transform);
  assert.deepEqual(transform.scale, { x: 2, y: 2, z: 2 });
});

test('group transform eligibility is atomic and deduplicated', () => {
  const first = entity('first');
  const second = entity('second');
  assert.deepEqual(
    inspectTransformSelection([first, second], ['first', 'second', 'first'], 'translate').entities
      .map((value) => value.id),
    ['first', 'second'],
  );

  const locked = { ...second, state: { ...second.state, locked: true } };
  const lockedResult = inspectTransformSelection([first, locked], ['first', 'second'], 'translate');
  assert.deepEqual(lockedResult.entities, []);
  assert.match(lockedResult.message ?? '', /1 locked/);

  const translateOnly = { ...second, transformModes: ['translate'] as EditorEntity['transformModes'] };
  const incompatible = inspectTransformSelection([first, translateOnly], ['first', 'second'], 'rotate');
  assert.deepEqual(incompatible.entities, []);
  assert.match(incompatible.message ?? '', /1 without rotation support/);
});

test('group transform eligibility allows linked collision but blocks unresolved members', () => {
  const first = entity('first');
  const collision = entity('collision');
  collision.collision = {
    kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: 0,
    faceCount: 1, vertexCount: 3, types: [],
    linkedInstanceEntityIds: [first.id],
  };
  const result = inspectTransformSelection([first, collision], ['first', 'collision'], 'translate', 2);
  assert.deepEqual(result.entities, []);
  assert.match(result.message ?? '', /2 missing/);
  const complete = inspectTransformSelection([first, collision], ['first', 'collision'], 'translate');
  assert.deepEqual(complete.entities.map((value) => value.id), ['first', 'collision']);
  const scale = inspectTransformSelection([first, collision], ['first'], 'scale');
  assert.deepEqual(scale.entities, []);
  assert.match(scale.message ?? '', /1 linked to source collision/);
});

test('billboard gizmos share resources, bound screen size, and dispose synchronously', () => {
  const resources = new SceneBillboardResources();
  const options = {
    entityId: 'first',
    label: 'First meshless object',
    worldPosition: { x: 0, y: 0, z: 0 },
    color: '#ff00ff',
    glyph: 'object' as const,
    depthPolicy: 'occluded' as const,
    selected: false,
    hovered: false,
    worldSize: 20,
    minScreenSize: 16,
    maxScreenSize: 40,
  };
  const first = resources.create(options);
  const second = resources.create({ ...options, entityId: 'second', label: 'Second meshless object' });
  assert.equal(first.geometry, second.geometry);
  assert.equal(first.material, second.material);
  assert.equal(first.material.side, THREE.DoubleSide);
  assert.equal(first.material.forceSinglePass, true);
  assert.deepEqual(resources.counts(), { geometries: 1, materials: 1, textures: 1 });
  assert.equal(first.userData.forgeAccessibleLabel, options.label);

  const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 1_000);
  camera.position.set(0, 0, 100);
  camera.lookAt(0, 0, 0);
  camera.updateMatrixWorld();
  first.updateForCamera(camera, 1_000);
  const projectedPixels = first.scale.y
    / (2 * camera.position.distanceTo(first.position) * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / 1_000);
  assert.ok(Math.abs(projectedPixels - options.maxScreenSize) < 1e-6);
  camera.position.z = 100_000;
  camera.updateMatrixWorld();
  first.updateForCamera(camera, 1_000);
  const distantPixels = first.scale.y
    / (2 * camera.position.distanceTo(first.position) * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) / 1_000);
  assert.ok(Math.abs(distantPixels - options.minScreenSize) < 1e-6);
  assert.ok(first.quaternion.angleTo(camera.quaternion) < 1e-6);
  first.updateMatrixWorld(true);
  for (const side of [-1, 1]) {
    const ray = new THREE.Raycaster(new THREE.Vector3(0, 0, side * 100), new THREE.Vector3(0, 0, -side));
    assert.ok(ray.intersectObject(first).length > 0, 'single-pass billboards remain pickable from either side');
  }

  first.setState(true, false);
  assert.notEqual(first.material, second.material);
  assert.equal(first.material.forceSinglePass, true);
  assert.deepEqual(resources.counts(), { geometries: 1, materials: 2, textures: 2 });
  first.configure({
    ...options,
    glyph: 'ship',
    depthPolicy: 'overlay',
    selected: true,
  });
  assert.equal(first.material.depthTest, false);
  assert.equal(first.material.forceSinglePass, true);
  assert.equal(first.renderOrder, 1_000);
  assert.deepEqual(resources.counts(), { geometries: 1, materials: 2, textures: 2 });
  first.dispose();
  second.dispose();
  assert.deepEqual(resources.counts(), { geometries: 1, materials: 0, textures: 0 });
  resources.dispose();
  assert.deepEqual(resources.counts(), { geometries: 0, materials: 0, textures: 0 });
});

test('level positions use editable Phosphor billboards outside entity projection', () => {
  const projection = new LevelSettingsOverlayProjection();
  const settings = {
    deathHeight: -100,
    isSphericalWorld: false,
    sphereCenter: { x: 1, y: 2, z: 3 },
    shipPosition: { x: 10, y: 20, z: 30 },
    shipRotationZ: 0,
    shipPath: { sourceValue: -1 },
    shipCameraCuboidStart: { sourceValue: -1 },
    shipCameraCuboidEnd: { sourceValue: -1 },
  };
  projection.sync(settings);
  const ship = projection.root.children.find(
    (child) => child.userData.forgeBillboardGlyph === 'airplane-tilt',
  ) as THREE.Mesh;
  assert.equal(ship.userData.forgeBillboardGizmo, true);
  assert.equal(ship.userData.forgeBillboardGlyph, 'airplane-tilt');
  assert.equal(ship.userData.forgeAccessibleLabel, 'Ship position');
  assert.deepEqual(ship.position.toArray(), [10, 30, -20]);
  assert.deepEqual(projection.getResourceCounts(), { geometries: 1, materials: 1, textures: 1 });

  projection.sync({ ...settings, shipPosition: { x: 40, y: 50, z: 60 } });
  assert.equal(projection.root.children.find(
    (child) => child.userData.forgeBillboardGlyph === 'airplane-tilt',
  ), ship);
  assert.deepEqual(ship.position.toArray(), [40, 60, -50]);

  projection.sync({ ...settings, isSphericalWorld: true, shipPosition: { x: 40, y: 50, z: 60 } });
  const sphere = projection.root.children.find((child) => child.userData.forgeBillboardGlyph === 'sphere')!;
  assert.equal(sphere.userData.forgeAccessibleLabel, 'Spherical world center');
  assert.deepEqual(sphere.position.toArray(), [1, 3, -2]);
  assert.equal(levelSettingsPositionField(sphere.userData.forgeEntityId), 'sphereCenter');
  assert.equal(projection.resolvePick([{ object: sphere } as THREE.Intersection]),
    sphere.userData.forgeEntityId);
  assert.deepEqual(projection.getTransformEntities().map((entity) => [
    levelSettingsPositionField(entity.id), entity.transform.position, entity.transformModes,
  ]), [
    ['shipPosition', { x: 40, y: 50, z: 60 }, ['translate']],
    ['sphereCenter', { x: 1, y: 2, z: 3 }, ['translate']],
  ]);
  const preview = projection.getTransformEntities()[1]!;
  preview.transform.position = { x: 7, y: 8, z: 9 };
  projection.previewTransforms([{ entityId: preview.id, transform: preview.transform }]);
  assert.deepEqual(sphere.position.toArray(), [7, 9, -8]);
  assert.deepEqual(projection.getBounds(sphere.userData.forgeEntityId)!.getCenter(new THREE.Vector3()).toArray(),
    [7, 9, -8]);

  projection.sync({ ...settings, shipPosition: { x: 40, y: 50, z: 60 } });
  assert.equal(projection.root.children.filter(
    (child) => child.userData.forgeBillboardGizmo,
  ).length, 1);
  projection.sync(undefined);
  assert.equal(projection.root.children.length, 0);
  assert.deepEqual(projection.getResourceCounts(), { geometries: 1, materials: 0, textures: 0 });
  projection.dispose();
  assert.deepEqual(projection.getResourceCounts(), { geometries: 0, materials: 0, textures: 0 });
});

test('death height aid follows scene bounds, toggles, ignores picking, and disposes', () => {
  const projection = new LevelSettingsOverlayProjection();
  const settings = {
    deathHeight: -100,
    isSphericalWorld: false,
    sphereCenter: { x: 1, y: 2, z: 3 },
    shipPosition: { x: 10, y: 20, z: 30 },
    shipRotationZ: 0,
    shipPath: { sourceValue: -1 },
    shipCameraCuboidStart: { sourceValue: -1 },
    shipCameraCuboidEnd: { sourceValue: -1 },
  };
  projection.setSceneBounds(new THREE.Box3(
    new THREE.Vector3(-100, -20, -200),
    new THREE.Vector3(300, 100, 600),
  ));
  projection.sync(settings);

  const plane = projection.root.children.find(
    (child) => child.userData.forgeDeathHeightPlane,
  ) as THREE.Mesh<THREE.PlaneGeometry, THREE.MeshBasicMaterial>;
  assert.ok(plane);
  assert.deepEqual(plane.position.toArray(), [100, -100, 200]);
  assert.deepEqual(plane.scale.toArray().map(Math.round), [440, 880, 1]);
  assert.equal(plane.rotation.x, -Math.PI / 2);
  assert.equal(plane.renderOrder, -1);
  assert.equal(plane.material.transparent, true);
  assert.equal(plane.material.side, THREE.DoubleSide);
  assert.equal(plane.material.forceSinglePass, true);
  assert.equal(plane.material.depthWrite, false);
  assert.equal(plane.material.map!.wrapS, THREE.RepeatWrapping);
  assert.ok(Math.abs(plane.material.map!.repeat.x - 440 / 128) < 0.0001);
  assert.ok(Math.abs(plane.material.map!.repeat.y - 880 / 128) < 0.0001);
  assert.equal(plane.material.map!.minFilter, THREE.LinearMipmapLinearFilter);
  assert.equal(plane.material.map!.generateMipmaps, true);
  const image = plane.material.map!.image as { data: Uint8Array };
  const alpha = image.data.filter((_, index) => index % 4 === 3);
  assert.ok(alpha.includes(0) && alpha.includes(72) && alpha.includes(210));
  assert.equal(projection.resolvePick([{ object: plane } as unknown as THREE.Intersection]), undefined);
  assert.equal(projection.getTransformEntities().some((entity) => entity.name === 'Death height'), false);

  projection.setDeathPlaneVisible(false);
  assert.equal(plane.visible, false);
  projection.setDeathPlaneVisible(true);
  projection.sync({ ...settings, deathHeight: -120 });
  assert.equal(plane.visible, true);
  assert.equal(plane.position.y, -120);

  let geometryDisposed = false;
  let materialDisposed = false;
  let textureDisposed = false;
  plane.geometry.addEventListener('dispose', () => { geometryDisposed = true; });
  plane.material.addEventListener('dispose', () => { materialDisposed = true; });
  plane.material.map!.addEventListener('dispose', () => { textureDisposed = true; });
  projection.dispose();
  assert.equal(geometryDisposed, true);
  assert.equal(materialDisposed, true);
  assert.equal(textureDisposed, true);
});

test('meshless projections use pickable billboards but never placement geometry', () => {
  const projection = new SceneProjection();
  const meshless = entity('meshless', 4, false);
  projection.sync([meshless]);
  const billboard = projection.getObject(meshless.id)! as THREE.Mesh;
  const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 1_000);
  camera.position.set(4, 3, 100);
  camera.lookAt(4, 3, -2);
  camera.updateMatrixWorld();
  projection.updateBillboards(camera, 800);

  assert.equal(billboard.userData.forgeBillboardGizmo, true);
  assert.equal(billboard.userData.forgeBillboardGlyph, 'robot');
  assert.equal(projection.resolvePick([{ object: billboard }] as unknown as THREE.Intersection[]), meshless.id);
  assert.equal(projection.isPlacementSurface({ object: billboard } as unknown as THREE.Intersection), false);
  assert.deepEqual(projection.getWorldVertices([meshless.id]), []);
  assert.deepEqual(projection.getBillboardResourceCounts(), { geometries: 1, materials: 1, textures: 1 });

  projection.setHoveredEntityId(meshless.id);
  projection.sync([meshless], [meshless.id]);
  assert.deepEqual(projection.getBillboardResourceCounts(), { geometries: 1, materials: 1, textures: 1 });
  projection.sync([{ ...meshless, state: { ...meshless.state, locked: true } }]);
  assert.equal(projection.resolvePick([{ object: billboard }] as unknown as THREE.Intersection[]), undefined);
  projection.sync([{ ...meshless, state: { ...meshless.state, hidden: true } }]);
  assert.equal(billboard.visible, false);
  projection.dispose();
  assert.deepEqual(projection.getBillboardResourceCounts(), { geometries: 0, materials: 0, textures: 0 });
});

test('asset preview scheduler caps work at four and cancels queued jobs', async () => {
  const scheduler = new AssetPreviewScheduler();
  const releases: Array<() => void> = [];
  let active = 0;
  let maximum = 0;
  const jobs = Array.from({ length: 4 }, (_, index) => scheduler.schedule(undefined, async () => {
    active += 1;
    maximum = Math.max(maximum, active);
    await new Promise<void>((resolve) => { releases[index] = resolve; });
    active -= 1;
    return String(index);
  }));
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(active, 4);

  const cancellation = new AbortController();
  let queuedRan = false;
  const queued = scheduler.schedule(cancellation.signal, async () => {
    queuedRan = true;
    return 'queued';
  });
  cancellation.abort();
  await assert.rejects(queued, { name: 'AbortError' });
  assert.equal(queuedRan, false);

  releases.forEach((release) => release());
  await Promise.all(jobs);
  assert.equal(maximum, 4);
  scheduler.dispose();
});

test('asset previews use current visibility and preserve FIFO within each priority', async () => {
  const scheduler = new AssetPreviewScheduler();
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const blockers = Array.from({ length: 4 }, () => scheduler.schedule(undefined, async () => {
    await gate;
    return 'active';
  }));
  let oldVisible = true;
  let newVisible = false;
  const started: string[] = [];
  const enqueue = (id: string, visible?: () => boolean) => scheduler.schedule(undefined, async () => {
    started.push(id);
    return id;
  }, visible);
  const queued = [
    enqueue('old-visible', () => oldVisible), enqueue('prefetch', () => false),
    enqueue('new-visible', () => newVisible), enqueue('still-visible', () => true), enqueue('default'),
  ];
  await Promise.resolve();
  assert.deepEqual(started, []);
  oldVisible = false;
  newVisible = true;
  release();
  assert.deepEqual(await Promise.all(queued), ['old-visible', 'prefetch', 'new-visible', 'still-visible', 'default']);
  assert.deepEqual(started, ['new-visible', 'still-visible', 'default', 'old-visible', 'prefetch']);
  await Promise.all(blockers);
  scheduler.dispose();
});

test('failed previews release capacity and disposal rejects pending work', async () => {
  const scheduler = new AssetPreviewScheduler();
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const blockers = Array.from({ length: 3 }, () => scheduler.schedule(undefined, async () => {
    await gate;
    return 'active';
  }));
  const failure = scheduler.schedule(undefined, async () => { throw new Error('missing blob'); });
  const failed = assert.rejects(failure, /missing blob/);
  const replacement = scheduler.schedule(undefined, async () => 'replacement', () => true);
  assert.equal(await replacement, 'replacement');
  await failed;

  const fourth = scheduler.schedule(undefined, async () => { await gate; return 'fourth'; });
  await Promise.resolve();
  await Promise.resolve();
  let pendingRan = false;
  const pending = scheduler.schedule(undefined, async () => { pendingRan = true; return 'pending'; }, () => true);
  const rejected = assert.rejects(pending, { name: 'AbortError' });
  scheduler.dispose();
  await rejected;
  await assert.rejects(scheduler.schedule(undefined, async () => 'disposed'), { name: 'AbortError' });
  release();
  await Promise.all([...blockers, fourth]);
  assert.equal(pendingRan, false);
});

test('cached thumbnails and texture sources still honor cancellation and runtime disposal', async () => {
  const runtime: AssetThumbnailRuntime = Object.assign(Object.create(AssetThumbnailRuntime.prototype), {
    cache: new Map([['tie:asset:::', 'cached:tie'], ['texture-source:asset', 'cached:texture']]),
    disposed: false,
  });
  const cancellation = new AbortController();
  cancellation.abort();
  await assert.rejects(runtime.get('asset', 'tie', undefined, cancellation.signal), { name: 'AbortError' });
  await assert.rejects(runtime.getTextureSource('asset', cancellation.signal), { name: 'AbortError' });
  Object.assign(runtime, { disposed: true });
  await assert.rejects(runtime.get('asset', 'tie'), { name: 'AbortError' });
  await assert.rejects(runtime.getTextureSource('asset'), { name: 'AbortError' });
});

test('texture thumbnails cancelled during persistence do not repopulate the runtime cache', async () => {
  const names = ['forge', 'fetch', 'document', 'createImageBitmap'] as const;
  const originals = names.map(name => Object.getOwnPropertyDescriptor(globalThis, name));
  const scheduler = new AssetPreviewScheduler();
  const cache = new Map<string, string>();
  const runtime: AssetThumbnailRuntime = Object.assign(Object.create(AssetThumbnailRuntime.prototype), {
    scheduler, cache, activeRequests: new Set(), targetGame: 'UYA', disposed: false,
    encoder: { encode: async () => new Blob(['png']) },
  });
  let stored!: () => void;
  const storing = new Promise<void>(resolve => { stored = resolve; });
  let finish!: (value: { url: string }) => void;
  const persisted = new Promise<{ url: string }>(resolve => { finish = resolve; });
  let closed = 0;
  const mocks = {
    forge: {
      getAssetThumbnail: async () => undefined,
      getAssetPreview: async () => ({ url: 'source:texture' }),
      cancelAssetPreview: async () => {},
      storeAssetThumbnail: () => { stored(); return persisted; },
    },
    fetch: async () => new Response(new Blob(['source'])),
    document: { createElement: () => ({ getContext: () => ({ drawImage() {} }) }) },
    createImageBitmap: async () => ({ width: 16, height: 16, close() { closed++; } }),
  };
  for (const name of names) Object.defineProperty(globalThis, name, { configurable: true, value: mocks[name] });
  try {
    const cancellation = new AbortController();
    const thumbnail = runtime.get('texture', 'texture', undefined, cancellation.signal);
    await storing;
    cancellation.abort();
    finish({ url: 'stored:texture' });
    await assert.rejects(thumbnail, { name: 'AbortError' });
    assert.equal(cache.size, 0);
    assert.equal(closed, 1);
  } finally {
    scheduler.dispose();
    names.forEach((name, index) => {
      if (originals[index]) Object.defineProperty(globalThis, name, originals[index]!);
      else Reflect.deleteProperty(globalThis, name);
    });
  }
});

test('cold thumbnail cache lookups enter in grid order with visible cards first', async () => {
  const savedForge = Object.getOwnPropertyDescriptor(globalThis, 'forge');
  const scheduler = new AssetPreviewScheduler();
  const runtime: AssetThumbnailRuntime = Object.assign(Object.create(AssetThumbnailRuntime.prototype), {
    scheduler, cache: new Map<string, string>(), targetGame: 'UYA', disposed: false,
  });
  const lookups: string[] = [];
  const releases = new Map<string, () => void>();
  Object.defineProperty(globalThis, 'forge', { configurable: true, value: {
    getAssetThumbnail: async (_game: string, id: string) => {
      lookups.push(id);
      await new Promise<void>(resolve => { releases.set(id, resolve); });
      return { url: `cached:${id}` };
    },
  } });
  const ids = ['prefetch-0', 'prefetch-1', 'visible-0', 'visible-1', 'visible-2', 'visible-3'];
  try {
    const jobs = ids.map(id => runtime.get(id, 'tie', undefined, undefined, undefined, undefined,
      () => id.startsWith('visible')));
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.deepEqual(lookups, ids.slice(2), 'Initial slots belong to visible cards in grid order');
    const cancelled = new AbortController();
    const queued = runtime.get('cancelled', 'tie', undefined, cancelled.signal);
    cancelled.abort();
    await assert.rejects(queued, { name: 'AbortError' });
    for (const id of ids.slice(2).reverse()) releases.get(id)!();
    await new Promise<void>(resolve => setImmediate(resolve));
    assert.deepEqual(lookups, [...ids.slice(2), ...ids.slice(0, 2)]);
    releases.get('prefetch-0')!();
    releases.get('prefetch-1')!();
    assert.deepEqual(await Promise.all(jobs), ids.map(id => `cached:${id}`));
  } finally {
    scheduler.dispose();
    if (savedForge) Object.defineProperty(globalThis, 'forge', savedForge);
    else Reflect.deleteProperty(globalThis, 'forge');
  }
});

test('sky thumbnail batches collect shell nodes in source order', () => {
  const root = new THREE.Group();
  root.add(
    new THREE.Group(),
    Object.assign(new THREE.Group(), { name: 'skybox_shell_07' }),
    Object.assign(new THREE.Group(), { name: 'skybox_shell_01' }),
  );
  assert.deepEqual(collectSkyShellObjects(root).map(([index]) => index), [1, 7]);
});

test('model preview framing targets object bounds without fog state', () => {
  const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 10_000);
  const object = new THREE.Mesh(new THREE.BoxGeometry(4, 2, 6));
  object.position.set(10, 20, 30);
  const hidden = new THREE.Mesh(new THREE.BoxGeometry(100, 100, 100));
  hidden.position.set(10_000, 0, 0);
  hidden.visible = false;
  object.add(hidden);
  const target = new THREE.Vector3();
  frameCameraOnObject(camera, object, target);
  assert.ok(target.distanceTo(object.position) < 1e-6);
  assert.ok(camera.position.distanceTo(target) > 1);
  assert.ok(camera.near >= 0.001);
  assert.ok(camera.far > camera.near);
  const firstRadius = new THREE.Box3().setFromObject(object).getBoundingSphere(new THREE.Sphere()).radius;
  const firstRatio = camera.position.distanceTo(target) / firstRadius;
  object.scale.setScalar(100);
  frameCameraOnObject(camera, object, target);
  const largeRadius = new THREE.Box3().setFromObject(object).getBoundingSphere(new THREE.Sphere()).radius;
  assert.ok(Math.abs(camera.position.distanceTo(target) / largeRadius - firstRatio) < 1e-6);
  disposeObject(object);
});

test('scene projection diffs entities by ID and updates transforms in place', () => {
  const projection = new SceneProjection();
  assert.deepEqual(projection.sync([entity('a'), entity('b', 10, false)]), {
    created: 2, updated: 0, removed: 0,
  });

  const first = projection.getObject('a') as THREE.Mesh;
  assert.equal((first.material as THREE.MeshBasicMaterial).fog, false);
  assert.equal((first.material as THREE.MeshBasicMaterial).color.getHexString(), '4363d8');
  const meshlessMaterial = (projection.getObject('b') as THREE.Mesh).material as THREE.MeshBasicMaterial;
  assert.equal(meshlessMaterial.fog, false);
  assert.equal(meshlessMaterial.color.getHexString(), 'ff9d00');
  const geometry = first.geometry;
  assert.equal(projection.resolveEntityId(first), 'a');
  assert.deepEqual(projection.sync([entity('a', 20), entity('c')]), {
    created: 1, updated: 1, removed: 1,
  });
  assert.equal(projection.getObject('a'), first);
  assert.equal((projection.getObject('a') as THREE.Mesh).geometry, geometry);
  assert.equal(projection.getObject('a')?.position.x, 20);
  assert.equal(projection.getObject('a')?.position.y, 3);
  assert.equal(projection.getObject('a')?.position.z, -2);
  assert.equal(projection.getObject('b'), undefined);
  assert.deepEqual(projection.sync([entity('a', 20), entity('c')]), {
    created: 0, updated: 0, removed: 0,
  });
  projection.dispose();
});

test('scene projection leaves sky shell entities to the dedicated sky renderer', () => {
  const projection = new SceneProjection();
  const sky = entity('sky');
  sky.asset = { id: 'sky-asset', kind: 'sky' };
  sky.skyShell = {
    sourceShellIndex: 0, order: 0,
    initialRotationRadians: { x: 0, y: 0, z: 0 },
    angularVelocityRadiansPerSecond: { x: 0, y: 0, z: 0 },
  };
  assert.deepEqual(projection.sync([sky], [sky.id]), { created: 0, updated: 0, removed: 0 });
  assert.equal(projection.getObject(sky.id), undefined);
  projection.dispose();
});

test('scene projection converts PS2 Z-up transforms to Three.js Y-up', () => {
  const projection = new SceneProjection();
  const value = entity('rotated');
  const rotation = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 0, 1), Math.PI / 2);
  value.transform.rotation = { x: rotation.x, y: rotation.y, z: rotation.z, w: rotation.w };
  value.transform.scale = { x: 2, y: 3, z: 4 };
  projection.sync([value]);

  const object = projection.getObject(value.id)!;
  const rotatedX = new THREE.Vector3(1, 0, 0).applyQuaternion(object.quaternion);
  assert.ok(rotatedX.distanceTo(new THREE.Vector3(0, 0, -1)) < 1e-6);
  assert.deepEqual(object.scale.toArray(), [2, 4, 3]);
  projection.dispose();
});

test('scene projection renders decoded geometry and lighting markers', () => {
  const projection = new SceneProjection();
  projection.setViewportSize(1000, 1000);
  projection.setGeometryColors({
    ...DEFAULT_SCENE_TREE_COLORS,
    cuboid: '#112233',
    sphere: '#aa0000',
    cylinder: '#00aa00',
    pill: '#0000aa',
    spline: '#445566',
    grindPath: '#aabbcc',
    area: '#778899',
    directionalLight: '#123456',
    pointLight: '#654321',
    environmentSample: '#abcdef',
    environmentTransition: '#fedcba',
    camera: '#1122aa',
    ambientSound: '#aa22aa',
  });
  const cuboid = entity('cuboid', 0, false, { readOnly: true });
  cuboid.geometry = { kind: 'cuboid', points: [] };
  cuboid.transform.position = { x: 10, y: 20, z: 30 };
  cuboid.transform.scale = { x: 2, y: 3, z: 4 };
  const spline = entity('spline', 0, false, { readOnly: true });
  spline.geometry = {
    kind: 'spline',
    points: [{ x: 1, y: 2, z: 3, w: 4 }, { x: 5, y: 6, z: 7, w: 8 }],
  };
  spline.transform = {
    position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 },
  };
  const sphere = entity('sphere', 0, false, { readOnly: true });
  sphere.geometry = { kind: 'sphere', points: [] };
  const cylinder = entity('cylinder', 0, false, { readOnly: true });
  cylinder.geometry = { kind: 'cylinder', points: [] };
  const pill = entity('pill', 0, false, { readOnly: true });
  pill.geometry = { kind: 'pill', points: [] };
  const grindPath = entity('grind-path', 0, false, { readOnly: true });
  grindPath.geometry = {
    kind: 'grindPath',
    points: [{ x: 9, y: 10, z: 11, w: 12 }, { x: 13, y: 14, z: 15, w: 16 }],
  };
  grindPath.transform = {
    position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 },
  };
  const area = entity('area', 0, false, { readOnly: true });
  area.geometry = { kind: 'area', points: [] };
  area.transform.position = { x: 40, y: 50, z: 60 };
  area.transform.scale = { x: 7, y: 7, z: 7 };
  const directionalLight = entity('directional-light', 0, false, { readOnly: true });
  directionalLight.geometry = {
    kind: 'directionalLight', points: [{ x: 0, y: 0, z: 0, w: 1 }, { x: 1, y: 2, z: 3, w: 1 }],
  };
  const pointLight = entity('point-light', 0, false, { readOnly: true });
  pointLight.geometry = { kind: 'pointLight', points: [] };
  const environmentSample = entity('environment-sample', 0, false, { readOnly: true });
  environmentSample.geometry = { kind: 'environmentSample', points: [] };
  const environmentTransition = entity('environment-transition', 0, false, { readOnly: true });
  environmentTransition.geometry = { kind: 'environmentTransition', points: [] };
  const camera = entity('camera', 0, false, { readOnly: true });
  camera.geometry = { kind: 'camera', points: [] };
  const ambientSound = entity('ambient-sound', 0, false, { readOnly: true });
  ambientSound.geometry = { kind: 'ambientSound', points: [] };
  ambientSound.transform.scale = { x: 2, y: 3, z: 4 };

  const entities = [cuboid, sphere, cylinder, pill, spline, grindPath, area,
    directionalLight, pointLight, environmentSample, environmentTransition, camera, ambientSound];
  projection.sync(entities);
  const cuboidObject = projection.getObject('cuboid') as THREE.Mesh;
  assert.ok(cuboidObject instanceof THREE.Mesh);
  assert.equal((cuboidObject.material as THREE.MeshBasicMaterial).color.getHexString(), '112233');
  assert.equal((cuboidObject.material as THREE.MeshBasicMaterial).fog, false);
  assert.deepEqual(projection.getObject('cuboid')!.position.toArray(), [10, 30, -20]);
  assert.deepEqual(projection.getObject('cuboid')!.scale.toArray(), [2, 4, 3]);
  const line = projection.getObject('spline') as Line2;
  assert.ok(line instanceof Line2);
  assert.equal((line.material as LineMaterial).color.getHexString(), '445566');
  assert.equal((line.material as LineMaterial).linewidth, 5);
  assert.equal((line.material as LineMaterial).fog, false);
  const splineNodes = line.children.find((child) => child instanceof THREE.Points) as THREE.Points;
  assert.ok(splineNodes instanceof THREE.Points);
  assert.equal((splineNodes.material as THREE.PointsMaterial).size, 12);
  assert.equal((splineNodes.material as THREE.PointsMaterial).sizeAttenuation, false);
  assert.equal((splineNodes.material as THREE.PointsMaterial).color.getHexString(), '445566');
  assert.deepEqual(Array.from(splineNodes.geometry.getAttribute('position').array), [1, 3, -2, 5, 7, -6]);
  assert.equal(projection.resolvePick([{
    object: splineNodes, index: 1,
  } as unknown as THREE.Intersection]), splinePointId('spline', 1));
  assert.deepEqual(Array.from(line.geometry.getAttribute('instanceStart').array), [1, 3, -2, 5, 7, -6]);
  assert.deepEqual(projection.getWorldVertices(['spline']).map((point) => point.toArray()), [
    [1, 3, -2], [5, 7, -6],
  ]);
  assert.equal(((projection.getObject('sphere') as THREE.Mesh).material as THREE.MeshBasicMaterial)
    .color.getHexString(), 'aa0000');
  assert.equal(((projection.getObject('cylinder') as THREE.Mesh).material as THREE.MeshBasicMaterial)
    .color.getHexString(), '00aa00');
  assert.equal(((projection.getObject('pill') as THREE.Mesh).material as THREE.MeshBasicMaterial)
    .color.getHexString(), '0000aa');
  const grindLine = projection.getObject('grind-path') as Line2;
  assert.ok(grindLine instanceof Line2);
  assert.equal((grindLine.material as LineMaterial).color.getHexString(), 'aabbcc');
  assert.equal((grindLine.material as LineMaterial).linewidth, 5);
  assert.deepEqual(Array.from(grindLine.geometry.getAttribute('instanceStart').array), [9, 11, -10, 13, 15, -14]);
  assert.equal(projection.getObject('area'), undefined);
  assert.equal(projection.resolvePick([{ object: line }] as unknown as THREE.Intersection[]), 'spline');
  const directionalObject = projection.getObject('directional-light') as THREE.Line;
  assert.equal((directionalObject.material as THREE.LineBasicMaterial).color.getHexString(), '123456');
  assert.deepEqual(Array.from(directionalObject.geometry.getAttribute('position').array), [0, 0, 0, 1, 3, -2]);
  const pointLightObject = projection.getObject('point-light') as THREE.Mesh;
  assert.equal(pointLightObject.userData.forgeBillboardGizmo, true);
  assert.equal(pointLightObject.userData.forgeBillboardGlyph, 'lightbulb-filament');
  assert.equal(pointLightObject.children.length, 0);
  const pointLightMaterial = pointLightObject.material as THREE.MeshBasicMaterial;
  assert.equal(pointLightMaterial.color.getHexString(), '654321');
  const pointLightTexture = pointLightMaterial.map!.image as {
    data: Uint8Array;
    width: number;
    height: number;
  };
  assert.equal(pointLightTexture.width, 128);
  assert.equal(textureAlpha(pointLightTexture, 0, 0.57), 255, 'bulb outline is opaque inside the well');
  assert.equal(textureAlpha(pointLightTexture, 0, 0.18), 255, 'bulb filament is opaque inside the well');
  assert.equal(textureAlpha(pointLightTexture, 0.32, 0.25), 255, 'well is opaque around the glyph');
  assert.deepEqual(textureRgb(pointLightTexture, 0.32, 0.25), [0, 0, 0], 'well is black');
  assert.equal(textureAlpha(pointLightTexture, 0.45, 0.25), 255, 'pin remains opaque outside the well');
  assert.equal(((projection.getObject('environment-sample') as THREE.Mesh).material as THREE.MeshBasicMaterial)
    .color.getHexString(), 'abcdef');
  assert.equal(((projection.getObject('environment-transition') as THREE.Mesh).material as THREE.MeshBasicMaterial)
    .color.getHexString(), 'fedcba');
  const cameraObject = projection.getObject('camera') as THREE.Mesh;
  assert.equal((cameraObject.material as THREE.MeshBasicMaterial).color.getHexString(), '1122aa');
  assert.equal(projection.resolvePick([
    { object: cameraObject }, { object: splineNodes, index: 1 },
  ] as unknown as THREE.Intersection[]), 'camera');
  const soundObject = projection.getObject('ambient-sound') as THREE.Mesh;
  assert.equal((soundObject.material as THREE.MeshBasicMaterial).color.getHexString(), 'aa22aa');
  assert.equal(soundObject.geometry.type, 'BoxGeometry');
  assert.deepEqual(soundObject.scale.toArray(), [2, 4, 3]);
  assert.equal((soundObject.material as THREE.MeshBasicMaterial).fog, false);
  assert.equal((directionalObject.material as THREE.LineBasicMaterial).fog, false);
  projection.sync(entities, ['spline']);
  assert.equal((splineNodes.material as THREE.PointsMaterial).color.getHexString(), '22d3ee');
  projection.sync(entities, [splinePointId('spline', 1)]);
  const selectedSplineNodes = line.children.find(
    (child) => child instanceof THREE.Points && child !== splineNodes,
  ) as THREE.Points;
  assert.equal(selectedSplineNodes.visible, true);
  assert.deepEqual(Array.from(selectedSplineNodes.geometry.getAttribute('position').array), [5, 7, -6]);
  const previewSpline = {
    ...spline,
    geometry: {
      kind: 'spline' as const,
      points: [{ x: 10, y: 20, z: 30, w: 4 }, { x: 50, y: 60, z: 70, w: 8 }],
    },
  };
  projection.previewSpline(previewSpline, [splinePointId('spline', 1)]);
  assert.deepEqual(Array.from(line.geometry.getAttribute('instanceStart').array), [10, 30, -20, 50, 70, -60]);
  assert.deepEqual(Array.from(selectedSplineNodes.geometry.getAttribute('position').array), [50, 70, -60]);
  projection.previewSpline(previewSpline, []);
  assert.equal(selectedSplineNodes.visible, false);
  projection.sync(entities, [splinePointId('spline', 1)]);
  const restoredLine = projection.getObject('spline') as Line2;
  assert.deepEqual(Array.from(restoredLine.geometry.getAttribute('instanceStart').array), [1, 3, -2, 5, 7, -6]);
  assert.equal(restoredLine.children.find(child => child.userData.forgeSelectedSplineNodes)?.visible, true);
  projection.sync(entities, ['area']);
  const unselectedNodes = (projection.getObject('spline') as Line2).children.find(
    (child) => child.userData.forgeSelectedSplineNodes,
  );
  assert.equal(unselectedNodes?.visible, false);
  assert.equal(projection.getObject('area'), undefined);
  projection.dispose();
});

test('scene projection tolerates empty and single-point paths', () => {
  const empty = entity('empty', 0, false);
  empty.geometry = { kind: 'spline', points: [] };
  const single = entity('single', 0, false);
  single.geometry = { kind: 'grindPath', points: [{ x: 1, y: 2, z: 3, w: 1 }] };
  const projection = new SceneProjection();
  projection.sync([empty, single]);
  assert.ok(projection.getObject(empty.id));
  assert.ok(projection.getObject(single.id));
  assert.equal(projection.getObject(single.id)?.children.find(
    child => child.userData.forgeSelectedSplineNodes,
  )?.visible, false);
  projection.dispose();
});

test('spline point transforms preserve unselected points and gameplay metadata', () => {
  const points = [{ x: 0, y: 0, z: 0, w: 7 }, { x: 4, y: 5, z: 6, w: 8 }];
  const transform = {
    position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 },
  };
  const result = transformSplinePoints(points, new Set([0]), transform,
    new THREE.Matrix4().makeTranslation(1, 2, 3));
  assert.deepEqual(result, [{ x: 1, y: -3, z: 2, w: 7 }, points[1]]);
});

test('spline picking matches its five-pixel visible stroke', () => {
  const projection = new SceneProjection();
  const spline = entity('spline', 0, false);
  spline.geometry = {
    kind: 'spline',
    points: [{ x: -1, y: 0, z: 0, w: 0 }, { x: 1, y: 0, z: 0, w: 0 }],
  };
  spline.transform = {
    position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 },
  };
  projection.sync([spline]);
  projection.root.updateMatrixWorld(true);
  const line = projection.getObject('spline') as Line2;
  line.material.resolution.set(1000, 1000);
  const camera = new THREE.PerspectiveCamera(50, 1, 0.1, 100);
  camera.position.z = 10;
  camera.updateMatrixWorld();
  const raycaster = new THREE.Raycaster();
  raycaster.setFromCamera(new THREE.Vector2(0, 0.004), camera);
  assert.equal(projection.resolvePick(raycaster.intersectObject(projection.root, true)), 'spline');
  raycaster.setFromCamera(new THREE.Vector2(0, 0.012), camera);
  assert.equal(projection.resolvePick(raycaster.intersectObject(projection.root, true)), undefined);
  projection.dispose();
});

test('project transforms round-trip through scene coordinates', () => {
  const value = entity('round-trip').transform;
  value.position = { x: 12, y: -34, z: 56 };
  value.rotation = { x: 0.2, y: -0.3, z: 0.1, w: 0.92 };
  value.scale = { x: -2, y: 3, z: 4 };
  const result = sceneMatrixToProjectTransform(projectTransformToSceneMatrix(value));
  assert.ok(new THREE.Vector3(result.position.x, result.position.y, result.position.z)
    .distanceTo(new THREE.Vector3(value.position.x, value.position.y, value.position.z)) < 1e-5);
  assert.ok(Math.abs(result.scale.x * result.scale.y * result.scale.z
    - value.scale.x * value.scale.y * value.scale.z) < 1e-5);
  const expectedRotation = new THREE.Quaternion(
    value.rotation.x, value.rotation.y, value.rotation.z, value.rotation.w,
  ).normalize();
  const actualRotation = new THREE.Quaternion(
    result.rotation.x, result.rotation.y, result.rotation.z, result.rotation.w,
  );
  assert.ok(Math.abs(expectedRotation.dot(actualRotation)) > 0.99999);
});

test('scene projection applies entity states and picks the nearest eligible entity', () => {
  const projection = new SceneProjection();
  const selected = entity('selected');
  const normal = entity('normal');
  const locked = entity('locked', 0, true, { locked: true });
  const hidden = entity('hidden', 0, true, { hidden: true });
  const disabled = entity('disabled', 0, true, { disabled: true });
  projection.sync([selected, normal, locked, hidden, disabled], [selected.id]);

  assert.notEqual(
    (projection.getObject(selected.id) as THREE.Mesh).material,
    (projection.getObject(normal.id) as THREE.Mesh).material,
  );
  assert.equal(((projection.getObject(selected.id) as THREE.Mesh).material as THREE.MeshBasicMaterial).fog, false);
  assert.equal(((projection.getObject(normal.id) as THREE.Mesh).material as THREE.MeshBasicMaterial).fog, false);
  assert.equal(projection.getObject(locked.id)?.visible, true);
  assert.equal(projection.getObject(hidden.id)?.visible, false);
  assert.equal(projection.getObject(disabled.id)?.visible, false);
  const intersections = [locked, hidden, disabled, normal].map((value) => ({
    object: projection.getObject(value.id)!,
  })) as unknown as THREE.Intersection[];
  assert.equal(projection.resolvePick(intersections), normal.id);
  assert.equal(projection.resolvePick(intersections.slice(0, 3)), undefined);
  projection.dispose();
});

test('enclosing wireframe volumes do not block picking their contents', () => {
  const projection = new SceneProjection();
  projection.setViewportSize(1000, 1000);
  const volume = entity('volume');
  volume.geometry = { kind: 'ambientSound', points: [] };
  const spline = entity('spline');
  spline.geometry = { kind: 'spline', points: [{ x: 0, y: 0, z: 0, w: 0 }] };
  projection.sync([volume, spline]);
  const volumeObject = projection.getObject(volume.id)!;
  const edgePicker = volumeObject.children.find((child) => child instanceof LineSegments2)!;
  const volumeHit = { object: volumeObject } as THREE.Intersection;
  const splineHit = { object: projection.getObject(spline.id)! } as THREE.Intersection;

  assert.equal(projection.resolvePick([volumeHit, splineHit]), spline.id);
  assert.equal(projection.resolvePick([volumeHit]), volume.id);
  const edgeHit = { object: edgePicker } as unknown as THREE.Intersection;
  assert.equal(projection.resolvePick([edgeHit, splineHit]), volume.id);

  volumeObject.updateMatrixWorld(true);
  const camera = new THREE.PerspectiveCamera(120, 1, 0.1, 10);
  camera.position.copy(volumeObject.position);
  camera.lookAt(volumeObject.localToWorld(new THREE.Vector3(0, 0, -1)));
  camera.updateMatrixWorld();
  const corner = volumeObject.localToWorld(new THREE.Vector3(1, 1, -1)).project(camera);
  const raycaster = new THREE.Raycaster();
  raycaster.setFromCamera(new THREE.Vector2(corner.x, corner.y), camera);
  const edgeHits = raycaster.intersectObject(edgePicker);
  assert.ok(edgeHits.length > 0);
  assert.equal(projection.resolvePick(edgeHits), volume.id);
  projection.dispose();
});

test('scene projection merges matching parts, instances assets, and resolves instance picks', () => {
  const template = new THREE.Group();
  const geometry = new THREE.BoxGeometry();
  const material = new THREE.MeshBasicMaterial();
  const billboard = new THREE.Mesh(geometry, material);
  billboard.name = 'shrub_billboard';
  template.add(new THREE.Mesh(geometry, material), new THREE.SkinnedMesh(geometry, material), billboard);
  const projection = new SceneProjection();
  projection.setSelectionColor('#123456');
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('a'), entity('b', 10)], ['b']);

  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  assert.equal(meshes.length, 1);
  const mesh = meshes[0];
  assert.equal(mesh.count, 2);
  assert.notEqual(mesh.getColorAt(1, new THREE.Color()).getHexString(), 'ffffff');
  const selectionOutlines = projection.getSelectionOutlineObjects();
  assert.equal(projection.getSelectionOutlineObjects(), selectionOutlines);
  assert.equal(selectionOutlines.length, 1);
  assert.equal(selectionOutlines[0].children.length, 2);
  assert.equal((selectionOutlines[0].children[0] as THREE.Mesh).geometry, geometry);
  assert.equal(projection.root.children.includes(selectionOutlines[0]), false);
  assert.equal(mesh.geometry.getAttribute('position').count, geometry.getAttribute('position').count * 2);
  assert.equal(projection.resolvePick([{ object: mesh, instanceId: 1 }] as unknown as THREE.Intersection[]), 'b');
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 10) < 1e-6);
  const moved = entity('b', 25).transform;
  projection.previewTransforms([{ entityId: 'b', transform: moved }]);
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 25) < 1e-6);
  assert.ok(Math.abs(projection.getBounds('a')!.getCenter(new THREE.Vector3()).x) < 1e-6);
  assert.ok(Math.abs(new THREE.Vector3().setFromMatrixPosition(selectionOutlines[0].matrix).x - 25) < 1e-6);
  projection.sync([entity('a'), entity('b', 10)], [], new Set(), false);
  assert.equal(mesh.count, 0);
  assert.equal(projection.resolvePick([{ object: mesh, instanceId: 0 }] as unknown as THREE.Intersection[]), undefined);

  projection.dispose();
  disposeObject(template);
});

test('selection changes preserve instance transforms, bounds and unchanged uploads', () => {
  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  const values = [entity('a'), entity('b', 10), entity('mirrored', 20)];
  values[2].transform.scale.x = -1;
  projection.sync(values);
  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  const versions = meshes.map((mesh) => mesh.instanceMatrix.version);
  const bounds = meshes.map((mesh) => mesh.boundingSphere!.clone());
  projection.sync(values, ['b', 'mirrored']);
  meshes.forEach((mesh, index) => {
    assert.equal(mesh.instanceMatrix.version, versions[index]);
    assert.ok(mesh.boundingSphere!.equals(bounds[index]));
  });
  const colors = meshes.map((mesh) => mesh.instanceColor!.version);
  projection.setSelectionColor('#123456');
  projection.sync(values, ['b', 'mirrored']);
  meshes.forEach((mesh, index) => assert.equal(mesh.instanceColor!.version, colors[index]));
  const moved = entity('b', 99).transform;
  projection.previewTransforms([{ entityId: 'b', transform: moved }]);
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 99) < 1e-6);
  projection.sync(values, ['b']);
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 10) < 1e-6);
  projection.sync([values[1], values[0], values[2]], ['a']);
  const normal = meshes.find((mesh) => mesh.count === 2)!;
  assert.equal(projection.resolvePick([{ object: normal, instanceId: 0 }] as unknown as THREE.Intersection[]), 'b');
  projection.setPreviewHiddenEntity('b');
  projection.sync(values);
  assert.equal(normal.count, 1);
  assert.equal(projection.resolvePick([{ object: normal, instanceId: 0 }] as unknown as THREE.Intersection[]), 'a');
  projection.setPreviewHiddenEntity();
  projection.sync(values);
  assert.equal(normal.count, 2);
  projection.dispose();
  disposeObject(template);
});

test('tfrag source groups become local entity templates with exact picking and visibility', () => {
  const scene = new THREE.Group();
  const lod = new THREE.Group();
  lod.name = 'lod_0';
  const source = new THREE.Mesh(
    new THREE.BoxGeometry(2, 2, 2).translate(10, 30, -20),
    new THREE.MeshBasicMaterial(),
  );
  source.name = 'chunk_0007_lod_0';
  lod.add(source);
  scene.add(lod);
  const piece = entity('piece');
  piece.layer = 'tfrags';
  piece.asset = { id: 'tfrag-asset', kind: 'Tfrag' };
  piece.transform.position = { x: 14, y: 25, z: 35 };
  piece.transform.rotation = { x: 0, y: 0, z: 0.38268343, w: 0.9238795 };
  piece.transform.scale = { x: 1.5, y: 1.5, z: 1.5 };
  piece.transformModes = [];
  piece.tfrag = {
    sourceArchive: 'level_wad/assets/asset_wad.bin', sourceKind: 'primary', sourceIndex: 0, groupIndex: 7,
    occlusionIndex: -1, occlusionIndexStash: 0, textureIds: [3],
    sourcePosition: { x: 10, y: 20, z: 30 },
    bounds: { minimum: { x: -1, y: -1, z: -1 }, maximum: { x: 1, y: 1, z: 1 }, radius: 2 },
  };
  const repeatedPiece = structuredClone(piece);
  repeatedPiece.id = 'repeated-piece';
  repeatedPiece.transform.position.x = 24;
  repeatedPiece.tfrag!.groupIndex = 8;
  repeatedPiece.tfrag!.sourcePosition = { x: 20, y: 20, z: 30 };
  const templates = new Map<string, THREE.Object3D>();
  const result = addTfragTemplates(
    scene, 'forge-asset://cache/assets/tfrag/tfrag.gltf', [piece, repeatedPiece], templates,
  );
  assert.equal(tfragSourceIndex('forge-asset://cache/assets/tfrag/chunks/chunk12/tfrag.gltf'), 12);
  assert.deepEqual(result, { matched: 2, missingAssetIds: [] });

  const projection = new SceneProjection();
  projection.setAssetTemplates(templates);
  projection.sync([piece, repeatedPiece]);
  const mesh = projection.root.getObjectByProperty('isBatchedMesh', true) as THREE.BatchedMesh;
  assert.ok(mesh);
  assert.equal(mesh.instanceCount, 2);
  assert.equal(mesh.getGeometryIdAt(0), mesh.getGeometryIdAt(1));
  assert.equal(mesh.perObjectFrustumCulled, true);
  assert.equal(mesh.sortObjects, false);
  assert.equal(projection.resolvePick([
    { object: mesh, batchId: 0 },
  ] as unknown as THREE.Intersection[]), piece.id);
  const center = projection.getBounds(piece.id)!.getCenter(new THREE.Vector3());
  assert.ok(center.distanceTo(new THREE.Vector3(14, 35, -25)) < 1e-6);
  projection.setTfragsVisible(false);
  projection.sync([piece, repeatedPiece]);
  assert.equal(mesh.getVisibleAt(0), false);
  projection.setAssetTemplates(new Map());
  assert.equal(projection.root.getObjectByProperty('isBatchedMesh', true), undefined);
  projection.setAssetTemplates(templates);
  projection.setTfragsVisible(true);
  projection.sync([piece, repeatedPiece]);
  const reloadedBatches: THREE.BatchedMesh[] = [];
  projection.root.traverse((object) => {
    if (object instanceof THREE.BatchedMesh) reloadedBatches.push(object);
  });
  assert.equal(reloadedBatches.length, 1);
  projection.dispose();
  disposeObject(scene);
});

test('largest observed complete-level tfrag count stays in shared material batches', () => {
  const count = 1_006;
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([
    0, 0, 0, 1, 0, 0, 0, 1, 0,
  ], 3));
  geometry.setIndex([0, 1, 2]);
  const material = new THREE.MeshBasicMaterial();
  const templates = new Map<string, THREE.Object3D>();
  const pieces = Array.from({ length: count }, (_, index) => {
    const value = entity(`tfrag-${index}`, index);
    value.layer = 'tfrags';
    value.asset = { id: `tfrag-asset-${index}`, kind: 'Tfrag' };
    value.transformModes = [];
    value.tfrag = {
      sourceArchive: 'level_wad/assets/asset_wad.bin', sourceKind: 'primary', sourceIndex: 0, groupIndex: index,
      occlusionIndex: -1, occlusionIndexStash: 0, textureIds: [0],
    };
    templates.set(value.asset.id, new THREE.Mesh(geometry, material));
    return value;
  });
  const projection = new SceneProjection();
  projection.setAssetTemplates(templates);
  const started = performance.now();
  projection.sync(pieces, [pieces.at(-1)!.id]);
  const elapsed = performance.now() - started;
  const batches: THREE.BatchedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.BatchedMesh) batches.push(object); });
  assert.equal(batches.length, 1);
  assert.equal(projection.root.children.length, 1);
  assert.equal(batches[0].instanceCount, count);
  assert.equal(batches[0].matrixAutoUpdate, false);
  assert.equal(batches[0].parent!.matrixAutoUpdate, false);
  assert.equal(projection.resolvePick([
    { object: batches[0], batchId: count - 1 },
  ] as unknown as THREE.Intersection[]), pieces.at(-1)!.id);
  assert.ok(elapsed < 2_000, `projecting ${count} tfrag pieces took ${elapsed.toFixed(1)} ms`);
  projection.dispose();
  assert.equal(projection.root.children.length, 0);
  geometry.dispose();
  material.dispose();
});

test('large multi-selections keep tinting and use one bounded outline proxy', () => {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([
    0, 0, 0, 1, 0, 0, 0, 1, 0,
  ], 3));
  geometry.setIndex([0, 1, 2]);
  const material = new THREE.MeshBasicMaterial();
  const template = new THREE.Mesh(geometry, material);
  const templates = new Map<string, THREE.Object3D>();
  const pieces = Array.from({ length: 65 }, (_, index) => {
    const value = entity(`bulk-tfrag-${index}`, index);
    value.asset = { id: `bulk-tfrag-asset-${index}`, kind: 'Tfrag' };
    value.tfrag = {
      sourceArchive: 'level_wad/assets/asset_wad.bin', sourceKind: 'primary', sourceIndex: 0, groupIndex: index,
      occlusionIndex: -1, occlusionIndexStash: 0, textureIds: [0],
    };
    templates.set(value.asset.id, template);
    return value;
  });
  const projection = new SceneProjection();
  projection.setAssetTemplates(templates);
  projection.sync(pieces, pieces.map((piece) => piece.id));
  assert.equal(projection.getSelectionOutlineObjects().length, 1);
  assert.equal(projection.getSelectionOutlineObjects()[0].name, 'Selection bounds outline');
  const batch = projection.root.getObjectByProperty('isBatchedMesh', true) as THREE.BatchedMesh;
  assert.equal(batch.getColorAt(0, new THREE.Color()).getHexString(), 'fffeff');
  projection.dispose();
  geometry.dispose();
  material.dispose();
});

test('asset template bounds use the visible rendered geometry', () => {
  const template = new THREE.Group();
  const tall = new THREE.Mesh(new THREE.BoxGeometry(2, 20, 2), new THREE.MeshBasicMaterial());
  tall.position.y = 5;
  const hidden = new THREE.Mesh(new THREE.BoxGeometry(2, 200, 2), new THREE.MeshBasicMaterial());
  hidden.position.y = -100;
  hidden.visible = false;
  template.add(tall, hidden);
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['tall', template]]));
  const bounds = projection.getAssetTemplateBounds('tall')!;
  assert.equal(bounds.min.y, -5);
  assert.equal(bounds.max.y, 15);
  projection.dispose();
  disposeObject(template);
});

test('scene projection hides only the source instance during collision preview', () => {
  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('source'), entity('neighbor', 10)]);

  let mesh: THREE.InstancedMesh | undefined;
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) mesh = object; });
  assert.equal(mesh?.count, 2);

  projection.setPreviewHiddenEntity('source');
  projection.sync([entity('source'), entity('neighbor', 10)]);
  assert.equal(mesh?.count, 1);
  assert.deepEqual(mesh?.userData.forgeInstanceIds, ['neighbor']);

  projection.setPreviewHiddenEntity();
  projection.sync([entity('source'), entity('neighbor', 10)]);
  assert.equal(mesh?.count, 2);

  projection.dispose();
  disposeObject(template);
});

test('simulation proxy suppression composes with other preview visibility', () => {
  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.setSimulationHiddenEntity('source', true);
  projection.setPreviewHiddenEntity('neighbor');
  projection.sync([entity('source'), entity('neighbor', 10)]);

  let mesh: THREE.InstancedMesh | undefined;
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) mesh = object; });
  assert.equal(mesh?.count, 0);

  projection.setPreviewHiddenEntity();
  projection.sync([entity('source'), entity('neighbor', 10)]);
  assert.deepEqual(mesh?.userData.forgeInstanceIds, ['neighbor']);
  projection.setSimulationHiddenEntity('source', false);
  projection.sync([entity('source'), entity('neighbor', 10)]);
  assert.equal(mesh?.count, 2);

  projection.dispose();
  disposeObject(template);
});

test('derived simulation meshes resolve to their authoritative entity identity', () => {
  const projection = new SceneProjection();
  const source = entity('water');
  projection.sync([source]);
  projection.setSimulationHiddenEntity(source.id, true);
  projection.sync([source]);
  const derived = new THREE.Mesh(new THREE.PlaneGeometry(), new THREE.MeshBasicMaterial());
  derived.userData.forgeEntityId = source.id;
  projection.setSimulationPickableEntity(source.id, true);
  assert.equal(projection.resolvePick([{ object: derived }] as unknown as THREE.Intersection[]), source.id);
  projection.setSimulationPickableEntity(source.id, false);
  assert.equal(projection.resolvePick([{ object: derived }] as unknown as THREE.Intersection[]), undefined);
  derived.geometry.dispose();
  derived.material.dispose();
  projection.dispose();
});

test('scene projection batches unique collision pieces and resolves batch picks', () => {
  const material = new THREE.MeshBasicMaterial();
  const templates = new Map<string, THREE.Object3D>();
  const pieces = ['a', 'b'].map((id, index) => {
    const value = entity(id, index * 10);
    value.asset = { id: 'collision', kind: 'collision' };
    value.collision = {
      kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: index,
      faceCount: 1, vertexCount: 3, types: [],
    };
    value.transformModes = ['translate'];
    templates.set(`collision:solid:${index}`, new THREE.Mesh(new THREE.BoxGeometry(), material));
    return value;
  });
  const projection = new SceneProjection();
  projection.setAssetTemplates(templates);
  projection.sync(pieces, ['b']);

  const meshes: THREE.BatchedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.BatchedMesh) meshes.push(object); });
  assert.equal(meshes.length, 1);
  assert.equal(meshes[0].instanceCount, 2);
  assert.equal(meshes[0].getColorAt(1, new THREE.Color()).getHexString(), 'fffeff');
  assert.equal(projection.root.children.length, 1);
  assert.equal(projection.getSelectionOutlineObjects().length, 1);
  assert.equal(projection.resolvePick([
    { object: meshes[0], batchId: 1 },
  ] as unknown as THREE.Intersection[]), 'b');
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 10) < 1e-6);
  assert.ok(projection.getWorldVertices(['b']).length > 0);

  pieces[1] = { ...pieces[1], state: { ...pieces[1].state, hidden: true } };
  projection.sync(pieces);
  assert.equal(meshes[0].getVisibleAt(1), false);
  pieces[1] = { ...pieces[1], state: { ...pieces[1].state, hidden: false } };
  projection.sync(pieces, [], undefined, true, new Set());
  assert.equal(meshes[0].getVisibleAt(1), false);
  assert.equal(projection.resolvePick([
    { object: meshes[0], batchId: 1 },
  ] as unknown as THREE.Intersection[]), undefined);
  projection.dispose();
  templates.forEach(disposeObject);
});

test('linked collision rigid transforms expand across the connected component', () => {
  const tie = entity('tie', 10);
  tie.asset = { id: 'tie-asset', kind: 'Tie' };
  const secondTie = entity('second-tie', 20);
  secondTie.asset = { id: 'tie-asset', kind: 'Tie' };
  const collision = entity('collision');
  collision.collision = {
    kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: 0,
    faceCount: 1, vertexCount: 3, types: [],
    linkedInstanceEntityIds: [tie.id, secondTie.id],
  };
  const index = indexLinkedCollisionTransforms([tie, secondTie, collision]);
  const movedFromTie = expandLinkedCollisionTransforms(index, [{
    entityId: tie.id,
    transform: { ...tie.transform, position: { ...tie.transform.position, x: 15 } },
  }]);
  assert.deepEqual(movedFromTie.map((update) => [update.entityId, update.transform.position.x]), [
    ['collision', 5], ['second-tie', 25], ['tie', 15],
  ]);
  const movedFromCollision = expandLinkedCollisionTransforms(index, [{
    entityId: collision.id,
    transform: { ...collision.transform, position: { ...collision.transform.position, x: -2 } },
  }]);
  assert.deepEqual(movedFromCollision.map((update) => [update.entityId, update.transform.position.x]), [
    ['collision', -2], ['second-tie', 18], ['tie', 8],
  ]);

  const pivot = new THREE.Vector3(15, 2, 3);
  const rotation = new THREE.Matrix4().makeTranslation(pivot.x, pivot.y, pivot.z)
    .multiply(new THREE.Matrix4().makeRotationZ(Math.PI / 2))
    .multiply(new THREE.Matrix4().makeTranslation(-pivot.x, -pivot.y, -pivot.z));
  const rotate = (value: EditorEntity) => sceneMatrixToProjectTransform(
    rotation.clone().multiply(projectTransformToSceneMatrix(value.transform)),
  );
  const rotated = expandLinkedCollisionTransforms(index, [tie, secondTie].map((value) => ({
    entityId: value.id,
    transform: rotate(value),
  })));
  assert.deepEqual(rotated.map((update) => update.entityId), ['collision', 'second-tie', 'tie']);
  for (const update of rotated) {
    const source = [tie, secondTie, collision].find((value) => value.id === update.entityId)!;
    const expected = projectTransformToSceneMatrix(rotate(source));
    const actual = projectTransformToSceneMatrix(update.transform);
    assert.ok(actual.elements.every((value, offset) => Math.abs(value - expected.elements[offset]) < 1e-5));
  }
});

test('scene projection keeps a representative heavy collision level in one batch', () => {
  const pieceCount = 3_036;
  const geometry = new THREE.BufferGeometry().setAttribute(
    'position',
    new THREE.Float32BufferAttribute([0, 0, 0, 1, 0, 0, 0, 1, 0], 3),
  );
  const material = new THREE.MeshBasicMaterial();
  const templates = new Map<string, THREE.Object3D>();
  const pieces = Array.from({ length: pieceCount }, (_, index) => {
    const value = entity(`collision-${index}`, index % 64);
    value.asset = { id: 'collision', kind: 'collision' };
    value.collision = {
      kind: index < 2_967 ? 'solid' : 'playerBarrier',
      sourcePayloadIndex: 0,
      sourcePieceIndex: index,
      faceCount: 1,
      vertexCount: 3,
      types: [],
    };
    value.transformModes = ['translate'];
    templates.set(
      `collision:${value.collision.kind}:${index}`,
      new THREE.Mesh(geometry, material),
    );
    return value;
  });
  const projection = new SceneProjection();
  projection.setAssetTemplates(templates);
  projection.sync(pieces);

  const batches: THREE.BatchedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.BatchedMesh) batches.push(object); });
  assert.equal(batches.length, 1);
  assert.equal(batches[0].instanceCount, pieceCount);
  const batch = batches[0];
  let matrixWrites = 0;
  let colorWrites = 0;
  const setMatrix = batch.setMatrixAt.bind(batch);
  const setColor = batch.setColorAt.bind(batch);
  batch.setMatrixAt = (index, matrix) => { matrixWrites++; return setMatrix(index, matrix); };
  batch.setColorAt = (index, color) => { colorWrites++; return setColor(index, color); };
  projection.setSelectionColor('#123456');
  projection.sync(pieces, ['collision-1']);
  assert.equal(matrixWrites, 0);
  assert.equal(colorWrites, 1);
  projection.sync(pieces, ['collision-1']);
  assert.equal(matrixWrites, 0);
  assert.equal(colorWrites, 1);
  pieces[1] = {
    ...pieces[1],
    transform: { ...pieces[1].transform, position: { x: 99, y: 2, z: 3 } },
  };
  projection.sync(pieces, ['collision-1']);
  assert.equal(matrixWrites, 1);
  assert.equal(colorWrites, 1);
  assert.equal(batches[0], batch);
  assert.ok(Math.abs(projection.getBounds('collision-1')!.getCenter(new THREE.Vector3()).x - 99.5) < 1e-6);

  projection.dispose();
  assert.equal(projection.root.children.length, 0);
  geometry.dispose();
  material.dispose();
});

test('projection and scene resources dispose once across repeated loads', () => {
  for (let index = 0; index < 25; index += 1) {
    const projection = new SceneProjection();
    projection.sync([entity(String(index))]);
    const object = projection.getObject(String(index)) as THREE.Mesh;
    let geometryDisposals = 0;
    let materialDisposals = 0;
    object.geometry.addEventListener('dispose', () => { geometryDisposals += 1; });
    (object.material as THREE.Material).addEventListener('dispose', () => { materialDisposals += 1; });
    projection.dispose();
    projection.dispose();
    assert.equal(geometryDisposals, 1);
    assert.equal(materialDisposals, 1);
    assert.equal(projection.root.children.length, 0);
  }
});

test('scene disposal releases shared geometry, materials, and textures once', () => {
  const root = new THREE.Group();
  const geometry = new THREE.BoxGeometry();
  const texture = new THREE.Texture();
  const material = new THREE.MeshBasicMaterial({ map: texture });
  root.add(new THREE.Mesh(geometry, material), new THREE.Mesh(geometry, material));
  const disposals = { geometry: 0, material: 0, texture: 0 };
  geometry.addEventListener('dispose', () => { disposals.geometry += 1; });
  material.addEventListener('dispose', () => { disposals.material += 1; });
  texture.addEventListener('dispose', () => { disposals.texture += 1; });
  disposeObject(root);
  assert.deepEqual(disposals, { geometry: 1, material: 1, texture: 1 });
  assert.equal(root.children.length, 0);
});

test('thumbnail disposal releases shared skeleton textures and owned bitmaps once', () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, 'ImageBitmap');
  class Bitmap {
    closes = 0;
    close() { this.closes++; }
  }
  Object.defineProperty(globalThis, 'ImageBitmap', { value: Bitmap, configurable: true });
  try {
    const bitmap = new Bitmap();
    const firstTexture = new THREE.Texture(bitmap as unknown as ImageBitmap);
    const secondTexture = new THREE.Texture(bitmap as unknown as ImageBitmap);
    const externalImage = { close() { assert.fail('Non-bitmap images must not be closed'); } };
    const externalTexture = new THREE.Texture(externalImage as unknown as HTMLImageElement);
    const geometry = new THREE.BoxGeometry();
    const material = new THREE.MeshStandardMaterial({ map: firstTexture, emissiveMap: secondTexture, alphaMap: externalTexture });
    const skeleton = new THREE.Skeleton([]);
    const boneTexture = new THREE.DataTexture(new Float32Array(4), 1, 1);
    skeleton.boneTexture = boneTexture;
    const first = new THREE.SkinnedMesh(geometry, material);
    const second = new THREE.SkinnedMesh(geometry, material);
    first.skeleton = second.skeleton = skeleton;
    const root = new THREE.Group();
    root.add(first, second);
    const counts = { geometry: 0, material: 0, texture: 0, bones: 0 };
    geometry.addEventListener('dispose', () => { counts.geometry++; });
    material.addEventListener('dispose', () => { counts.material++; });
    for (const texture of [firstTexture, secondTexture, externalTexture])
      texture.addEventListener('dispose', () => { counts.texture++; });
    boneTexture.addEventListener('dispose', () => { counts.bones++; });
    disposeThumbnailModel(root);
    disposeThumbnailModel(root);
    assert.deepEqual(counts, { geometry: 1, material: 1, texture: 3, bones: 1 });
    assert.equal(bitmap.closes, 1);
    assert.equal(skeleton.boneTexture, null);
    assert.equal(root.children.length, 0);
  } finally {
    if (original) Object.defineProperty(globalThis, 'ImageBitmap', original);
    else Reflect.deleteProperty(globalThis, 'ImageBitmap');
  }
});

test('scene environment and sky configure WebGL fidelity defaults', () => {
  const scene = new THREE.Scene();
  const backgroundScene = new THREE.Scene();
  applySceneEnvironment(scene, {
    backgroundColor: [57, 65, 50],
    fogColor: [40, 50, 40],
    fogNearDistance: 10,
    fogFarDistance: 175,
    fogNearIntensity: 255,
    fogFarIntensity: 0,
  }, backgroundScene);
  assert.equal(scene.background, null);
  assert.ok(backgroundScene.background instanceof THREE.Color);
  assert.ok(scene.fog instanceof THREE.Fog);
  assert.equal(scene.fog.near, 9);
  assert.equal(scene.fog.far, 157.5);

  const material = new THREE.MeshBasicMaterial();
  const sky = new THREE.Mesh(new THREE.BoxGeometry(10, 10, 10), material);
  sky.geometry.userData.SkyboxDrawOrder = 3;
  sky.geometry.userData.SkyboxDrawBlendMode = 'Bloom';
  sky.geometry.userData.SkyboxShellRotationRaw = [0, 0, 16_384];
  sky.geometry.userData.SkyboxShellRotationDeltaRaw = [0, 0, 1];
  sky.geometry.userData.SkyboxRotationTickRadians = Math.PI / 32_768;
  sky.geometry.userData.SkyboxRuntimeFrameRate = 60;
  sky.name = 'skybox_shell_00';
  const configured = configureSkybox(sky);
  const initialRotation = sky.quaternion.clone();
  assert.equal(sky.renderOrder, -997);
  assert.deepEqual(configured.pieces, ['skybox_shell_00']);
  assert.equal(material.depthTest, false);
  assert.equal(material.depthWrite, false);
  assert.equal(material.fog, false);
  assert.equal(material.blending, THREE.AdditiveBlending);
  assert.ok(initialRotation.angleTo(new THREE.Quaternion()) > 1);
  configured.update(1);
  assert.ok(sky.quaternion.angleTo(initialRotation) > 0.001);
  configured.setRotation({ x: 0, y: 0, z: 0 }, { x: 0, y: 0, z: 0 });
  assert.ok(sky.quaternion.angleTo(new THREE.Quaternion()) < 0.001);
  configured.setRotation({ x: 0, y: 0, z: 0 }, { x: 0, y: 0, z: 1 });
  configured.update(1);
  assert.ok(sky.quaternion.angleTo(new THREE.Quaternion()) > 0.001);
  disposeObject(sky);
});

test('sky composition eye is independent of shell order', () => {
  const upper = new THREE.Box3(new THREE.Vector3(-10, 10, -10), new THREE.Vector3(10, 30, 10));
  const lower = new THREE.Box3(new THREE.Vector3(-25, -30, -25), new THREE.Vector3(25, -10, 25));
  const upperFirst = skyEyeFromBounds(upper.clone().union(lower));
  const lowerFirst = skyEyeFromBounds(lower.clone().union(upper));
  assert.ok(upperFirst.distanceTo(lowerFirst) < 1e-9);
  assert.ok(Math.abs(upperFirst.y) < 0.01);
});

test('PS2 fog clamps to the game far intensity instead of increasing to full fog', () => {
  const material = new THREE.MeshBasicMaterial();
  const root = new THREE.Mesh(new THREE.BoxGeometry(), material);
  configurePs2MaterialFog(root, {
    backgroundColor: [0, 0, 0], fogColor: [40, 50, 40],
    fogNearDistance: 10, fogFarDistance: 175, fogNearIntensity: 255, fogFarIntensity: 128,
  });
  const shader = {
    fragmentShader: '#include <fog_pars_fragment>\n#include <fog_fragment>',
    uniforms: {} as Record<string, { value: number }>,
  };
  material.onBeforeCompile(shader as never, {} as never);
  assert.doesNotMatch(shader.fragmentShader, /#include <fog_fragment>/);
  assert.match(shader.fragmentShader, /vFogDepth - forgeFogNear/);
  assert.equal(shader.uniforms.forgeFogNear.value, 9);
  assert.equal(shader.uniforms.forgeFogFar.value, 157.5);
  assert.equal(shader.uniforms.forgeFogNearAmount.value, 1 / 256);
  assert.equal(shader.uniforms.forgeFogFarAmount.value, 0.5);
  const compile = material.onBeforeCompile;
  configurePs2MaterialFog(root, {
    backgroundColor: [0, 0, 0], fogColor: [1, 2, 3],
    fogNearDistance: 20, fogFarDistance: 200, fogNearIntensity: 128, fogFarIntensity: 64,
  });
  assert.equal(material.onBeforeCompile, compile);
  assert.equal(shader.uniforms.forgeFogNear.value, 18);
  assert.equal(shader.uniforms.forgeFogFar.value, 180);
  assert.equal(shader.uniforms.forgeFogNearAmount.value, 0.5);
  assert.equal(shader.uniforms.forgeFogFarAmount.value, 0.75);
  disposeObject(root);
});

test('player barrier materials receive a stable translucent stripe decorator', () => {
  const material = new THREE.MeshBasicMaterial({ transparent: true, side: THREE.DoubleSide });
  material.name = 'player_barrier';
  const root = new THREE.Mesh(new THREE.BoxGeometry(), material);
  configureCollisionMaterials(root, DEFAULT_UYA_COLLISION_VISUALIZATION);
  const compile = material.onBeforeCompile;
  configureCollisionMaterials(root, DEFAULT_UYA_COLLISION_VISUALIZATION);
  assert.equal(material.onBeforeCompile, compile);
  assert.equal(material.depthWrite, false);
  assert.equal(material.side, THREE.FrontSide);
  assert.match(material.customProgramCacheKey(), /player-barrier-object-stripes/);
  const shader = {
    vertexShader: 'void main() {\n#include <begin_vertex>\n}',
    fragmentShader: 'void main() {\n#include <color_fragment>\n#include <dithering_fragment>\n}',
  };
  material.onBeforeCompile(shader as never, {} as never);
  assert.match(shader.fragmentShader, /forgeBarrierStripe/);
  assert.match(shader.fragmentShader, /forgeBarrierSelectionColor = vColor\.rgb/);
  assert.match(shader.fragmentShader, /gl_FragColor\.a \*= 1\.0 - forgeBarrierStripe/);
  assert.equal(material.vertexColors, false);
  assert.match(shader.vertexShader, /vForgeBarrierPosition = transformed/);
  assert.doesNotMatch(shader.fragmentShader, /gl_FragCoord/);
  disposeObject(root);
});

test('solid collision materials combine live nibble palettes with a size-independent tint', () => {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([0, 0, 0, 1, 0, 0, 0, 1, 0], 3));
  geometry.setAttribute('_collision_type', new THREE.Uint8BufferAttribute([11, 11, 11], 1));
  geometry.setAttribute('_sound_type', new THREE.Uint8BufferAttribute([10, 10, 10], 1));
  const material = new THREE.MeshBasicMaterial({ side: THREE.DoubleSide });
  material.name = 'solid_collision';
  const root = new THREE.Mesh(geometry, material);
  configureCollisionMaterials(root, DEFAULT_UYA_COLLISION_VISUALIZATION);
  const compile = material.onBeforeCompile;
  const shader = {
    vertexShader: 'void main() {\n#include <begin_vertex>\n#include <project_vertex>\n}',
    fragmentShader: 'void main() {\n#include <color_fragment>\n}',
    uniforms: {} as Record<string, { value: THREE.Color[] }>,
  };
  material.onBeforeCompile(shader as never, {} as never);
  assert.match(shader.vertexShader, /_collision_type/);
  assert.match(shader.fragmentShader, /forgeSoundColors\[forgeSoundIndex\], 0\.2/);
  assert.match(shader.fragmentShader, /forgeFaceNormal.*cross/s);
  assert.match(shader.fragmentShader,
    /mix\(forgeSurfaceColor, forgeCollisionObjectColor, forgeCollisionSelected\).*forgeLight/s);
  assert.equal(material.vertexColors, false);
  assert.equal(material.side, THREE.FrontSide);
  assert.doesNotMatch(shader.fragmentShader, /gl_FragCoord/);

  const selectedMaterial = createSelectionOverrideMaterial(material, new THREE.Color('#22d3ee'));
  const selectedShader = {
    vertexShader: 'void main() {\n#include <begin_vertex>\n#include <project_vertex>\n#include <color_vertex>\n}',
    fragmentShader: 'void main() {\n#include <color_fragment>\n#include <opaque_fragment>\n}',
    uniforms: {} as Record<string, { value: THREE.Color | THREE.Color[] }>,
  };
  selectedMaterial.onBeforeCompile(selectedShader as never, {} as never);
  assert.match(selectedShader.fragmentShader, /uniform vec3 forgeSelectionColor;/);
  assert.doesNotMatch(selectedShader.fragmentShader, /vec3 forgeSelectionColor =/);
  assert.match(selectedShader.fragmentShader, /outgoingLight = forgeSelectionColor \* mix/);
  selectedMaterial.dispose();
  const changed = {
    ...DEFAULT_UYA_COLLISION_VISUALIZATION,
    collisionTypeColors: [...DEFAULT_UYA_COLLISION_VISUALIZATION.collisionTypeColors],
    soundTypeColors: [...DEFAULT_UYA_COLLISION_VISUALIZATION.soundTypeColors],
  };
  changed.collisionTypeColors[11] = '#010203';
  changed.soundTypeColors[10] = '#040506';
  configureCollisionMaterials(root, changed);
  assert.equal(material.onBeforeCompile, compile);
  assert.ok(shader.uniforms.forgeCollisionColors.value[11].equals(new THREE.Color('#010203')));
  assert.ok(shader.uniforms.forgeSoundColors.value[10].equals(new THREE.Color('#040506')));
  disposeObject(root);
});

test('collision painting resolves one native face ID across triangulated faces', () => {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([
    0, 0, 0,
    1, 0, 0,
    1, 1, 0,
    0, 1, 0,
  ], 3));
  geometry.setAttribute('_collision_face_id', new THREE.Uint32BufferAttribute([7, 7, 7, 7], 1));
  geometry.setAttribute('_collision_type', new THREE.Uint8BufferAttribute([1, 1, 1, 1], 1));
  geometry.setAttribute('_sound_type', new THREE.Uint8BufferAttribute([2, 2, 2, 2], 1));
  geometry.setIndex([3, 2, 1, 3, 1, 0]);
  const mesh = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial());
  const first = {
    distance: 0, point: new THREE.Vector3(), object: mesh,
    face: { a: 3, b: 2, c: 1, normal: new THREE.Vector3(), materialIndex: 0 },
  } satisfies THREE.Intersection;
  const second = {
    distance: 0, point: new THREE.Vector3(), object: mesh,
    face: { a: 3, b: 1, c: 0, normal: new THREE.Vector3(), materialIndex: 0 },
  } satisfies THREE.Intersection;

  assert.equal(collisionFaceIdFromIntersection(first), 7);
  assert.equal(collisionFaceIdFromIntersection(second), 7);
  assert.equal(collisionRawTypeFromIntersection(first), 0x21);
  applyCollisionFaceTypes(mesh, 0x31, [{ faceIndex: 7, rawType: 0xaf }]);
  assert.equal(collisionRawTypeFromIntersection(second), 0xaf);
  applyCollisionFaceTypes(mesh, 0x31, []);
  assert.equal(collisionRawTypeFromIntersection(first), 0x31);
  geometry.setAttribute('_collision_face_id', new THREE.Uint32BufferAttribute([7, 8, 7, 7], 1));
  assert.equal(collisionFaceIdFromIntersection(second), undefined);

  geometry.dispose();
  mesh.material.dispose();
});

test('instanced collision wireframe overlays solid geometry without replacing it', () => {
  const geometry = new THREE.BoxGeometry(1, 1, 1);
  const material = new THREE.MeshBasicMaterial({ color: 0xff0000 });
  const source = new THREE.Group();
  source.add(new THREE.Mesh(geometry, material));

  const overlay = createInstancedCollisionWireframeOverlay(source);
  const wireframe = overlay.children[0] as THREE.Mesh;
  assert.equal(material.wireframe, false);
  assert.equal((wireframe.material as THREE.MeshBasicMaterial).wireframe, true);
  assert.equal(wireframe.renderOrder, 1);
  assert.equal(wireframe.geometry, geometry);
  assert.notEqual(wireframe.material, material);

  disposeObject(overlay);
  disposeObject(source);
});

test('collision painting interpolates fast pointer movement at bounded spacing', () => {
  const samples = interpolatePointerSegment({ x: 0, y: 0 }, { x: 10, y: 0 }, 4);
  assert.equal(samples.length, 3);
  assert.ok(Math.abs(samples[0]!.x - 10 / 3) < 1e-9);
  assert.ok(Math.abs(samples[1]!.x - 20 / 3) < 1e-9);
  assert.deepEqual(samples[2], { x: 10, y: 0 });
  assert.deepEqual(interpolatePointerSegment({ x: 2, y: 3 }, { x: 2, y: 3 }), [{ x: 2, y: 3 }]);
});

test('collision painting reserves middle drag for camera orbit', () => {
  assert.equal(shouldOrbitWhileCollisionPainting(1, false), true);
  assert.equal(shouldOrbitWhileCollisionPainting(0, true), true);
  assert.equal(shouldOrbitWhileCollisionPainting(0, false), false);
  assert.equal(shouldOrbitWhileCollisionPainting(2, false), false);
});

test('PS2 blend materials normalize byte 127 to full opacity', () => {
  const material = new THREE.MeshBasicMaterial({ map: new THREE.Texture(), transparent: true });
  material.userData.TieTextureFullOpacityAlpha = 127;
  const root = new THREE.Mesh(new THREE.BufferGeometry(), material);
  configurePs2MaterialAlpha(root, 'tie');
  const shader = { fragmentShader: '#include <map_fragment>\n#include <alphatest_fragment>' };
  material.onBeforeCompile(shader as never, {} as never);
  assert.match(shader.fragmentShader, /diffuseColor\.a = min\(diffuseColor\.a \/ 0\.49803922, 1\.0\)/);
  assert.equal(material.opacity, 1);
  const opaque = createPs2OpaquePassMaterial(material)!;
  assert.equal(opaque.transparent, false);
  assert.equal(opaque.depthWrite, true);
  assert.equal(opaque.alphaTest, 254 / 255);
  const translucentShader = { fragmentShader: '#include <map_fragment>\n#include <alphatest_fragment>' };
  material.onBeforeCompile(translucentShader as never, {} as never);
  assert.match(translucentShader.fragmentShader, /if \(diffuseColor\.a >= 0\.99607843\) discard/);
  opaque.dispose();
  disposeObject(root);
});

test('tfrag materials render front faces only', () => {
  const material = new THREE.MeshBasicMaterial({ side: THREE.DoubleSide });
  const root = new THREE.Mesh(new THREE.BufferGeometry(), material);
  configurePs2MaterialAlpha(root, 'tfrag');
  assert.equal(material.side, THREE.FrontSide);
  disposeObject(root);
});

test('selected asset materials tint textured luminance while preserving alpha', () => {
  const source = new THREE.MeshBasicMaterial({ map: new THREE.Texture(), transparent: true });
  const selectionColor = new THREE.Color('#22d3ee');
  const material = createSelectionOverrideMaterial(source, selectionColor);
  const shader = {
    vertexShader: 'void main() {\n#include <color_vertex>\n}',
    fragmentShader: 'void main() {\n#include <map_fragment>\n#include <opaque_fragment>\n}',
    uniforms: {} as Record<string, { value: THREE.Color }>,
  };
  material.onBeforeCompile(shader as never, {} as never);
  assert.match(shader.vertexShader, /vForgeInstanceColor = instanceColor\.rgb/);
  assert.match(shader.vertexShader, /vForgeInstanceColor = getBatchingColor\(getIndirectIndex\(gl_DrawID\)\)\.rgb/);
  assert.match(shader.fragmentShader, /distance\(vForgeInstanceColor, forgeInstanceSelectionMarker\) < 0\.001/);
  assert.match(shader.fragmentShader, /forgeSelectionLuminance = dot\(outgoingLight/);
  assert.match(shader.fragmentShader, /outgoingLight = forgeSelectionColor \* mix/);
  assert.match(shader.fragmentShader, /#include <map_fragment>/);
  assert.doesNotMatch(shader.fragmentShader, /diffuseColor\.a\s*=/);
  assert.equal(shader.uniforms.forgeSelectionColor.value, selectionColor);
  assert.equal(shader.uniforms.forgeInstanceSelectionMarker.value, INSTANCE_SELECTION_MARKER);
  material.dispose();
  source.dispose();
});

test('asset previews hide unsupported moby metals and shrub billboards', () => {
  const moby = new THREE.Group();
  const metals = new THREE.Group();
  metals.name = 'metals';
  metals.add(new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial()));
  moby.add(metals);
  assert.equal(configurePs2AssetPreview(moby, 'moby'), false);
  assert.equal(metals.visible, false);

  const shrub = new THREE.Group();
  const billboard = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  billboard.name = 'shrub_billboard';
  shrub.add(billboard);
  assert.equal(configurePs2AssetPreview(shrub, 'shrub'), false);
  assert.equal(billboard.visible, false);
  disposeObject(moby);
  disposeObject(shrub);
});

test('asset previews split mixed-alpha meshes into opaque and translucent passes', () => {
  const material = new THREE.MeshBasicMaterial({ map: new THREE.Texture(), transparent: true });
  material.userData.MobyTextureFullOpacityAlpha = 127;
  const root = new THREE.Group();
  root.add(new THREE.Mesh(new THREE.BoxGeometry(), material));
  assert.equal(configurePs2AssetPreview(root, 'moby'), true);
  const meshes = root.children as THREE.Mesh[];
  assert.equal(meshes.length, 2);
  assert.equal(material.depthWrite, false);
  assert.ok(meshes.some((mesh) => !(mesh.material as THREE.Material).transparent
    && (mesh.material as THREE.Material).depthWrite));
  disposeObject(root);
});

test('mixed-alpha entity materials create opaque and translucent instance passes', () => {
  const geometry = new THREE.BoxGeometry();
  const material = new THREE.MeshBasicMaterial({ map: new THREE.Texture(), transparent: true });
  material.userData.ShrubTextureFullOpacityAlpha = 127;
  configurePs2MaterialAlpha(new THREE.Mesh(geometry, material), 'shrub');
  const template = new THREE.Mesh(geometry, material);
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('tree')]);
  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  assert.equal(meshes.length, 2);
  assert.ok(meshes.some((mesh) => !(mesh.material as THREE.Material).transparent));
  assert.ok(meshes.some((mesh) => (mesh.material as THREE.Material).transparent));
  projection.dispose();
  disposeObject(template);
});

test('unsupported moby metal overlays do not obscure the textured base mesh', () => {
  const template = new THREE.Group();
  const baseMaterial = new THREE.MeshBasicMaterial({ transparent: true });
  template.add(new THREE.Mesh(new THREE.BoxGeometry(), baseMaterial));
  const metals = new THREE.Group();
  metals.name = 'metals';
  metals.add(new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial()));
  template.add(metals);
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('moby')]);
  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  assert.equal(meshes.length, 1);
  assert.equal((meshes[0].material as THREE.Material).transparent, true);
  projection.dispose();
  disposeObject(template);
});

test('Page Down placement preserves group offsets and ignores selected meshes', () => {
  const projection = new SceneProjection();
  const template = new THREE.Mesh(new THREE.BoxGeometry(2, 2, 2), new THREE.MeshBasicMaterial());
  projection.setAssetTemplates(new Map([['asset', template]]));
  const lower = entity('lower', 0);
  lower.transform.position.z = 10;
  const upper = entity('upper', 4);
  upper.transform.position.z = 12;
  projection.sync([lower, upper]);
  const ground = new THREE.Mesh(new THREE.PlaneGeometry(100, 100), new THREE.MeshBasicMaterial());
  ground.rotateX(-Math.PI / 2);
  const result = buildGroundPlacement(
    [lower, upper], ['lower', 'upper'], projection, [ground, projection.root],
  );
  assert.equal(result.updates.length, 2);
  assert.equal(result.updates[0].transform.position.z, 1);
  assert.equal(result.updates[1].transform.position.z, 3);
  assert.equal(
    result.updates[1].transform.position.z - result.updates[0].transform.position.z,
    upper.transform.position.z - lower.transform.position.z,
  );
  projection.dispose();
  disposeObject(template);
  disposeObject(ground);
});

test('Page Down rejects a partly ineligible selection atomically', () => {
  const projection = new SceneProjection();
  const template = new THREE.Mesh(new THREE.BoxGeometry(2, 2, 2), new THREE.MeshBasicMaterial());
  projection.setAssetTemplates(new Map([['asset', template]]));
  const movable = entity('movable');
  movable.transform.position.z = 10;
  const locked = entity('locked', 4, true, { locked: true });
  locked.transform.position.z = 12;
  projection.sync([movable, locked]);
  const ground = new THREE.Mesh(new THREE.PlaneGeometry(100, 100), new THREE.MeshBasicMaterial());
  ground.rotateX(-Math.PI / 2);

  const result = buildGroundPlacement(
    [movable, locked], [movable.id, locked.id], projection, [ground, projection.root],
  );
  assert.deepEqual(result.updates, []);
  assert.match(result.message, /1 locked/);

  projection.dispose();
  disposeObject(template);
  disposeObject(ground);
});

test('Page Down lands only on tfrags and unselected TIEs', () => {
  const projection = new SceneProjection();
  const template = new THREE.Mesh(new THREE.BoxGeometry(2, 2, 2), new THREE.MeshBasicMaterial());
  projection.setAssetTemplates(new Map([['asset', template]]));
  const selected = entity('selected');
  selected.transform.position.z = 10;
  const moby = entity('moby');
  moby.transform.position.z = 7;
  const shrub = entity('shrub');
  shrub.asset!.kind = 'Shrub';
  shrub.transform.position.z = 5;
  const collision = entity('collision');
  collision.asset!.kind = 'Collision';
  collision.transform.position.z = 9;
  collision.collision = {
    kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: 0,
    faceCount: 1, vertexCount: 3, types: [],
  };
  const spline = entity('spline', 0, false);
  spline.geometry = {
    kind: 'spline',
    points: [{ x: 0, y: 0, z: 8, w: 0 }, { x: 1, y: 0, z: 8, w: 0 }],
  };
  const tie = entity('tie');
  tie.asset!.kind = 'Tie';
  tie.transform.position.z = 3;
  const roof = entity('roof');
  roof.asset!.kind = 'Tie';
  roof.transform.position.z = 12;
  const ground = new THREE.Mesh(new THREE.PlaneGeometry(100, 100), new THREE.MeshBasicMaterial());
  ground.rotateX(-Math.PI / 2);

  projection.sync([selected, moby, shrub, collision, spline]);
  const groundResult = buildGroundPlacement(
    [selected, moby, shrub, collision, spline], [selected.id], projection, [ground, projection.root],
  );
  assert.equal(groundResult.updates[0].transform.position.z, 1);

  projection.sync([selected, moby, shrub, collision, spline, tie, roof]);
  const findGround = createGroundSurfaceRaycast(
    [selected, moby, shrub, collision, spline, tie, roof], projection, [ground, projection.root],
  );
  assert.equal(findGround(0, -2, 20)?.y, 13);
  assert.equal(findGround(0, -2, 6)?.y, 4);
  const tieResult = buildGroundPlacement(
    [selected, moby, shrub, collision, spline, tie, roof],
    [selected.id], projection, [ground, projection.root],
  );
  assert.equal(tieResult.updates[0].transform.position.z, 5);

  projection.dispose();
  disposeObject(template);
  disposeObject(ground);
});

test('asset placement surfaces ignore meshless proxy bounds', () => {
  const projection = new SceneProjection();
  const proxy = entity('proxy');
  projection.sync([proxy]);
  const proxyObject = projection.getObject(proxy.id) as THREE.Mesh;
  assert.equal(projection.isPlacementSurface({ object: proxyObject } as unknown as THREE.Intersection), false);

  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  projection.addAssetTemplate('asset', template);
  projection.sync([proxy]);
  let mesh: THREE.InstancedMesh | undefined;
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) mesh = object; });
  assert.equal(projection.isPlacementSurface({ object: mesh! } as unknown as THREE.Intersection), true);
  projection.dispose();
  disposeObject(template);
});

test('translation snapping resolves centers, visible vertices, surfaces, and nearest source vertices', () => {
  const index = new VertexSnapIndex([
    new THREE.Vector3(-5, 0, 0),
    new THREE.Vector3(2, 1, 0),
    new THREE.Vector3(8, 0, 0),
  ]);
  assert.deepEqual(index.nearest(new THREE.Vector3(2.1, 1.1, 0))?.toArray(), [2, 1, 0]);

  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('target')]);
  assert.ok(projection.getWorldVertices(['target']).some((point) => point.distanceTo(
    new THREE.Vector3(0.5, 3.5, -1.5),
  ) < 1e-6));
  const raycaster = new THREE.Raycaster(new THREE.Vector3(0.4, 10, -1.6), new THREE.Vector3(0, -1, 0));
  const targets = [projection.root];
  assert.deepEqual(
    resolvePointerSnapTarget('center', raycaster, projection, targets, new Set())?.toArray(),
    [0, 3, -2],
  );
  const vertex = resolvePointerSnapTarget('vertex', raycaster, projection, targets, new Set())!;
  assert.ok(Math.abs(Math.abs(vertex.x) - 0.5) < 1e-6);
  assert.ok(Math.abs(Math.abs(vertex.y - 3) - 0.5) < 1e-6);
  assert.ok(Math.abs(Math.abs(vertex.z + 2) - 0.5) < 1e-6);
  assert.ok(Math.abs(resolvePointerSnapTarget('surface', raycaster, projection, targets, new Set())!.y - 3.5) < 1e-6);
  assert.equal(resolvePointerSnapTarget('center', raycaster, projection, targets, new Set(['target'])), undefined);
  projection.dispose();
  disposeObject(template);
});

test('fixed instance nodes keep parent motion and transform previews correct without local matrix rebuilds', () => {
  const template = new THREE.Mesh(new THREE.BoxGeometry(2, 2, 2), new THREE.MeshBasicMaterial());
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  const normal = entity('normal', 10);
  const mirrored = entity('mirrored', 30);
  mirrored.transform.scale.x = -1;
  projection.sync([normal, mirrored]);
  const parent = new THREE.Group();
  parent.add(projection.root);
  parent.position.set(50, 60, 70);
  parent.updateMatrixWorld(true);
  const fixed: THREE.Object3D[] = [projection.root, ...projection.root.children];
  projection.root.traverse(object => { if (object instanceof THREE.InstancedMesh) fixed.push(object); });
  let updates = 0;
  for (const object of fixed) {
    assert.equal(object.matrixAutoUpdate, false);
    const update = object.updateMatrix.bind(object);
    object.updateMatrix = () => { updates++; update(); };
  }
  const pick = (x: number) => {
    const ray = new THREE.Raycaster(new THREE.Vector3(x, 63, 90), new THREE.Vector3(0, 0, -1));
    return projection.resolvePick(ray.intersectObject(projection.root, true));
  };
  assert.equal(pick(60), 'normal');
  assert.equal(pick(80), 'mirrored');
  const moved = entity('normal', 20).transform;
  projection.previewTransforms([{ entityId: normal.id, transform: moved }]);
  parent.updateMatrixWorld();
  assert.equal(pick(70), 'normal');
  parent.position.x += 100;
  parent.updateMatrixWorld();
  assert.equal(pick(170), 'normal');
  assert.equal(pick(180), 'mirrored');
  assert.equal(updates, 0);
  projection.dispose();
  disposeObject(template);
});

test('mirrored instances use a culling-aware outer reflection without changing their world transform', () => {
  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  const mirrored = entity('mirrored');
  mirrored.transform.scale.x = -1;
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('normal'), mirrored], ['mirrored']);
  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  assert.equal(meshes.length, 2);
  const mirroredMesh = meshes.find((mesh) => mesh.matrix.determinant() < 0)!;
  const instanceMatrix = new THREE.Matrix4();
  mirroredMesh.getMatrixAt(0, instanceMatrix);
  assert.ok(instanceMatrix.determinant() > 0);
  assert.ok(mirroredMesh.matrix.clone().multiply(instanceMatrix).determinant() < 0);
  assert.equal(mirroredMesh.getColorAt(0, new THREE.Color()).getHexString(), 'fffeff');
  assert.ok(projection.getSelectionOutlineObjects()[0].matrix.determinant() < 0);
  projection.dispose();
  disposeObject(template);
});

test('editor geometry batches reuse buffers, track previews and leave picking resources owned by the projection', () => {
  const geometry = new THREE.BoxGeometry(2, 2, 2);
  const material = new THREE.MeshBasicMaterial({ color: '#123456', wireframe: true, fog: false });
  const batch = new SceneGeometryBatch([material]);
  const sources = [new THREE.Mesh(geometry, batch.pickMaterial(material)), new THREE.Mesh(geometry, batch.pickMaterial(material))];
  sources[1].position.x = 20;
  sources.forEach(source => source.updateMatrix());
  batch.sync(sources);
  const mesh = batch.root.children[0] as THREE.InstancedMesh;
  assert.equal(batch.root.children.length, 1);
  assert.equal(mesh.count, 2);
  assert.equal(mesh.geometry, geometry);
  assert.equal(mesh.material, material, 'batching retains the original material sort order');
  assert.equal(sources[0].material.visible, false);
  assert.equal(material.visible, true);
  assert.equal((mesh.material as THREE.MeshBasicMaterial).visible, true);
  material.color.set('#abcdef');
  assert.equal((mesh.material as THREE.MeshBasicMaterial).color.getHexString(), 'abcdef');
  const version = mesh.instanceMatrix.version;
  batch.sync(sources);
  assert.equal(mesh.instanceMatrix.version, version, 'unchanged sync does not upload instance matrices');
  sources[0].position.x = 80;
  sources[0].scale.x = -2;
  sources[0].updateMatrix();
  batch.previewTransforms([sources[0], sources[1]]);
  const matrix = new THREE.Matrix4();
  mesh.getMatrixAt(0, matrix);
  assert.ok(matrix.equals(sources[0].matrix));
  assert.equal(mesh.instanceMatrix.version, version + 1);
  assert.ok(mesh.boundingSphere!.containsPoint(new THREE.Vector3(80, 0, 0)));
  let geometryDisposals = 0;
  let materialDisposals = 0;
  let meshDisposals = 0;
  let pickMaterialDisposals = 0;
  geometry.addEventListener('dispose', () => geometryDisposals++);
  material.addEventListener('dispose', () => materialDisposals++);
  mesh.addEventListener('dispose', () => meshDisposals++);
  sources[0].material.addEventListener('dispose', () => pickMaterialDisposals++);
  batch.sync([]);
  assert.equal(batch.root.children.length, 0);
  assert.equal(meshDisposals, 1);
  batch.dispose();
  assert.equal(material.visible, true);
  assert.equal(geometryDisposals, 0);
  assert.equal(materialDisposals, 0);
  assert.equal(pickMaterialDisposals, 1);
  geometry.dispose();
  material.dispose();
});

test('batched geometry preserves volume picking, selection colors, visibility and parent motion', () => {
  const first = entity('first', 0, false);
  const second = entity('second', 10, false);
  const hidden = entity('hidden', 20, false, { hidden: true });
  for (const value of [first, second, hidden]) value.geometry = { kind: 'cuboid', points: [] };
  second.transform.scale.x = -2;
  const projection = new SceneProjection();
  projection.setViewportSize(800, 600);
  const parent = new THREE.Group();
  parent.add(projection.root);
  const values = [first, second, hidden];
  projection.sync(values);
  const batches = () => {
    const result: THREE.InstancedMesh[] = [];
    projection.root.traverse(object => { if (object instanceof THREE.InstancedMesh) result.push(object); });
    return result;
  };
  assert.equal(batches().length, 1);
  assert.equal(batches()[0].count, 2);
  const camera = new THREE.PerspectiveCamera(60, 800 / 600, 0.1, 1_000);
  const pick = (x: number) => {
    parent.updateMatrixWorld(true);
    camera.position.set(x, 3, 30);
    camera.lookAt(x, 3, -2);
    camera.updateMatrixWorld(true);
    const ray = new THREE.Raycaster();
    ray.setFromCamera(new THREE.Vector2(), camera);
    const hits = ray.intersectObject(projection.root, true);
    assert.ok(hits.every(hit => !(hit.object instanceof THREE.InstancedMesh)));
    return projection.resolvePick(hits);
  };
  assert.equal(pick(0), first.id);
  assert.equal(pick(10), second.id);
  assert.equal(pick(20), undefined);
  projection.sync(values, [first.id]);
  const selected = batches().find(mesh => (mesh.material as THREE.MeshBasicMaterial).color.getHexString() === '22d3ee')!;
  assert.equal(selected.count, 1);
  projection.setSelectionColor('#ff8800');
  assert.equal((selected.material as THREE.MeshBasicMaterial).color.getHexString(), 'ff8800');
  const transform = { ...first.transform, position: { ...first.transform.position, x: 30 } };
  projection.previewTransforms([{ entityId: first.id, transform }]);
  assert.equal(pick(0), undefined);
  assert.equal(pick(30), first.id);
  parent.position.x = 50;
  assert.equal(pick(80), first.id);
  assert.equal(pick(60), second.id);
  assert.ok(projection.getWorldVertices([first.id]).every(point => point.x >= 79 && point.x <= 81));
  projection.sync(values, [], new Set());
  assert.equal(batches().length, 0);
  projection.dispose();
});

test('camera movement accelerates and translates the camera and orbit target together', () => {
  const camera = new THREE.PerspectiveCamera();
  camera.lookAt(0, 0, -1);
  const target = new THREE.Vector3(0, 0, -10);
  const velocity = new THREE.Vector3();
  updateCameraMovement(camera, target, new Set(['KeyW']), velocity, 1 / 60);
  assert.ok(camera.position.z < 0 && camera.position.z > -0.1);
  assert.ok(Math.abs(camera.position.z - (target.z + 10)) < 1e-9);
  for (let index = 0; index < 300; index++)
    updateCameraMovement(camera, target, new Set(['KeyW']), velocity, 1 / 60);
  assert.ok(velocity.length() <= 300);
});

test('camera look rotates in place and carries the orbit target', () => {
  const camera = new THREE.PerspectiveCamera();
  camera.lookAt(0, 0, -1);
  const position = camera.position.clone();
  const target = new THREE.Vector3(0, 0, -10);
  rotateCamera(camera, target, 100, 0);
  assert.ok(camera.position.equals(position));
  assert.ok(Math.abs(target.length() - 10) < 1e-6);
  assert.ok(Math.abs(target.x) > 2);
});

test('camera flight eases to its exact destination', () => {
  const position = new THREE.Vector3();
  const target = new THREE.Vector3();
  const flight = {
    cameraStart: position.clone(),
    cameraEnd: new THREE.Vector3(10, 20, 30),
    targetStart: target.clone(),
    targetEnd: new THREE.Vector3(20, 10, 0),
    elapsed: 0,
    duration: 0.4,
  };
  assert.equal(updateCameraFlight(position, target, flight, 0.2), false);
  assert.deepEqual(position.toArray(), [5, 10, 15]);
  assert.deepEqual(target.toArray(), [10, 5, 0]);
  assert.equal(updateCameraFlight(position, target, flight, 0.2), true);
  assert.deepEqual(position.toArray(), flight.cameraEnd.toArray());
  assert.deepEqual(target.toArray(), flight.targetEnd.toArray());
});

test('camera framing ignores distant entity outliers', () => {
  const camera = new THREE.PerspectiveCamera();
  const controls = { target: new THREE.Vector3(), update() {} } as unknown as OrbitControls;
  const cluster = Array.from({ length: 20 }, (_, index) => ({ x: index, y: 0, z: 0 }));
  assert.equal(framePs2Positions(camera, controls, [...cluster, { x: 10_000, y: 0, z: 0 }]), true);
  assert.ok(controls.target.x < 20);
  assert.ok(camera.position.distanceTo(controls.target) < 50);
  assert.ok(camera.far > 19_000);
});

test('camera spawn prefers Ratchet, then Clank, then the fallback moby', () => {
  const camera = new THREE.PerspectiveCamera();
  const controls = { target: new THREE.Vector3(), update() {} } as unknown as OrbitControls;
  const fallback = entity('fallback');
  fallback.sourceClassId = 0x1c31;
  fallback.transform.position = { x: 100, y: 200, z: 300 };
  const clank = entity('clank');
  clank.sourceClassId = 0x0057;
  clank.transform.position = { x: 10, y: 20, z: 30 };
  const ratchet = entity('ratchet');
  ratchet.sourceClassId = 0x0000;
  ratchet.transform.position = { x: 1, y: 2, z: 3 };
  const rotation = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 0, 1), Math.PI / 2);
  ratchet.transform.rotation = { x: rotation.x, y: rotation.y, z: rotation.z, w: rotation.w };

  assert.equal(positionCameraAtPreferredMoby(camera, controls, [fallback, clank, ratchet]), true);
  const expectedPosition = new THREE.Vector3();
  const expectedRotation = new THREE.Quaternion();
  projectTransformToSceneMatrix(ratchet.transform).decompose(
    expectedPosition, expectedRotation, new THREE.Vector3(),
  );
  expectedPosition.y += 2;
  expectedRotation.premultiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), -Math.PI / 2));
  assert.ok(camera.position.distanceTo(expectedPosition) < 1e-6);
  assert.ok(Math.abs(camera.quaternion.dot(expectedRotation)) > 0.99999);
  assert.equal(positionCameraAtPreferredMoby(camera, controls, [entity('none')]), false);
});
