import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { RecentProjects } from '../src/main/RecentProjects.ts';
import { safeProjectDirectoryName } from '../src/utils/ApplicationPaths.ts';

test('project names produce safe directory names', () => {
  assert.equal(safeProjectDirectoryName('  Daxx / Test.  '), 'Daxx - Test');
  assert.equal(safeProjectDirectoryName('...'), 'New Project');
});

test('recent projects are unique, ordered, bounded, and removable', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'forge-recents-'));
  try {
    const file = path.join(directory, 'recent-projects.json');
    const recents = new RecentProjects(file);
    for (let index = 0; index < 22; index++) await recents.add(path.join(directory, `project-${index}`));
    await recents.add(path.join(directory, 'project-10'));
    const projects = await recents.list();
    assert.equal(projects.length, RecentProjects.maxProjects);
    assert.equal(projects[0], path.join(directory, 'project-10'));
    assert.equal(new Set(projects).size, projects.length);
    await recents.remove(projects[0]!);
    assert.equal((await recents.list()).includes(projects[0]!), false);
    assert.deepEqual(JSON.parse(await readFile(file, 'utf8')).version, 0);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
