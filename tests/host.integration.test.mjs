import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import {
  HostClient,
  HostOperationError,
} from '../dist-electron/main/bridge/HostClient.js';
import { BridgeErrorCode } from '../dist-electron/main/bridge/FrameCodec.js';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const hostDll = path.join(projectRoot, 'src/Forge.Host/bin/Release/net10.0/Forge.Host.dll');

test('host handshake, echo, progress, cancellation, crash recovery, and concurrency', { timeout: 30_000 }, async (context) => {
  const client = new HostClient('dotnet', [hostDll], 25);
  context.after(() => client.stop());

  const expectedRevision = (await readFile(path.join(projectRoot, 'ratchet-sdk.version'), 'utf8')).trim();
  const handshake = await client.start();
  assert.equal(handshake.sdkRevision, expectedRevision);
  assert.deepEqual(handshake.supportedGames, ['UYA']);
  assert.ok(handshake.capabilities.includes('bridge.cancellation'));

  const concurrent = await Promise.all([
    client.echo('alpha'),
    client.echo('beta'),
    client.echo('gamma'),
  ]);
  assert.deepEqual(await Promise.all(concurrent.map((request) => request.result)), ['alpha', 'beta', 'gamma']);

  const progress = [];
  let slowRequest;
  slowRequest = await client.echo('cancel me', 500, (value) => {
    progress.push(value);
    if (progress.length === 1) void client.cancel(slowRequest.requestId);
  });
  await assert.rejects(
    slowRequest.result,
    (error) => error instanceof HostOperationError && error.code === BridgeErrorCode.Cancelled,
  );
  assert.ok(progress.length >= 1);

  const doomed = await client.echo('crash me', 2_000);
  const oldProcessId = client.processId;
  assert.ok(oldProcessId);
  process.kill(oldProcessId);
  await assert.rejects(doomed.result, /Forge host exited/);

  await waitForRestart(client, oldProcessId);
  const recovered = await client.echo('recovered');
  assert.equal(await recovered.result, 'recovered');
});

async function waitForRestart(client, previousProcessId) {
  for (let attempt = 0; attempt < 100; attempt++) {
    try {
      await client.start();
      if (client.processId && client.processId !== previousProcessId) return;
    } catch {
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  throw new Error('Forge host did not restart');
}
