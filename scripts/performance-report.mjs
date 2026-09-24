import { readFile, writeFile } from 'node:fs/promises';
import { cpus, platform, release, totalmem } from 'node:os';
import { pathToFileURL } from 'node:url';

const round = (value) => Math.round(value * 1_000) / 1_000;

function percentile(values, fraction) {
  const sorted = [...values].sort((left, right) => left - right);
  const index = Math.min(sorted.length - 1, Math.ceil(sorted.length * fraction) - 1);
  return sorted[index];
}

function median(values) {
  const sorted = [...values].sort((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0
    ? (sorted[middle - 1] + sorted[middle]) / 2
    : sorted[middle];
}

function slope(values) {
  const center = (values.length - 1) / 2;
  const valueCenter = values.reduce((total, value) => total + value, 0) / values.length;
  let numerator = 0;
  let denominator = 0;
  for (let index = 0; index < values.length; index += 1) {
    const distance = index - center;
    numerator += distance * (values[index] - valueCenter);
    denominator += distance * distance;
  }
  return denominator === 0 ? 0 : numerator / denominator;
}

function requireText(value, name) {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new Error(`${name} must be a non-empty string.`);
  }
}

function requireNumber(value, name) {
  if (!Number.isFinite(value) || value < 0) {
    throw new Error(`${name} must be a non-negative finite number.`);
  }
}

function requireSamples(values, name, minimum) {
  if (!Array.isArray(values) || values.length < minimum) {
    throw new Error(`${name} must contain at least ${minimum} samples.`);
  }
  values.forEach((value, index) => requireNumber(value, `${name}[${index}]`));
}

function validateRun(run, index) {
  const prefix = `runs[${index}]`;
  requireText(run.id, `${prefix}.id`);
  if (!['recommended', 'low-end'].includes(run.tier)) {
    throw new Error(`${prefix}.tier must be recommended or low-end.`);
  }
  for (const field of ['os', 'cpu', 'gpu']) requireText(run.hardware?.[field], `${prefix}.hardware.${field}`);
  requireNumber(run.hardware?.memoryGiB, `${prefix}.hardware.memoryGiB`);
  for (const field of ['game', 'region', 'level']) requireText(run.fixture?.[field], `${prefix}.fixture.${field}`);
  requireNumber(run.fixture?.entityCount, `${prefix}.fixture.entityCount`);
  requireNumber(run.fixture?.triangleCount, `${prefix}.fixture.triangleCount`);
  requireNumber(run.settings?.viewportWidth, `${prefix}.settings.viewportWidth`);
  requireNumber(run.settings?.viewportHeight, `${prefix}.settings.viewportHeight`);
  requireNumber(run.settings?.pixelRatio, `${prefix}.settings.pixelRatio`);
  requireNumber(run.settings?.durationSeconds, `${prefix}.settings.durationSeconds`);
  requireSamples(run.samples?.frameTimesMs, `${prefix}.samples.frameTimesMs`, 300);
  requireSamples(run.samples?.interactionLatencyMs, `${prefix}.samples.interactionLatencyMs`, 20);
  requireSamples(run.samples?.mainLoopDelayMs, `${prefix}.samples.mainLoopDelayMs`, 20);
  requireSamples(run.samples?.rendererLoopDelayMs, `${prefix}.samples.rendererLoopDelayMs`, 20);
  requireSamples(run.samples?.hostMemoryMiB, `${prefix}.samples.hostMemoryMiB`, 10);
  requireSamples(run.samples?.rendererMemoryMiB, `${prefix}.samples.rendererMemoryMiB`, 10);
  requireSamples(run.samples?.gpuMemoryMiB, `${prefix}.samples.gpuMemoryMiB`, 10);
  for (const field of [
    'minimumMedianFps',
    'minimumOnePercentLowFps',
    'maximumInteractionP95Ms',
    'maximumBackgroundP99Ms',
    'maximumMemoryGrowthMiB',
    'maximumMemorySlopeMiBPerCycle',
  ]) requireNumber(run.limits?.[field], `${prefix}.limits.${field}`);
}

function summarizeRun(run) {
  const frameMedian = median(run.samples.frameTimesMs);
  const frameP99 = percentile(run.samples.frameTimesMs, 0.99);
  const memory = Object.fromEntries(['host', 'renderer', 'gpu'].map((name) => {
    const values = run.samples[`${name}MemoryMiB`];
    return [name, {
      firstMiB: round(values[0]),
      peakMiB: round(Math.max(...values)),
      growthMiB: round(Math.max(0, values.at(-1) - values[0])),
      slopeMiBPerCycle: round(Math.max(0, slope(values))),
    }];
  }));
  const metrics = {
    medianFrameTimeMs: round(frameMedian),
    onePercentLowFrameTimeMs: round(frameP99),
    medianFps: round(1_000 / frameMedian),
    onePercentLowFps: round(1_000 / frameP99),
    interactionP95Ms: round(percentile(run.samples.interactionLatencyMs, 0.95)),
    mainLoopDelayP99Ms: round(percentile(run.samples.mainLoopDelayMs, 0.99)),
    rendererLoopDelayP99Ms: round(percentile(run.samples.rendererLoopDelayMs, 0.99)),
    memory,
  };
  const checks = {
    medianFps: metrics.medianFps >= run.limits.minimumMedianFps,
    onePercentLowFps: metrics.onePercentLowFps >= run.limits.minimumOnePercentLowFps,
    interactionLatency: metrics.interactionP95Ms <= run.limits.maximumInteractionP95Ms,
    mainLoopDelay: metrics.mainLoopDelayP99Ms <= run.limits.maximumBackgroundP99Ms,
    rendererLoopDelay: metrics.rendererLoopDelayP99Ms <= run.limits.maximumBackgroundP99Ms,
    memoryGrowth: Object.values(memory).every(({ growthMiB }) => growthMiB <= run.limits.maximumMemoryGrowthMiB),
    memorySlope: Object.values(memory).every(
      ({ slopeMiBPerCycle }) => slopeMiBPerCycle <= run.limits.maximumMemorySlopeMiBPerCycle),
  };
  return { ...run, metrics, checks, passed: Object.values(checks).every(Boolean) };
}

export function createPerformanceReport(capture) {
  if (capture?.schemaVersion !== 1) throw new Error('schemaVersion must be 1.');
  requireText(capture.forgeVersion, 'forgeVersion');
  requireText(capture.capturedAt, 'capturedAt');
  if (!Array.isArray(capture.runs) || capture.runs.length === 0) {
    throw new Error('runs must contain at least one benchmark run.');
  }
  capture.runs.forEach(validateRun);
  const runs = capture.runs.map(summarizeRun);
  return {
    schemaVersion: 1,
    forgeVersion: capture.forgeVersion,
    capturedAt: capture.capturedAt,
    passed: runs.every(({ passed }) => passed),
    runs,
  };
}

export function createCaptureTemplate(forgeVersion, tier = 'recommended', capturedAt = new Date().toISOString()) {
  if (!['recommended', 'low-end'].includes(tier)) throw new Error('tier must be recommended or low-end.');
  const recommended = tier === 'recommended';
  return {
    schemaVersion: 1,
    forgeVersion,
    capturedAt,
    runs: [{
      id: `${tier}-uya-level-NN`,
      tier,
      hardware: {
        os: `${platform()} ${release()}`,
        cpu: cpus()[0]?.model ?? 'Unknown',
        gpu: 'REPLACE WITH GPU',
        memoryGiB: round(totalmem() / 1024 ** 3),
      },
      fixture: { game: 'UYA', region: 'NTSC-U', level: 'NN', entityCount: 0, triangleCount: 0 },
      settings: { viewportWidth: 0, viewportHeight: 0, pixelRatio: 1, durationSeconds: 60 },
      samples: {
        frameTimesMs: [],
        interactionLatencyMs: [],
        mainLoopDelayMs: [],
        rendererLoopDelayMs: [],
        hostMemoryMiB: [],
        rendererMemoryMiB: [],
        gpuMemoryMiB: [],
      },
      limits: {
        minimumMedianFps: recommended ? 60 : 30,
        minimumOnePercentLowFps: recommended ? 45 : 22,
        maximumInteractionP95Ms: recommended ? 16.7 : 33.3,
        maximumBackgroundP99Ms: 50,
        maximumMemoryGrowthMiB: 128,
        maximumMemorySlopeMiBPerCycle: 8,
      },
    }],
  };
}

export function formatPerformanceReport(report) {
  const rows = report.runs.map((run) => [
    run.id,
    run.tier,
    `${run.hardware.cpu} / ${run.hardware.gpu}`,
    `${run.fixture.game} ${run.fixture.region} ${run.fixture.level}`,
    `${run.settings.viewportWidth}x${run.settings.viewportHeight} @ ${run.settings.pixelRatio}x`,
    `${run.metrics.medianFrameTimeMs} ms / ${run.metrics.medianFps} FPS`,
    `${run.metrics.onePercentLowFrameTimeMs} ms / ${run.metrics.onePercentLowFps} FPS`,
    `${run.metrics.interactionP95Ms} ms`,
    `${run.metrics.mainLoopDelayP99Ms} / ${run.metrics.rendererLoopDelayP99Ms} ms`,
    `${run.metrics.memory.host.growthMiB} / ${run.metrics.memory.renderer.growthMiB} / ${run.metrics.memory.gpu.growthMiB} MiB`,
    run.passed ? 'PASS' : 'FAIL',
  ]);
  return [
    '# Horizon Forge performance baseline',
    '',
    `Forge: ${report.forgeVersion}  `,
    `Captured: ${report.capturedAt}  `,
    `Result: **${report.passed ? 'PASS' : 'FAIL'}**`,
    '',
    '| Run | Tier | CPU / GPU | Fixture | Viewport | Median | 1% low | Interaction p95 | Main / renderer p99 | Host / renderer / GPU growth | Result |',
    '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |',
    ...rows.map((row) => `| ${row.join(' | ')} |`),
    '',
  ].join('\n');
}

async function main() {
  const [inputPath, jsonPath, markdownPath] = process.argv.slice(2);
  if (inputPath === '--init') {
    if (!jsonPath) throw new Error('Usage: node scripts/performance-report.mjs --init <capture.json> [recommended|low-end]');
    const packageJson = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
    await writeFile(jsonPath, `${JSON.stringify(createCaptureTemplate(packageJson.version, markdownPath), null, 2)}\n`, { flag: 'wx' });
    console.log(`Created ${jsonPath}. Replace fixture details and add measured samples before generating a report.`);
    return;
  }
  if (!inputPath || !jsonPath || !markdownPath) {
    throw new Error('Usage: node scripts/performance-report.mjs <capture.json> <report.json> <report.md>\n'
      + 'Create a starter file with: node scripts/performance-report.mjs --init <capture.json> [recommended|low-end]');
  }
  let contents;
  try {
    contents = await readFile(inputPath, 'utf8');
  } catch (error) {
    if (error?.code === 'ENOENT') {
      throw new Error(`Capture file '${inputPath}' does not exist. Create it with --init first.`);
    }
    throw error;
  }
  const report = createPerformanceReport(JSON.parse(contents));
  await Promise.all([
    writeFile(jsonPath, `${JSON.stringify(report, null, 2)}\n`),
    writeFile(markdownPath, formatPerformanceReport(report)),
  ]);
  if (!report.passed) process.exitCode = 1;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
