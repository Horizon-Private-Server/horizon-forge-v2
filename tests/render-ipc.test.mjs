import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import test from 'node:test';
import { setImmediate } from 'node:timers/promises';

// Exercise the compiled IPC handlers with only Electron's registration API replaced.
const electronUrl = `data:text/javascript,${encodeURIComponent(`
  export const handlers = new Map();
  export const ipcMain = { handle: (channel, handler) => handlers.set(channel, handler) };
  export const dialog = {};
`)}`;
const hooks = registerHooks({
  resolve(specifier, context, next) {
    return specifier === 'electron' ? { url: electronUrl, shortCircuit: true } : next(specifier, context);
  },
});
let registerRenderIpcHandlers;
let handlers;
try {
  ({ handlers } = await import(electronUrl));
  ({ registerRenderIpcHandlers } = await import('../dist-electron/main/RenderIpcHandlers.js'));
} finally { hooks.deregister(); }

const token = '12345678-1234-4123-8123-123456789abc';
const sourceToken = '12345678-1234-4123-8123-123456789def';
const assetId = 'a'.repeat(64);
const event = { sender: { id: 7 } };
const paths = [
  { channel: 'forge:asset-preview', args: [assetId, 'moby', token] },
  { channel: 'forge:editor-instanced-collision-model', args: [sourceToken, token] },
  { channel: 'forge:editor-instanced-collision-applied-model', args: [assetId, token] },
];
const payload = { rootPath: '/cache/preview', cacheKey: 'key', modelPath: 'model.glb', cacheHit: false };

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function harness() {
  handlers.clear();
  const dispatch = deferred();
  const result = deferred();
  const registration = deferred();
  const cancelled = [];
  const published = [];
  const prepared = [];
  const prepare = (...args) => { prepared.push(args); return dispatch.promise; };
  const host = {
    prepareAssetPreview: prepare,
    prepareInstancedCollisionPreview: prepare,
    prepareAppliedInstancedCollision: prepare,
    cancel: async id => { cancelled.push(id); },
  };
  registerRenderIpcHandlers({
    host,
    settings: { paths: { renderCache: '/cache/render', assets: '/cache/assets' } },
    renderAssets: { addPreview: value => { published.push(value); return registration.promise; } },
    getEditorTargetGame: () => 'UYA', getEditorProjectPath: () => '/project',
    getMainWindow: () => undefined,
    assertSender: id => { if (id !== 7) throw new Error('Untrusted sender'); },
  });
  const invoke = (channel, ...args) => handlers.get(channel)(event, ...args);
  const finishDispatch = () => dispatch.resolve({ requestId: 101, result: result.promise });
  const finish = () => { finishDispatch(); result.resolve(payload); registration.resolve({ url: 'forge-asset://preview' }); };
  return { host, invoke, dispatch, result, registration, cancelled, published, prepared, finishDispatch, finish };
}

for (const path of paths) {
  test(`${path.channel} remembers cancellation before host dispatch returns`, async () => {
    const h = harness();
    const preview = h.invoke(path.channel, ...path.args);
    void preview.catch(() => {});
    try {
      await h.invoke('forge:asset-preview-cancel', token);
      assert.deepEqual(h.cancelled, []);
      h.finishDispatch();
      await setImmediate();
      assert.deepEqual(h.cancelled, [101]);
      h.result.resolve(payload); // Even a host cache hit after cancel must not publish.
      await assert.rejects(preview, { name: 'AbortError' });
      assert.deepEqual(h.published, []);
    } finally { h.finish(); await preview.catch(() => {}); }
  });

  test(`${path.channel} reserves tokens across all preview paths during dispatch`, async () => {
    const h = harness();
    const preview = h.invoke(path.channel, ...path.args);
    try {
      for (const other of paths) await assert.rejects(h.invoke(other.channel, ...other.args), /already active/);
      assert.equal(h.prepared.length, 1);
    } finally { h.finish(); await preview; }
  });
}

test('preview cancellation after dispatch suppresses results and releases the token after settlement', async () => {
  const h = harness();
  const path = paths[0];
  const preview = h.invoke(path.channel, ...path.args);
  h.finishDispatch();
  await setImmediate();
  await h.invoke('forge:asset-preview-cancel', token);
  assert.deepEqual(h.cancelled, [101]);
  await assert.rejects(h.invoke(path.channel, ...path.args), /already active/);
  h.result.resolve(payload);
  await assert.rejects(preview, { name: 'AbortError' });
  assert.deepEqual(h.published, []);
  h.registration.resolve({ url: 'forge-asset://preview' });
  assert.deepEqual(await h.invoke(path.channel, ...path.args), { url: 'forge-asset://preview' });
});

test('cancellation during URL registration suppresses completion without cancelling other tokens', async () => {
  const h = harness();
  const path = paths[0];
  const preview = h.invoke(path.channel, ...path.args);
  h.finishDispatch();
  h.result.resolve(payload);
  await setImmediate();
  assert.equal(h.published.length, 1);
  await h.invoke('forge:asset-preview-cancel', sourceToken);
  assert.deepEqual(h.cancelled, []);
  await h.invoke('forge:asset-preview-cancel', token);
  h.registration.resolve({ url: 'forge-asset://preview' });
  await assert.rejects(preview, { name: 'AbortError' });
  await h.invoke('forge:asset-preview-cancel', token);
  assert.deepEqual(h.cancelled, [101]);
});

test('a host cancellation result is handled while the cancellation write is still pending', async () => {
  const h = harness();
  const write = deferred();
  h.host.cancel = () => write.promise;
  const path = paths[0];
  const preview = h.invoke(path.channel, ...path.args);
  const rejected = assert.rejects(preview, /Host cancelled/);
  await h.invoke('forge:asset-preview-cancel', token);
  h.finishDispatch();
  await setImmediate();
  h.result.reject(new Error('Host cancelled'));
  await rejected;
  assert.deepEqual(h.published, []);
  write.resolve();
});

test('normal previews retain host parameters, registered URLs and completed-token cleanup', async () => {
  const h = harness();
  const preview = h.invoke('forge:asset-preview', assetId, 'tie', token, undefined, 'thumbnail');
  h.finish();
  assert.deepEqual(await preview, { url: 'forge-asset://preview' });
  assert.deepEqual(h.prepared, [[{
    cacheRootPath: '/cache/render', catalogRootPath: '/cache/assets', projectPath: '/project',
    assetId, kind: 'tie', targetGame: 'UYA', viewPreset: 'custom-thumbnail',
  }]]);
  assert.deepEqual(h.published, [payload]);
  await h.invoke('forge:asset-preview-cancel', token);
  assert.deepEqual(h.cancelled, []);
});

for (const stage of ['dispatch', 'result', 'registration']) {
  test(`preview ${stage} errors release the reserved token and preserve the error`, async () => {
    const h = harness();
    const path = paths[0];
    const preview = h.invoke(path.channel, ...path.args);
    const rejected = assert.rejects(preview, new RegExp(`${stage} failed`));
    if (stage === 'dispatch') h.dispatch.reject(new Error('dispatch failed'));
    else {
      h.finishDispatch();
      if (stage === 'result') h.result.reject(new Error('result failed'));
      else {
        h.result.resolve(payload);
        await setImmediate();
        h.registration.reject(new Error('registration failed'));
      }
    }
    await rejected;
    await h.invoke('forge:asset-preview-cancel', token);
    assert.deepEqual(h.cancelled, []);
    // The same failing operation can start again, rather than report a token conflict.
    await assert.rejects(h.invoke(path.channel, ...path.args), new RegExp(`${stage} failed`));
    assert.equal(h.prepared.length, 2);
  });
}

test('render IPC still rejects untrusted senders and invalid tokens before dispatch', async () => {
  const h = harness();
  await assert.rejects(handlers.get('forge:asset-preview')({ sender: { id: 8 } }, assetId, 'moby', token), /Untrusted/);
  await assert.rejects(h.invoke('forge:asset-preview', assetId, 'moby', 'invalid'), /token is invalid/);
  await assert.rejects(h.invoke('forge:asset-preview-cancel', 'invalid'), /token is invalid/);
  await assert.rejects(h.invoke('forge:editor-instanced-collision-model', 'invalid', token), /token is invalid/);
  await assert.rejects(h.invoke('forge:editor-instanced-collision-applied-model', 'invalid', token), /ID is invalid/);
  assert.deepEqual(h.prepared, []);
});
