import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { EventEmitter } from 'node:events';
import * as fs from 'node:fs';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { profile } from '../dist-electron/occlusion/Scene.js';

test('GPU worker publishes its result and exits while Chromium still holds its profile open', { timeout: 10000 }, async () => {
  const job = await mkdtemp(path.join(tmpdir(), 'forge-worker-shutdown-'));
  const origins = [[2, 2, 2], ...Array.from({ length: 8 }, (_, i) => [(i & 1) * 4, ((i >> 1) & 1) * 4, ((i >> 2) & 1) * 4])];
  const scene = { schemaVersion: 1, profile, inputHash: 'a'.repeat(64), triangles: 1, origins,
    cells: [{ x: 0, y: 0, z: 0, reservedVisible: false, samples: origins.map((_, i) => i) }],
    spheres: [], checks: [{ origin: [2, 2, 2], direction: [0, 0, 1], geometry: '00'.repeat(128), mobys: '00'.repeat(128) }],
    fallbackMask: '00'.repeat(128) };
  const bytes = Buffer.from(JSON.stringify(scene)), triangles = Buffer.alloc(80);
  triangles.writeInt32LE(-1, 44); triangles.writeUInt32LE(1, 76);
  await Promise.all([['scene.json', bytes], ['triangles.bin', triangles], ['textures.bin', Buffer.alloc(0)], ['alpha.bin', Buffer.alloc(0)]]
    .map(([name, data]) => writeFile(path.join(job, name), data)));
  const result = { schemaVersion: 2, profile, inputHash: scene.inputHash,
    sceneHash: createHash('sha256').update(bytes).digest('hex'), origins: 9, octants: 1, batches: 1,
    wallMs: 1, gpuMs: null, sampleMasks: Buffer.alloc(9 * 256).toString('base64') };

  const require = createRequire(import.meta.url), Module = require('node:module');
  const originalLoad = Module._load, argv = process.argv;
  const signals = new Map(['SIGINT', 'SIGTERM'].map(name => [name, process.listeners(name)]));
  const loaded = Promise.withResolvers(), handlers = new Map(), exits = [];
  let window, cleanupAttempts = 0;
  const app = Object.assign(new EventEmitter(), {
    commandLine: { appendSwitch() {} }, setPath() {}, whenReady: async () => {},
    exit(code) { loaded.reject(new Error(`Worker exited early with code ${code}`)); },
    quit() {
      app.emit('before-quit', { preventDefault() { throw new Error('Successful shutdown was prevented.'); } });
      app.emit('will-quit');
      exits.push(0);
    },
  });
  class BrowserWindow {
    constructor() {
      window = this;
      this.webContents = Object.assign(new EventEmitter(), {
        mainFrame: {}, setWindowOpenHandler() {}, session: { setPermissionRequestHandler() {} },
      });
    }
    isDestroyed() { return false; }
    async loadURL() { loaded.resolve(); }
  }
  const electron = { app, BrowserWindow, protocol: { registerSchemesAsPrivileged() {}, handle() {} },
    ipcMain: Object.assign(new EventEmitter(), { handle: (name, callback) => handlers.set(name, callback) }) };
  const entry = require.resolve('../dist-electron/occlusion/Main.cjs');
  try {
    process.argv = [process.execPath, entry, '--forge-occlusion-worker', job];
    Module._load = function (name, ...args) {
      if (name === 'electron') return electron;
      if (name === 'node:fs') return { ...fs, rmSync() {
        cleanupAttempts++;
        throw Object.assign(new Error('Chromium still holds the profile lock.'), { code: 'EBUSY' });
      } };
      return originalLoad.call(this, name, ...args);
    };
    require(entry);
    await loaded.promise;
    const event = { sender: window.webContents, senderFrame: window.webContents.mainFrame };
    await handlers.get('occlusion:finish')(event, result);
    assert.deepEqual(exits, [0]);
    assert.equal(cleanupAttempts, 0, 'the parent must remove the profile after the worker exits');
    assert.deepEqual(JSON.parse(await readFile(path.join(job, 'result.json'), 'utf8')), result);
  } finally {
    Module._load = originalLoad;
    process.argv = argv;
    delete require.cache[entry];
    for (const [name, previous] of signals)
      for (const listener of process.listeners(name)) if (!previous.includes(listener)) process.removeListener(name, listener);
    await rm(job, { recursive: true, force: true });
  }
});
