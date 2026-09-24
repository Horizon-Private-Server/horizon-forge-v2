# M6 performance and memory baseline

Status: capture tooling ready; recommended and low-end packaged-app records pending

This qualification uses the largest supported UYA NTSC-U project available to the
tester. Record its level, entity count, and rendered triangle count so later releases
use the same fixture. Do not commit project paths, ISO metadata, or proprietary data.

## Provisional tiers and limits

| Tier | Median FPS | 1% low FPS | Interaction p95 | Main/renderer loop-delay p99 |
| --- | ---: | ---: | ---: | ---: |
| Recommended | 60 | 45 | 16.7 ms | 50 ms |
| Low-end | 30 | 22 | 33.3 ms | 50 ms |

For the ten-cycle open/close soak, each of the Forge.Host, Electron renderer, and GPU
processes may grow by at most 128 MiB total and 8 MiB per cycle. These are provisional
release limits; revise them only in a reviewed capture file, not in the report script.

## Capture procedure

1. Generate a starter capture. The command fills in the Forge version, OS, CPU, RAM,
   provisional tier limits, and required sample fields without overwriting an existing file:

   `npm run performance:report -- --init capture.json recommended`

2. Use a packaged release build with viewport statistics enabled. Record OS, CPU, GPU,
   RAM, Forge version, viewport dimensions, scale factor, fixture counts, and settings.
3. After loading completes, warm the viewport for 30 seconds. Capture at least 60 seconds
   of consecutive frame intervals while flying through the densest area. Record at least
   300 frame intervals and 20 input-to-present samples covering selection and transforms.
4. Run one bake/pack/patch while sampling the Electron main and renderer event-loop delay.
   Record at least 20 samples from each loop; use the same project and viewport settings.
5. Close and reopen the project ten times. After each close and a ten-second idle period,
   record resident memory for Forge.Host, the Electron renderer, and the Electron GPU
   process. Use Electron/Chromium process metrics or the OS process monitor consistently.
6. Put the raw numeric samples in the generated capture JSON. Generate
   the sanitized, versioned reports with:

   `npm run performance:report -- capture.json report.json report.md`

The command exits nonzero when a declared release limit fails. Check both generated
files into `docs/qualification/reports/` only after reviewing them for local paths or
source-game data.

## Capture format

```json
{
  "schemaVersion": 1,
  "forgeVersion": "2.0.0-nightly.RUN.SHA",
  "capturedAt": "2026-09-23T12:00:00Z",
  "runs": [{
    "id": "recommended-uya-level-NN",
    "tier": "recommended",
    "hardware": { "os": "...", "cpu": "...", "gpu": "...", "memoryGiB": 16 },
    "fixture": {
      "game": "UYA", "region": "NTSC-U", "level": "NN",
      "entityCount": 0, "triangleCount": 0
    },
    "settings": {
      "viewportWidth": 1920, "viewportHeight": 1080,
      "pixelRatio": 1, "durationSeconds": 60
    },
    "samples": {
      "frameTimesMs": [],
      "interactionLatencyMs": [],
      "mainLoopDelayMs": [],
      "rendererLoopDelayMs": [],
      "hostMemoryMiB": [],
      "rendererMemoryMiB": [],
      "gpuMemoryMiB": []
    },
    "limits": {
      "minimumMedianFps": 60,
      "minimumOnePercentLowFps": 45,
      "maximumInteractionP95Ms": 16.7,
      "maximumBackgroundP99Ms": 50,
      "maximumMemoryGrowthMiB": 128,
      "maximumMemorySlopeMiBPerCycle": 8
    }
  }]
}
```

The report calculates the median frame time, 99th-percentile frame time (the 1% low),
input p95, both loop-delay p99 values, and per-process memory growth and trend. A complete
M6-005 matrix contains one passing run for each hardware tier using the same fixture.
