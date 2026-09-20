import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  isAllowedNavigation,
  isPathInside,
  isTrustedSender,
} from '../dist-electron/main/Security.js';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

test('IPC sender and navigation checks are deny-by-default', () => {
  assert.equal(isTrustedSender(7, 7), true);
  assert.equal(isTrustedSender(7, 8), false);
  assert.equal(isTrustedSender(7, undefined), false);

  const applicationUrl = 'file:///opt/forge/dist/index.html';
  assert.equal(isAllowedNavigation(applicationUrl, applicationUrl), true);
  assert.equal(isAllowedNavigation(`${applicationUrl}#viewport`, applicationUrl), true);
  assert.equal(isAllowedNavigation('https://example.com/', applicationUrl), false);
  assert.equal(isAllowedNavigation('not a url', applicationUrl), false);
});

test('asset paths cannot escape their configured root', () => {
  assert.equal(isPathInside('/maps/level3', '/maps/level3/terrain.gltf'), true);
  assert.equal(isPathInside('/maps/level3', '/maps/level30/terrain.gltf'), false);
  assert.equal(isPathInside('/maps/level3', '/maps/terrain.gltf'), false);
});

test('renderer CSP excludes remote scripts and unsafe evaluation', async () => {
  const html = await readFile(path.join(projectRoot, 'index.html'), 'utf8');
  const policy = html.match(/Content-Security-Policy"\s+content="([^"]+)"/)?.[1];
  assert.ok(policy);
  assert.match(policy, /default-src 'self'/);
  assert.match(policy, /script-src 'self'/);
  assert.doesNotMatch(policy, /unsafe-eval|https?:/);
});
