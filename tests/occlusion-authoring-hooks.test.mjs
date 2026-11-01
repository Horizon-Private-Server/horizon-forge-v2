import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import test from 'node:test';
import { forgeLog } from '../src/renderer/log/LogStore.ts';
import { createOcclusionCoverageSource, createOcclusionCoverageMarker } from '../src/utils/OcclusionCoverage.ts';
import { resolveKeybindings } from '../src/utils/Keybindings.ts';
import { createGraphVolume } from './fixtures/OcclusionCoverage.ts';

const moduleUrl = source => `data:text/javascript,${encodeURIComponent(source)}`;
// Run the hook's effects and dependency comparisons without a browser or a rendering library.
const reactUrl = moduleUrl(`
  let slots=[], cursor=0, pending=[], dirty=false;
  const changed=(a,b)=>!a||a.some((v,i)=>!Object.is(v,b[i]));
  export function reset(){for(const slot of slots)slot?.cleanup?.();slots=[];}
  export function render(component){
    let result;
    do{dirty=false;cursor=0;pending=[];result=component();for(const effect of pending)effect();}while(dirty);
    return result;
  }
  export function useState(initial){
    const i=cursor++, slot=slots[i]??=( {value:initial} );
    return [slot.value, value=>{const next=typeof value==='function'?value(slot.value):value;
      if(!Object.is(slot.value,next)){slot.value=next;dirty=true;}}];
  }
  export function useRef(value){const i=cursor++;return slots[i]??={current:value};}
  export function useCallback(callback,deps){
    const i=cursor++,old=slots[i];if(changed(old?.deps,deps))slots[i]={deps,value:callback};return slots[i].value;
  }
  export function useEffect(effect,deps){
    const i=cursor++,old=slots[i];if(!changed(old?.deps,deps))return;
    const next=slots[i]={deps};pending.push(()=>{old?.cleanup?.();next.cleanup=effect();});
  }
  export const useSyncExternalStore=()=>{};
`);
const hooks = registerHooks({ resolve(specifier, context, next) {
  const url = specifier === 'react' ? reactUrl : undefined;
  return url ? { url, shortCircuit: true } : next(specifier, context);
} });
let react, useOcclusionVisibility, useOcclusionCoverage, useOcclusionCoverageActions, useForgeActions, useKeyboardContext;
try {
  react = await import(reactUrl);
  ({ useOcclusionVisibility } = await import('../src/renderer/editor/UseOcclusionVisibility.ts'));
  ({ useOcclusionCoverage } = await import('../src/renderer/editor/UseOcclusionCoverage.ts'));
  ({ useOcclusionCoverageActions } = await import('../src/renderer/editor/UseOcclusionCoverageActions.ts'));
  ({ useForgeActions } = await import('../src/renderer/hooks/UseForgeActions.ts'));
  ({ useKeyboardContext } = await import('../src/renderer/hooks/UseKeyboardContext.ts'));
} finally { hooks.deregister(); }

function fixture() {
  react.reset();
  const calls = [];
  globalThis.window = { forge: { getOcclusionVisibility: () => new Promise((resolve, reject) => calls.push({ resolve, reject })) } };
  const snapshot = { projectFingerprint: 'a'.repeat(64), visibilityFingerprint: 'b'.repeat(64), recipeFingerprint: 'c'.repeat(64),
    state: { schemaVersion: 1, primaryEnabled: false, sources: [] } };
  const props = { project: { projectId: 'project', projectPath: '/project' }, coverage: { available: true, snapshot },
    host: true, busy: false, revision: 0 };
  const data = { schemaVersion: 1, source: 'generated', projectFingerprint: snapshot.visibilityFingerprint,
    coverageRecipeFingerprint: snapshot.recipeFingerprint, octants: [], entities: [], masks: new Uint8Array(), gridHash: 'd'.repeat(64) };
  const render = () => react.render(() => useOcclusionVisibility(props.project, props.coverage, props.host, props.busy, props.revision));
  const settle = async value => { calls.at(-1).resolve(value); await new Promise(setImmediate); return render(); };
  return { calls, props, data, render, settle };
}

test('visibility waits for metadata and edits stale loaded masks without repeated exports', async () => {
  const f = fixture(), snapshot = f.props.coverage.snapshot;
  f.props.coverage.snapshot = undefined;
  f.render(); assert.equal(f.calls.length, 0);
  f.props.coverage.snapshot = snapshot;
  assert.equal(f.render().loading, true); assert.equal(f.calls.length, 1);
  assert.equal((await f.settle(f.data)).visibility, f.data);
  for (let i = 0; i < 20; i++) {
    f.props.coverage.snapshot = { ...snapshot, visibilityFingerprint: String(i).padStart(64, '0') };
    f.props.busy = true; f.render();
    f.props.busy = false;
    const current = f.render();
    assert.equal(current.visibility, undefined); assert.match(current.warning, /needs rebuilding/);
    assert.equal(current.error, undefined); assert.equal(f.calls.length, 1);
  }
  f.props.coverage.snapshot = snapshot;
  assert.equal(f.render().visibility, f.data, 'undo can restore the loaded masks without reloading');
  assert.equal(f.render().warning, undefined);
  f.props.revision++;
  f.render(); assert.equal(f.calls.length, 2);
  assert.equal((await f.settle(f.data)).visibility, f.data);
  react.reset();
});

test('needs-build is a stable warning and explicit retry recovers real load failures', async () => {
  const f = fixture(); f.render();
  const stale = await f.settle({ schemaVersion: 1, status: 'needs-build' });
  assert.match(stale.warning, /needs rebuilding/); assert.equal(stale.error, undefined);
  f.props.busy = true; f.render(); f.props.busy = false; f.render();
  assert.equal(f.calls.length, 1);
  stale.reload(); f.render(); assert.equal(f.calls.length, 2);
  f.calls.at(-1).reject(new Error('Malformed cache')); await new Promise(setImmediate);
  const failed = f.render(); assert.equal(failed.error, 'Malformed cache'); assert.equal(failed.warning, undefined);
  f.props.busy = true; f.render(); f.props.busy = false; f.render();
  assert.equal(f.calls.length, 2, 'real errors must also wait for an explicit retry');
  failed.reload(); f.render();
  assert.equal((await f.settle(f.data)).visibility, f.data);
  assert.equal(f.render().error, undefined);
  react.reset();
});

test('project switching discards old responses and an interrupted initial read can resume', async () => {
  const f = fixture(); f.render(); const first = f.calls[0];
  f.props.project = { ...f.props.project, projectPath: '/other' }; f.render();
  first.resolve(f.data); await new Promise(setImmediate);
  assert.equal(f.render().visibility, undefined);
  f.props.busy = true; f.render();
  f.calls[1].resolve(f.data); await new Promise(setImmediate);
  f.props.busy = false; f.render(); assert.equal(f.calls.length, 3);
  assert.equal((await f.settle(f.data)).visibility, f.data);
  react.reset();
});

test('switching between authored coverage and Primary reloads the appropriate masks', async () => {
  const f = fixture(); f.render(); await f.settle(f.data);
  f.props.coverage.snapshot = { ...f.props.coverage.snapshot, state: { schemaVersion: 1, primaryEnabled: true, sources: [] } };
  f.render(); assert.equal(f.calls.length, 2);
  const primary = { ...f.data, source: 'vanilla', projectFingerprint: undefined, coverageRecipeFingerprint: undefined };
  assert.equal((await f.settle(primary)).visibility, primary);
  assert.equal(f.render().warning, undefined);
  react.reset();
});

test('coverage enumeration only runs explicitly and selection refreshes do not flash stale octants', async () => {
  react.reset(); forgeLog.clear(); let calls = 0;
  let snapshot = { projectFingerprint: 'a'.repeat(64), recipeFingerprint: 'b'.repeat(64), visibilityFingerprint: 'c'.repeat(64),
    state: { schemaVersion: 1, primaryEnabled: false, sources: [createOcclusionCoverageSource('cylinder', 'Shape')] } };
  globalThis.window = { forge: {
    onOcclusionCoverageProgress: () => () => {}, cancelOcclusionCoveragePreview: async () => {},
    getOcclusionCoverage: async () => snapshot,
    previewOcclusionCoverage: async fingerprint => { calls++; return { projectFingerprint: fingerprint, cells: [] }; },
  } };
  const project = { projectId: 'p', projectPath: '/p', lastEventSequence: 0, selection: [],
    capabilities: ['editor.occlusion-coverage.query', 'editor.occlusion-coverage.update', 'editor.occlusion-coverage.preview'] };
  const update = async (fingerprint, state) => { assert.equal(fingerprint, snapshot.projectFingerprint);
    return snapshot = { ...snapshot, projectFingerprint: 'd'.repeat(64), state }; };
  const render = () => react.render(() => useOcclusionCoverage(project, true, false, update, () => {}));
  render(); await new Promise(setImmediate); let current = render(); assert.equal(calls, 0);
  current.setPlacementProvider(distance => ({ x: distance, y: 100, z: 20 }));
  assert.deepEqual(current.placementPosition(16), { x: 16, y: 100, z: 20 });
  assert.equal(await current.startPreview(), true); current = render(); assert.equal(calls, 1); assert.equal(current.previewStale, false);
  project.lastEventSequence++; current = render();
  assert.equal(current.loading, true); assert.equal(current.previewStale, false, 'metadata refresh alone does not stale geometry');
  await new Promise(setImmediate); current = render(); assert.equal(calls, 1);
  await current.commit({ ...snapshot.state, sources: [{ ...snapshot.state.sources[0], cylinder: { radius: 5, height: 8 } }] });
  current = render(); assert.equal(current.previewStale, true); assert.equal(calls, 1);
  await current.startPreview(); current = render(); assert.equal(calls, 2); assert.equal(current.previewStale, false);
  const previous = current.preview;
  const limitError = 'Graph preview exceeds its scene-query work limit; reduce regions, seeds or blocker geometry.';
  window.forge.previewOcclusionCoverage = async () => { throw new Error(limitError); };
  assert.equal(await current.startPreview(), false); current = render();
  assert.equal(current.preview, previous); assert.equal(current.previewing, false);
  assert.deepEqual(forgeLog.getSnapshot().map(({ source, severity, message }) => ({ source, severity, message })),
    [{ source: 'Occlusion', severity: 'error', message: limitError }]);
  render(); render(); assert.equal(forgeLog.getSnapshot().length, 1, 'rerenders must not duplicate operation errors');
  react.reset();
});

test('generation survives metadata refreshes but rejects a changed scene without replacing the last result', async () => {
  react.reset(); let resolvePreview, cancels = 0;
  const snapshot = { projectFingerprint: 'a'.repeat(64), recipeFingerprint: 'b'.repeat(64), visibilityFingerprint: 'c'.repeat(64),
    state: { schemaVersion: 1, primaryEnabled: false, sources: [createGraphVolume('Graph')] } };
  const preview = { projectFingerprint: snapshot.projectFingerprint, cells: [{ x: 1, y: 2, z: 3 }] };
  globalThis.window = { forge: {
    onOcclusionCoverageProgress: () => () => {}, cancelOcclusionCoveragePreview: async () => { cancels++; },
    getOcclusionCoverage: async () => ({ ...snapshot }),
    previewOcclusionCoverage: () => new Promise(resolve => { resolvePreview = resolve; }),
  } };
  const project = { projectId: 'p', projectPath: '/p', lastEventSequence: 0, selection: [],
    capabilities: ['editor.occlusion-coverage.query', 'editor.occlusion-coverage.update', 'editor.occlusion-coverage.preview'] };
  const render = () => react.render(() => useOcclusionCoverage(project, true, false, async () => snapshot, () => {}));
  render(); await new Promise(setImmediate); let current = render();
  const before = cancels, pending = current.startPreview(); render();
  project.lastEventSequence++; render(); await new Promise(setImmediate); render();
  resolvePreview(preview);
  assert.equal(await pending, true); current = render();
  assert.equal(cancels, before, 'unchanged project content must not cancel generation');
  assert.equal(current.preview, preview); assert.equal(current.showPreview, true); assert.equal(current.previewStale, false);
  const changed = current.startPreview(); render();
  snapshot.projectFingerprint = 'd'.repeat(64); project.lastEventSequence++;
  render(); await new Promise(setImmediate); render();
  assert.equal(cancels, before + 1, 'actual scene edits cancel the captured generation');
  resolvePreview(preview); assert.equal(await changed, false); current = render();
  assert.equal(current.preview, preview); assert.equal(current.previewStale, true);
  react.reset();
});

test('recipe updates keep the loaded panel locked without treating metadata refresh as an edit', async () => {
  react.reset();
  const snapshot = { projectFingerprint: 'a'.repeat(64), state: { schemaVersion: 1, primaryEnabled: false, sources: [] } };
  let finishUpdate;
  globalThis.window = { forge: {
    onOcclusionCoverageProgress: () => () => {}, cancelOcclusionCoveragePreview: async () => {},
    getOcclusionCoverage: async () => snapshot,
  } };
  const project = { projectId: 'p', projectPath: '/p', lastEventSequence: 0, selection: [],
    capabilities: ['editor.occlusion-coverage.query', 'editor.occlusion-coverage.update'] };
  const render = () => react.render(() => useOcclusionCoverage(project, true, false,
    () => new Promise(resolve => { finishUpdate = resolve; }), () => {}));
  let current = render();
  assert.equal(current.loading, true); assert.equal(current.updating, false);
  await new Promise(setImmediate); current = render();
  const pending = current.commit(snapshot.state); current = render();
  assert.equal(current.loading, true); assert.equal(current.updating, true);
  assert.equal(current.snapshot, snapshot, 'Keep the current panel contents during a pending edit');
  finishUpdate(snapshot); assert.equal(await pending, true); current = render();
  assert.equal(current.loading, false); assert.equal(current.updating, false);
  project.lastEventSequence++; current = render();
  assert.equal(current.loading, true); assert.equal(current.updating, false);
  await new Promise(setImmediate); current = render();
  assert.equal(current.loading, false); assert.equal(current.snapshot, snapshot);
  react.reset();
});

test('coverage read, edit and cancellation failures use the shared session log', async () => {
  react.reset(); forgeLog.clear();
  const snapshot = { projectFingerprint: 'a'.repeat(64), state: { schemaVersion: 1, primaryEnabled: false, sources: [] } };
  let failRead = true, rejectPreview;
  globalThis.window = { forge: {
    onOcclusionCoverageProgress: () => () => {}, cancelOcclusionCoveragePreview: async () => {},
    getOcclusionCoverage: async () => { if (failRead) throw new Error('Read failed'); return snapshot; },
    previewOcclusionCoverage: () => new Promise((resolve, reject) => { rejectPreview = reject; }),
  } };
  const project = { projectId: 'p', projectPath: '/p', lastEventSequence: 0, selection: [],
    capabilities: ['editor.occlusion-coverage.query', 'editor.occlusion-coverage.update', 'editor.occlusion-coverage.preview'] };
  const render = () => react.render(() => useOcclusionCoverage(project, true, false,
    async () => { throw new Error('Edit failed'); }, () => {}));
  render(); await new Promise(setImmediate); let current = render();
  assert.equal(current.loading, false); assert.equal(current.snapshot, undefined);
  failRead = false; project.lastEventSequence++; render(); await new Promise(setImmediate); current = render();
  assert.equal(await current.commit(snapshot.state), false); current = render(); assert.equal(current.snapshot, snapshot);
  assert.equal(current.updating, false); assert.equal(current.loading, false);
  const preview = current.startPreview(); current = render();
  window.forge.cancelOcclusionCoveragePreview = async () => { throw new Error('Cancel failed'); };
  current.cancelPreview(); await new Promise(setImmediate);
  rejectPreview(new Error('Request cancelled'));
  assert.equal(await preview, false); current = render(); assert.equal(current.previewing, false);
  assert.deepEqual(forgeLog.getSnapshot().map(({ source, severity, message }) => ({ source, severity, message })),
    ['Read failed', 'Edit failed', 'Cancel failed'].map(message => ({ source: 'Occlusion', severity: 'error', message })));
  react.reset();
});

test('coverage selection copy/paste, duplicate and delete edit seeds and volumes with fresh IDs', async () => {
  react.reset();
  const graph = createGraphVolume('Graph');
  graph.graph.markers = [createOcclusionCoverageMarker('Seed 1 copy copy copy', { x: 1, y: 2, z: 3 })];
  let actions, commits = 0;
  const authoring = { snapshot: { state: { schemaVersion: 1, primaryEnabled: false, sources: [graph] } },
    selectedId: graph.id, selectedMarkerId: graph.graph.markers[0].id, loading: false,
    commit: async state => { commits++; authoring.snapshot = { state }; return true; },
    select: (id, markerId) => { authoring.selectedId = id; authoring.selectedMarkerId = markerId; } };
  const register = value => { actions = value; };
  const render = () => react.render(() => useOcclusionCoverageActions('p', authoring, false, register));
  const run = async action => { actions.handle(action); await new Promise(setImmediate); render(); };
  render(); assert.equal(actions.canCopy, true); assert.equal(actions.canPaste, false);
  await run('copyEntities'); assert.equal(commits, 0); assert.equal(actions.canPaste, true);
  await run('pasteEntities');
  let markers = authoring.snapshot.state.sources[0].graph.markers;
  assert.equal(markers.length, 2); assert.notEqual(markers[0].id, markers[1].id);
  assert.equal(markers[1].name, 'Seed 2');
  assert.deepEqual(markers[1].position, { x: 1, y: 2, z: 3 }); assert.equal(authoring.selectedMarkerId, markers[1].id);
  await run('duplicateEntities'); assert.equal(authoring.snapshot.state.sources[0].graph.markers.length, 3);
  assert.equal(authoring.snapshot.state.sources[0].graph.markers[2].name, 'Seed 3');
  await run('deleteEntities'); assert.equal(authoring.snapshot.state.sources[0].graph.markers.length, 2);
  assert.equal(authoring.selectedId, graph.id); assert.equal(authoring.selectedMarkerId, undefined);
  await run('duplicateEntities');
  const sources = authoring.snapshot.state.sources;
  assert.equal(sources.length, 2); assert.notEqual(sources[0].id, sources[1].id);
  assert.notEqual(sources[0].graph.markers[0].id, sources[1].graph.markers[0].id);
  authoring.selectedId = 'primary'; render(); const before = commits;
  await run('deleteEntities'); assert.equal(commits, before, 'Primary cannot be deleted');
  react.reset();
});

test('global edit actions prefer the active coverage selection over game entities', () => {
  react.reset(); const entityCalls = [], coverageCalls = [];
  globalThis.window = { forge: { executeEditorCommand: async command => { entityCalls.push(command); } } };
  const noop = () => {};
  const render = () => react.render(() => useForgeActions(noop, noop, noop));
  let current = render(); current.setActiveProject({ selection: ['game-object'], entities: [{ id: 'game-object', state: {} }], canPaste: true });
  current.setEditorSelectionActions({ canCopy: true, canPaste: true, canMutate: true, handle: action => coverageCalls.push(action) });
  current = render();
  for (const action of ['copyEntities', 'pasteEntities', 'duplicateEntities', 'deleteEntities']) current.handleForgeAction(action);
  assert.deepEqual(coverageCalls, ['copyEntities', 'pasteEntities', 'duplicateEntities', 'deleteEntities']);
  assert.equal(entityCalls.length, 0);
  react.reset();
});

test('seed paste respects graph bounds, failed commits preserve selection, and project switches clear the clipboard', async () => {
  react.reset();
  const source = createGraphVolume('Source'), target = createGraphVolume('Small graph');
  source.graph.markers = [createOcclusionCoverageMarker('Seed', { x: 3, y: 0, z: 0 })];
  target.boxSize = { x: 2, y: 2, z: 2 };
  let actions, commits = 0, project = 'one', busy = false;
  const authoring = { snapshot: { state: { schemaVersion: 1, primaryEnabled: false, sources: [source, target] } },
    selectedId: source.id, selectedMarkerId: source.graph.markers[0].id, loading: false,
    commit: async () => { commits++; return false; }, select: () => assert.fail('Failed edits must not change selection') };
  const register = value => { actions = value; };
  const render = () => react.render(() => useOcclusionCoverageActions(project, authoring, busy, register));
  render(); actions.handle('copyEntities'); render(); assert.equal(actions.canPaste, true);
  authoring.selectedId = target.id; authoring.selectedMarkerId = undefined; render(); assert.equal(actions.canPaste, false);
  actions.handle('pasteEntities'); await new Promise(setImmediate); assert.equal(commits, 0);
  authoring.selectedId = source.id; authoring.selectedMarkerId = source.graph.markers[0].id; render();
  actions.handle('duplicateEntities'); await new Promise(setImmediate); assert.equal(commits, 1);
  busy = true; render(); actions.handle('deleteEntities'); assert.equal(commits, 1);
  busy = false; project = 'two'; render(); assert.equal(actions.canPaste, false);
  react.reset();
});

test('configured edit shortcuts stop dock navigation and preserve text editing', () => {
  react.reset(); const listeners = new Map(), actions = [];
  const original = Object.fromEntries(['HTMLInputElement', 'HTMLTextAreaElement', 'HTMLElement', 'document'].map(key => [key, globalThis[key]]));
  globalThis.HTMLInputElement = class {}; globalThis.HTMLTextAreaElement = class {}; globalThis.HTMLElement = class {};
  globalThis.document = { activeElement: null, querySelector: () => null, addEventListener() {}, removeEventListener() {} };
  globalThis.window = { forge: { setEditorTextInputActive() {} }, getSelection: () => null,
    addEventListener: (name, listener) => listeners.set(name, listener), removeEventListener: name => listeners.delete(name) };
  try {
    react.render(() => useKeyboardContext(resolveKeybindings({ 'edit.delete': 'KeyX' }), action => actions.push(action)));
    let prevented = false, stopped = false;
    const event = { code: 'KeyX', ctrlKey: false, metaKey: false, shiftKey: false, altKey: false, target: {},
      preventDefault() { prevented = true; }, stopPropagation() { stopped = true; } };
    listeners.get('keydown')(event);
    assert.deepEqual(actions, ['deleteEntities']); assert.equal(prevented, true); assert.equal(stopped, true);
    listeners.get('keydown')({ ...event, target: new HTMLInputElement() });
    assert.equal(actions.length, 1, 'text fields retain normal editing behavior');
  } finally {
    react.reset();
    for (const [key, value] of Object.entries(original)) { if (value === undefined) delete globalThis[key]; else globalThis[key] = value; }
  }
});
