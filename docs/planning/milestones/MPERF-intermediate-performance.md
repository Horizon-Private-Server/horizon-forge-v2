# MPERF — Intermediate performance improvements

Status: Implementation checkpoint accepted; full matrix and budgets pending MPERF-001  
Priority: Intermediate stabilization before further feature expansion  
Depends on: Existing implemented project, catalog, editor, and build workflows  
Task register: [MPERF tasks](../tasks/MPERF-tasks.md)

## Outcome

Existing Forge workflows become measurably faster without changing authored content,
weakening validation, or trading latency for unbounded memory. This milestone covers
UYA extraction, project creation, scene preparation, selection, viewport rendering,
Moby browsing, map builds, and Forge application builds.

The [base specification](../../forge-v2-spec.md), especially NFR-PERF-001 through
NFR-PERF-006, remains authoritative. This milestone makes those requirements concrete
for the current feature set. It can run before unfinished P3 features and does not
depend on M18 occlusion generation. Existing M6 and P3 qualification gates still apply.

## Observations and investigation boundaries

These are code paths to profile, not measured root causes or proof of a speedup.

| Reported delay | Current implementation to investigate |
| --- | --- |
| UYA asset extraction | `UyaAssetImportService.ReadAssets` invokes the SDK asset extractor and then unpacks the WAD for other families; import processes levels sequentially and commits a catalog batch per level. Measure repeated decoding, canonicalization, hashing, blob writes, and catalog commits separately. |
| Map selection to Create enabled | `ProjectHub` waits for preflight; `UyaProjectService.Preflight` calls `ReadBase`, also used by creation. Measure full base hydration and repeated work between preflight and creation. |
| Map render-package preparation | `UyaRenderPackageService` resolves project assets before checking its package cache, converts assets sequentially on a miss, and materializes individual files. Measure resolution, verification, conversion, publication, and renderer loading separately. |
| Click to visible selection | `SceneViewport` recursively raycasts scene roots for hover and click; `SceneProjection.sync` scans and groups entities. Measure raycast, selection dispatch, host response, projection, properties, outlines, and first presented frame. |
| Low viewport FPS | The viewport already uses instancing, batching, and an effect composer. Measure remaining draw submissions, material groups, culling, billboards, overlays, selection passes, simulation, and GPU time before changing these systems. |
| Slow Moby grid | Each explorer request opens the catalog; paging scans and sorts matches and computes facets. Page payloads include source metadata and Moby sound slots. The UI already pages, virtualizes, and queues thumbnails; measure each stage independently. |
| Slower builds | Separate map bake/pack/patch from application compile/bundle/package time. `package.json` builds the desktop and host sequentially; the host references the SDK and uses constrained MSBuild parallelism. Measure these choices before changing them. |

## Scope decisions

- Profile the whole user action, including queues and bridge transfers. Moving work
  behind a spinner or into another phase does not count as a latency improvement.
- Prefer eliminating repeated reads, conversions, scans, allocations, and uploads
  before increasing parallelism. Any concurrency has explicit CPU, memory, and I/O
  bounds; interactive work takes priority over background previews.
- Reuse existing catalog paging, caches, batching, staging, and performance reports.
  Replace a foundation only when a retained profile and comparison justify it.
- Keep parsing/conversion in the SDK, orchestration in Forge.Host, privileged access
  in Electron, and presentation in React/Three.js. Keep shared infrastructure neutral
  and UYA-specific behavior in its game adapter; add no speculative game interfaces.
- Cover both meanings of build time: user map builds and contributor application
  builds. A packaging shortcut cannot weaken release checks or the pinned SDK contract.
- New rendering backends, native occlusion generation, new asset families, and a
  wholesale project-format rewrite are outside this milestone.

## Functional requirements

### FR-PERF-001: Attributable workflow measurements

- Qualification MUST correlate a user action with host work,
  bridge queue/transfer/decode time, renderer work, and its visible completion.
  Use process-local monotonic durations and correlation IDs, not unsynchronized clocks.
  Temporary or external instrumentation may provide these measurements; permanent
  production tracing and capture commands are not required for this checkpoint.
- Captures MUST distinguish cold application caches, warm caches, source validation,
  and OS-cache conditions. Record cache hits/misses and reasons, work counts, bytes
  read/written/transferred, allocations, peak working set, and frame/loop timings
  where relevant. Unavailable counters must be identified explicitly.
- Diagnostics MUST be bounded and sanitized, exclude proprietary payloads and local
  paths, and have negligible overhead when disabled. Reports identify Forge/SDK
  revisions, hardware/storage, fixture identity/counts, settings, and capture procedure.
- MPERF-001 MUST retain baselines and freeze numeric budgets before optimization
  tasks are accepted. A budget adjustment requires a recorded explanation and review.

### FR-PERF-002: Efficient catalog access and publication

- Repeated reads of an unchanged catalog MUST reuse a validated revision/snapshot
  rather than repeatedly deserialize and fingerprint the whole catalog per request.
  Query ordering, facets, filtering, class families, and cursor semantics remain stable.
- Common ID, kind, source, and class queries MUST use measured reusable lookup/order
  structures where they remove repeated full scans. Search indexes, if needed, must
  stay bounded and invalidate with their owning revision.
- Import publication MUST avoid repeatedly rebuilding unchanged metadata as the
  catalog grows. Commit/checkpoint policy must preserve resume and repair behavior.
- Reuse MUST account for writes from other store instances/processes, repair, removal,
  and external replacement. Readers see a consistent committed revision; stale cursors
  remain actionable. A persistent-store migration is justified only by MPERF-001 data.

### FR-PERF-003: Faster complete and resumed UYA extraction

- Extraction MUST account separately for ISO verification/read, decompression,
  per-family extraction, canonical encoding, hashing/deduplication, blob persistence,
  catalog publication, and resume-state persistence.
- The pipeline MUST reuse decoded source data when compatible SDK operations need
  the same bytes, avoid unnecessary whole-payload copies, and avoid reconverting
  identical content when identity and converter version prove reuse safe.
- Any pipeline parallelism MUST respect stream ownership and bounded in-flight bytes.
  Progress and cancellation checkpoints must remain available within long levels.
- Full, resumed, repair, and already-complete imports MUST retain the same supported
  asset coverage, identities, provenance, sound dependencies, and failure diagnostics.
  A level becomes complete only after its required outputs are durably published.

### FR-PERF-004: Fast and correct project readiness

- Map selection MUST enable Create after the minimum authoritative compatibility,
  availability, and warning checks complete. Rendering and thumbnail work MUST NOT
  gate readiness. Deferred checks must still complete before project publication.
- Preflight and creation MUST share safely reusable results or avoid duplicate full
  hydration. Reuse is keyed by source identity, map, catalog revision, relevant
  options, and parser/importer version; changed inputs invalidate it.
- Rapid map changes MUST cancel or supersede obsolete preflight work. Only the
  currently selected map's result may enable Create or determine its warnings.
- Capture map-selection-to-enabled, Create-to-project-initialized, and
  Create-to-interactive-scene independently. Partial-content consent, source
  verification, error recovery, and atomic project creation remain required.

### FR-PERF-005: Reusable scene preparation and bounded loading

- Scene preparation MUST distinguish host cache resolution, source parsing, asset
  conversion, file publication, protocol transfer, glTF/image decode, GPU upload,
  and first interactive frame. A host cache hit alone is not completion.
- Unchanged asset render products MUST be reusable across projects and map package
  rebuilds using content identity, effective overrides, SDK/converter version, and
  relevant render options. Per-project composition must not reconvert every asset.
- Cache hits MUST avoid source extraction/conversion while retaining required
  integrity checks. Corrupt, incompatible, missing, or replaced outputs must trigger
  bounded repair or actionable failure, never silently serve stale geometry.
- Loading/conversion MUST have bounded concurrency, cancellation, deduplicated
  in-flight requests, and disposal on superseded project loads. Incremental loading,
  if used, must expose incomplete content and cannot report a complete scene early.

### FR-PERF-006: Low-latency picking and selection updates

- Hover/click picking MUST narrow candidate objects before expensive mesh tests;
  reuse bounds or another measured acceleration strategy appropriate to the scene.
  Hover work must be coalesced so redundant pointer events cannot consume the frame.
- Selection-only changes MUST update affected highlights, gizmos, property views,
  and selection state without rebuilding unrelated geometry, batches, or textures.
- Preserve nearest-hit and pick-through behavior, hidden/locked rules, instance and
  batch IDs, volumes, spline points, simulation proxies, and modifier selection.
  Any optimistic feedback must reconcile with authoritative host state safely.
- Measure input-to-visible-highlight and input-to-correct-properties separately,
  including bridge work. A faster raycast alone does not satisfy this requirement.

### FR-PERF-007: Lower frame cost with existing visual semantics

- Captures MUST separate CPU scene/update/submission cost, GPU cost when available,
  and calls/triangles per pass, including outlines, transparency, collision overlays,
  markers, sky, and simulations. Record median and tail frame times while navigating.
- Optimize measured hot paths with existing batching/instancing, shared resources,
  spatially useful culling, cached derived state, and limited per-frame allocations.
  Large batches must not silently defeat culling or stable picking identity.
- Preserve PS2 material behavior, transparency/order, lighting, layer visibility,
  collision tools, selection outlines, and simulation fidelity. Default quality and
  viewport resolution remain fixed during before/after comparisons.
- Repeated opens/closes MUST dispose resources; performance cannot depend on keeping
  every visited map, preview, geometry, or texture resident indefinitely.

### FR-PERF-008: Responsive Moby browsing

- Metadata rows/cards MUST populate independently of model conversion and thumbnail
  completion. Query, payload encode/decode, React commit, and first visible thumbnails
  have separate measurements, including dense class families and sound-rich entries.
- Preserve bounded paging, class-family/variant grouping, stable ordering, filters,
  facets, keyboard navigation, placement compatibility, and missing-blob diagnostics.
  Avoid transferring full detail/dependency metadata for each tile when a bounded
  summary and on-demand detail lookup meet the same behavior.
- Visible tiles take priority over overscan/background previews. Reuse thumbnails
  and render products, deduplicate requests, bound queues/caches, and cancel obsolete
  work when scrolling, searching, switching category, or closing the project.
- Search/filter changes MUST discard stale results; thumbnail failures must remain
  local to the affected tile and must not stall the list or viewport interaction.

### FR-PERF-009: Faster map build iteration

- Measure validation, dependency/fingerprint planning, bake, archive read/compose/
  compress, patch planning, patch write, and verification separately for full,
  unchanged, transform-only, texture-only, and imported-content builds.
- Unchanged layers MUST reuse valid staged output. Changes invalidate only the
  necessary dependency closure. Repeated planning must avoid redundant full-project
  hydration, catalog loading, hashing, and source decompression where reuse is valid.
- An unchanged build SHOULD avoid archive recomposition and patch writes when
  verified source, staging, toolchain, and development-target identities establish
  that the intended output is already present. Changed or externally modified targets
  must still be detected and verified.
- Preserve deterministic output, warning acknowledgement, selective/deferred layers,
  opaque/native content, patch recovery, source immutability, and cancellation safety.
  Forced full rebuild remains available as a correctness comparison.

### FR-PERF-010: Faster Forge application builds

- Record cold clean, warm no-change, renderer-only, Electron-only, and host/SDK-change
  builds separately, with restore/install/bootstrap and packaging measured separately.
- Remove repeated compile/bundle/restore work and introduce incremental or parallel
  execution only where output ownership and available memory make it safe.
- A source change MUST rebuild its affected outputs; SDK revision/configuration
  changes invalidate affected caches. No-change builds must skip avoidable work.
- Keep a reproducible clean Linux/Windows release path and all existing type, test,
  protocol, dependency-pin, and package checks. Development convenience is not evidence
  that packaged runtime performance improved.

## Proposed performance budgets

These are planning targets, not measured results or established hardware promises.
MPERF-001 freezes the fixture/tier matrix and final thresholds, retaining any reason
for a different target. Absolute latency targets below apply to the recommended
tier; low-end thresholds are frozen separately. All workflows must stay usable on
both tiers, including while background work is active.

| Workflow | Proposed acceptance target | Completion boundary |
| --- | --- | --- |
| Full UYA extraction | At least 50% lower median elapsed time than baseline | Verified source through committed catalog and complete resume state; verification also reported separately |
| Map selection/preflight | p95 ≤500 ms warm and ≤2 s cold | Selection event to correct Create state and warnings |
| Project initialization | At least 50% lower median elapsed time | Create activation through valid project publication; no cost shifted into scene load |
| Scene preparation | At least 50% lower cold median; warm p95 ≤1 s host preparation and ≤3 s to interactive scene | Project initialized through required geometry, textures, picking, and navigation ready |
| Selection | p95 ≤16.7 ms highlight and ≤100 ms correct properties; low-end highlight ≤33.3 ms | Selection-triggering input event through presentation, not just ray intersection |
| Viewport | Recommended median ≥60 FPS / 1% low ≥45 FPS; low-end ≥30 / ≥22 | Fixed camera route, dimensions, quality, overlays, and simulation state |
| Moby explorer | Metadata p95 ≤250 ms warm / ≤1 s cold; first viewport thumbnails ≤500 ms warm / ≤3 s cold | Category/search activation through usable cards, then visible previews; includes debounce |
| Map builds | At least 40% lower median incremental edit-to-verified-patch time; full build no slower than baseline | Build activation through verified development target |
| Application builds | At least 25% lower clean median and 50% lower affected incremental median | Command invocation through all required outputs; record no-change cost separately |

The RTX 5090 development target is sustained 240 FPS (4.17 ms/frame), including
the zoomed-out level04 `marc2` case with the normal collision, barrier, death-height
and simulation switches enabled. Keep this target separate from hardware-tier
minimums. Freeze viewport dimensions and a camera route before claiming it is
met; isolated CPU/GPU replay timings do not establish presented frame pacing.

Draw-call, CPU/GPU phase, peak-memory, disk-cache, and in-flight-byte caps are frozen
from MPERF-001 profiles per fixture. Lower draw-call counts alone are not a pass.
No latency win may breach those caps or the existing 50 ms p99 main/renderer
loop-delay limit under background work. Retain the
[M6 memory-soak limits](../../qualification/M6-performance-baseline.md).

## Qualification and exit gate

- Retain same-machine before/after measurement summaries and requirement-to-task
  results in `docs/qualification/MPERF-workflow-baselines.md`. Keep raw captures
  local and use the existing M6 reporting procedure for packaged qualification.
- Use small, dense, and largest-supported UYA NTSC-U maps plus a feature-rich project
  containing imported/custom content, sound dependencies, collision overlays, and
  simulations. Include a full Moby catalog and high-variant class families.
- Expensive extraction/build workflows use at least five independent before/after
  runs and median/range reporting. Readiness/explorer latency uses at least 30 trials
  per cache condition; selection uses at least 300 samples. Frame and memory captures
  retain the M6 procedure. Disclose OS-cache limitations and run-to-run variance.
- Run packaged Linux and Windows qualification on declared recommended/low-end
  hardware. Synthetic/redistributable fixtures cover CI correctness and work counts;
  proprietary corpus captures remain local, with sanitized summaries only committed.
- Every optimization passes output equivalence or an explicit semantic comparison,
  invalidation, missing/corrupt-cache, cancellation, interrupted-publication, stale
  result, and bounded-memory checks appropriate to its path. Include ten-cycle
  project open/close and explorer scroll/category-switch soaks.
- The milestone closes only when frozen budgets pass, original workflows remain
  correct and accessible, and profiling identifies no unresolved bottleneck that
  prevents a listed acceptance target. Record remaining opportunities separately.
