# MPERF task register

Status: implementation checkpoint accepted; full milestone qualification pending.

The existing workflow improvements are ready for commit. Temporary performance
instrumentation, probes and raw captures have been retired. The
[measurement summary](../../qualification/MPERF-workflow-baselines.md) retains the
observed results and limitations; the [milestone](../milestones/MPERF-intermediate-performance.md)
retains functional requirements and proposed budgets. No task below claims that
packaged acceptance budgets have passed.

## Delivery order

MPERF-001 measurements inform every optimization. MPERF-002 catalog work supports
MPERF-003 extraction, MPERF-004 creation and MPERF-008 explorer work. MPERF-004
feeds MPERF-005 scene readiness. MPERF-006 selection and MPERF-007 viewport work
share renderer correctness checks. MPERF-009 map builds and MPERF-010 application
builds can progress independently. MPERF-011 closes the platform/resource matrix.

## MPERF-001 — Measure existing workflows and freeze budgets

Status: Development comparisons captured; release matrix and budgets pending.

Requirements: FR-PERF-001, NFR-PERF-001 through NFR-PERF-006.

Completed: measured catalog, preflight/creation, cold render packages, extraction,
selection, viewport, explorer and build costs. Retained concise results and explicit
measurement boundaries. Production contains no temporary tracing hooks or log writer.

Remaining:

- Measure user actions through visible completion, including React commit, renderer
  decode, image/GPU upload, highlight/properties and interactive readiness.
- Cover small/dense/largest maps, imported content, overlays and simulations;
  distinguish cold/warm application caches and source validation.
- Run declared packaged Linux/Windows hardware tiers, disclose unavailable counters
  and storage/cache limitations, and freeze budgets before acceptance.
- Use bounded temporary/external instrumentation for future captures; keep raw
  proprietary captures local and commit sanitized summaries.

## MPERF-002 — Reuse catalog snapshots and reduce scaling costs

Status: Snapshot reuse, indexes and coordinated publication implemented.

Requirements: FR-PERF-002, NFR-PERF-005, NFR-REL-002, NFR-SEC-003. Depends on MPERF-001.

Completed: content-validated bounded snapshots, reusable ID/kind/class ordering,
read-only opens, unchanged-metadata suppression, and serialized cross-process writer
leases. Readers retain a consistent committed snapshot. See the
[catalog contract](../../asset-catalog-v0.md).

Verification: query/facet/cursor equivalence, replacement and timestamp-preserving
edits, missing/malformed/oversized catalogs, shared concurrent readers, stale stores,
cross-process writers and failed publication. Remaining: larger catalog/query/commit
scaling measurements and packaged retention/resource budgets.

## MPERF-003 — Reduce UYA extraction and persistence work

Status: Buffered durable publication, bounded blob I/O and metadata comparisons implemented.

Requirements: FR-PERF-003, FR-PERF-002, NFR-PERF-004 through NFR-PERF-006.
Depends on MPERF-001 and MPERF-002.

Completed: deduplicate Asset IDs per batch; prepare blobs with at most sixteen
workers; retain ordered metadata merge, durable flush before rename, atomic catalog
replacement and per-level checkpoints. Typed source identity and definite-change
guards avoid redundant serialization. The latest full-disc comparison is 90.03 →
39.57 seconds with matching verified outputs.

Verification: all 20,209 canonical blobs, normalized metadata and exact checkpoints
match; source identity, sound enrichment, batch failures, cancellation/resume and
writer fixtures pass. Remaining: finer SDK conversion/preview-work profiles,
full/resumed/repair/already-complete platform matrix, contention and memory soaks.
The latest peak sample increased despite reduced cumulative allocation.

## MPERF-004 — Shorten preflight and verified creation

Status: Creation-only hydration deferred, bounded base summaries reused and creation overlapped.

Requirements: FR-PERF-004, NFR-REL-001 through NFR-REL-003, NFR-UX-001.
Depends on MPERF-001 and MPERF-002.

Completed: preflight uses read-only catalog access and defers HUD/FX, sound inventory,
terrain textures, collision links, PVars and opaque capture. Four content-hashed
summary entries retain counts without source/geometry retention. Creation overlaps
read-only materialization and full verification on one protected source handle,
refreshes changed catalog revisions, and revalidates the destination before publication.
Failures cancel/drain readers before source disposal.

Verification: full-read/preflight counts and warnings, source changes, catalog repair,
summary eviction/concurrency, invalid maps, partial acknowledgement, read-only access,
identity rejection, progress failure and cancellation. Remaining: first-visit and
Create-to-interactive-scene budgets, rapid A→B→A selection, superseded request
cancellation and packaged resource qualification.

## MPERF-005 — Shorten cold scene preparation

Status: Native preparation overlaps full source verification.

Requirements: FR-PERF-005, NFR-PERF-003 through NFR-PERF-005, NFR-REL-002.
Depends on MPERF-001, MPERF-002 and MPERF-004.

Completed: cold source verification and map products share one handle using offset
hash reads; publication waits for both to succeed. Progress is serialized and
monotonic; failures cancel/drain both tasks. Existing cache validation remains.

Verification: package equivalence, cold/warm/missing-source cache behavior, rejected
source, cancellation/progress failure and progress ordering. Remaining: renderer
conversion/upload and first-frame measurements, packaged end-to-end scene budgets,
invalidation coverage and ten-cycle open/close resource soaks.

## MPERF-006 — Make picking and selection responsive

Status: Compact selection patches, coalesced hover and static instance indexes implemented.

Requirements: FR-PERF-006, NFR-PERF-001 through NFR-PERF-003.
Depends on MPERF-001 and MPERF-002.

Completed: selection sends IDs/status and changed Moby PVars; unchanged entities and
instance buffers are reused. Hover queries coalesce once per frame. Static asset
bounds reduce candidate triangle tests and refit after transforms.

Verification: codec bounds/truncation, selection/PVar/history equivalence, viewport
occlusion and marker precedence, mirrored transforms, indexed/platform raycast
results, visibility, membership changes and teardown. Remaining: packaged click
through visible highlight/properties, dense-map latency and index memory budgets.

## MPERF-007 — Reduce viewport CPU work and draw calls

Status: Static matrix reuse, editor geometry batching and stable material passes implemented.

Requirements: FR-PERF-007, NFR-PERF-001 through NFR-PERF-003.
Depends on MPERF-001 and MPERF-006.

Completed: avoid rebuilding fixed world matrices; suppress unchanged instance writes;
batch editor wireframes while retaining picking objects; stabilize transparent
front/back materials without changing their draw ordering or picking semantics.

Verification: scene/selection/preview transforms, bounds and picking, hidden/locked
entities, mirrored models, material passes, disposal and measured framebuffer
comparisons. Remaining: packaged CPU/GPU/frame budgets, the 240 FPS development
target, feature-rich views and sustained renderer resource soaks.

## MPERF-008 — Populate the explorer and visible previews promptly

Status: Moby paging, visibility priority, worker PNG encoding and resource cleanup implemented.

Requirements: FR-PERF-008, NFR-PERF-003 through NFR-PERF-006, NFR-UX-001.
Depends on MPERF-001 and MPERF-002.

Completed: request 128 Moby variants per page, virtualize cards, prioritize visible
cards, and admit cold cache lookups through the four-slot queue. Retain cancellation
while host dispatch is pending. PNG workers capture distinct frames without waiting
for renderer idle time. Thumbnail disposal releases owned skeletons and decoded images.

Verification: paging/family/facet equivalence, current visibility/FIFO priority,
cancellation before/after dispatch and during persistence, cache hits after disposal,
worker errors/transfer/disposal, and deep-scroll/cold-TIE checks. Remaining: packaged
activation-to-visible-preview budgets, rapid category/search/scroll soaks and memory caps.

## MPERF-009 — Reduce map iteration work

Status: Unchanged patch writes eliminated, SDK codec improved and palette comparison allocations reduced.

Requirements: FR-PERF-009, NFR-PERF-004 through NFR-PERF-006, NFR-REL-001 through NFR-REL-003.
Depends on MPERF-001 and MPERF-002.

Completed: verify unchanged patch ranges and installed output without rewriting the
ISO or creating a journal; preserve changed-output journaling/recovery. Native SDK
WAD bulk copy/common-prefix work preserves encoded bytes and limits. Palette report
equality compares typed fields and ordered data without whole-report serialization.

Verification: unchanged/corrupt/stale targets, recovery guards, output hashes, palette
field mutations, frozen codec vectors and native/browser parity. Remaining: SDK
commit/release/pin, broader incremental/full/forced-full matrix, PCSX2, Windows and
packaged allocation/GC soaks. The broader SDK suite has a pre-existing archive-replacement
assertion in both baseline and candidate; its full qualification remains open.

## MPERF-010 — Keep application builds efficient

Status: Incremental renderer/test checks implemented.

Requirements: FR-PERF-010, NFR-PERF-006, NFR-MAINT-001 through NFR-MAINT-003.
Depends on MPERF-001.

Completed: reuse TypeScript check caches under `node_modules/.cache/forge/`; Electron
JavaScript emission remains unconditional. Paired output maps and clean outputs match.

Remaining: clean/incremental budgets across renderer, Electron, host and SDK edits;
install/bootstrap/package separation; locked packaged Linux/Windows checks. SDK
publication and pin validation remain mandatory.

## MPERF-011 — Qualify the checkpoint and close the milestone

Status: Pending packaged acceptance evidence. Depends on MPERF-001 through MPERF-010.

Run the milestone's declared capture counts and hardware/platform matrix. Verify
output equivalence, invalidation, cancellation, interrupted publication, accessibility,
resource bounds and ten-cycle project/explorer soaks. Record budget deviations and
remaining bottlenecks. Close only when frozen budgets and existing workflow/release
gates pass; the accepted implementation checkpoint does not waive those gates.
