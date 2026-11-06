import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { chmod, copyFile, mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

test('published updater runs with only its executable copied to the activation folder', async () => {
  assert.ok(process.env.FORGE_TEST_UPDATER_PATH, 'Set FORGE_TEST_UPDATER_PATH to the published updater executable.');
  const directory = await mkdtemp(path.join(os.tmpdir(), 'forge-updater-package-'));
  try {
    const helper = path.join(directory, path.basename(process.env.FORGE_TEST_UPDATER_PATH));
    await copyFile(process.env.FORGE_TEST_UPDATER_PATH, helper);
    if (process.platform !== 'win32') await chmod(helper, 0o700);
    const result = await new Promise((resolve, reject) => {
      const child = spawn(helper, [], {
        cwd: directory, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], timeout: 30_000,
        env: { ...process.env, DOTNET_ROOT: path.join(directory, 'no-installed-runtime') },
      });
      let output = '';
      child.stdout.on('data', (chunk) => { output += chunk; });
      child.stderr.on('data', (chunk) => { output += chunk; });
      child.once('error', reject);
      child.once('close', (code) => resolve({ code, output }));
    });
    assert.equal(result.code, 1, result.output);
    // Invalid arguments must reach managed code, rather than fail in the native runtime loader.
    assert.match(await readFile(path.join(directory, 'failure.txt'), 'utf8'), /Usage: Forge\.Updater/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
