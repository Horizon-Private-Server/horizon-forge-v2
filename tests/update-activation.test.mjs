import assert from 'node:assert/strict';
import childProcess from 'node:child_process';
import { EventEmitter } from 'node:events';
import { mkdtemp, mkdir, readFile, readdir, rm, symlink, utimes, writeFile } from 'node:fs/promises';
import { syncBuiltinESMExports } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { UpdateActivationManager } from '../dist-electron/main/UpdateActivationManager.js';

test('activation handoff is nonce-bound, recoverable, and health-marked', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'forge-activation-'));
  let aliasRoot;
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

    await writeFile(path.join(activationRoot, nonce, 'failure.txt'), 'Updater failed before activation.');
    const failedRecoveryCalls = [];
    const failedRecovery = new UpdateActivationManager({
      ...options,
      requestQuit: () => { quitRequests += 1; },
      spawnHelper: async (...args) => { failedRecoveryCalls.push(args); },
    });
    assert.equal(await failedRecovery.recover(), false);
    assert.equal(failedRecovery.state.state, 'error');
    assert.match(failedRecovery.state.message, /Updater failed before activation/);
    assert.ok(failedRecovery.state.message.includes(path.join(activationRoot, nonce, 'failure.txt')));
    assert.equal(failedRecoveryCalls.length, 0);
    assert.equal(quitRequests, 2);
    await rm(path.join(activationRoot, nonce, 'failure.txt'));

    const healthPath = path.join(activationRoot, nonce, 'health.json');
    let healthArgument = healthPath;
    if (process.platform === 'linux') {
      aliasRoot = `${root}-alias`;
      await symlink(root, aliasRoot, 'dir');
      healthArgument = path.join(aliasRoot, 'activation', nonce, 'health.json');
    }
    await writeFile(path.join(activationRoot, nonce, 'state.json'), JSON.stringify({ checkpoint: 'launched' }));
    const restarted = new UpdateActivationManager({
      ...options,
      currentVersion: '2.1.0',
      arguments: ['--forge-update-health', healthArgument, '--forge-update-nonce', nonce],
    });
    assert.equal(await restarted.recover(), false);
    assert.equal(await restarted.markStartupHealthy(), true);
    const health = JSON.parse(await readFile(healthPath, 'utf8'));
    assert.equal(health.nonce, nonce);
    assert.equal(health.version, '2.1.0');
  } finally {
    if (aliasRoot) await rm(aliasRoot, { force: true });
    await rm(root, { recursive: true, force: true });
  }
});

for (const checkpoint of ['healthy', 'failed', 'rolled-back']) {
  test(`the newest ${checkpoint} attempt supersedes older activation records`, async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'forge-activation-history-'));
    try {
      const helperPath = path.join(root, 'Forge.Updater');
      await writeFile(helperPath, 'helper');
      let launches = 0;
      let quits = 0;
      const handoffs = [];
      const options = {
        root: path.join(root, 'activation'), helperPath,
        installLayout: { kind: 'managed', root: path.join(root, 'forge'), manifest: { version: '2.0.0' } },
        platform: 'linux', currentVersion: '2.0.0', arguments: [],
        spawnHelper: async (_helper, handoff) => { launches += 1; handoffs.push(handoff); },
        requestQuit: () => { quits += 1; },
      };
      for (let attempt = 0; attempt < 2; attempt += 1) {
        await new UpdateActivationManager(options).start(fakeStage(path.join(root, 'stage')));
        const directory = path.dirname(handoffs[attempt]);
        const state = attempt === 0 ? (checkpoint === 'healthy' ? 'failed' : 'waiting-for-exit') : checkpoint;
        await writeFile(path.join(directory, 'state.json'), JSON.stringify({ checkpoint: state }));
        if (state === 'failed') await writeFile(path.join(directory, 'failure.txt'), 'Activation failed.');
        const modified = new Date(1_700_000_000_000 + attempt * 1000);
        await utimes(handoffs[attempt], modified, modified);
      }
      const recovery = new UpdateActivationManager(options);
      assert.equal(await recovery.recover(), false);
      assert.equal(recovery.state.state, checkpoint === 'healthy' ? 'idle' : checkpoint === 'failed' ? 'error' : 'rolled-back');
      assert.equal(launches, 2, 'older pending attempts must not resume');
      assert.equal(quits, 2, 'recovery must leave Forge open');
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
}

test('detached helper starts outside the installation on initial handoff and recovery', async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'forge-helper-cwd-'));
  const originalSpawn = childProcess.spawn;
  const calls = [];
  t.mock.method(childProcess, 'spawn', (command, args, options) => {
    calls.push({ command, args, options });
    const child = new EventEmitter();
    child.unref = () => {};
    queueMicrotask(() => child.emit('spawn'));
    return child;
  });
  syncBuiltinESMExports();
  try {
    const targetRoot = path.join(root, 'forge');
    const helperPath = path.join(root, 'Forge.Updater.exe');
    await mkdir(targetRoot);
    await writeFile(helperPath, 'helper');
    const options = {
      root: path.join(root, 'activation'), helperPath,
      installLayout: { kind: 'managed', root: targetRoot, manifest: { version: '2.0.0' } },
      platform: 'windows', currentVersion: '2.0.0', arguments: [], requestQuit() {},
    };
    assert.equal((await new UpdateActivationManager(options).start(fakeStage(path.join(root, 'stage')))).state,
      'waiting-for-exit');
    assert.equal(await new UpdateActivationManager(options).recover(), true);
    assert.equal(calls.length, 2);
    for (const { command, options: spawnOptions } of calls) {
      assert.equal(spawnOptions.cwd, path.dirname(command));
      assert.ok(!spawnOptions.cwd.startsWith(`${targetRoot}${path.sep}`));
      assert.equal(spawnOptions.detached, true);
    }
  } finally {
    childProcess.spawn = originalSpawn;
    syncBuiltinESMExports();
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
