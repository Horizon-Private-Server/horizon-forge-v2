import assert from 'node:assert/strict';
import { readFile as readArtifact, readdir } from 'node:fs/promises';
import { registerHooks } from 'node:module';
import test from 'node:test';

test('occlusion build ships compiled worker modules, sandboxed preload and shader assets', async () => {
  const directory = new URL('../dist-electron/occlusion/', import.meta.url);
  const names = await readdir(directory);
  for (const name of ['Main.cjs', 'Preload.cjs', 'Worker.js', 'Scene.js', 'Cameras.js', 'Gpu.js',
    'RasterBounds.js', 'SpatialGpu.js', 'raster.wgsl', 'spatial.wgsl', 'index.html'])
    assert.ok((await readArtifact(new URL(name, directory))).length > 0, `${name} must ship with the worker`);
  assert.equal(names.some(name => /\.(?:mjs|ts|cts)$/.test(name)), false);
  assert.match(await readArtifact(new URL('index.html', directory), 'utf8'), /src="\.\/Worker\.js"/);
  const preload = await readArtifact(new URL('Preload.cjs', directory), 'utf8');
  assert.match(preload, /require\("electron"\)/);
  assert.doesNotMatch(preload, /require\("\.\//);
});

const electronUrl = `data:text/javascript,${encodeURIComponent(`export const app={isPackaged:false,getAppPath:()=>'/forge'};`)}`;
const previewFixture = {
  schemaVersion: 1, profile: 'test',
  origins: [[2, 2, 2], ...[0, 1, 2, 3, 4, 5, 6, 7].map(i => [(i & 1) * 4, ((i >> 1) & 1) * 4, ((i >> 2) & 1) * 4])],
  cells: [{ x: 0, y: 0, z: 0, samples: [0, 1, 2, 3, 4, 5, 6, 7, 8], reservedVisible: true }], triangles: 100
};
const ioUrl = `data:text/javascript,${encodeURIComponent(`
  export const removed=[], removalOptions=[];
  let scene=${JSON.stringify(previewFixture)},symlink=false,size=1000;
  export function setScene(value,link=false,bytes=1000){scene=value;symlink=link;size=bytes;}
  export const lstat=async()=>({isFile:()=>!symlink,isSymbolicLink:()=>symlink,size});
  export const readFile=async()=>Buffer.from(JSON.stringify(scene));
  export const mkdtemp=async()=>'/tmp/occlusion-test-job';
  export const rm=async (directory,options)=>{removed.push(directory);removalOptions.push(options);};
`)}`;
const processUrl = `data:text/javascript,${encodeURIComponent(`
  import {EventEmitter} from 'node:events';
  export const calls=[];
  export let scenario='valid';
  export function reset(value){calls.length=0;scenario=value;}
  export function spawn(command,args,options){
    const child=new EventEmitter();child.stdout=new EventEmitter();child.stderr=new EventEmitter();let killed=false;
    child.kill=()=>{if(!killed){killed=true;queueMicrotask(()=>child.emit('close',null));}return true;};
    calls.push({command,args,options});
    queueMicrotask(()=>{
      const gpu=args.includes('--forge-occlusion-worker'),commit=args.includes('--occlusion-commit');
      if(gpu&&scenario==='failure'){child.stderr.emit('data',Buffer.from('GPU device lost'));child.emit('close',1);return;}
      const summary={octants:27519,origins:69046,wallMs:1234,gpuMs:1000};
      if(scenario==='invalid')summary.octants='bad';
      const value={completed:1,total:1,message:'Working'};
      if(commit&&scenario!=='missing')value.summary=summary;
      const text='not JSON\\nnull\\n'+JSON.stringify(value)+'\\n',split=Math.floor(text.length/2);
      child.stdout.emit('data',Buffer.from(text.slice(0,split)));child.stdout.emit('data',Buffer.from(text.slice(split)));
      if(!(gpu&&scenario==='cancel')&&!killed)child.emit('close',0);
    });
    return child;
  }
`)}`;
const hooks = registerHooks({
  resolve(specifier, context, next) {
    const url = specifier === 'electron' ? electronUrl : specifier === 'node:child_process' ? processUrl : specifier === 'node:fs/promises' ? ioUrl : undefined;
    return url ? { url, shortCircuit: true } : next(specifier, context);
  }
});
let rebuildOcclusion, previewOcclusionSamples, loadOcclusionVisibility, readOcclusionSamplePreview, readOcclusionVisibility, processMock, ioMock;
try {
  processMock = await import(processUrl);
  ioMock = await import(ioUrl);
  ({ rebuildOcclusion, previewOcclusionSamples, loadOcclusionVisibility } = await import('../dist-electron/main/OcclusionBakeService.js'));
  ({ readOcclusionSamplePreview } = await import('../dist-electron/main/OcclusionSamplePreview.js'));
  ({ readOcclusionVisibility } = await import('../dist-electron/main/OcclusionVisibility.js'));
}
finally {
  hooks.deregister();
}
function run(scenario, progress = () => { }, controller = new AbortController()) {
  processMock.reset(scenario);
  ioMock.removed.length = 0;
  ioMock.removalOptions.length = 0;
  return rebuildOcclusion('/project', '/catalog', '/source.iso', controller.signal, progress);
}
test('occlusion orchestration returns the host summary without loading the full mask file', async () => {
  const progress = [], summary = await run('valid', p => progress.push(p));
  assert.deepEqual(summary, { octants: 27519, origins: 69046, wallMs: 1234, gpuMs: 1000 });
  assert.equal(processMock.calls.length, 3);
  assert.ok(processMock.calls[1].args.includes('--forge-occlusion-worker'));
  assert.deepEqual(processMock.calls[0].args.slice(-2), ['2', 'false']);
  assert.equal(processMock.calls[1].options.env.ELECTRON_RUN_AS_NODE, undefined);
  assert.equal(progress.at(-1).phase, 'Validate occlusion');
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
  assert.deepEqual(ioMock.removalOptions, [{ recursive: true, force: true, maxRetries: 5, retryDelay: 100 }]);
});
function visibilityFixture() {
  return {
    schemaVersion: 1, source: 'generated', gridHash: 'a'.repeat(64), masks: Buffer.alloc(128, 1).toString('base64'),
    octants: [{ x: 0, y: 0, z: 0, maskIndex: 0 }], entities: [{ id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', groups: [567, 1022] }]
  };
}
test('viewport visibility loads masks using a read-only host command without GPU bake or commit', async () => {
  processMock.reset('valid');
  ioMock.removed.length = 0;
  ioMock.setScene(visibilityFixture());
  const value = await loadOcclusionVisibility('/project', '/catalog', '/source.iso', new AbortController().signal, () => { });
  assert.equal(value.source, 'generated');
  assert.equal(value.masks.length, 128);
  assert.equal(value.masks[0], 1);
  assert.equal(processMock.calls.length, 1);
  assert.deepEqual(processMock.calls[0].args.slice(-4), ['--occlusion-visibility', '/project', '/source.iso', '/tmp/occlusion-test-job']);
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
});
test('viewport visibility validates bounded files, native mask indices, unique coordinates and group bits', async () => {
  const read = () => readOcclusionVisibility('/job', new AbortController().signal);
  for (const mutate of [s => { s.schemaVersion = 2; }, s => { s.source = 'invalid'; }, s => { s.gridHash = 'bad'; },
  s => { s.masks = 'bad'; }, s => { s.octants[0].maskIndex = 1; }, s => { s.octants[0].x = .1; },
  s => { s.octants.push(s.octants[0]); }, s => { s.entities[0].groups = [1023]; },
  s => { s.entities[0].groups = []; }, s => { s.entities[0].id = 'bad'; }, s => { s.entities.push(s.entities[0]); }]) {
    const scene = visibilityFixture();
    mutate(scene);
    ioMock.setScene(scene);
    await assert.rejects(read(), /Invalid/);
  }
  ioMock.setScene(visibilityFixture(), true);
  await assert.rejects(read(), /regular file/);
  ioMock.setScene(visibilityFixture(), false, 65 * 1024 * 1024);
  await assert.rejects(read(), /resource budget/);
  ioMock.setScene(previewFixture);
});
test('occlusion orchestration rejects missing or malformed completion summaries', async () => {
  await assert.rejects(run('missing'), /did not return a completion summary/);
  await assert.rejects(run('invalid'), /invalid completion summary/);
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
});
test('GPU failure skips native publication and cleans the disposable job', async () => {
  await assert.rejects(run('failure'), /GPU device lost/);
  assert.equal(processMock.calls.length, 2);
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
});
test('GPU cancellation skips native publication and cleans the disposable job', async () => {
  const controller = new AbortController();
  await assert.rejects(run('cancel', p => {
    if (p.phase === 'Occlusion' && p.completed === 1)
      controller.abort();
  }, controller), /cancelled/);
  assert.equal(processMock.calls.length, 2);
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
});
test('sample preview only prepares the scene, strips unrelated export data and cleans the job', async () => {
  processMock.reset('valid');
  ioMock.removed.length = 0;
  ioMock.setScene(previewFixture);
  const preview = await previewOcclusionSamples('/project', '/catalog', '/source.iso', new AbortController().signal, () => { });
  assert.deepEqual(preview, {
    profile: 'test', origins: previewFixture.origins,
    cells: [{ x: 0, y: 0, z: 0, samples: previewFixture.cells[0].samples }]
  });
  assert.equal(processMock.calls.length, 1);
  assert.ok(processMock.calls[0].args.includes('--occlusion-prepare'));
  assert.deepEqual(processMock.calls[0].args.slice(-2), ['2', 'false']);
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
});
test('preview and full bake forward the same whole-number pull-in to qualification', async () => {
  for (const action of [previewOcclusionSamples, rebuildOcclusion]) {
    processMock.reset('valid');
    ioMock.setScene(previewFixture);
    await action('/project', '/catalog', '/source.iso', new AbortController().signal, () => { }, 5);
    assert.deepEqual(processMock.calls[0].args.slice(-2), ['5', 'false']);
    for (const invalid of [-1, 6, 1.5, NaN, '3', null]) {
      processMock.reset('valid');
      await assert.rejects(action('/project', '/catalog', '/source.iso', new AbortController().signal, () => { }, invalid), /whole number from 0 to 5/);
      assert.equal(processMock.calls.length, 0);
    }
  }
});
test('pixel edge allowance reaches the host as a boolean and rejects malformed settings before starting work', async () => {
  for (const enabled of [true, false]) {
    processMock.reset('valid');
    await rebuildOcclusion('/project', '/catalog', '/source.iso', new AbortController().signal, () => { }, 2, enabled);
    assert.deepEqual(processMock.calls[0].args.slice(-2), ['2', String(enabled)]);
  }
  for (const invalid of [0, 1, 'false', null, {}]) {
    processMock.reset('valid');
    await assert.rejects(rebuildOcclusion('/project', '/catalog', '/source.iso', new AbortController().signal, () => { }, 0, invalid), /must be a boolean/);
    assert.equal(processMock.calls.length, 0);
  }
});
test('sample preview cancellation during preparation skips GPU and publication', async () => {
  processMock.reset('valid');
  ioMock.removed.length = 0;
  const controller = new AbortController();
  await assert.rejects(previewOcclusionSamples('/project', '/catalog', '/source.iso', controller.signal, p => {
    if (p.phase === 'Prepare occlusion')
      controller.abort();
  }), /cancelled/);
  assert.equal(processMock.calls.length, 1);
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
});
test('sample preview accepts extra edge views only as a complete four-sample supplement', async () => {
  const scene = structuredClone(previewFixture);
  scene.origins.push([0, 0, 2], [4, 0, 2], [0, 4, 2], [4, 4, 2]);
  scene.cells[0].samples.push(9, 10, 11, 12);
  ioMock.setScene(scene);
  const preview = await readOcclusionSamplePreview('/job', new AbortController().signal);
  assert.deepEqual(preview.cells[0].samples, scene.cells[0].samples);
  for (const length of [10, 11, 12, 14]) {
    const invalid = structuredClone(scene);
    invalid.cells[0].samples = Array(length).fill(0);
    ioMock.setScene(invalid);
    await assert.rejects(readOcclusionSamplePreview('/job', new AbortController().signal), /Invalid/);
  }
  scene.origins[12] = [4, 4, 5];
  ioMock.setScene(scene);
  await assert.rejects(readOcclusionSamplePreview('/job', new AbortController().signal), /outside/);
});
test('sample preview preserves inward repair bits and rejects invalid masks', async () => {
  const scene = structuredClone(previewFixture);
  scene.cells[0].inwardCorners = 13;
  ioMock.setScene(scene);
  const preview = await readOcclusionSamplePreview('/job', new AbortController().signal);
  assert.equal(preview.cells[0].inwardCorners, 13);
  for (const invalid of [-1, 256, 1.5, null, '13']) {
    scene.cells[0].inwardCorners = invalid;
    ioMock.setScene(scene);
    await assert.rejects(readOcclusionSamplePreview('/job', new AbortController().signal), /Invalid/);
  }
});
test('sample preview validates file budgets, sample references, native centers and cell bounds', async () => {
  const read = () => readOcclusionSamplePreview('/job', new AbortController().signal);
  for (const mutate of [
    s => { s.schemaVersion = 2; }, s => { s.origins[1] = [NaN, 0, 0]; },
    s => { s.origins[1] = [5, 0, 0]; }, s => { s.origins[0] = [1, 2, 2]; },
    s => { s.origins.push(s.origins[1]); }, s => { s.cells.push(s.cells[0]); },
    s => { s.cells[0].samples[1] = 99; }, s => { s.cells[0].x = 0.5; },
  ]) {
    const scene = structuredClone(previewFixture);
    mutate(scene);
    ioMock.setScene(scene);
    await assert.rejects(read(), /Invalid|outside/);
  }
  ioMock.setScene(previewFixture, true);
  await assert.rejects(read(), /regular file/);
  ioMock.setScene(previewFixture, false, 33 * 1024 * 1024);
  await assert.rejects(read(), /resource budget/);
  const collapsed = structuredClone(previewFixture);
  collapsed.cells[0].samples[1] = 0;
  ioMock.setScene(collapsed);
  assert.deepEqual((await read()).cells[0].samples, collapsed.cells[0].samples);
  ioMock.setScene({});
  ioMock.removed.length = 0;
  processMock.reset('valid');
  await assert.rejects(previewOcclusionSamples('/project', '/catalog', '/source.iso', new AbortController().signal, () => { }), /Invalid/);
  assert.deepEqual(ioMock.removed, ['/tmp/occlusion-test-job']);
});
