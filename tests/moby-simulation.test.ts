import assert from 'node:assert/strict';
import test from 'node:test';
import * as THREE from 'three';

import type {
  MobySimulation,
  MobySimulationDiagnostic,
  MobySimulationEntitySnapshot,
  MobySimulationProjectSnapshot,
  MobySimulationRendererServices,
} from '../src/types/MobySimulation.js';
import { MobySimulationController } from '../src/renderer/simulation/MobySimulationController.ts';
import { MobySimulationRegistry } from '../src/renderer/simulation/MobySimulationRegistry.ts';
import {
  buildMobySimulationSnapshot,
  diffMobySimulationSnapshots,
  resolveMobySimulationFxAssetId,
} from '../src/renderer/simulation/MobySimulationSnapshot.ts';
import {
  createUyaOceanSimulation,
  parseUyaOceanConfig,
  uyaOceanFxBankIndex,
} from '../src/renderer/simulation/uya/UyaOceanSimulation.ts';
import {
  createUyaOceanGeometry,
  UYA_OCEAN_PATCH_TRIANGLES,
} from '../src/renderer/simulation/uya/UyaOceanGeometry.ts';
import { createUyaOceanMaterial } from '../src/renderer/simulation/uya/UyaOceanMaterial.ts';

function snapshot(projectId = 'project-a', revision = 1): MobySimulationProjectSnapshot {
  return {
    projectId,
    revision,
    game: 'UYA',
    entities: [0x0b37, 0x1234].map((classId) => ({
      id: `entity-${classId}`,
      classId,
      transform: {
        position: { x: 0, y: 0, z: 0 },
        rotation: { x: 0, y: 0, z: 0, w: 1 },
        scale: { x: 1, y: 1, z: 1 },
      },
      pvarFields: [],
      references: [],
      hidden: false,
      disabled: false,
    })),
    referenceTargets: [],
  };
}

function createServices() {
  const proxyChanges: [string, boolean][] = [];
  const diagnostics: MobySimulationDiagnostic[] = [];
  let bindings = 0;
  const services: MobySimulationRendererServices = {
    root: {} as MobySimulationRendererServices['root'],
    camera: {} as MobySimulationRendererServices['camera'],
    async acquireAsset() { return undefined; },
    async acquireFxTexture() { return undefined; },
    setProxyVisible(entityId, visible) { proxyChanges.push([entityId, visible]); },
    bindSelection() {
      bindings += 1;
      return () => { bindings -= 1; };
    },
    reportDiagnostic(diagnostic) { diagnostics.push(diagnostic); },
  };
  return { services, proxyChanges, diagnostics, get bindings() { return bindings; } };
}

function mockSimulation(entityId: string, events: string[], recreate = false): MobySimulation {
  return {
    entityIds: [entityId],
    selectionObjects: [{ object: {} as NonNullable<MobySimulation['selectionObjects']>[number]['object'], entityId }],
    enable() { events.push('enable'); },
    fixedUpdate(update) { events.push(`fixed:${update.tick}:${update.timeSeconds.toFixed(1)}`); },
    renderUpdate(update) { events.push(`render:${update.timeSeconds.toFixed(1)}`); },
    reset() { events.push('reset'); },
    disable() { events.push('disable'); },
    invalidate() { events.push('invalidate'); return recreate ? 'recreate' : 'handled'; },
    dispose() { events.push('dispose'); },
  };
}

test('controller allocates only when enabled and advances with bounded fixed steps', async () => {
  const registry = new MobySimulationRegistry();
  const runtime = createServices();
  const events: string[] = [];
  let created = 0;
  registry.register('uya', 0x0b37, ({ entities }) => {
    created += 1;
    return mockSimulation(entities[0].id, events);
  });
  const controller = new MobySimulationController(registry, runtime.services, {
    fixedStepSeconds: 0.1,
    maximumCatchUpSteps: 3,
  });

  await controller.setSnapshot(snapshot());
  controller.advance(1);
  assert.equal(created, 0);
  assert.equal(controller.getStats().activeSimulators, 0);

  await controller.setEnabled(true);
  assert.equal(created, 1);
  assert.deepEqual(events, ['enable']);
  assert.deepEqual(runtime.proxyChanges, [['entity-2871', false]]);
  assert.equal(runtime.bindings, 1);

  controller.advance(0.35);
  assert.deepEqual(events.slice(1), [
    'fixed:1:0.1', 'fixed:2:0.2', 'fixed:3:0.3', 'render:0.3',
  ]);
  assert.equal(controller.getStats().fixedTicks, 3);
  assert.ok(Math.abs(controller.getStats().droppedSeconds - 0.05) < 1e-9);

  controller.setPaused(true);
  controller.advance(1);
  controller.setPaused(false);
  controller.reset();
  assert.deepEqual(events.slice(-1), ['reset']);

  await controller.setEnabled(false);
  assert.deepEqual(events.slice(-2), ['disable', 'dispose']);
  assert.equal(controller.getStats().activeSimulators, 0);
  assert.equal(runtime.bindings, 0);
  assert.deepEqual(runtime.proxyChanges.at(-1), ['entity-2871', true]);
});

test('one failed simulator restores its proxy without stopping other classes', async () => {
  const registry = new MobySimulationRegistry();
  const runtime = createServices();
  registry.register('uya', 0x0b37, ({ entities }) => ({
    ...mockSimulation(entities[0].id, []),
    fixedUpdate() { throw new Error('bad water data'); },
  }));
  const healthyEvents: string[] = [];
  registry.register('uya', 0x1234, ({ entities }) => mockSimulation(entities[0].id, healthyEvents));
  const controller = new MobySimulationController(registry, runtime.services, {
    fixedStepSeconds: 0.1,
  });

  await controller.setEnabled(true, snapshot());
  controller.advance(0.1);

  assert.equal(controller.getStats().activeSimulators, 1);
  assert.deepEqual(healthyEvents.slice(-2), ['fixed:1:0.1', 'render:0.1']);
  assert.deepEqual(runtime.proxyChanges.filter(([id]) => id === 'entity-2871'), [
    ['entity-2871', false], ['entity-2871', true],
  ]);
  assert.match(runtime.diagnostics[0].message, /bad water data.*Ordinary proxies remain visible/);
});

test('invalidation, context loss, and project switches recreate and release resources', async () => {
  const registry = new MobySimulationRegistry();
  const runtime = createServices();
  const instances: string[][] = [];
  registry.register('uya', 0x0b37, ({ entities }) => {
    const events: string[] = [];
    instances.push(events);
    return mockSimulation(entities[0].id, events, true);
  });
  const controller = new MobySimulationController(registry, runtime.services);
  await controller.setEnabled(true, snapshot());

  await controller.invalidate(snapshot('project-a', 2), {
    kind: 'pvar', entityIds: ['entity-2871'],
  });
  assert.equal(instances.length, 2);
  assert.deepEqual(instances[0].slice(-3), ['invalidate', 'disable', 'dispose']);
  assert.equal(controller.getStats().hiddenProxies, 1);
  assert.equal(controller.getStats().selectionBindings, 1);

  controller.contextLost();
  assert.equal(controller.getStats().activeSimulators, 0);
  assert.equal(runtime.bindings, 0);
  await controller.contextRestored();
  assert.equal(instances.length, 3);

  await controller.setSnapshot(snapshot('project-b'));
  assert.equal(instances.length, 4);
  assert.equal(controller.getStats().activeSimulators, 1);
  controller.dispose();
  assert.equal(controller.getStats().activeSimulators, 0);
  assert.throws(() => controller.advance(0.1), /disposed/);
});

test('registry keys factories by normalized game and class without shared conditionals', () => {
  const registry = new MobySimulationRegistry();
  const factory = () => null;
  const unregister = registry.register(' UYA ', 0x0b37, factory);
  assert.equal(registry.resolve('uya', 0x0b37), factory);
  assert.throws(() => registry.register('UYA', 0x0b37, factory), /already registered/);
  unregister();
  assert.equal(registry.resolve('uya', 0x0b37), undefined);
});

test('late asynchronous creation is disposed after simulations are disabled', async () => {
  const registry = new MobySimulationRegistry();
  const runtime = createServices();
  const events: string[] = [];
  let finishCreate!: (simulation: MobySimulation) => void;
  registry.register('uya', 0x0b37, () => new Promise((resolve) => { finishCreate = resolve; }));
  const controller = new MobySimulationController(registry, runtime.services);

  const enabling = controller.setEnabled(true, snapshot());
  await Promise.resolve();
  await controller.setEnabled(false);
  finishCreate(mockSimulation('entity-2871', events));
  await enabling;

  assert.deepEqual(events, ['disable', 'dispose']);
  assert.equal(controller.getStats().activeSimulators, 0);
  assert.deepEqual(runtime.proxyChanges, []);
});

test('snapshot projection copies Moby inputs and reports narrow invalidations', () => {
  const rawData = new Uint8Array([1, 2, 3]);
  const entity = {
    id: 'water', name: 'Water', layer: 'mobys', sourceClassId: 0x0b37,
    transform: {
      position: { x: 1, y: 2, z: 3 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 },
    },
    mobyPVar: { rawData }, transformModes: [],
    state: { dirty: false, hidden: false, disabled: false, locked: false, readOnly: false, invalid: false, missingAsset: false },
  } as unknown as Parameters<typeof buildMobySimulationSnapshot>[3][number];
  const unsupported = { ...entity, id: 'ordinary', sourceClassId: 0x1234 };
  const cuboid = {
    ...entity, id: 'cuboid', layer: 'cuboids', sourceClassId: undefined, geometry: { kind: 'cuboid' },
  } as unknown as Parameters<typeof buildMobySimulationSnapshot>[3][number];
  const unrelated = {
    ...cuboid, id: 'unrelated',
  } as unknown as Parameters<typeof buildMobySimulationSnapshot>[3][number];
  const reference = {
    ownerEntityId: 'water', fieldKey: 'water-volume', nullable: false, missing: false,
    domain: 'entity', targetKind: 'cuboid', targetEntityId: 'cuboid',
  } as Parameters<typeof buildMobySimulationSnapshot>[4][number];
  const inputs = [entity, unsupported, cuboid, unrelated];
  const first = buildMobySimulationSnapshot(
    'project', 1, 'UYA', inputs, [reference], (classId) => classId === 0x0b37,
  );
  rawData[0] = 9;
  entity.transform.position.x = 4;
  assert.deepEqual(first.entities.map((value) => value.id), ['water']);
  assert.deepEqual(first.referenceTargets.map((value) => value.id), ['cuboid']);
  assert.equal(first.entities[0].pvar?.[0], 1);
  assert.equal(first.entities[0].transform.position.x, 1);

  const second = buildMobySimulationSnapshot(
    'project', 2, 'UYA', inputs, [reference], (classId) => classId === 0x0b37,
  );
  assert.deepEqual(diffMobySimulationSnapshots(first, second).map((value) => value.kind), ['transform', 'pvar']);
  assert.equal(resolveMobySimulationFxAssetId({
    canRead: true, isDirty: true, canReplace: true, canAppend: true, maximumTextureCount: 10,
    sourceTextures: [{
      sourceIndex: 3, label: 'FX 3', width: 8, height: 8, paletteOffset: 0, pixelOffset: 0,
      isSwizzled: false, effectiveTexture: { id: 'source', kind: 'Texture' },
    }],
    additions: [{ width: 8, height: 8, texture: { id: 'addition', kind: 'Texture' } }],
  }, 4), 'addition');
});

test('one class simulator receives 100 instances and one fixed update', async () => {
  const registry = new MobySimulationRegistry();
  const runtime = createServices();
  let received = 0;
  let fixedUpdates = 0;
  registry.register('uya', 0x0b37, ({ entities }) => {
    received = entities.length;
    return {
      ...mockSimulation(entities[0].id, []),
      entityIds: entities.map((entity) => entity.id),
      selectionObjects: [],
      fixedUpdate() { fixedUpdates += 1; },
    };
  });
  const base = snapshot();
  const many: MobySimulationProjectSnapshot = {
    ...base,
    entities: Array.from({ length: 100 }, (_, index) => ({
      ...base.entities[0], id: `water-${index}`,
    })),
  };
  const controller = new MobySimulationController(registry, runtime.services, { fixedStepSeconds: 0.1 });
  await controller.setEnabled(true, many);
  controller.advance(0.1);
  assert.equal(received, 100);
  assert.equal(fixedUpdates, 1);
  assert.equal(controller.getStats().simulatedEntities, 100);
  controller.dispose();
  assert.equal(runtime.bindings, 0);
});

test('UYA ocean reads the named MobyDex contract and keeps geometry bounded', () => {
  const config = parseUyaOceanConfig(waterEntity());
  assert.equal(config.overlayFxId, 7);
  assert.equal(config.underlayFxId, -1);
  assert.equal(config.overlayAdditive, true);
  assert.equal(config.overlayTiling, 32);
  assert.equal(config.height, 10);
  assert.equal(config.waves.length, 8);
  assert.deepEqual(config.hiddenCuboidIds, ['hide-water']);
  assert.equal(uyaOceanFxBankIndex(config.overlayFxId), 0x64 + 7);
  assert.ok((config.fog?.color.r ?? 1) < 20 / 255);

  const geometry = createUyaOceanGeometry();
  assert.equal(geometry.index!.count / 3, UYA_OCEAN_PATCH_TRIANGLES);
  assert.ok(geometry.getAttribute('position').count < 4_000);
  geometry.dispose();
});

test('UYA ocean framebuffer copy matches the composer half-float target', () => {
  const state = createUyaOceanMaterial({
    name: 'test-water',
    color: new THREE.Color(1, 1, 1),
    opacity: 1,
    additive: false,
    depthWrite: false,
    underlayProjection: false,
    textureRepeat: 1,
    waves: [],
    shimmerScale: 0,
  });
  const destination = state.material.uniforms.destinationTexture.value as THREE.FramebufferTexture;
  assert.equal(destination.type, THREE.HalfFloatType);
  assert.match(state.material.fragmentShader, /vec2 waterSlopeAtFragment\(\)/);
  assert.match(state.material.fragmentShader, /underlayStqUv\(waterSlope\)/);
  assert.match(state.material.fragmentShader, /float q = \(length\(projected\) \+ projected\.z\) \* 2\.0/);
  assert.doesNotMatch(state.material.fragmentShader, /underlayQuadrantUv/);
  assert.doesNotMatch(state.material.vertexShader, /vUnderlayUv/);
  state.dispose();
});

test('UYA ocean follows hidden cuboid references and disposes derived resources', async () => {
  const root = new THREE.Group();
  const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 2_000);
  camera.position.set(0, 10, 0);
  camera.lookAt(0, 0, -10);
  camera.updateMatrixWorld(true);
  const diagnostics: MobySimulationDiagnostic[] = [];
  const sourceEntity = waterEntity();
  const entity: MobySimulationEntitySnapshot = {
    ...sourceEntity,
    pvarFields: sourceEntity.pvarFields.map((field) => field.path === 'overlayColor'
      ? { ...field, value: { kind: 'color', value: [100, 120, 140, 0] } }
      : field),
  };
  const project = snapshot();
  const simulation = await createUyaOceanSimulation({
    snapshot: {
      ...project,
      entities: [entity],
      referenceTargets: [{
        id: 'hide-water', kind: 'cuboid', hidden: false, disabled: false,
        transform: {
          position: { x: 0, y: 0, z: 10 },
          rotation: { x: 0, y: 0, z: 0, w: 1 },
          scale: { x: 20, y: 20, z: 20 },
        },
      }],
    },
    entities: [entity],
    services: {
      root,
      camera,
      async acquireAsset() { return undefined; },
      async acquireFxTexture() { return undefined; },
      setProxyVisible() {},
      bindSelection() { return () => {}; },
      reportDiagnostic(value) { diagnostics.push(value); },
    },
  });
  assert.ok(simulation);
  simulation.enable();
  simulation.fixedUpdate({ stepSeconds: 1 / 60, tick: 1, timeSeconds: 1 / 60 });
  simulation.renderUpdate({ timeSeconds: 1 / 60, interpolationAlpha: 0 });
  assert.equal((root.children[0] as THREE.Group).children[0].visible, false);

  camera.position.x = 100;
  camera.updateMatrixWorld(true);
  simulation.renderUpdate({ timeSeconds: 1 / 60, interpolationAlpha: 0 });
  assert.equal((root.children[0] as THREE.Group).children[0].visible, true);
  simulation.dispose();
  assert.equal(root.children.length, 0);
  assert.deepEqual(diagnostics, []);
});

function waterEntity(): MobySimulationEntitySnapshot {
  const number = (path: string, value: number, integer = false) => ({
    path, invalid: false,
    value: integer ? { kind: 'integer' as const, value: String(value) } : { kind: 'float' as const, value },
  });
  const color = (path: string, value: number[]) => ({
    path, invalid: false, value: { kind: 'color' as const, value },
  });
  return {
    id: 'water',
    classId: 0x0b37,
    transform: {
      position: { x: 0, y: 0, z: 0 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
    pvarFields: [
      number('overlayFxTexture', 7, true), number('underlayFxTexture', -1, true),
      number('waveSpeed', 1), number('waveCrest', 0.2), number('waveSurge', 40),
      number('waveRippleSize', 8), number('waveDirection', 25), number('waveDirectionVariation', 0.2),
      number('waveShimmerIntensity', 0.5), number('overlayTiling', 32), number('overlayDirection', 90),
      number('overlaySpeed', 2), color('underlayColor', [20, 40, 80, 128]),
      color('overlayColor', [100, 120, 140, 64]),
      { path: 'overlayAdditive', invalid: false, value: { kind: 'boolean', value: true } },
      color('fogColor', [20, 30, 40]), number('fogIntensityNear', 10, true),
      number('fogIntensityFar', 50, true), number('fogNearDistance', 10), number('fogFarDistance', 500),
      number('waterShimmerThreshold', 0.4), number('zPosition', 10),
      { path: 'hideWhenCameraInCuboid0', invalid: false, targetEntityId: 'hide-water',
        value: { kind: 'reference' } },
      { path: 'hideWhenCameraInCuboid1', invalid: false, value: { kind: 'reference' } },
    ],
    references: [],
    hidden: false,
    disabled: false,
  };
}
