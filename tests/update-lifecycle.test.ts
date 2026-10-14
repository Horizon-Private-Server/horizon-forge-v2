import assert from 'node:assert/strict';
import test from 'node:test';

import { NotificationCenter } from '../src/main/NotificationCenter.ts';
import { UpdateLifecycleCoordinator } from '../src/main/UpdateLifecycleCoordinator.ts';
import type { UpdateLifecycleSnapshot } from '../src/types/Updates.js';
import { formatBytes } from '../src/utils/Format.ts';
import { describeUpdateLifecycle } from '../src/utils/UpdateLifecycle.ts';

const available: UpdateLifecycleSnapshot = {
  state: 'available',
  title: 'Forge update available',
  message: 'Version 2.1.0\nChannel: stable\nDownload: 100 MB\nTrusted source: GitHub',
  severity: 'info',
  version: '2.1.0',
  channel: 'stable',
  source: 'GitHub',
  completed: 0,
  total: 100,
  progressUnit: 'bytes',
  actions: [{ action: 'download', label: 'Download update' }, { action: 'defer', label: 'Defer' }],
};

test('update lifecycle synchronizes the ribbon event and notification action', async () => {
  const events: { channel: string; value: unknown }[] = [];
  const window = { webContents: { send: (channel: string, value: unknown) => events.push({ channel, value }) } } as never;
  const notifications = new NotificationCenter(() => window);
  const actions: string[] = [];
  const lifecycle = new UpdateLifecycleCoordinator({
    notifications,
    getMainWindow: () => window,
    runAction: async (action) => { actions.push(action); },
  });

  lifecycle.publish(available);
  assert.deepEqual(events.find(({ channel }) => channel === 'forge:updates-changed')?.value, available);
  assert.equal(notifications.list()[0]?.title, available.title);
  assert.equal(notifications.list()[0]?.actionLabel, 'Download update');

  await notifications.runAction('update:lifecycle');
  assert.deepEqual(actions, ['download']);
  assert.equal(notifications.list().length, 1, 'shared state owns notification dismissal');
});

test('deferring keeps resumable actions while removing the notification', () => {
  const notifications = new NotificationCenter(() => undefined);
  const lifecycle = new UpdateLifecycleCoordinator({
    notifications,
    getMainWindow: () => undefined,
    runAction: async () => undefined,
  });
  lifecycle.publish(available);
  lifecycle.defer();

  assert.equal(lifecycle.state.state, 'deferred');
  assert.deepEqual(lifecycle.state.actions, [{ action: 'download', label: 'Download update' }]);
  assert.equal(notifications.list().length, 0);
});

test('lifecycle presentation keeps the required label, severity color, progress, and busy states', () => {
  assert.deepEqual(describeUpdateLifecycle(available), {
    label: 'Forge update available', color: 'red', busy: false, progress: undefined,
  });
  assert.deepEqual(describeUpdateLifecycle({
    ...available, state: 'downloading', completed: 120,
  }), { label: 'Downloading update', color: 'blue', busy: true, progress: 100 });
  assert.equal(describeUpdateLifecycle({ ...available, state: 'failed' })?.color, 'red');
  assert.deepEqual(describeUpdateLifecycle({ ...available, state: 'current' }), {
    label: 'Forge is up to date', color: 'green', busy: false, progress: undefined,
  });
  assert.equal(describeUpdateLifecycle({ ...available, state: 'rolled-back' })?.label, 'Update rolled back');
});

test('download sizes stay in MiB until they reach one GiB', () => {
  assert.equal(formatBytes(188_406_585), '179.7 MiB');
  assert.equal(formatBytes(1024 ** 3), '1.00 GiB');
});
