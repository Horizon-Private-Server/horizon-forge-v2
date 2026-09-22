import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import { SceneProjection } from '../src/renderer/editor/SceneProjection.ts';
import { buildGroundPlacement } from '../src/renderer/editor/ScenePlacement.ts';
import { resolvePointerSnapTarget, VertexSnapIndex } from '../src/renderer/editor/SceneSnapping.ts';
import type { EditorEntity } from '../src/types/EditorRuntime.js';
import {
  applySceneEnvironment,
  configureSkybox,
  disposeObject,
  framePs2Positions,
  positionCameraAtPreferredMoby,
  rotateCamera,
  updateCameraFlight,
  updateCameraMovement,
} from '../src/utils/Scene.ts';
import {
  configurePs2MaterialAlpha,
  configurePs2MaterialFog,
  createPs2OpaquePassMaterial,
} from '../src/utils/Ps2Materials.ts';
import { projectTransformToSceneMatrix, sceneMatrixToProjectTransform } from '../src/utils/Transforms.ts';
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
    state: {
      dirty: false, hidden: false, disabled: false, locked: false, invalid: false, missingAsset: false, ...state,
    },
  };
}

test('scene projection diffs entities by ID and updates transforms in place', () => {
  const projection = new SceneProjection();
  assert.deepEqual(projection.sync([entity('a'), entity('b', 10, false)]), {
    created: 2, updated: 0, removed: 0,
  });

  const first = projection.getObject('a') as THREE.Mesh;
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

test('scene projection merges matching parts, instances assets, and resolves instance picks', () => {
  const template = new THREE.Group();
  const geometry = new THREE.BoxGeometry();
  const material = new THREE.MeshBasicMaterial();
  const billboard = new THREE.Mesh(geometry, material);
  billboard.name = 'shrub_billboard';
  template.add(new THREE.Mesh(geometry, material), new THREE.SkinnedMesh(geometry, material), billboard);
  const projection = new SceneProjection();
  projection.setAssetTemplates(new Map([['asset', template]]));
  projection.sync([entity('a'), entity('b', 10)], ['b']);

  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  assert.equal(meshes.length, 1);
  const mesh = meshes[0];
  assert.equal(mesh.count, 2);
  assert.equal(mesh.geometry.getAttribute('position').count, geometry.getAttribute('position').count * 2);
  assert.equal(projection.resolvePick([{ object: mesh, instanceId: 1 }] as unknown as THREE.Intersection[]), 'b');
  assert.ok(Math.abs(projection.getBounds('b')!.getCenter(new THREE.Vector3()).x - 10) < 1e-6);
  projection.sync([entity('a'), entity('b', 10)], [], new Set(), false);
  assert.equal(mesh.count, 0);
  assert.equal(projection.resolvePick([{ object: mesh, instanceId: 0 }] as unknown as THREE.Intersection[]), undefined);

  projection.dispose();
  disposeObject(template);
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
  sky.name = 'skybox_shell_00';
  const configured = configureSkybox(sky);
  assert.equal(sky.renderOrder, -997);
  assert.deepEqual(configured.pieces, ['skybox_shell_00']);
  assert.equal(material.depthTest, false);
  assert.equal(material.depthWrite, false);
  assert.equal(material.fog, false);
  assert.equal(material.blending, THREE.AdditiveBlending);
  disposeObject(sky);
});

test('PS2 fog clamps to the game far intensity instead of increasing to full fog', () => {
  const material = new THREE.MeshBasicMaterial();
  const root = new THREE.Mesh(new THREE.BoxGeometry(), material);
  configurePs2MaterialFog(root, {
    backgroundColor: [0, 0, 0], fogColor: [40, 50, 40],
    fogNearDistance: 10, fogFarDistance: 175, fogNearIntensity: 255, fogFarIntensity: 128,
  });
  const shader = { fragmentShader: '#include <fog_fragment>' };
  material.onBeforeCompile(shader as never, {} as never);
  assert.doesNotMatch(shader.fragmentShader, /#include <fog_fragment>/);
  assert.match(shader.fragmentShader, /vFogDepth - 9\.00000000/);
  assert.match(shader.fragmentShader, /mix\(0\.00390625, 0\.50000000/);
  disposeObject(root);
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
  projection.sync([entity('normal'), mirrored]);
  const meshes: THREE.InstancedMesh[] = [];
  projection.root.traverse((object) => { if (object instanceof THREE.InstancedMesh) meshes.push(object); });
  assert.equal(meshes.length, 2);
  const mirroredMesh = meshes.find((mesh) => mesh.matrix.determinant() < 0)!;
  const instanceMatrix = new THREE.Matrix4();
  mirroredMesh.getMatrixAt(0, instanceMatrix);
  assert.ok(instanceMatrix.determinant() > 0);
  assert.ok(mirroredMesh.matrix.clone().multiply(instanceMatrix).determinant() < 0);
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
