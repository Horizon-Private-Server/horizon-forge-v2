import assert from 'node:assert/strict';
import test from 'node:test';

import { createCaptureTemplate, createPerformanceReport, formatPerformanceReport } from '../scripts/performance-report.mjs';

const repeat = (value, count) => Array.from({ length: count }, () => value);

function capture(overrides = {}) {
  return {
    schemaVersion: 1,
    forgeVersion: '2.0.0-nightly.1.abc12345',
    capturedAt: '2026-09-23T12:00:00Z',
    runs: [{
      id: 'recommended-uya-level-01',
      tier: 'recommended',
      hardware: { os: 'Linux', cpu: 'Test CPU', gpu: 'Test GPU', memoryGiB: 16 },
      fixture: { game: 'UYA', region: 'NTSC-U', level: '01', entityCount: 4_589, triangleCount: 500_000 },
      settings: { viewportWidth: 1920, viewportHeight: 1080, pixelRatio: 1, durationSeconds: 60 },
      samples: {
        frameTimesMs: repeat(16, 300),
        interactionLatencyMs: repeat(10, 20),
        mainLoopDelayMs: repeat(20, 20),
        rendererLoopDelayMs: repeat(20, 20),
        hostMemoryMiB: [200, 205, 202, 206, 204, 207, 205, 208, 206, 209],
        rendererMemoryMiB: [500, 510, 505, 512, 508, 514, 511, 515, 513, 516],
        gpuMemoryMiB: [300, 305, 302, 306, 304, 307, 305, 308, 306, 309],
      },
      limits: {
        minimumMedianFps: 60,
        minimumOnePercentLowFps: 45,
        maximumInteractionP95Ms: 16.7,
        maximumBackgroundP99Ms: 50,
        maximumMemoryGrowthMiB: 128,
        maximumMemorySlopeMiBPerCycle: 8,
      },
      ...overrides,
    }],
  };
}

test('performance reports calculate release gates and reject weak captures', () => {
  const template = createCaptureTemplate('2.0.0', 'low-end', '2026-09-23T12:00:00Z');
  assert.equal(template.runs[0].limits.minimumMedianFps, 30);
  assert.equal(template.runs[0].samples.frameTimesMs.length, 0);

  const report = createPerformanceReport(capture());
  assert.equal(report.passed, true);
  assert.equal(report.runs[0].metrics.medianFps, 62.5);
  assert.equal(report.runs[0].metrics.onePercentLowFrameTimeMs, 16);
  assert.match(formatPerformanceReport(report), /recommended-uya-level-01.*PASS/);

  const slow = createPerformanceReport(capture({
    samples: {
      ...capture().runs[0].samples,
      frameTimesMs: repeat(40, 300),
    },
  }));
  assert.equal(slow.passed, false);
  assert.equal(slow.runs[0].checks.medianFps, false);
  assert.throws(
    () => createPerformanceReport(capture({ samples: { ...capture().runs[0].samples, frameTimesMs: [16] } })),
    /at least 300 samples/,
  );
});
