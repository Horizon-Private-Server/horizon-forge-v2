import assert from 'node:assert/strict';
import test from 'node:test';

import type { LogMessage } from '../src/types/Log.js';
import { LOG_ENTRY_LIMIT, LogStore } from '../src/renderer/log/LogStore.ts';

test('session history preserves ordered, typed messages across panel subscriptions and clear', () => {
  const log = new LogStore();
  let updates = 0;
  const unsubscribe = log.subscribe(() => updates++);
  const empty = log.getSnapshot();
  log.write('Build', 'info', 'Starting…');
  log.write('Build', 'success', 'Complete.');
  log.write('Scene', 'warning', 'Missing asset.', 'asset.missing');
  log.write('Project', 'error', 'Save failed.');
  const entries = log.getSnapshot();
  assert.deepEqual(entries.map((value) => value.severity), ['info', 'success', 'warning', 'error']);
  assert.deepEqual(entries.map((value) => value.id), [1, 2, 3, 4]);
  assert.equal(entries[2].code, 'asset.missing');
  assert.ok(entries.every((value) => value.timestamp > 0));
  assert.equal(log.getSnapshot(), entries, 'React snapshots remain stable between writes');
  assert.deepEqual(empty, [], 'existing snapshots are never mutated');
  unsubscribe();
  log.write('Project', 'info', 'Still retained with panel closed.');
  assert.equal(updates, 4);
  assert.equal(log.getSnapshot().length, 5);
  log.clear();
  assert.deepEqual(log.getSnapshot(), []);
  log.write('Project', 'info', 'After clear.');
  assert.equal(log.getSnapshot()[0].id, 6, 'entry identities are not reused after clearing');
});

test('repeated diagnostic snapshots do not spam history, resolved issues can recur', () => {
  const log = new LogStore();
  const warning: LogMessage = { source: 'Scene', severity: 'warning', code: 'asset.missing', message: 'Missing asset.' };
  log.sync('project', [warning, warning]);
  const first = log.getSnapshot();
  log.sync('project', [{ ...warning }]);
  assert.equal(log.getSnapshot(), first);
  log.clear();
  log.sync('project', [warning]);
  assert.deepEqual(log.getSnapshot(), [], 'clear is respected during unchanged editor snapshots');
  log.sync('project', []);
  log.sync('project', [warning]);
  log.sync('project', [{ ...warning, severity: 'error' }]);
  assert.deepEqual(log.getSnapshot().map((value) => value.severity), ['warning', 'error']);
  log.sync('build', [warning]);
  assert.equal(log.getSnapshot().length, 3, 'independent producers do not suppress one another');
});

test('log history is bounded and blank messages do not trigger updates', () => {
  const log = new LogStore();
  const empty = log.getSnapshot();
  log.write('Scene', 'info', '  \n ');
  assert.equal(log.getSnapshot(), empty);
  log.append(Array.from({ length: LOG_ENTRY_LIMIT + 10 }, (_, index) => ({
    source: 'Build', severity: 'info', message: `Step ${index}`,
  })));
  assert.equal(log.getSnapshot().length, LOG_ENTRY_LIMIT);
  assert.equal(log.getUnreadSnapshot().length, LOG_ENTRY_LIMIT);
  assert.equal(log.getSnapshot()[0].message, 'Step 10');
  assert.equal(log.getSnapshot().at(-1)?.message, `Step ${LOG_ENTRY_LIMIT + 9}`);
});

test('reading the shared log clears unread messages without changing history', () => {
  const log = new LogStore();
  let updates = 0;
  log.subscribe(() => updates++);
  log.write('Project', 'error', 'Save failed.');
  log.write('Scene', 'warning', 'Missing asset.');
  const history = log.getSnapshot();
  const unread = log.getUnreadSnapshot();
  assert.equal(log.getUnreadSnapshot(), unread, 'unread snapshots remain stable between changes');
  assert.deepEqual(unread, history);

  log.markRead();
  assert.equal(updates, 3, 'the header is notified when a different surface marks messages read');
  assert.deepEqual(log.getUnreadSnapshot(), []);
  assert.equal(log.getSnapshot(), history, 'reading does not remove history');
  log.markRead();
  assert.equal(updates, 3, 'reading an already-read log does not trigger another update');

  log.write('Build', 'success', 'Complete.');
  assert.deepEqual(log.getUnreadSnapshot().map((entry) => entry.message), ['Complete.']);
  assert.equal(log.getSnapshot().length, 3);
  log.clear();
  assert.deepEqual(log.getSnapshot(), []);
  assert.deepEqual(log.getUnreadSnapshot(), []);
  log.write('Project', 'info', 'New level opened.');
  assert.equal(log.getUnreadSnapshot().length, 1);
});
