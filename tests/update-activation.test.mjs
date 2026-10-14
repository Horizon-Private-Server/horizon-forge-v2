import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { UpdateActivationManager } from '../dist-electron/main/UpdateActivationManager.js';

test('activation handoff is nonce-bound, recoverable, and health-marked', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'forge-activation-'));
  try {
    const activationRoot = path.join(root, 'activation');
    const targetRoot = path.join(root, 'forge');
    const stageRoot = path.join(root, 'stage');
    const helperPath = path.join(root, 'Forge.Updater');
    await Promise.all([mkdir(targetRoot), mkdir(stageRoot), writeFile(helperPath, 'helper', { mode: 0o700 })]);
    const calls = [];
    let quitRequests = 0;
    const options = {
      root: activationRoot,
      helperPath,
      installLayout: { kind: 'managed', root: targetRoot, reason: 'test', manifest: { version: '2.0.0' } },
      platform: 'linux', currentVersion: '2.0.0', arguments: [],
      requestQuit: () => { quitRequests += 1; },
      spawnHelper: async (...args) => { calls.push(args); },
    };
    const manager = new UpdateActivationManager(options);
    const snapshot = await manager.start(fakeStage(stageRoot));
    assert.equal(snapshot.state, 'waiting-for-exit');
    assert.equal(quitRequests, 1);
    assert.equal(calls.length, 1);
    const directories = await readdir(activationRoot);
    assert.equal(directories.length, 1);
    const nonce = directories[0];
    const handoffPath = path.join(activationRoot, nonce, 'handoff.json');
    const handoff = JSON.parse(await readFile(handoffPath, 'utf8'));
    assert.equal(handoff.nonce, nonce);
    assert.equal(handoff.targetRoot, targetRoot);
    assert.equal(handoff.stageRoot, stageRoot);
    assert.equal(calls[0][1], handoffPath);
    assert.equal(calls[0][2], nonce);

    const recoveryCalls = [];
    const recovery = new UpdateActivationManager({
      ...options,
      requestQuit: () => { quitRequests += 1; },
      spawnHelper: async (...args) => { recoveryCalls.push(args); },
    });
    assert.equal(await recovery.recover(), true);
    assert.equal(recoveryCalls.length, 1);
    assert.equal(quitRequests, 2);

    const healthPath = path.join(activationRoot, nonce, 'health.json');
    await writeFile(path.join(activationRoot, nonce, 'state.json'), JSON.stringify({ checkpoint: 'launched' }));
    const restarted = new UpdateActivationManager({
      ...options,
      currentVersion: '2.1.0',
      arguments: ['--forge-update-health', healthPath, '--forge-update-nonce', nonce],
    });
    assert.equal(await restarted.recover(), false);
    assert.equal(await restarted.markStartupHealthy(), true);
    const health = JSON.parse(await readFile(healthPath, 'utf8'));
    assert.equal(health.nonce, nonce);
    assert.equal(health.version, '2.1.0');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

function fakeStage(root) {
  return {
    root,
    targetPackageManifestSha256: '1'.repeat(64),
    manifest: { version: '2.1.0', executable: 'horizon-forge' },
    download: {
      envelope: {
        manifest: { channel: 'stable' },
        artifact: { packageManifestSha256: '2'.repeat(64) },
      },
    },
  };
}
