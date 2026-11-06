import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

import {
  HostClient,
  HostOperationError,
} from '../dist-electron/main/bridge/HostClient.js';
import { decodeEditorCommandResult, encodeEditorCommand } from '../dist-electron/main/bridge/EditorPayloadCodec.js';
import { PayloadWriter } from '../dist-electron/main/bridge/PayloadIO.js';
import { BridgeErrorCode } from '../dist-electron/main/bridge/BridgeProtocol.js';
import { applyEditorCommandResult } from '../dist-electron/utils/EditorCommandResult.js';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const hostDll = path.join(projectRoot, 'src/Forge.Host/bin/Release/net10.0/Forge.Host.dll');

test('placement source encoding matches the C# interoperability fixture', async () => {
  const fixture = JSON.parse(await readFile(new URL('./fixtures/editor-asset-source-v6.json', import.meta.url), 'utf8'));
  assert.equal(encodeEditorCommand(fixture.command).toString('hex'), fixture.payloadHex);
});

test('host handshake, echo, progress, cancellation, crash recovery, and concurrency', { timeout: 30_000 }, async (context) => {
  const client = new HostClient('dotnet', [hostDll], 25);
  context.after(() => client.stop());

  const expectedRevision = (await readFile(path.join(projectRoot, 'ratchet-sdk.version'), 'utf8')).trim();
  const handshake = await client.start();
  assert.equal(handshake.sdkRevision, expectedRevision);
  assert.deepEqual(handshake.supportedGames, ['UYA', 'DL', 'GC', 'RC1']);
  assert.ok(handshake.capabilities.includes('dl.assets.import'));
  assert.ok(handshake.capabilities.includes('gc.assets.import'));
  assert.ok(handshake.capabilities.includes('dl.iso.validate'));
  assert.ok(handshake.capabilities.includes('bridge.cancellation'));
  assert.ok(handshake.capabilities.includes('uya.iso.copy'));
  assert.ok(handshake.capabilities.includes('uya.assets.import'));
  assert.ok(handshake.capabilities.includes('uya.bake.tfrags.target-native'));
  assert.ok(handshake.capabilities.includes('uya.bake.ties.target-native'));
  assert.ok(handshake.capabilities.includes('uya.bake.shrubs.target-native'));
  assert.ok(handshake.capabilities.includes('uya.bake.mobys.target-native'));
  assert.ok(handshake.capabilities.includes('uya.bake.gameplay.pvars-pass-through'));
  assert.ok(handshake.capabilities.includes('uya.bake.lighting.target-native'));
  assert.ok(handshake.capabilities.includes('uya.bake.validate'));
  assert.ok(handshake.capabilities.includes('uya.bake.incremental'));
  assert.ok(handshake.capabilities.includes('uya.build.level-wad'));
  assert.ok(handshake.capabilities.includes('uya.build.patch'));
  assert.ok(handshake.capabilities.includes('editor.runtime'));
  assert.ok(handshake.capabilities.includes('assets.explorer.query'));
  assert.ok(handshake.capabilities.includes('uya.render.asset-preview'));
  assert.ok(handshake.capabilities.includes('mobydex.reload'));

  const mobyDex = JSON.parse(await readFile(
    path.join(projectRoot, 'src/Forge.Host/Games/UYA/MobyDex/dataset.json'), 'utf8'));
  mobyDex.id = 'test.uya.reload';
  mobyDex.version = 2;
  const reloaded = await client.reloadMobyDexDataset(Buffer.from(JSON.stringify(mobyDex)));
  assert.equal(await reloaded.result, 'test.uya.reload v2 (75 entries)');
  const wrongGame = structuredClone(mobyDex);
  wrongGame.entries[0].game = 'DL';
  await assert.rejects(
    (await client.reloadMobyDexDataset(Buffer.from(JSON.stringify(wrongGame)))).result,
    (error) => error instanceof HostOperationError && error.code === BridgeErrorCode.InvalidInput,
  );

  const concurrent = await Promise.all([
    client.echo('alpha'),
    client.echo('beta'),
    client.echo('gamma'),
  ]);
  assert.deepEqual(await Promise.all(concurrent.map((request) => request.result)), ['alpha', 'beta', 'gamma']);

  const copyDirectory = await mkdtemp(path.join(tmpdir(), 'forge-host-copy-'));
  context.after(() => rm(copyDirectory, { recursive: true, force: true }));
  const copySource = path.join(copyDirectory, 'source.iso');
  const copyTarget = path.join(copyDirectory, 'target.iso');
  const copyBytes = Buffer.from('synthetic development ISO');
  await writeFile(copySource, copyBytes);
  const copyProgress = [];
  const copy = await client.createDevelopmentIso(
    copySource,
    copyTarget,
    createHash('md5').update(copyBytes).digest('hex'),
    false,
    (value) => copyProgress.push(value),
  );
  assert.equal(await realpath((await copy.result).path), await realpath(copyTarget));
  assert.deepEqual(await readFile(copyTarget), copyBytes);
  assert.ok(copyProgress.length > 0);
  const invalidGame = await client.validateSourceIso('Unknown', copySource);
  await assert.rejects(invalidGame.result, error => error.code === BridgeErrorCode.InvalidInput);
  const gcLevelsPath = path.join(copyDirectory, 'gc-levels.iso');
  const gcLevels = Buffer.alloc(0x1F97F8 + 27 * 0x18);
  for (const tableIndex of [1, 21]) {
    gcLevels.writeInt32LE(0x100, 0x1F97F8 + tableIndex * 0x18);
    gcLevels.writeInt32LE(1, 0x1F97F8 + tableIndex * 0x18 + 4);
  }
  await writeFile(gcLevelsPath, gcLevels);
  assert.deepEqual(await (await client.listSourceLevels('GC', gcLevelsPath)).result, [1, 30]);
  const invalidLevelGame = await client.listSourceLevels('Unknown', gcLevelsPath);
  await assert.rejects(invalidLevelGame.result, error => error.code === BridgeErrorCode.InvalidInput);
  const dlImport = await client.importAssets('DL', copySource, path.join(copyDirectory, 'assets'), '0'.repeat(32), '1.00');
  await assert.rejects(dlImport.result, error => error instanceof HostOperationError && error.code === BridgeErrorCode.Conflict);


  const maintenanceRoot = await mkdtemp(path.join(tmpdir(), 'forge-host-maintenance-'));
  context.after(() => rm(maintenanceRoot, { recursive: true, force: true }));
  const catalogRoot = path.join(maintenanceRoot, 'catalog');
  const projectsRoot = path.join(maintenanceRoot, 'projects');
  await mkdir(projectsRoot);
  const maintenance = await client.previewCatalogGarbageCollection(catalogRoot, [projectsRoot]);
  const preview = await maintenance.result;
  assert.equal(preview.candidateCount, 0);
  const collection = await client.collectCatalogGarbage(catalogRoot, [projectsRoot], preview.confirmationToken);
  assert.equal((await collection.result).candidateCount, 0);

  const editorRoot = await mkdtemp(path.join(tmpdir(), 'forge-host-editor-'));
  context.after(() => rm(editorRoot, { recursive: true, force: true }));
  const editorContent = path.join(editorRoot, 'content');
  await mkdir(editorContent);
  const projectId = '10000000-0000-4000-8000-000000000001';
  const entityId = '20000000-0000-4000-8000-000000000002';
  await writeFile(path.join(editorRoot, 'forge-project.json'), JSON.stringify({
    schemaVersion: 9,
    documentType: 'forge-project',
    projectId,
    name: 'Bridge project',
    target: { game: 'UYA', region: 'NTSC-U', revision: '1.00', bakeProfile: 'uya-ntsc-u' },
    baseLevel: {
      game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 3,
      sourceFingerprint: 'a'.repeat(32), missingAssetCount: 0,
    },
    content: 'content/project.json.gz',
  }));
  await writeFile(path.join(editorContent, 'project.json.gz'), gzipSync(JSON.stringify({
    schemaVersion: 9,
    documentType: 'forge-project-content',
    entities: [{
      entityId,
      name: 'Moby',
      layer: 'mobys',
      transform: {
        position: { x: 1, y: 2, z: 3 },
        rotation: { x: 0, y: 0, z: 0, w: 1 },
        scale: { x: 1, y: 1, z: 1 },
      },
      asset: null,
      provenance: null,
    }],
    assets: [],
    instancedCollisionBindings: [],
  })));
  const opened = await (await client.openEditorProject(editorRoot, catalogRoot, 0)).result;
  let currentSnapshot = opened;
  let lastCommandResult;
  const executeEditorCommand = async (command) => {
    lastCommandResult = await (await client.executeEditorCommand(command)).result;
    currentSnapshot = applyEditorCommandResult(
      currentSnapshot,
      lastCommandResult,
    );
    return currentSnapshot;
  };
  assert.equal(opened.projectId, projectId);
  assert.equal(opened.entities[0].id, entityId);
  const selected = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000001',
    kind: 'setSelection',
    entityIds: [entityId],
  });
  assert.deepEqual(selected.selection, [entityId]);
  assert.equal(lastCommandResult.kind, 'selection');
  assert.match(opened.soundRevision, /^[0-9a-f]{32}$/);
  assert.equal(selected.soundRevision, opened.soundRevision);
  assert.equal(selected.entities, opened.entities);
  const transformed = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000002',
    kind: 'updateTransform',
    entityIds: [entityId],
    transform: {
      position: { x: 10.5, y: 20.25, z: -30.75 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
  });
  assert.deepEqual(transformed.entities[0].transform.position, { x: 10.5, y: 20.25, z: -30.75 });
  assert.equal(lastCommandResult.kind, 'transforms');
  assert.equal(transformed.entities[0].state.dirty, true);
  assert.equal(transformed.canUndo, true);
  const undone = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000007', kind: 'undo', entityIds: [],
  });
  assert.deepEqual(undone.entities[0].transform.position, { x: 1, y: 2, z: 3 });
  assert.equal(undone.canRedo, true);
  const redone = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000008', kind: 'redo', entityIds: [],
  });
  assert.deepEqual(redone.entities[0].transform.position, { x: 10.5, y: 20.25, z: -30.75 });
  const batchTransformed = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000006',
    kind: 'updateTransforms',
    entityIds: [entityId],
    transforms: [{
      entityId,
      transform: {
        position: { x: 11, y: 22, z: 33 },
        rotation: { x: 0, y: 0, z: 0, w: 1 },
        scale: { x: 1, y: 1, z: 1 },
      },
    }],
  });
  assert.deepEqual(batchTransformed.entities[0].transform.position, { x: 11, y: 22, z: 33 });
  const duplicated = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000009', kind: 'duplicateEntities', entityIds: [entityId],
  });
  const duplicateId = duplicated.selection[0];
  assert.notEqual(duplicateId, entityId);
  assert.equal(duplicated.entities.length, 2);
  const deleted = await executeEditorCommand({
    id: '30000000-0000-4000-8000-00000000000a', kind: 'deleteEntities', entityIds: [duplicateId],
  });
  assert.equal(deleted.entities.length, 1);
  const copied = await executeEditorCommand({
    id: '30000000-0000-4000-8000-00000000000b', kind: 'copyEntities', entityIds: [entityId],
  });
  assert.equal(copied.canPaste, true);
  const pasted = await executeEditorCommand({
    id: '30000000-0000-4000-8000-00000000000c', kind: 'pasteEntities', entityIds: [],
  });
  assert.equal(pasted.entities.length, 2);
  assert.notEqual(pasted.selection[0], entityId);
  const updatedEntity = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000004',
    kind: 'renameEntity',
    entityIds: [entityId],
    text: 'Renamed moby',
  });
  assert.equal(updatedEntity.entities[0].name, 'Renamed moby');
  const updatedState = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000005',
    kind: 'setEntityState',
    entityIds: [entityId],
    state: { hidden: true, locked: true },
  });
  assert.equal(updatedState.entities[0].state.hidden, true);
  assert.equal(updatedState.entities[0].state.locked, true);
  const renamed = await executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000003',
    kind: 'renameProject',
    entityIds: [],
    text: 'Renamed over bridge',
  });
  assert.equal(renamed.projectName, 'Renamed over bridge');
  assert.equal(renamed.isDirty, true);
  const events = await (await client.readEditorEvents(0)).result;
  assert.deepEqual(events.map((event) => event.kind), [
    'projectOpened', 'selectionChanged', 'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged',
    'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged',
  ]);
  const withLevelSettings = await executeEditorCommand({
    id: '30000000-0000-4000-8000-00000000000d',
    kind: 'updateLevelSettings',
    entityIds: [],
    levelSettings: {
      backgroundColor: [1, 2, 3],
      fogColor: [4, 5, 6],
      fogNearDistance: 10,
      fogFarDistance: 100,
      fogNearIntensity: 1,
      fogFarIntensity: 2,
    },
  });
  assert.equal(lastCommandResult.kind, 'levelSettings');
  assert.deepEqual(withLevelSettings.levelSettings.backgroundColor, [1, 2, 3]);
  const saved = await (await client.saveEditorProject()).result;
  assert.equal(saved.isDirty, false);
  assert.equal(saved.entities[0].state.dirty, false);
  await (await client.closeEditorProject()).result;
  assert.equal(JSON.parse(await readFile(path.join(editorRoot, 'forge-project.json'), 'utf8')).name, 'Renamed over bridge');

  const progress = [];
  let slowRequest;
  slowRequest = await client.echo('cancel me', 500, (value) => {
    progress.push(value);
    if (progress.length === 1) void client.cancel(slowRequest.requestId);
  });
  await assert.rejects(
    slowRequest.result,
    (error) => error instanceof HostOperationError && error.code === BridgeErrorCode.Cancelled,
  );
  assert.ok(progress.length >= 1);

  const doomed = await client.echo('crash me', 2_000);
  const oldProcessId = client.processId;
  assert.ok(oldProcessId);
  process.kill(oldProcessId);
  await assert.rejects(doomed.result, /Forge host exited/);

  await waitForRestart(client, oldProcessId);
  const recovered = await client.echo('recovered');
  assert.equal(await recovered.result, 'recovered');
});

async function waitForRestart(client, previousProcessId) {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      await client.start();
      if (client.processId && client.processId !== previousProcessId) return;
    } catch {
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error('Forge host did not restart');
}

test('selection result codec bounds lists and rejects truncated or trailing data', () => {
  const writer = new PayloadWriter();
  writer.writeUInt32(4);
  writer.writeUInt32(1);
  writer.writeString('a');
  writer.writeUInt32(1);
  writer.writeString('a');
  writer.writeBoolean(false);
  writer.writeBoolean(false);
  writer.writeBoolean(true);
  writer.writeBoolean(false);
  writer.writeUInt64(12);
  const bytes = writer.toBuffer();
  assert.deepEqual(decodeEditorCommandResult(bytes), {
    kind: 'selection', selection: ['a'], mobyPVars: [{ entityId: 'a', mobyPVar: undefined }],
    isDirty: false, canUndo: true, canRedo: false, lastEventSequence: 12,
  });
  assert.throws(() => decodeEditorCommandResult(bytes.slice(0, -1)));
  assert.throws(() => decodeEditorCommandResult(new Uint8Array([...bytes, 0])));
  const oversized = new PayloadWriter();
  oversized.writeUInt32(4);
  oversized.writeUInt32(100_001);
  assert.throws(() => decodeEditorCommandResult(oversized.toBuffer()));
});

test('editor snapshots carry directional-light colors and directions across the host bridge', async (context) => {
  const root = await mkdtemp(path.join(tmpdir(), 'forge-host-lighting-'));
  const client = new HostClient('dotnet', [hostDll], 25);
  context.after(async () => {
    await client.stop();
    await rm(root, { recursive: true, force: true });
  });
  const directionalLight = {
    topColor: { x: 0.5, y: 0.25, z: 1, w: -0.5 },
    topDirection: { x: 1, y: 0, z: -1, w: 0 },
    inverseColor: { x: 0.25, y: 0.5, z: 0, w: 0 },
    inverseDirection: { x: 0, y: 1, z: 0, w: 0 },
  };
  await mkdir(path.join(root, 'content'));
  await writeFile(path.join(root, 'forge-project.json'), JSON.stringify({
    schemaVersion: 9, documentType: 'forge-project',
    projectId: '10000000-0000-4000-8000-000000000001', name: 'Lighting bridge project',
    target: { game: 'UYA', region: 'NTSC-U', revision: '1.00', bakeProfile: 'uya-ntsc-u' },
    baseLevel: { game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 14, sourceFingerprint: 'a'.repeat(32) },
    content: 'content/project.json.gz',
  }));
  await writeFile(path.join(root, 'content/project.json.gz'), gzipSync(JSON.stringify({
    schemaVersion: 9, documentType: 'forge-project-content', assets: [], instancedCollisionBindings: [],
    entities: [{
      entityId: '20000000-0000-4000-8000-000000000002', name: 'Directional light', layer: 'lighting',
      transform: {
        position: { x: 0, y: 0, z: 0 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 },
      },
      provenance: { game: 'UYA', level: 14, section: 'directional_lights', sourceIndex: 0 },
      lighting: { directionalLight },
    }],
  })));
  await client.start();
  const snapshot = await (await client.openEditorProject(root, path.join(root, 'catalog'), 0)).result;
  assert.deepEqual(snapshot.entities[0].directionalLight, directionalLight);
  assert.equal(snapshot.entities[0].provenance.sourceIndex, 0);
});

test('coverage bridge edits are atomic, reject stale state, join history and survive save/reopen', async (context) => {
  const root = await mkdtemp(path.join(tmpdir(), 'forge-host-coverage-'));
  const client = new HostClient('dotnet', [hostDll], 25);
  context.after(async () => { await client.stop(); await rm(root, { recursive: true, force: true }); });
  const fixture = JSON.parse(await readFile(path.join(projectRoot, 'tests/fixtures/occlusion-coverage-v1.json'), 'utf8'));
  await mkdir(path.join(root, 'content'));
  await writeFile(path.join(root, 'forge-project.json'), JSON.stringify({
    schemaVersion: 26, documentType: 'forge-project', projectId: randomUUID(), name: 'Coverage bridge project',
    target: { game: 'UYA', region: 'NTSC-U', revision: '1.00', bakeProfile: 'uya-ntsc-u' },
    baseLevel: { game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 3, sourceFingerprint: 'a'.repeat(32) },
    content: 'content/project.json.gz',
  }));
  await writeFile(path.join(root, 'content/project.json.gz'), gzipSync(JSON.stringify({
    schemaVersion: 26, documentType: 'forge-project-content', entities: [], assets: [], instancedCollisionBindings: [],
  })));
  await client.start();
  const opened = await (await client.openEditorProject(root, path.join(root, 'catalog'), 0)).result;
  assert.ok(opened.capabilities.includes('editor.occlusion-coverage.query'));
  assert.ok(opened.capabilities.includes('editor.occlusion-coverage.update'));
  const initial = await (await client.queryOcclusionCoverage()).result;
  assert.deepEqual(initial.state, { schemaVersion: 1, primaryEnabled: true, sources: [] });
  const result = await (await client.updateOcclusionCoverage(initial.projectFingerprint, fixture.state)).result;
  assert.deepEqual(result.coverage.state, fixture.state);
  assert.equal(result.project.isDirty, true);
  assert.equal(result.project.canUndo, true);
  assert.deepEqual(result.project.entities, []);
  assert.deepEqual(await (await client.queryOcclusionCoverage()).result, result.coverage);
  await assert.rejects((await client.updateOcclusionCoverage(initial.projectFingerprint, initial.state)).result,
    error => error instanceof HostOperationError && error.code === BridgeErrorCode.Conflict);
  assert.deepEqual(await (await client.queryOcclusionCoverage()).result, result.coverage);
  await (await client.executeEditorCommand({ id: randomUUID(), kind: 'undo', entityIds: [] })).result;
  assert.deepEqual(await (await client.queryOcclusionCoverage()).result, initial);
  await (await client.executeEditorCommand({ id: randomUUID(), kind: 'redo', entityIds: [] })).result;
  assert.deepEqual(await (await client.queryOcclusionCoverage()).result, result.coverage);
  await (await client.saveEditorProject()).result;
  await (await client.closeEditorProject()).result;
  const reopened = await (await client.openEditorProject(root, path.join(root, 'catalog'), 0)).result;
  assert.equal(reopened.isDirty, false);
  const persisted = await (await client.queryOcclusionCoverage()).result;
  assert.deepEqual(persisted, result.coverage);
  const restored = await (await client.updateOcclusionCoverage(persisted.projectFingerprint, {
    ...persisted.state, primaryEnabled: true,
    sources: persisted.state.sources.map(source => ({ ...source, enabled: false })),
  })).result;
  assert.equal(restored.coverage.recipeFingerprint, initial.recipeFingerprint);
  assert.equal(restored.coverage.state.sources.length, 3);
  const previewSupported = opened.capabilities.includes('editor.occlusion-coverage.preview');
  const configuredPreview = execFileSync('dotnet', ['msbuild', path.join(projectRoot, 'src/Forge.Host/Forge.Host.csproj'),
    '-getProperty:ForgeEnableOcclusionCoveragePreview'], { encoding: 'utf8', cwd: projectRoot }).trim();
  assert.equal(previewSupported, configuredPreview === 'true');
  const configuredGeneration = execFileSync('dotnet', ['msbuild', path.join(projectRoot, 'src/Forge.Host/Forge.Host.csproj'),
    '-getProperty:ForgeEnableOcclusionCoverageGeneration'], { encoding: 'utf8', cwd: projectRoot }).trim();
  assert.equal(opened.capabilities.includes('editor.occlusion-coverage.build'), configuredGeneration === 'true');
  if (previewSupported) {
    const box = { ...fixture.state.sources[0], enabled: true, boxSize: { x: 1, y: 1, z: 1 },
      transform: { position: { x: 10, y: 10, z: 10 }, rotation: { x: 0, y: 0, z: 0, w: 1 }, scale: { x: 1, y: 1, z: 1 } } };
    const authored = await (await client.updateOcclusionCoverage(restored.coverage.projectFingerprint,
      { schemaVersion: 1, primaryEnabled: false, sources: [box] })).result;
    const progress = [];
    const preview = await (await client.previewOcclusionCoverage(authored.coverage.projectFingerprint, '', value => progress.push(value))).result;
    assert.deepEqual(preview.cells, [{ x: 2, y: 2, z: 2 }]);
    assert.deepEqual(preview.sources, [{ id: box.id, cells: 1 }]);
    assert.equal(preview.algorithmVersion, 2);
    assert.equal(preview.projectFingerprint, authored.coverage.projectFingerprint);
    assert.equal(preview.recipeFingerprint, authored.coverage.recipeFingerprint);
    assert.equal(preview.primaryCells, 0);
    assert.equal(preview.candidateTests, 1);
    assert.equal(progress.at(-1).completed, progress.at(-1).total);
    assert.deepEqual(await (await client.queryOcclusionCoverage()).result, authored.coverage);
    const moved = await (await client.updateOcclusionCoverage(authored.coverage.projectFingerprint, {
      ...authored.coverage.state, sources: [{ ...box, transform: { ...box.transform, position: { x: 14, y: 10, z: 10 } } }],
    })).result;
    const movedPreview = await (await client.previewOcclusionCoverage(moved.coverage.projectFingerprint, '')).result;
    assert.deepEqual(movedPreview.cells, [{ x: 3, y: 2, z: 2 }]);
    const { boxSize, ...cylinder } = moved.coverage.state.sources[0];
    cylinder.cylinder = { radius: 1, height: 8 };
    const resized = await (await client.updateOcclusionCoverage(moved.coverage.projectFingerprint, {
      ...moved.coverage.state, sources: [cylinder],
    })).result;
    const cylinderPreview = await (await client.previewOcclusionCoverage(resized.coverage.projectFingerprint, '')).result;
    assert.deepEqual(cylinderPreview.cells, [{ x: 3, y: 2, z: 1 }, { x: 3, y: 2, z: 2 }, { x: 3, y: 2, z: 3 }]);
    assert.deepEqual(cylinderPreview.sources, [{ id: cylinder.id, cells: 3 }]);
    const cleared = await (await client.updateOcclusionCoverage(resized.coverage.projectFingerprint, {
      ...resized.coverage.state, sources: [],
    })).result;
    assert.deepEqual((await (await client.previewOcclusionCoverage(cleared.coverage.projectFingerprint, '')).result).cells, []);
    assert.deepEqual(await (await client.queryOcclusionCoverage()).result, cleared.coverage);
    await assert.rejects((await client.previewOcclusionCoverage(initial.projectFingerprint, '')).result,
      error => error instanceof HostOperationError && error.code === BridgeErrorCode.Conflict);
  } else {
    await assert.rejects((await client.previewOcclusionCoverage(restored.coverage.projectFingerprint, '')).result,
      error => error instanceof HostOperationError && error.code === BridgeErrorCode.Conflict);
  }
});
