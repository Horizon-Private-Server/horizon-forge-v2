// A separate GPU process keeps bake flags and heavy work away from the editor.
import { app, BrowserWindow, protocol, ipcMain } from 'electron';
import type { IpcMainEvent, IpcMainInvokeEvent } from 'electron';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { rmSync } from 'node:fs';
import type { OcclusionWorkerResult } from '../types/OcclusionWorker.js';

const flag = process.argv.indexOf('--forge-occlusion-worker');
const job = process.argv[flag >= 0 ? flag + 1 : 2] ?? '';
if (!job || (flag >= 0 ? process.argv.length !== flag + 2 : process.argv.length !== 3)) {
  console.error('Expected an occlusion job directory.');
  app.exit(2);
}
app.commandLine.appendSwitch('enable-unsafe-webgpu');
if (process.platform === 'linux') {
  app.commandLine.appendSwitch('enable-features', 'Vulkan,VulkanFromANGLE,DefaultANGLEVulkan');
  app.commandLine.appendSwitch('use-angle', 'vulkan');
}
const userData = path.resolve(job, 'gpu-profile');
app.setPath('userData', userData);
protocol.registerSchemesAsPrivileged([{ scheme: 'forge-occlusion',
  privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true } }]);
let window: BrowserWindow | undefined, cancelled = false, finished = false;

function cancel() {
  cancelled = true;
  if (window && !window.isDestroyed()) window.webContents.send('occlusion:cancel');
  else app.exit(130);
}
process.on('SIGINT', cancel);
process.on('SIGTERM', cancel);

async function read(file: string, max: number) {
  const stat = await fs.lstat(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > max)
    throw Error('Occlusion job resource exceeds limit or is not a regular file.');
  const bytes = await fs.readFile(file);
  if (bytes.length > max) throw Error('Occlusion job resource exceeds limit.');
  return bytes;
}

async function writeResult(result: OcclusionWorkerResult, max: number) {
  const bytes = JSON.stringify(result);
  if (Buffer.byteLength(bytes) > max) throw Error('Occlusion result exceeds limit.');
  const output = path.join(job, 'result.json'), temp = output + '.tmp';
  try {
    const file = await fs.open(temp, 'wx');
    try {
      await file.writeFile(bytes);
      await file.sync();
    } finally {
      await file.close();
    }
    if (cancelled) throw Error('Occlusion bake cancelled.');
    await fs.rename(temp, output);
  } finally {
    await fs.rm(temp, { force: true });
  }
}

app.whenReady().then(async () => {
  const sceneProtocol: typeof import('./Scene.js') = await import('./Scene.js');
  const { limits } = sceneProtocol;
  const routes = new Map<string, { bytes: Buffer<ArrayBuffer>; type: string }>();
  for (const name of ['index.html', 'Worker.js', 'Scene.js', 'Cameras.js', 'Gpu.js', 'RasterBounds.js',
    'SpatialGpu.js', 'raster.wgsl', 'spatial.wgsl'])
    routes.set('/' + name, { bytes: await read(path.join(__dirname, name), 256 * 1024),
      type: name.endsWith('.html') ? 'text/html' : 'text/javascript' });
  for (const [name, max] of [['scene.json', limits.sceneBytes], ['triangles.bin', 80_000_000],
    ['textures.bin', 65_536], ['alpha.bin', 67_108_864]] as const)
    routes.set('/' + name, { bytes: await read(path.join(job, name), max),
      type: name.endsWith('.json') ? 'application/json' : 'application/octet-stream' });
  const sceneBytes = routes.get('/scene.json')!.bytes;
  const scene: unknown = JSON.parse(sceneBytes.toString('utf8'));
  const array = (name: string) => {
    const bytes = routes.get('/' + name + '.bin')!.bytes;
    return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
  };
  sceneProtocol.validateScene(scene, array('triangles'), array('textures'), array('alpha'));
  const sceneHash = createHash('sha256').update(sceneBytes).digest('hex');
  protocol.handle('forge-occlusion', request => {
    const url = new URL(request.url), resource = routes.get(url.pathname);
    if (url.host !== 'local' || url.search || request.method !== 'GET' || !resource)
      return new Response(null, { status: 404 });
    return new Response(resource.bytes, { headers: { 'Content-Type': resource.type, 'Cache-Control': 'no-store' } });
  });
  window = new BrowserWindow({ show: false, webPreferences: {
    preload: path.join(__dirname, 'Preload.cjs'), nodeIntegration: false,
    contextIsolation: true, sandbox: true, backgroundThrottling: false,
  } });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.webContents.session.setPermissionRequestHandler((_window, _permission, reply) => reply(false));
  window.webContents.on('render-process-gone', (_event, details) => {
    console.error(`Occlusion GPU worker exited: ${details.reason}`);
    app.exit(1);
  });
  const own = (event: IpcMainEvent | IpcMainInvokeEvent) => window !== undefined
    && event.sender === window.webContents && event.senderFrame === window.webContents.mainFrame;
  ipcMain.on('occlusion:progress', (event, value: unknown) => {
    const progress = value as { completed?: unknown; total?: unknown; message?: unknown } | null;
    if (own(event) && typeof progress?.completed === 'number' && typeof progress.total === 'number'
      && Number.isInteger(progress.completed) && Number.isInteger(progress.total)
      && progress.completed >= 0 && progress.completed <= progress.total && progress.total === scene.origins.length)
      console.log(JSON.stringify({ completed: progress.completed, total: progress.total,
        message: String(progress.message).slice(0, 256) }));
  });
  ipcMain.on('occlusion:fail', (event, value: unknown) => {
    if (!own(event)) return;
    console.error(String(value).slice(0, 8192));
    app.exit(cancelled ? 130 : 1);
  });
  ipcMain.handle('occlusion:finish', async (event, result: unknown) => {
    if (!own(event) || finished || cancelled) throw Error('Unexpected occlusion result.');
    sceneProtocol.validateResult(result, scene, sceneHash);
    await writeResult(result, limits.resultBytes);
    finished = true;
    app.quit();
  });
  if (cancelled) {
    app.exit(130);
    return;
  }
  await window.loadURL('forge-occlusion://local/index.html');
}).catch(error => { console.error(error); app.exit(1); });
app.on('before-quit', event => {
  if (!finished && !cancelled && window && !window.isDestroyed()) {
    event.preventDefault();
    cancel();
  }
});
app.on('will-quit', () => { rmSync(userData, { recursive: true, force: true }); });
