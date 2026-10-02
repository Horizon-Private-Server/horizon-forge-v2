import assert from 'node:assert/strict';
import { access, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { createApplicationPaths } from '../src/utils/ApplicationPaths.ts';
import { clearRenderCache, SettingsStore } from '../src/main/Settings.ts';
import { createForgeTheme } from '../src/renderer/theme.ts';
import { DEFAULT_SCENE_TREE_COLORS } from '../src/utils/SceneTreeColors.ts';
import {
  DEFAULT_UYA_COLLISION_VISUALIZATION,
  serializeUyaCollisionVisualization,
} from '../src/utils/UyaCollisionVisualization.ts';

test('settings validate defaults and preserve unknown keys', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-settings-'));
  try {
    const paths = createApplicationPaths(
      path.join(directory, 'data'),
      path.join(directory, 'documents'),
      path.join(directory, 'logs'),
    );
    const store = new SettingsStore(paths);
    await store.ensureFile();
    await writeFile(paths.settingsFile, JSON.stringify({
      'editor.autosaveSeconds': 1,
      'future.setting': { enabled: true },
      'ui.sceneTreeColors.tie': 'cyan',
      'visualization.uya.collisionPalette': '{}',
    }));

    const invalid = await store.getSnapshot();
    assert.equal(invalid.entries.find((entry) => entry.key === 'build.uya.forceOversizedInPlace')?.value, false);
    assert.equal(invalid.entries.find((entry) => entry.key === 'editor.autosaveSeconds')?.value, 30);
    assert.equal(invalid.entries.find((entry) => entry.key === 'ui.showViewportStats')?.value, true);
    assert.equal(invalid.entries.find((entry) => entry.key === 'ui.componentSize')?.value, 'xs');
    assert.equal(invalid.entries.find((entry) => entry.key === 'ui.selectionColor')?.value, '#22d3ee');
    assert.equal(invalid.entries.find((entry) => entry.key === 'ui.sceneTreeColors.tie')?.value, '#ffd8b1');
    assert.equal(invalid.entries.find((entry) => entry.key === 'visualization.uya.collisionPalette')?.value,
      serializeUyaCollisionVisualization(DEFAULT_UYA_COLLISION_VISUALIZATION));
    assert.equal(invalid.entries.find((entry) => entry.key === 'keybindings.overrides')?.value, '{}');
    assert.equal(invalid.entries.find((entry) => entry.key === 'updates.automaticChecks')?.value, true);
    assert.equal(invalid.entries.find((entry) => entry.key === 'updates.channel')?.value, 'stable');
    assert.equal((await new SettingsStore(paths, 'nightly').getSnapshot()).entries
      .find((entry) => entry.key === 'updates.channel')?.value, 'nightly');
    assert.match(invalid.diagnostics.join('\n'), /editor\.autosaveSeconds/);
    assert.match(invalid.diagnostics.join('\n'), /ui\.sceneTreeColors\.tie/);

    await store.set('paths.projects', '/maps/projects');
    await store.set('ui.editorLayout', '{"panels":{}}');
    await store.set('ui.showViewportStats', true);
    await store.set('ui.componentSize', 'lg');
    await store.set('ui.selectionColor', '#123456');
    await store.set('ui.sceneTreeColors.tie', '#00ffff');
    const persisted = JSON.parse(await readFile(paths.settingsFile, 'utf8'));
    assert.deepEqual(persisted['future.setting'], { enabled: true });
    assert.equal(persisted['editor.autosaveSeconds'], 1);
    assert.equal(persisted['paths.projects'], '/maps/projects');
    assert.equal(persisted['ui.editorLayout'], '{"panels":{}}');
    assert.equal(persisted['ui.showViewportStats'], true);
    assert.equal(persisted['ui.componentSize'], 'lg');
    assert.equal(persisted['ui.selectionColor'], '#123456');
    assert.equal(persisted['ui.sceneTreeColors.tie'], '#00ffff');

    const resetColor = await store.reset('ui.sceneTreeColors.tie');
    assert.equal(resetColor.entries.find((entry) => entry.key === 'ui.sceneTreeColors.tie')?.value, '#ffd8b1');
    await store.reset('editor.autosaveSeconds');
    const reset = JSON.parse(await readFile(paths.settingsFile, 'utf8'));
    assert.equal(reset['editor.autosaveSeconds'], undefined);
    assert.deepEqual(reset['future.setting'], { enabled: true });

    assert.deepEqual(await store.export(false), {
      'build.uya.forceOversizedInPlace': false,
      'editor.autosaveSeconds': 30,
      'imports.uya.enabled': true,
      'keybindings.overrides': '{}',
      'ui.sceneTreeColors.area': '#aaf442',
      'ui.sceneTreeColors.ambientSound': '#0062ff',
      'ui.sceneTreeColors.camera': '#cc3737',
      'ui.sceneTreeColors.collision': '#00b7eb',
      'ui.sceneTreeColors.cylinder': '#ffe119',
      'ui.sceneTreeColors.cuboid': '#e6e619',
      'ui.sceneTreeColors.directionalLight': '#ad57a9',
      'ui.sceneTreeColors.environmentSample': '#469990',
      'ui.sceneTreeColors.environmentTransition': '#9a6324',
      'ui.sceneTreeColors.grindPath': '#743c85',
      'ui.sceneTreeColors.moby': '#ff9d00',
      'ui.sceneTreeColors.occlusionOctant': '#22ff00',
      'ui.sceneTreeColors.object': '#a9a9a9',
      'ui.sceneTreeColors.pill': '#4363d8',
      'ui.sceneTreeColors.pointLight': '#698c6f',
      'ui.sceneTreeColors.playerBarrier': '#ff477e',
      'ui.sceneTreeColors.shrub': '#4dc900',
      'ui.sceneTreeColors.sky': '#dcbeff',
      'ui.sceneTreeColors.sphere': '#3cb44b',
      'ui.sceneTreeColors.spline': '#2bff99',
      'ui.sceneTreeColors.tfrag': '#aaffc3',
      'ui.sceneTreeColors.tie': '#ffd8b1',
      'ui.componentSize': 'lg',
      'ui.selectionColor': '#123456',
      'ui.showViewportStats': true,
      'updates.automaticChecks': true,
      'updates.channel': 'stable',
    });
    await store.reset();
    assert.deepEqual(JSON.parse(await readFile(paths.settingsFile, 'utf8')), { 'future.setting': { enabled: true } });
    assert.equal(paths.settingsFile.startsWith(paths.data), true);
    assert.equal(paths.defaultProjects.startsWith(paths.data), false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('default scene colors are unique', () => {
  assert.equal(new Set(Object.values(DEFAULT_SCENE_TREE_COLORS)).size, Object.keys(DEFAULT_SCENE_TREE_COLORS).length);
});

test('render cache clearing cannot remove other application data', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-render-cache-'));
  try {
    const paths = createApplicationPaths(path.join(directory, 'data'), directory);
    await mkdir(paths.renderCache, { recursive: true });
    await mkdir(paths.assets, { recursive: true });
    await writeFile(path.join(paths.renderCache, 'preview.bin'), 'cached');
    const assetPath = path.join(paths.assets, 'asset.bin');
    await writeFile(assetPath, 'asset');

    await clearRenderCache(paths);

    await assert.rejects(access(path.join(paths.renderCache, 'preview.bin')));
    await access(assetPath);
    await assert.rejects(clearRenderCache({ ...paths, renderCache: paths.data }), /unsafe/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('interface size raises fixed Mantine tokens to the selected breakpoint', () => {
  const compact = createForgeTheme('xs');
  const large = createForgeTheme('lg');
  assert.notEqual(compact.fontSizes!.xs, compact.fontSizes!.lg);
  assert.equal(large.fontSizes!.xs, large.fontSizes!.lg);
  assert.equal(large.spacing!.xs, large.spacing!.lg);
  assert.equal(large.components!.SegmentedControl!.defaultProps.size, 'lg');
});
