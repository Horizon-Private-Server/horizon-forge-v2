import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import test from 'node:test';

import { NotificationCenter } from '../dist-electron/main/NotificationCenter.js';

const electronUrl = `data:text/javascript,${encodeURIComponent('export const app = {}; export const dialog = {}; export const shell = {};')}`;
const hooks = registerHooks({ resolve(specifier, context, next) {
  return specifier === 'electron' ? { url: electronUrl, shortCircuit: true } : next(specifier, context);
} });
let UpdateService;
try {
  ({ UpdateService } = await import('../dist-electron/main/UpdateService.js'));
} finally { hooks.deregister(); }

for (const state of ['error', 'rolled-back', 'waiting-for-exit']) {
  test(`startup preserves ${state} activation instead of preparing the cached update again`, async () => {
    const service = createService();
    service.activationManager = { state: { state } };
    service.publishActivationState({ state, message: 'Saved activation result', version: '2.1.0' });
    const result = service.getLifecycleState();
    let recoveries = 0;
    service.downloadManager = { recover: async () => { recoveries += 1; throw new Error('Unexpected recovery'); } };
    service.stageManager = {};

    service.startBackgroundRecovery();
    assert.equal((await service.check(false, 'stable')).status, 'busy');
    assert.equal(recoveries, 0);
    assert.deepEqual(service.getLifecycleState(), result);
    if (state === 'error') {
      assert.ok(result.actions.some(({ action }) => action === 'retry'));
      let retries = 0;
      service.installReadyUpdate = async () => { retries += 1; };
      await service.runUpdateAction('retry');
      assert.equal(retries, 1, 'an explicit user retry remains available');
    }
  });
}

test('normal startup still recovers and prepares a completed download', async () => {
  const service = createService();
  service.activationManager = { state: { state: 'idle' } };
  const download = { version: '2.1.0' };
  service.downloadManager = {
    recover: async () => ({ state: 'ready' }),
    getReadyDownload: async () => download,
  };
  service.stageManager = { recover: async (value) => {
    assert.equal(value, download);
    return { state: 'idle' };
  } };
  let preparations = 0;
  service.prepareDownloadedUpdate = async () => { preparations += 1; };
  service.startBackgroundRecovery();
  await service.startupRecovery;
  assert.equal(preparations, 1);
});

function createService() {
  return new UpdateService({
    version: '2.0.0', channel: 'stable', trustedKeys: [],
    installLayout: { kind: 'managed', root: '/unused', reason: 'test' },
    notifications: new NotificationCenter(() => undefined),
    getMainWindow: () => undefined, isProjectDirty: () => false,
    saveProject: async () => {}, requestQuit() {},
  });
}
