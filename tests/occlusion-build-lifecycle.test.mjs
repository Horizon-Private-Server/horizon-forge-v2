import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { registerHooks } from 'node:module';
import test from 'node:test';

const moduleUrl = source => `data:text/javascript,${encodeURIComponent(source)}`;
const electronUrl = moduleUrl(`
  export const handlers = new Map();
  export const app = { on() {} }, dialog = {};
  export const ipcMain = { handle(name, callback) { handlers.set(name, callback); } };
`);
const bakeUrl = moduleUrl(`
  export const signals = [], builds = [], reads = [];
  function pending(jobs, project, signal) {
    return new Promise((resolve, reject) => {
      jobs.push({ project, signal, resolve, reject });
      signal.addEventListener('abort', () => reject(new Error('Occlusion job cancelled.')), { once: true });
    });
  }
  export function rebuildOcclusion(project, catalog, iso, signal) {
    signals.push(signal);
    return pending(builds, project, signal);
  }
  export const previewOcclusionSamples = rebuildOcclusion;
  export function loadOcclusionVisibility(project, catalog, iso, signal) { return pending(reads, project, signal); }
`);
const hooks = registerHooks({ resolve(specifier, context, next) {
  const url = specifier === 'electron' ? electronUrl : specifier === './OcclusionBakeService.js' ? bakeUrl : undefined;
  return url ? { url, shortCircuit: true } : next(specifier, context);
} });
let registerBuildIpcHandlers, handlers, signals, builds, reads;
try {
  ({ handlers } = await import(electronUrl)); ({ signals, builds, reads } = await import(bakeUrl));
  ({ registerBuildIpcHandlers } = await import('../dist-electron/main/BuildIpcHandlers.js'));
} finally { hooks.deregister(); }

test('project lifecycle cancellation aborts an occlusion job and releases the build slot', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'forge-build-lifecycle-'));
  const fingerprint = 'a'.repeat(32);
  const project = { projectPath: root, target: { game: 'UYA' }, baseLevel: { sourceFingerprint: fingerprint } };
  const sender = Object.assign(new EventEmitter(), { id: 1, isDestroyed: () => false, send() {} });
  const cancel = registerBuildIpcHandlers({
    host: { saveEditorProject: async () => ({ result: Promise.resolve(project) }) },
    settings: { paths: { assets: root, logs: root }, getSnapshot: async () => ({ entries: [
      { key: 'sources.uya.iso', value: '/fixture.iso' }, { key: 'sources.uya.fingerprint', value: fingerprint },
    ] }) },
    getMainWindow: () => undefined, assertSender(id) { assert.equal(id, sender.id); },
  });
  try {
    for (const cancelJob of [cancel, () => handlers.get('forge:editor-build-cancel')({ sender })]) {
      const started = signals.length;
      const pending = handlers.get('forge:editor-rebuild-occlusion')({ sender });
      const rejected = assert.rejects(pending, /cancelled/);
      for (let turn = 0; signals.length === started && turn < 10; turn++) await new Promise(setImmediate);
      assert.equal(signals.length, started + 1, 'The next job must reach the existing bake service.');
      await cancelJob(); await rejected;
      assert.equal(signals.at(-1).aborted, true);
      assert.equal(sender.listenerCount('destroyed'), 0);
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});


async function visibilityFixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'forge-visibility-requests-'));
  const fingerprint = 'a'.repeat(32);
  const project = { projectId: 'project', projectPath: root, lastEventSequence: 0,
    target: { game: 'UYA' }, baseLevel: { sourceFingerprint: fingerprint } };
  const sender = Object.assign(new EventEmitter(), { id: 1, isDestroyed: () => false, send() {} });
  let saves = 0;
  const cancel = registerBuildIpcHandlers({
    host: { getEditorSnapshot: async () => ({ result: Promise.resolve(project) }),
      saveEditorProject: async () => { saves++; return { result: Promise.resolve(project) }; } },
    settings: { paths: { assets: root, logs: root }, getSnapshot: async () => ({ entries: [
      { key: 'sources.uya.iso', value: '/fixture.iso' }, { key: 'sources.uya.fingerprint', value: fingerprint },
    ] }) }, getMainWindow: () => undefined, assertSender(id) { assert.equal(id, sender.id); },
  });
  return { root, project, sender, cancel, saves: () => saves,
    read: () => handlers.get('forge:editor-occlusion-visibility')({ sender }),
    build: () => handlers.get('forge:editor-rebuild-occlusion')({ sender }) };
}

async function until(condition) {
  for (let turn = 0; !condition() && turn < 20; turn++) await new Promise(setImmediate);
  assert.ok(condition(), 'The requested job must start.');
}

test('overlapping visibility requests on project load share one export and failures allow retry', async () => {
  const fixture = await visibilityFixture(); const initial = reads.length;
  try {
    const pending = Promise.allSettled([fixture.read(), fixture.read()]);
    await until(() => reads.length > initial);
    assert.equal(reads.length, initial + 1);
    const data = { source: 'vanilla', gridHash: 'b'.repeat(64) };
    reads.at(-1).resolve(data);
    assert.deepEqual(await pending, [{ status: 'fulfilled', value: data }, { status: 'fulfilled', value: data }]);
    assert.equal(fixture.saves(), 0, 'Background reads must never save the project.');
    const failed = assert.rejects(fixture.read(), /broken cache/);
    await until(() => reads.length === initial + 2);
    reads.at(-1).reject(new Error('broken cache')); await failed;
    const retry = fixture.read();
    await until(() => reads.length === initial + 3);
    reads.at(-1).resolve(data); assert.equal(await retry, data);
    assert.equal(fixture.sender.listenerCount('destroyed'), 0);
  } finally { await fixture.cancel(); await rm(fixture.root, { recursive: true, force: true }); }
});

test('builds can start during visibility loading and queued reads run after the real build', async () => {
  const fixture = await visibilityFixture(); const initialReads = reads.length, initialBuilds = builds.length;
  try {
    const oldRead = assert.rejects(fixture.read(), /cancelled/);
    await until(() => reads.length === initialReads + 1);
    const build = fixture.build();
    await until(() => builds.length === initialBuilds + 1);
    await oldRead;
    await assert.rejects(fixture.build(), /A build is already running/);
    const queued = fixture.read();
    await new Promise(setImmediate);
    assert.equal(reads.length, initialReads + 1, 'A visibility read must wait for build publication.');
    builds.at(-1).resolve({ octants: 2 }); await build;
    await until(() => reads.length === initialReads + 2);
    const latest = { source: 'generated', gridHash: 'c'.repeat(64) };
    reads.at(-1).resolve(latest); assert.equal(await queued, latest);
  } finally { await fixture.cancel(); await rm(fixture.root, { recursive: true, force: true }); }
});

test('visibility requests for a changed project cannot reuse or survive the previous read', async () => {
  const fixture = await visibilityFixture(); const initial = reads.length;
  try {
    const previous = assert.rejects(fixture.read(), /cancelled/);
    await until(() => reads.length === initial + 1);
    fixture.project.lastEventSequence++;
    const changed = fixture.read();
    await until(() => reads.length === initial + 2);
    await previous;
    reads.at(-1).resolve({ source: 'generated' }); await changed;
    const closing = assert.rejects(fixture.read(), /cancelled/);
    await until(() => reads.length === initial + 3);
    await fixture.cancel(); await closing;
    assert.equal(fixture.sender.listenerCount('destroyed'), 0);
  } finally { await fixture.cancel(); await rm(fixture.root, { recursive: true, force: true }); }
});

test('closing a project cancels visibility reads queued behind its active build', async () => {
  const fixture = await visibilityFixture(); const initialReads = reads.length, initialBuilds = builds.length;
  try {
    const build = assert.rejects(fixture.build(), /cancelled/);
    await until(() => builds.length === initialBuilds + 1);
    const queued = assert.rejects(fixture.read(), /cancelled/);
    await new Promise(setImmediate);
    await fixture.cancel(); await build; await queued;
    assert.equal(reads.length, initialReads, 'Closing a project must not start its queued visibility export.');
  } finally { await fixture.cancel(); await rm(fixture.root, { recursive: true, force: true }); }
});
