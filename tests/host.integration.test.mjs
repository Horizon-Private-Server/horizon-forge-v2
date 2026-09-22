import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  HostClient,
  HostOperationError,
} from '../dist-electron/main/bridge/HostClient.js';
import { BridgeErrorCode } from '../dist-electron/main/bridge/BridgeProtocol.js';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const hostDll = path.join(projectRoot, 'src/Forge.Host/bin/Release/net10.0/Forge.Host.dll');

test('host handshake, echo, progress, cancellation, crash recovery, and concurrency', { timeout: 30_000 }, async (context) => {
  const client = new HostClient('dotnet', [hostDll], 25);
  context.after(() => client.stop());

  const expectedRevision = (await readFile(path.join(projectRoot, 'ratchet-sdk.version'), 'utf8')).trim();
  const handshake = await client.start();
  assert.equal(handshake.sdkRevision, expectedRevision);
  assert.deepEqual(handshake.supportedGames, ['UYA']);
  assert.ok(handshake.capabilities.includes('bridge.cancellation'));
  assert.ok(handshake.capabilities.includes('uya.iso.copy'));
  assert.ok(handshake.capabilities.includes('uya.assets.import'));
  assert.ok(handshake.capabilities.includes('editor.runtime'));

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
    schemaVersion: 1,
    documentType: 'forge-project',
    projectId,
    name: 'Bridge project',
    target: { game: 'UYA', region: 'NTSC-U', revision: '1.00', bakeProfile: 'uya-ntsc-u' },
    baseLevel: {
      game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 3,
      sourceFingerprint: 'a'.repeat(32), missingAssetCount: 0,
    },
    content: 'content/project.json',
  }));
  await writeFile(path.join(editorContent, 'project.json'), JSON.stringify({
    schemaVersion: 1,
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
  }));
  const opened = await (await client.openEditorProject(editorRoot, catalogRoot, 0)).result;
  assert.equal(opened.projectId, projectId);
  assert.equal(opened.entities[0].id, entityId);
  const selected = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000001',
    kind: 'setSelection',
    entityIds: [entityId],
  })).result;
  assert.deepEqual(selected.selection, [entityId]);
  const transformed = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000002',
    kind: 'updateTransform',
    entityIds: [entityId],
    transform: {
      position: { x: 10.5, y: 20.25, z: -30.75 },
      rotation: { x: 0, y: 0, z: 0, w: 1 },
      scale: { x: 1, y: 1, z: 1 },
    },
  })).result;
  assert.deepEqual(transformed.entities[0].transform.position, { x: 10.5, y: 20.25, z: -30.75 });
  assert.equal(transformed.entities[0].state.dirty, true);
  assert.equal(transformed.canUndo, true);
  const undone = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000007', kind: 'undo', entityIds: [],
  })).result;
  assert.deepEqual(undone.entities[0].transform.position, { x: 1, y: 2, z: 3 });
  assert.equal(undone.canRedo, true);
  const redone = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000008', kind: 'redo', entityIds: [],
  })).result;
  assert.deepEqual(redone.entities[0].transform.position, { x: 10.5, y: 20.25, z: -30.75 });
  const batchTransformed = await (await client.executeEditorCommand({
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
  })).result;
  assert.deepEqual(batchTransformed.entities[0].transform.position, { x: 11, y: 22, z: 33 });
  const duplicated = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000009', kind: 'duplicateEntities', entityIds: [entityId],
  })).result;
  const duplicateId = duplicated.selection[0];
  assert.notEqual(duplicateId, entityId);
  assert.equal(duplicated.entities.length, 2);
  const deleted = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-00000000000a', kind: 'deleteEntities', entityIds: [duplicateId],
  })).result;
  assert.equal(deleted.entities.length, 1);
  const copied = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-00000000000b', kind: 'copyEntities', entityIds: [entityId],
  })).result;
  assert.equal(copied.canPaste, true);
  const pasted = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-00000000000c', kind: 'pasteEntities', entityIds: [],
  })).result;
  assert.equal(pasted.entities.length, 2);
  assert.notEqual(pasted.selection[0], entityId);
  const updatedEntity = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000004',
    kind: 'renameEntity',
    entityIds: [entityId],
    text: 'Renamed moby',
  })).result;
  assert.equal(updatedEntity.entities[0].name, 'Renamed moby');
  const updatedState = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000005',
    kind: 'setEntityState',
    entityIds: [entityId],
    state: { hidden: true, locked: true },
  })).result;
  assert.equal(updatedState.entities[0].state.hidden, true);
  assert.equal(updatedState.entities[0].state.locked, true);
  const renamed = await (await client.executeEditorCommand({
    id: '30000000-0000-4000-8000-000000000003',
    kind: 'renameProject',
    entityIds: [],
    text: 'Renamed over bridge',
  })).result;
  assert.equal(renamed.projectName, 'Renamed over bridge');
  assert.equal(renamed.isDirty, true);
  const events = await (await client.readEditorEvents(0)).result;
  assert.deepEqual(events.map((event) => event.kind), [
    'projectOpened', 'selectionChanged', 'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged',
    'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged', 'projectChanged',
  ]);
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
