import assert from 'node:assert/strict';
import test from 'node:test';
import { createRendererHarness, find } from './support/RendererHarness.mjs';

import * as explorer from '../src/utils/AssetExplorer.ts';
import * as placement from '../src/utils/AssetPlacement.ts';
import * as virtualGrid from '../src/utils/VirtualGrid.ts';
import * as format from '../src/utils/Format.ts';
import * as sounds from '../src/utils/SoundWorkspace.ts';

function harness(context, forge = {}) {
  let fetches = 0, disposed = 0;
  class SoundPreviewRuntime {
    cacheBytes = 0;
    setDirectVolume() {}
    stop() {}
    async dispose() { disposed++; }
  }
  const runtime = createRendererHarness({
    forge: {
      getSoundWorkspace: async () => { fetches++; return { programs: [], sampleCount: 0, mobyRemapCount: 0, globalRemapCount: 0 }; },
      getSettings: async () => ({ entries: [] }), ...forge,
    },
    dependencies: specifier => {
      if (specifier.endsWith('/AssetExplorer.ts')) return explorer;
      if (specifier.endsWith('/AssetPlacement.ts')) return placement;
      if (specifier.endsWith('/VirtualGrid.ts')) return virtualGrid;
      if (specifier.endsWith('/Format.ts')) return format;
      if (specifier.endsWith('/SoundWorkspace.ts')) return sounds;
      if (specifier === './EditorContext.ts') return { useEditor: () => context };
      if (specifier === './TexturePanelHooks.ts') return { useVirtualGridViewport: () => ({ width: 800, height: 0, scrollTop: 0 }) };
      if (specifier === './SoundPreviewRuntime.ts') return { SoundPreviewRuntime };
      if (specifier.endsWith('/SoundPreviewPreferences.ts')) return {
        readSoundPreviewPreferences: () => ({ directVolume: 1, directMuted: false }),
      };
    },
  });
  return {
    ...runtime,
    get fetches() { return fetches; },
    get disposed() { return disposed; },
    load: (filename, exportName) => runtime.load(`editor/${filename}`, exportName),
  };
}

test('sound revision changes reload inventory and dispose old decoded previews', async () => {
  const context = { project: { projectId: 'project', soundRevision: 'before-import' } };
  const runtime = harness(context);
  const panel = runtime.load('SoundWorkspacePanel');
  runtime.render(panel);
  await new Promise(setImmediate);
  assert.equal(runtime.fetches, 1);
  context.project = { ...context.project, selection: ['moby'] };
  runtime.render(panel);
  assert.equal(runtime.fetches, 1);
  context.project = { ...context.project, soundRevision: 'after-import' };
  runtime.render(panel);
  assert.equal(runtime.fetches, 2);
  assert.equal(runtime.disposed, 1);
  runtime.dispose();
});

test('preview, drag, origin placement, and explorer level filters use one exact source', async () => {
  const source = level => ({ game: 'DL', region: 'NTSC-U', revision: '1.00', level, archive: 'classes.bin', sourceIndex: 0 });
  const item = { assetId: 'a'.repeat(64), category: 'mobys', displayLabel: 'moby:0x20FA', classIds: [0x20fa],
    byteSize: 100, canonicalFormatVersion: 1,
    tags: [], aliases: [], canPlace: true, previewState: 'notCached', sources: [source('level01'), source('level42')] };
  const target = { game: 'UYA', region: 'NTSC-U', revision: '1.00', level: 3 };
  const [family] = explorer.buildAssetFamilies([item], target);
  const commands = [];
  const context = { assetPreview: family, assetVariants: {}, project: { projectId: 'project', entities: [], baseLevel: target, target },
    selectAssetVariant: (key, value) => { context.assetVariants[key] = value; },
    execute: command => { commands.push(command); return Promise.resolve(true); },
    synchronizeAssetFamilies: families => { context.assetPreview = families.find(value => value.familyId === context.assetPreview.familyId) ?? context.assetPreview; },
  };
  const previewRuntime = harness(context);
  const previewPanel = previewRuntime.load('AssetPreviewPanel');
  const options = explorer.assetFamilyVariantOptions(family);
  let preview = previewRuntime.render(previewPanel);
  const initialSoundKey = find(preview, element => element.type === 'MobySoundButtons').key;
  find(preview, element => element.type === 'Select').props.onChange(explorer.assetVariantKey(options[1]));
  preview = previewRuntime.render(previewPanel);
  const sounds = find(preview, element => element.type === 'MobySoundButtons');
  assert.notEqual(sounds.key, initialSoundKey, 'changing the source disposes the previous sound preview');
  assert.deepEqual(sounds.props.sources.map(value => value.level), ['level42']);
  find(preview, element => element.type === 'Button' && element.props.children === 'Place at origin').props.onClick();
  assert.equal(commands[0].placement.source.level, 'level42');
  const cardRuntime = harness(context);
  const card = cardRuntime.render(cardRuntime.load('AssetExplorerPanel', 'AssetCard'), {
    family, selection: context.assetVariants[family.familyId], runtime: null, onActivate() {},
  });
  const data = new Map();
  card.props.onDragStart({ dataTransfer: { setData: (key, value) => data.set(key, value) }, preventDefault() { assert.fail('Drag blocked'); } });
  assert.equal(placement.readAssetPlacementDrag({ getData: key => data.get(key) ?? '' }).source.level, 'level42');
  const explorerRuntime = harness(context, {
    queryAssetExplorer: async () => ({ items: [item], facets: { games: [], levels: [], regions: [], revisions: [], tags: [] } }),
    cancelAssetExplorerQuery: async () => {},
  });
  const panel = explorerRuntime.load('AssetExplorerPanel');
  explorerRuntime.render(panel);
  await new Promise(setImmediate);
  let tree = explorerRuntime.render(panel);
  find(tree, element => element.type === 'Select' && element.props['aria-label'] === 'Filter assets by level').props.onChange('level01');
  explorerRuntime.render(panel);
  await new Promise(setImmediate);
  explorerRuntime.render(panel);
  preview = previewRuntime.render(previewPanel);
  assert.equal(explorer.resolveAssetFamilySelection(context.assetPreview, context.assetVariants[family.familyId]).source.level, 'level01');
  assert.deepEqual(find(preview, element => element.type === 'MobySoundButtons').props.sources.map(value => value.level), ['level01']);
  for (const runtime of [previewRuntime, cardRuntime, explorerRuntime]) runtime.dispose();
});

test('a silent catalog source does not borrow the placed class sound mapping', async () => {
  const runtime = harness({});
  const panel = runtime.load('MobySoundButtons');
  const props = { assetId: 'a'.repeat(64), classId: 0x20fa, projectId: 'project', sources: [{
    game: 'DL', region: 'NTSC-U', revision: '1.00', level: 'level99', archive: 'classes.bin', sourceIndex: 0,
  }] };
  const tree = runtime.render(panel, props);
  await new Promise(setImmediate);
  assert.equal(runtime.fetches, 0);
  assert.ok(find(tree, element => element.type === 'Text' && element.props.children === 'This source has no sound remaps.'));
  runtime.render(panel, { ...props, sources: [] });
  assert.equal(runtime.fetches, 1, 'scene selections still read their project sound mapping');
  runtime.dispose();
});
