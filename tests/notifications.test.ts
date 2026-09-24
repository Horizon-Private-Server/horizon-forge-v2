import assert from 'node:assert/strict';
import test from 'node:test';

import { NotificationCenter } from '../src/main/NotificationCenter.ts';

test('notifications publish, run actions, and dismiss through one shared center', async () => {
  const changes: unknown[] = [];
  const center = new NotificationCenter(() => ({
    webContents: { send: (_channel: string, value: unknown) => changes.push(value) },
  }) as never);
  let ran = false;
  center.publish({
    id: 'update:stable', title: 'Update available', message: '2.0.1', severity: 'info',
    createdUnixMilliseconds: 1, actionLabel: 'Download',
  }, async () => { ran = true; });

  assert.equal(center.list().length, 1);
  await center.runAction('update:stable');
  assert.equal(ran, true);
  assert.deepEqual(center.list(), []);
  assert.equal(changes.length, 2);

  center.publish({
    id: 'update:nightly', title: 'Nightly available', message: '2.0.1-nightly', severity: 'info',
    createdUnixMilliseconds: 2, actionLabel: 'Download',
  }, async () => false);
  await center.runAction('update:nightly');
  assert.equal(center.list().length, 1);
});
