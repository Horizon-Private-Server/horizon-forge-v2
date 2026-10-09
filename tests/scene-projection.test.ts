import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';
import { Line2 } from 'three/addons/lines/Line2.js';
import { LineMaterial } from 'three/addons/lines/LineMaterial.js';
import { LineSegments2 } from 'three/addons/lines/LineSegments2.js';

import { SceneProjection } from '../src/renderer/editor/SceneProjection.ts';
import { SceneBillboardResources } from '../src/renderer/editor/SceneBillboardGizmo.ts';
import { verticalDragScale } from '../src/renderer/editor/TransformTool.ts';
import { AssetPreviewScheduler, collectSkyShellObjects } from '../src/renderer/editor/AssetThumbnailRuntime.ts';
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

test('vertical gizmo drags provide stable scale control', () => {
  assert.equal(verticalDragScale(1, 200, null), 2);
  assert.equal(verticalDragScale(1, -200, null), 0.5);
  assert.equal(verticalDragScale(1, 30, 0.1), 1.1);
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

  first.setState(true, false);
  assert.notEqual(first.material, second.material);
  assert.deepEqual(resources.counts(), { geometries: 1, materials: 2, textures: 2 });
  first.configure({
    ...options,
    glyph: 'ship',
    depthPolicy: 'overlay',
    selected: true,
  });
  assert.equal(first.material.depthTest, false);
  assert.equal(first.renderOrder, 1_000);
  assert.deepEqual(resources.counts(), { geometries: 1, materials: 2, textures: 2 });
  first.dispose();
  second.dispose();
  assert.deepEqual(resources.counts(), { geometries: 1, materials: 0, textures: 0 });
  resources.dispose();
  assert.deepEqual(resources.counts(), { geometries: 0, materials: 0, textures: 0 });
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
  const areaObject = projection.getObject('area') as THREE.Mesh;
  assert.ok(areaObject instanceof THREE.Mesh);
  assert.equal((areaObject.material as THREE.MeshBasicMaterial).color.getHexString(), '778899');
  assert.equal((areaObject.material as THREE.MeshBasicMaterial).fog, false);
  assert.deepEqual(areaObject.position.toArray(), [40, 60, -50]);
  assert.equal(projection.resolvePick([{ object: line }] as unknown as THREE.Intersection[]), 'spline');
  const directionalObject = projection.getObject('directional-light') as THREE.Line;
  assert.equal((directionalObject.material as THREE.LineBasicMaterial).color.getHexString(), '123456');
  assert.deepEqual(Array.from(directionalObject.geometry.getAttribute('position').array), [0, 0, 0, 1, 3, -2]);
  assert.equal(((projection.getObject('point-light') as THREE.Mesh).material as THREE.MeshBasicMaterial)
    .color.getHexString(), '654321');
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
  assert.deepEqual(Array.from(selectedSplineNodes.geometry.getAttribute('position').array), [5, 7, -6]);
  projection.sync(entities, ['area']);
  assert.equal((areaObject.material as THREE.MeshBasicMaterial).fog, false);
  projection.root.updateMatrixWorld(true);
  const insideCamera = new THREE.PerspectiveCamera(60, 1, 0.1, 100);
  insideCamera.position.copy(areaObject.position);
  insideCamera.lookAt(areaObject.position.clone().add(new THREE.Vector3(1, 0, 0)));
  insideCamera.updateMatrixWorld();
  const insideArea = new THREE.Raycaster();
  insideArea.setFromCamera(new THREE.Vector2(), insideCamera);
  assert.equal(projection.resolvePick(insideArea.intersectObject(areaObject)), 'area');
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
  assert.equal(selectionOutlines.length, 1);
  assert.equal(selectionOutlines[0].children.length, 2);
  assert.equal(mesh.geometry.getAttribute('position').count, geometry.getAttribute('position').count * 2);
  assert.equal(projection.resolvePick([{ object: mesh, instanceId: 1 }] as unknown as THREE.Intersection[]), 'b');
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 10) < 1e-6);
  projection.sync([entity('a'), entity('b', 10)], [], new Set(), false);
  assert.equal(mesh.count, 0);
  assert.equal(projection.resolvePick([{ object: mesh, instanceId: 0 }] as unknown as THREE.Intersection[]), undefined);

  projection.dispose();
  disposeObject(template);
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
  const outlines = projection.getSelectionOutlineObjects();
  assert.equal(meshes.length, 1);
  assert.equal(meshes[0].instanceCount, 2);
  assert.equal(meshes[0].getColorAt(1, new THREE.Color()).getHexString(), '22d3ee');
  assert.equal(outlines.length, 1);
  const outlineMesh = outlines[0].children[0] as THREE.Mesh;
  assert.equal(outlineMesh.material instanceof THREE.MeshBasicMaterial, true);
  assert.equal((outlineMesh.material as THREE.MeshBasicMaterial).colorWrite, false);
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

test('recovered collision follows its attached TIE instance', () => {
  const tie = entity('tie', 10);
  tie.asset = { id: 'tie-asset', kind: 'Tie' };
  const collision = entity('collision');
  collision.transform = {
    position: { x: 0, y: 0, z: 0 },
    rotation: { x: 0, y: 0, z: 0, w: 1 },
    scale: { x: 1, y: 1, z: 1 },
  };
  collision.asset = { id: 'collision-asset', kind: 'collision' };
  collision.collision = {
    kind: 'solid', sourcePayloadIndex: 0, sourcePieceIndex: 0,
    faceCount: 1, vertexCount: 3, types: [],
    attachment: { parentEntityId: tie.id, bindTransform: structuredClone(tie.transform) },
  };
  collision.transformModes = ['translate'];
  const template = new THREE.Mesh(new THREE.BoxGeometry(), new THREE.MeshBasicMaterial());
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['collision-asset:solid:0', template]]));
  projection.sync([tie, collision]);
  assert.ok(Math.abs(projection.getBounds(collision.id)!.getCenter(new THREE.Vector3()).x) < 1e-6);

  tie.transform = { ...tie.transform, position: { ...tie.transform.position, x: 15 } };
  projection.sync([tie, collision]);
  assert.ok(Math.abs(projection.getBounds(collision.id)!.getCenter(new THREE.Vector3()).x - 5) < 1e-6);

  tie.state = { ...tie.state, disabled: true };
  projection.sync([tie, collision]);
  const batch: THREE.BatchedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.BatchedMesh) batch.push(object); });
  assert.equal(batch[0].getVisibleAt(0), false);
  projection.dispose();
  disposeObject(template);
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
  pieces[1] = {
    ...pieces[1],
    transform: { ...pieces[1].transform, position: { x: 99, y: 2, z: 3 } },
  };
  projection.sync(pieces, ['collision-1']);
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
  const material = new THREE.MeshBasicMaterial({ transparent: true });
  material.name = 'player_barrier';
  const root = new THREE.Mesh(new THREE.BoxGeometry(), material);
  configureCollisionMaterials(root, DEFAULT_UYA_COLLISION_VISUALIZATION);
  const compile = material.onBeforeCompile;
  configureCollisionMaterials(root, DEFAULT_UYA_COLLISION_VISUALIZATION);
  assert.equal(material.onBeforeCompile, compile);
  assert.equal(material.depthWrite, false);
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
  const material = new THREE.MeshBasicMaterial();
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
  assert.match(shader.fragmentShader, /mix\(forgeSurfaceColor, forgeSelectionColor, forgeSelected\).*forgeLight/s);
  assert.equal(material.vertexColors, false);
  assert.doesNotMatch(shader.fragmentShader, /gl_FragCoord/);
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
  assert.ok(projection.getSelectionOutlineObjects()[0].matrix.determinant() < 0);
  projection.dispose();
  disposeObject(template);
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
