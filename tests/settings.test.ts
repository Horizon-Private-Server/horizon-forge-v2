import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { createApplicationPaths } from '../src/utils/ApplicationPaths.ts';
import { SettingsStore } from '../src/main/Settings.ts';

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
    }));

    const invalid = await store.getSnapshot();
    assert.equal(invalid.entries.find((entry) => entry.key === 'editor.autosaveSeconds')?.value, 30);
    assert.equal(invalid.entries.find((entry) => entry.key === 'ui.showViewportStats')?.value, true);
    assert.equal(invalid.entries.find((entry) => entry.key === 'ui.sceneTreeColors.tie')?.value, '#15aabf');
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
    await store.set('ui.sceneTreeColors.tie', '#00ffff');
    const persisted = JSON.parse(await readFile(paths.settingsFile, 'utf8'));
    assert.deepEqual(persisted['future.setting'], { enabled: true });
    assert.equal(persisted['editor.autosaveSeconds'], 1);
    assert.equal(persisted['paths.projects'], '/maps/projects');
    assert.equal(persisted['ui.editorLayout'], '{"panels":{}}');
    assert.equal(persisted['ui.showViewportStats'], true);
    assert.equal(persisted['ui.sceneTreeColors.tie'], '#00ffff');

    const resetColor = await store.reset('ui.sceneTreeColors.tie');
    assert.equal(resetColor.entries.find((entry) => entry.key === 'ui.sceneTreeColors.tie')?.value, '#15aabf');
    await store.reset('editor.autosaveSeconds');
    const reset = JSON.parse(await readFile(paths.settingsFile, 'utf8'));
    assert.equal(reset['editor.autosaveSeconds'], undefined);
    assert.deepEqual(reset['future.setting'], { enabled: true });

    assert.deepEqual(await store.export(false), {
      'editor.autosaveSeconds': 30,
      'imports.uya.enabled': true,
      'keybindings.overrides': '{}',
      'ui.sceneTreeColors.moby': '#ae3ec9',
      'ui.sceneTreeColors.object': '#868e96',
      'ui.sceneTreeColors.shrub': '#40c057',
      'ui.sceneTreeColors.sky': '#4c6ef5',
      'ui.sceneTreeColors.tfrag': '#82c91e',
      'ui.sceneTreeColors.tie': '#15aabf',
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
