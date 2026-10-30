import { app } from 'electron';
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

import type { BuildPatchProgress } from '../types/ForgeApi.js';
import type { OcclusionBakeSummary, OcclusionSamplePreview, OcclusionVisibilityData } from '../types/Occlusion.js';
import { hostLaunch } from './HostLaunch.js';
import { readOcclusionSamplePreview } from './OcclusionSamplePreview.js';
import { readOcclusionVisibility } from './OcclusionVisibility.js';

export async function rebuildOcclusion(
  projectRoot: string, catalogRoot: string, sourceIso: string, signal: AbortSignal,
  progress: (value: BuildPatchProgress) => void,
  samplePullIn = 2,
  pixelEdgeAllowance = false,
): Promise<OcclusionBakeSummary> {
  return withPreparedScene(projectRoot, catalogRoot, sourceIso, signal, progress, samplePullIn, pixelEdgeAllowance, async (directory, run) => {
    await run(process.execPath, [...(app.isPackaged ? [] : [app.getAppPath()]), '--forge-occlusion-worker', directory], 'Occlusion', true);
    const host = hostLaunch();
    const summary = await run(host.command, [...host.args, '--occlusion-commit', projectRoot, directory], 'Validate occlusion');
    if (!summary) throw new Error('Occlusion worker did not return a completion summary.');
    return summary;
  });
}

export async function previewOcclusionSamples(
  projectRoot: string, catalogRoot: string, sourceIso: string, signal: AbortSignal,
  progress: (value: BuildPatchProgress) => void,
  samplePullIn = 2,
  pixelEdgeAllowance = false,
): Promise<OcclusionSamplePreview> {
  return withPreparedScene(projectRoot, catalogRoot, sourceIso, signal, progress, samplePullIn, pixelEdgeAllowance,
    directory => readOcclusionSamplePreview(directory, signal));
}

async function withPreparedScene<T>(
  projectRoot: string, catalogRoot: string, sourceIso: string, signal: AbortSignal,
  progress: (value: BuildPatchProgress) => void,
  samplePullIn: number,
  pixelEdgeAllowance: boolean,
  action: (directory: string, run: (command: string, args: string[], phase: string, gpu?: boolean) => Promise<OcclusionBakeSummary | undefined>) => Promise<T>,
): Promise<T> {
  if (!Number.isInteger(samplePullIn) || samplePullIn < 0 || samplePullIn > 5)
    throw new TypeError('Occlusion sample pull-in must be a whole number from 0 to 5.');
  if (typeof pixelEdgeAllowance !== 'boolean') throw new TypeError('Occlusion pixel edge allowance must be a boolean.');
  return withOcclusionJob(signal, progress, async (directory, run) => {
    const host = hostLaunch();
    progress({ phase: 'Occlusion', completed: 0, total: 1, message: 'Preparing the current project scene.' });
    await run(host.command, [...host.args, '--occlusion-prepare', projectRoot, catalogRoot, sourceIso, directory, String(samplePullIn), String(pixelEdgeAllowance)], 'Prepare occlusion');
    return action(directory, run);
  });
}

export async function loadOcclusionVisibility(
  projectRoot: string, _catalogRoot: string, sourceIso: string, signal: AbortSignal,
  progress: (value: BuildPatchProgress) => void,
): Promise<OcclusionVisibilityData> {
  return withOcclusionJob(signal, progress, async (directory, run) => {
    const host = hostLaunch();
    await run(host.command, [...host.args, '--occlusion-visibility', projectRoot, sourceIso, directory], 'Load occlusion');
    return readOcclusionVisibility(directory, signal);
  });
}

async function withOcclusionJob<T>(signal: AbortSignal, progress: (value: BuildPatchProgress) => void,
  action: (directory: string, run: (command: string, args: string[], phase: string, gpu?: boolean) => Promise<OcclusionBakeSummary | undefined>) => Promise<T>,
): Promise<T> {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'forge-occlusion-job-'));
  const run = async (command: string, args: string[], phase: string, gpu = false) => {
    signal.throwIfAborted();
    let summary: OcclusionBakeSummary | undefined;
    await new Promise<void>((resolve, reject) => {
      const env = { ...process.env };
      if (gpu) delete env.ELECTRON_RUN_AS_NODE;
      const child = spawn(command, args, { env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
      let line = '', errors = '', timeout: NodeJS.Timeout | undefined;
      const cancel = () => { child.kill('SIGTERM'); timeout ??= setTimeout(() => child.kill('SIGKILL'), 5000); };
      signal.addEventListener('abort', cancel, { once: true });
      if (signal.aborted) cancel();
      child.stdout.on('data', (data: Buffer) => {
        line += data.toString();
        if (line.length > 65536) { errors = 'Occlusion worker progress exceeded its limit.'; child.kill(); return; }
        let end: number;
        while ((end = line.indexOf('\n')) >= 0) {
          const text = line.slice(0, end); line = line.slice(end + 1);
          let value: Record<string, unknown>;
          try { value = JSON.parse(text) as Record<string, unknown>; }
          catch { continue; }
          if (!value || typeof value !== 'object') continue;
          if (phase === 'Validate occlusion' && value.summary !== undefined) {
            const result = value.summary as OcclusionBakeSummary;
            if (!result || !Number.isSafeInteger(result.octants) || result.octants < 1
              || !Number.isSafeInteger(result.origins) || result.origins < 1
              || !Number.isFinite(result.wallMs) || result.wallMs < 0
              || result.gpuMs !== null && (!Number.isFinite(result.gpuMs) || result.gpuMs < 0)) {
              errors = 'Occlusion worker returned an invalid completion summary.';
              child.kill();
              return;
            }
            summary = result;
          }
          if (Number.isInteger(value.completed) && Number.isInteger(value.total)
            && Number(value.completed) >= 0 && Number(value.completed) <= Number(value.total)
            && typeof value.message === 'string') progress({
            phase, completed: Number(value.completed), total: Number(value.total), message: value.message.slice(0, 1024),
          });
        }
      });
      child.stderr.on('data', (data: Buffer) => { errors = (errors + data.toString()).slice(-16384); });
      child.once('error', reject);
      child.once('close', (code) => {
        signal.removeEventListener('abort', cancel); if (timeout) clearTimeout(timeout);
        if (signal.aborted) reject(new Error('Occlusion rebuild cancelled.'));
        else if (code !== 0) reject(new Error(errors.trim() || `Occlusion ${phase} failed (${code}).`));
        else resolve();
      });
    });
    signal.throwIfAborted();
    return summary;
  };
  try { return await action(directory, run); }
  // Chromium can keep its profile locked until the GPU worker has fully exited.
  finally { await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); }
}
