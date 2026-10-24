# MPERF performance checkpoint

Status: implementation checkpoint accepted; packaged qualification and frozen budgets pending.

This summarizes the October 23–24, 2026 development measurements that guided the
[MPERF milestone](../planning/milestones/MPERF-intermediate-performance.md).
Temporary tracing, capture commands, diagnostic probes and raw reports were removed
when preparing the checkpoint for commit. Production keeps the optimizations and
behavioral regression tests. These summaries do not establish packaged release
budgets or replace the [M6 qualification procedure](M6-performance-baseline.md).

## Measurement conditions

Measurements used Linux, a Ryzen 9 9950X3D, an RTX 5090 where graphics were involved,
.NET 10, and warm/uncontrolled OS caches. Native workflow captures exclude renderer
presentation unless explicitly stated. Comparisons ran sequentially without tests
or builds competing for resources. Fixtures were private copies; original projects,
the user catalog and source ISO were preserved.

Forge was based on `fa7d6d8119d9631b526f9b6fb4912ebae150deca`. SDK codec comparisons
used local changes based on `b6a5ae49ec67229335da3dd6415dad69ff7cee7a` and selector
`v0.9.1`. SDK changes still require their separate commit/release/pin workflow before
release qualification. Results below come from different controlled comparisons;
rows must not be summed or treated as one continuous benchmark.

## Catalog and preflight

| Comparison | Before median | After median | Boundary |
| --- | ---: | ---: | --- |
| Warm Moby metadata after snapshot reuse | 386 ms | 43 ms | Native request |
| Warm scene package after snapshot reuse | 437 ms | 46 ms | Native request, existing render cache |
| Warm preflight after separating creation work | 897 ms | 360 ms | Native request |
| Cold preflight after separating creation work | 3,038 ms | 1,891 ms | Fresh host process |
| Warm preflight after base-summary reuse | 349 ms | 65 ms | 30 trials per variant |

The summary-reuse comparison's cold median increased from 1,797 to 1,864 ms over
five trials, with overlapping ranges. First-visit latency remains open. Snapshots
retain content-based invalidation; catalog files are still read on each open.

## UYA extraction

The first persistence change reduced one fresh full-disc capture from about 291 to
87 seconds. It retained one durable flush per completed temporary file and atomic
publication, while introducing four bounded blob writers/verifiers.

The subsequent comparison selected sixteen workers using five fresh Hoven repair
trials per variant on both filesystems:

| Variant | ext4 median | btrfs median |
| --- | ---: | ---: |
| Previous implementation, 4 workers | 4.463 s | 2.300 s |
| Metadata comparisons optimized, 4 workers | 4.137 s | 2.274 s |
| Metadata comparisons optimized, 8 workers | 2.256 s | 1.458 s |
| Metadata comparisons optimized, 16 workers | 1.414 s | 1.077 s |

A fresh full-disc control/candidate pair improved from **90.03 to 39.57 seconds**.
Cumulative managed allocation fell from 41.71 to 39.57 GB; observed peak working set
rose from 834 to 927 MB. Blob preparation fell from 64.98 to 15.87 seconds, while
extraction/conversion stayed around 14 seconds. These full-disc variants each have
one sample and exclude source verification and UI overhead.

Both produce 51 completed levels, 77,964 appearances, 20,209 unique assets and zero
failures. All 866,734,428 canonical bytes per variant were rehashed with Asset ID
headers. IDs/bytes, metadata after omitting import timestamps, and exact completion
checkpoints match. All forty repair catalogs also match and pass blob verification.

## Creation and cold scene preparation

Creation overlaps full source verification with read-only base materialization.
Three paired trials per map used fresh processes, projects and render caches:

| Map / native operation | Before median | After median |
| --- | ---: | ---: |
| Hoven preflight | 1.886 s | 1.908 s |
| Hoven creation | 7.157 s | 5.633 s |
| Hoven cold render package | 4.055 s | 4.003 s |
| Hoven sequential total | 13.062 s | 11.551 s |
| marc2 preflight | 2.353 s | 2.446 s |
| marc2 creation | 9.386 s | 7.228 s |
| marc2 cold render package | 4.297 s | 4.304 s |
| marc2 sequential total | 16.074 s | 13.975 s |

Creation ranges were 5.604–5.670 seconds for Hoven and 7.184–7.524 for marc2.
All six pairs match project/render products, preflight counts, warnings and cache
identities. Comparisons normalize generated UUIDs/references, gzip encoding, SDK
performance durations and the corresponding cache-marker manifest length; actual
marker lengths are validated first. Collision-link lists are compared as sets.
An independent catalog-change run also verified preparation refresh before publication.

The earlier render-overlap comparison reduced cold package preparation from
5.486 to 4.187 seconds over five trials; thirty warm-cache trials stayed around
52 ms. Full-disc verification remains included in creation and render preparation.
The remaining roughly four-second cold package wait is largely verification-bound.
Renderer decode, GPU upload and first interactive frame were not measured here.

## Selection, viewport and explorer

- Compact selection results reduced dispatch through decoded/applied result from
  48.02 to 4.18 ms median. Responses were 104–3,777 bytes in the measured sequence,
  replacing an approximately 4 MB snapshot; selected Moby PVars remain available.
- Static instance candidate indexing reduced a synthetic click median from
  0.472 to 0.027 ms with matching selected IDs. Hover is coalesced once per frame.
- marc2 overlay changes reduced a measured overview CPU median from 17.0 to
  11.1 ms with exact framebuffer equality. Wireframe batching subsequently removed
  315 draws and saved 0.20–0.40 ms in measured views. Stable transparent material
  passes saved another 0.20–0.35 ms. These are separate comparisons and do not
  establish presented FPS or the 240 FPS target.
- Moby paging reduced measured grid-fill time from 206.3 to 82.7 ms by requesting
  128 variants per page. Full traversal, family and facet results match.
- Preview admission prioritizes visible cards within the four-job cap. Cold disk
  lookups use the same queue, preserving admission order. PNG encoding moved to a
  worker after renderer idle starvation was found; thumbnail teardown closes owned
  decoded images and skeleton resources. Deep-scroll and cold-TIE checks completed,
  but packaged browse latency and sustained memory budgets remain open.

## Build work

- Verified unchanged patch ranges avoid rewriting the development ISO. Hoven's
  unchanged host-build median fell from 4,336 to 2,978 ms; patch application fell
  from 1,407 to 35 ms. Output verification and recovery checks remain in place.
- SDK WAD changes improved isolated compression by about 30% and decoding by about
  16%. Fifteen paired builds retained identical level-WAD hashes; total build
  medians improved 1.7–3.2% with overlapping ranges.
- Palette report comparison fell from 15.48 to 2.28 ms and avoided roughly 28 MB
  per call. Unchanged bake/pack pairs allocated about 84.5 MB less per run without
  a demonstrated overall latency improvement.
- Incremental renderer/test TypeScript checks improved unchanged/edit desktop
  builds; all thirty paired output maps matched. Clean-build medians did not improve.

Focused SDK codec tests, 520 native/browser buffer parity cases and the generated
UYA frontend build pass. The broader SDK suite previously stopped at the same
archive-replacement assertion in baseline and candidate; it is not qualified here.

## Remaining qualification

Packaged Linux/Windows, recommended/low-end hardware, source-storage variation,
end-to-end readiness/highlight/presentation, full/resumed extraction matrices,
contention, tracing overhead for future measurement tools, and memory soaks remain
open. Freeze numeric budgets against that evidence before marking the milestone
complete. The [task register](../planning/tasks/MPERF-tasks.md) records the remaining work.
