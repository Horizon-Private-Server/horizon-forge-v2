import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import type { EditorEntity } from '../src/types/EditorRuntime.js';
import { configureTfragLighting } from '../src/renderer/editor/TfragLighting.ts';
import { createSelectionOverrideMaterial } from '../src/utils/SelectionMaterials.ts';

function light(slot: number, color: [number, number, number]): EditorEntity {
  return {
    id: `light-${slot}`, name: 'Light', layer: 'lighting',
    provenance: { game: 'UYA', level: 14, section: 'directional_lights', sourceIndex: slot },
    transform: {
      position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 },
    },
    directionalLight: {
      topColor: { x: color[0], y: color[1], z: color[2], w: 0 },
      topDirection: { x: 0, y: 0, z: -1, w: 0 },
      inverseColor: { x: 0, y: 0, z: 0, w: 0 },
      inverseDirection: { x: 0, y: 0, z: 1, w: 0 },
    },
    transformModes: ['rotate'],
    state: { dirty: false, hidden: false, disabled: false, locked: false, readOnly: false, invalid: false, missingAsset: false },
  };
}

function tfrag(selector = 0, postScale = 1): THREE.Mesh<THREE.BufferGeometry, THREE.MeshBasicMaterial> {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([0, 0, 0], 3));
  geometry.setAttribute('color', new THREE.Float32BufferAttribute([0, 0, 0, 1], 4));
  geometry.setAttribute('_dl_light_selector', new THREE.Float32BufferAttribute([selector], 1));
  geometry.setAttribute('_dl_light_base_color', new THREE.Uint8BufferAttribute([0, 0, 0, 255], 4, true));
  geometry.setAttribute('_dl_light_normal', new THREE.Float32BufferAttribute([0, -1, 0], 3));
  geometry.setAttribute('_dl_light_post_scale', new THREE.Float32BufferAttribute([postScale], 1));
  return new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ vertexColors: true, map: new THREE.Texture() }));
}

function color(mesh: THREE.Mesh): number[] {
  const attribute = mesh.geometry.getAttribute('color');
  return [attribute.getX(0), attribute.getY(0), attribute.getZ(0)];
}

test('tfrags reconstruct black cached vertices from directional lighting in scene coordinates', () => {
  const mesh = tfrag();
  configureTfragLighting(mesh, [light(0, [0.5, 0.25, 1])]);
  assert.deepEqual(color(mesh), [0.5, 0.25, 1]);
  assert.equal(mesh.material.map!.colorSpace, THREE.NoColorSpace);
  const attribute = mesh.geometry.getAttribute('color');
  configureTfragLighting(mesh, [light(0, [0.25, 0.5, 0])]);
  assert.deepEqual(color(mesh), [0.25, 0.5, 0]);
  assert.equal(mesh.geometry.getAttribute('color'), attribute);
});

test('tfrag lighting blends source slots, expands RGB5, and quantizes post-scaled bytes', () => {
  const mesh = tfrag(0x8030, 0.5);
  mesh.geometry.setAttribute('_dl_light_base_color', new THREE.Float32BufferAttribute([1 / 31, 0, 0, 1], 4));
  configureTfragLighting(mesh, [light(3, [0, 0.5, 0]), light(0, [0.5, 0, 0])]);
  assert.deepEqual(color(mesh), [0.15625, 0.125, 0]);
});

test('missing tfrag light slots preserve cached colors without compounding post-scaling', () => {
  const mesh = tfrag(15, 0.5);
  mesh.geometry.setAttribute('color', new THREE.Float32BufferAttribute([0.5, 1, 0.25, 0.75], 4));
  configureTfragLighting(mesh, [light(0, [1, 1, 1])]);
  assert.deepEqual(color(mesh), [0.25, 0.5, 0.125]);
  configureTfragLighting(mesh, []);
  assert.deepEqual(color(mesh), [0.25, 0.5, 0.125]);
});

test('tfrag lighting applies entity rotation and signed light alpha', () => {
  const mesh = tfrag();
  const source = light(0, [0.5, 0.25, 1]);
  source.transform.rotation = { x: 1, y: 0, z: 0, w: 0 };
  source.directionalLight!.topColor.w = -0.5;
  configureTfragLighting(mesh, [source]);
  assert.deepEqual(color(mesh), [0.25, 0.125, 0.5]);
});

test('batched tfrags retain the original cached fallback when lights change', () => {
  const mesh = tfrag(15, 0.5);
  mesh.geometry.setAttribute('color', new THREE.Float32BufferAttribute([0.5, 1, 0.25, 0], 4));
  configureTfragLighting(mesh, []);
  const material = createSelectionOverrideMaterial(mesh.material, new THREE.Color('cyan'));
  const batch = new THREE.BatchedMesh(1, 1, 0, material);
  batch.addInstance(batch.addGeometry(mesh.geometry));
  const key = material.customProgramCacheKey();
  configureTfragLighting(batch, [light(0, [1, 1, 1])]);
  assert.deepEqual(color(batch), [0.25, 0.5, 0.125]);
  assert.equal(material.customProgramCacheKey(), key);
  batch.dispose();
  material.dispose();
});

test('tfrag byte modulation runs before sRGB decoding and survives selection material cloning', () => {
  const mesh = tfrag();
  configureTfragLighting(mesh, [light(0, [1, 1, 1])]);
  const material = createSelectionOverrideMaterial(mesh.material, new THREE.Color('cyan'));
  const shader = {
    uniforms: {}, vertexShader: THREE.ShaderLib.basic.vertexShader, fragmentShader: THREE.ShaderLib.basic.fragmentShader,
  };
  material.onBeforeCompile(shader as Parameters<THREE.Material['onBeforeCompile']>[0], {} as THREE.WebGLRenderer);
  assert.match(shader.fragmentShader, /floor\(clamp\(diffuseColor.rgb/);
  assert.match(shader.fragmentShader, /sRGBTransferEOTF/);
  assert.match(material.customProgramCacheKey(), /forge-tfrag-lighting.*forge-selection-override/);
});
