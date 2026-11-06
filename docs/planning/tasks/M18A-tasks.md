# M18A task register — Occlusion coverage authoring

Milestone and requirements: [M18A — Occlusion coverage authoring](../milestones/M18A-occlusion-coverage.md)

All M18A tasks are complete. Native format and transformation implementation belongs
in the Ratchet PS2 SDK; Forge consumes neutral SDK entry points through its target-game
adapter. No task requires a new generic graph engine.

Implementation checkpoint (2026-11-06): the pinned SDK `v0.11.0` declares preview, generation,
and graph-shape API version 1. `build/RatchetSdk.props` enables those adapters in a
clean pinned build. Shape/graph evaluation, editor controls, arbitrary-grid native
composition, GPU build/cache/pack integration, and viewport inspection are implemented.
SDK release/pin integration and M18A acceptance are complete. The parent M18's broader
automatic-coverage, native-group, streamed-terrain and release gates remain separate.

Completion update (2026-11-06): M18A-001 and M18A-003 through M18A-008 closed by
[maintainer sign-off](../README.md#maintainer-sign-off). Earlier progress and pending
checklists within those tasks are historical; no new run metadata or captures
were supplied. Unlisted tasks and documented support limits retain their scope.

## Delivery order

```text
001 Native contract
  ├─ 002 Project authoring state ──────────────────┐
  ├─ 003 Shape generation ── 005 Dock and gizmos ──┤
  └─ 004 Native grids ────────────────────────────┤
                                                 └─ 007 Box-only integration
003 + 005 ── 006 Spatial graphs ────────────────────── 007 Full integration
                                                       └─ 008 Qualification
```

001 must settle each native constraint before dependent production behavior is
enabled. Synthetic fixtures allow state/UI work and writer experiments while runtime
qualification remains open. Preserve the existing source-only builder throughout.

## M18A-001 — Freeze the native coverage contract

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Requirements: FR-OCC-AUTH-006, FR-OCC-001, NFR-P3-001, NFR-P3-003  
Depends on: integrated M18 existing-grid builder; synthetic fixtures need no ISO

Separate format limits, current SDK restrictions, and game runtime behavior. Record
the findings in [M18A coverage qualification](../../qualification/M18A-occlusion-coverage.md).

Acceptance:

- Structural fixtures cover sparse/disconnected cells, ordering, shared masks,
  missing rows/cells, coordinate/span/pointer bounds, alignment, and empty grids.
- Tests distinguish source-preserving mask rebuild APIs from the new arbitrary-grid
  requirement; add/remove/replace coordinates cannot bypass source-mode validation.
- Allocation-growth experiments verify relocation, updated offsets/scene-view sizes,
  and preservation of adjacent/opaque payloads. Engine allocation limits are recorded
  separately from successful archive composition.
- The contract fixes source/new-cell reserved bits and supported world coordinates
  from target/runtime evidence, with explicit diagnostics for unsupported inputs.
- Runtime lookup behavior outside coverage and for empty grids is qualified, or the
  unsupported publication cases remain explicitly gated with exact remaining work.

Verification: game-scoped host/SDK synthetic checks, local source/runtime inspection,
and recorded PCSX2 probes. A structural fixture alone cannot close runtime gates.

## M18A-002 — Persist coverage authoring state

Status: ✅ Complete

Requirements: FR-OCC-AUTH-001, FR-OCC-AUTH-002, FR-OCC-AUTH-003, NFR-P3-001  
Depends on: M18A-001 contract decisions; synthetic fixtures may establish state first

Add the smallest versioned project contract for Primary, box/cylinder sources,
bounded graph regions, owned markers, stable IDs, and semantic recipe identity.

Acceptance:

- Existing projects migrate to Primary-only behavior without changing source output.
- Create/edit/rename/duplicate/delete/contribution commands participate in history,
  save/reopen, recovery, attachment preservation, and boundary validation.
- Start from scratch disables Primary in one undoable edit and retains authored
  sources and source attachments. Undo/redo restores the prior semantic recipe.
- Missing/duplicate IDs, invalid ownership, malformed dimensions/transforms, unknown
  schema versions, and excessive source/marker counts fail without partial mutation.
- Names, tree order, and transient visualization state do not affect coverage hashes.

Verification: migration, commands/history, malformed-input, fingerprint, and portable
save/reopen/recovery tests. Contracts follow repository TypeScript/C# conventions.

Implementation: [project contract v1](../../features/occlusion-coverage-project-v1.md)
defines project schema 27 with a separate versioned coverage recipe. Primary control,
box/cylinder dimensions, optional seed graphs and owned local markers persist
without rewriting imported Primary authority. Validated whole-recipe updates use
normal history/recovery; semantic hashes ignore display metadata and ordering.
UYA preflight blocks ungenerated authored coverage instead of silently packing
Primary. Capability-gated IPC query/update operations validate the whole recipe,
reject stale edits, and return coherent editor/coverage state. Shared wire fixtures
and real-host history/save/reopen checks pass. M18A-005 now exposes box/cylinder
creation, duplication, deletion and numeric edits; M18A-005 also provides viewport
helpers and shared transform gizmos. M18A-006 now exposes optional volume graphs and owned
marker editing. Current-scene graph previews and accepted/rejected
inspection are connected in the pinned build; real-map qualification remains open.

## M18A-003 — Generate deterministic shape coverage

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Requirements: FR-OCC-AUTH-002, FR-OCC-AUTH-005, NFR-PERF-004, NFR-REL-003  
Depends on: M18A-001; M18A-002 for authoritative project integration

Implement bounded SDK cell enumeration and composition, delivering boxes first and
circular cylinders next through the same API.

Acceptance:

- Translated/rotated/resized shapes use intersection inclusion and full-containment
  exclusion with frozen tolerance/tangency rules and exclusion-final precedence.
- Primary, include shapes, and graph-result fixtures combine into one canonical set
  independent of source order, duplicate coverage, platform, or thread count.
- Thin shapes, exact boundaries, rotated cylinders, distant islands, unsupported
  coordinates, non-finite inputs, and excessive candidate spans have retained fixtures.
- Candidate work is checked before enumeration, cancellation is bounded, and preview
  reports source counts plus the deduplicated effective count without publication.

Verification: shape geometry/precedence fixtures, permutation hashes, limits,
cancellation, and a measured representative preview workload.

Implementation: the SDK API
`OcclusionCoverageBuilder.PreviewShapes(GameId, primary, shapes, token)` evaluates
translated/rotated boxes and circular cylinders against target cell cubes. It unions
Primary and include shapes, deduplicates cells, then removes only cells clearly
contained by exclusions. The result reports canonical Z/Y/X coordinates, per-source
counts, actual candidate tests, and a versioned coordinate fingerprint. Input, source/cell and worst-case
work limits are checked before candidate enumeration; cancellation returns no
partial result. Independent box edge/face, analytical circle/rectangle and bounding
prism references, containment/tangency/thin-shape fixtures, permutation/hash checks
and measured overlap workloads pass. The original box-only API retains its geometry
and version-one fingerprint; mixed evaluation uses version two.
See the [box](../../qualification/M18A-occlusion-coverage.md#box-coverage-preview-development)
and [cylinder qualification records](../../qualification/M18A-occlusion-coverage.md#cylinder-and-mixed-shape-preview-development).
These APIs are available through the pinned SDK. The host adapter
runs evaluations in a background task, reads imported/original Primary through
the SDK, reports progress, supports cancellation and rejects stale results. The dock
reports per-source and final counts while retaining the prior result as stale.
See the [development preview record](../../qualification/M18A-occlusion-coverage.md#host-and-dock-preview-development).
The clean pinned build declares preview API version one and enables the adapter
automatically without launch flags. Shape and graph edits
mark previous results stale; enumeration runs through explicit preview, qualification
or build actions. Metadata refresh does not flash stale colors. Viewport cell overlays are implemented
in M18A-005; graph preview integration is recorded under M18A-006. Broader runtime
qualification remains open; generation/build integration is connected in M18A-007.

## M18A-004 — Serialize and compose arbitrary native grids

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Requirements: FR-OCC-AUTH-006, FR-OCC-007, NFR-REL-001, NFR-REL-002  
Depends on: M18A-001; use explicit synthetic cell/mask sets before M18A-003 integration

Extend the SDK with an explicit arbitrary-coordinate grid path. Retain the existing
source-preserving APIs and no-edit behavior. Pin the released SDK revision in Forge
when the API is ready; a dirty SDK checkout is not a reproducible dependency update.

Acceptance:

- New coordinates can be added, removed, moved, and rebuilt without source octants.
- Deterministic tree/mask encoding handles gaps and mask deduplication, enforcing
  proven coordinate/span/pointer/mask/byte budgets before unsafe allocation/output.
- Source-free cells follow the qualified reserved-bit policy. Unsupported empty
  publication remains gated. Source inputs and supplied masks are never mutated.
- Growth/shrink composition preserves neighboring and opaque payloads, relocates all
  understood references, and re-reads the intended coordinates and masks.
- Cancellation/failure does not expose an accepted partial result; any compaction
  retains the generated visibility superset and reserved-bit constraints.

Verification: independent source-free fixtures, semantic readback, input-order
determinism, source no-edit, growth/shrink, limits, cancellation, and archive readback.

Implementation evidence: the pinned SDK includes
`OcclusionComposer.CreateGrid(GameId, masks, token)` with explicit complete masks and
one shared serializer for creation and source-preserving replacement. Native spans,
row pointers, mask slots, and cell counts are bounded; the tree is measured before
output allocation. Empty creation remains rejected and reserved-bit defaults are not
inferred. Source-free fixtures and the existing occlusion regressions pass. Forge's
complete-level fixture replaces four cells with twenty, crosses a sector boundary,
and preserves unrelated payloads. See the [qualification record](../../qualification/M18A-occlusion-coverage.md).
The SDK now also exposes `ApplyCoverageToLevel` through its existing asset/archive
composition. It accepts changed coordinates and allocation growth/shrinkage, inserts
missing grids within the scene view, relocates adjacent/sequence references, and
verifies exact packed grid bytes. SDK tests retain the source-only rejection path,
opaque payloads, determinism and input ownership. Static UYA/DL inspection establishes
bit 1023 as always visible; empty publication and grown runtime allocations remain
unqualified. Forge consumes these APIs in its clean pinned build through M18A-007;
SDK release/pin integration is complete. Native/runtime gates remain open.

## M18A-005 — Add the coverage tree and shape controls

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Requirements: FR-OCC-AUTH-001, FR-OCC-AUTH-004, NFR-UX-001, NFR-UX-002  
Depends on: M18A-002, M18A-003; synthetic preview data may establish UI first

Extend the established Occlusion dock with Primary and authored-source rows. Deliver
box creation/selection/transforms first, then add cylinder controls to the same flow.

Acceptance:

- Tree and viewport selection/focus agree without creating per-octant rows or native
  game entities. Source kind/role/count/staleness are visible without color alone.
- Contribution and visualization controls are distinct. Primary can be restored;
  creation, rename, duplicate, delete, numeric editing, and snapping follow history.
- Existing gizmos preview drags and commit one history entry; Escape restores the
  original value. Cylinder sizing consistently preserves its circular section.
- Helper rendering and cell previews are bounded and disposed on project close.
  Keyboard editing, focus, display scaling, and conflicts with camera controls work.

Verification: command/component checks and recorded viewport/accessibility walkthrough
with overlapping volumes and a large preview.

Implementation: coarse source rows expose kind, role, contribution and selection.
Boxes/cylinders are created ahead of the viewport camera (with the existing selected
object/default fallback when no viewport is available), renamed, moved, rotated, resized, duplicated and deleted through normal
recipe history. Numeric drafts apply in one edit or can be discarded; quaternion
input normalizes on apply. Cylinder radius remains circular. Duplication copies
owned graph markers with fresh IDs when a saved graph is selected. Development
preview builds show source/final counts, cancellation/progress and stale state.
The dock and viewport share source selection, helper visibility and focus. Helpers
remain distinct from game entities; visibility does not change contribution. The
shared Move/Rotate/Scale tool previews locally and commits one captured recipe on
release, with Escape/capture loss restoring the original value. Existing coordinate
spaces and snapping settings apply, with cylinder radial handles retaining a circular
section. Preview cells use one bounded instanced overlay, with explicit stale state
and disposal on replacement/close. Session visualization state survives closing the
dock and resets on project changes. Focused regression checks and an isolated Electron
pointer/keyboard walkthrough are recorded in the
[qualification record](../../qualification/M18A-occlusion-coverage.md#viewport-authoring-development).
Real-map overlapping-volume, large-preview, display-scaling and broader accessibility
qualification remain open. Graph-specific authoring is tracked below.

## M18A-006 — Add bounded spatial graph authoring

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Requirements: FR-OCC-AUTH-003, FR-OCC-AUTH-004, NFR-P3-003, NFR-P3-006  
Depends on: M18A-002, M18A-003, M18A-005

Add owned markers inside a graph's bounded region, applying SDK line-of-sight
filtering without graph edges, arbitrary execution, or a node canvas.

Acceptance:

- Markers can be created, moved, duplicated, focused, deleted, saved, and recovered;
  graph movement transforms its region and markers together.
- Empty/invalid graphs and buried markers report actionable diagnostics. Candidate,
  ray, marker, and diagnostic limits are enforced with progress and cancellation.
- Frozen fixtures define probes, blocker classification, endpoint/backface/alpha
  handling, uncertainty retention, and scene-dependent invalidation.
- Accepted/rejected cells are inspectable and include shapes restore missed coverage
  without changing the graph. Graph filtering never replaces visibility baking.

Verification: separated rooms, walls/doorways, transparent blockers, invalid markers,
scripted/airborne camera overrides, deterministic replay, and accessible editing.

Implementation: Volumes → Add volume offers Box and Cylinder. Each may enable
Use seed graph, reusing its own transform/boundary rather than a separate graph shape.
That toggle immediately applies valid volume edits and reveals Add seed; moving the
volume preserves the saved graph. Invalid property drafts must be corrected or discarded first.
Legacy graph-only records display as equivalent boxes without unsolicited migration.
Turning seed filtering off retains seeds and Sticky settings; plain/exclusion shapes
use the original shape evaluator. SDK cylinder graphs reuse the exact existing
cylinder/cell intersection code, including rotations, and validate circular seed bounds.
Box/cylinder shape changes retain seeds; shrinking past them is rejected. Cross-language
kinds 5/6 carry attached graph settings while legacy kinds 3/4 remain unchanged.
Save/history, preview, native qualification/publication and boundary regressions cover
both shapes. SDK graph-shape API version 1 gates cylinder attachments. The dock adds,
focuses and deletes owned seeds using normal whole-recipe history; the viewport Move
tool positions them, and configured shortcuts duplicate/copy/paste them. Marker rows
use the shared editor tree inside a labeled, bordered seed list with selection and
pagination. A + action sits at the bottom of the list, and only the selected row
shows a delete icon. Double-click focuses a seed; separate seed details and numeric
coordinates are omitted. Seed labels use one-based graph order (Seed 1, Seed 2, …),
including delete labels. Names are not editable; deletion renumbers labels
and copy/paste never appends “copy.” Legacy stored names remain format compatible.
Volume properties owns the Use when generating octants switch. The Build tab groups Generate
octants, Qualify samples and Build occlusion; the Viewport tab groups visibility and
overlay controls, including selected-graph accepted/rejected inspection. Compact
menus hold creation and secondary volume actions. Sample pull-in and pixel edge
allowance are visible directly above a full-width Build occlusion button. Generation
details and help remain collapsed; progress/cancellation stay above the tabs and tab
switches retain volume property drafts. Volume dimensions remain directly editable;
position and rotation use the viewport tools without numeric coordinates in the dock.
New volumes appear ahead of the viewport camera; new seeds use a
camera-relative point converted/clamped into the volume. The configured copy/paste,
duplicate and delete commands and Forge Edit menu target the selected seed/volume;
text fields retain ordinary text editing. Edits validate finite local coordinates and containment;
volume sizing rejects shrinking past existing seeds. The viewport picks seeds
with an eight-pixel target and uses the shared Move tool for one-edit drags and
cancellation. Volume translation/rotation carries its local seeds; resizing changes
the boundary without scaling marker positions. New volumes start without seed
filtering. Active filters require seeds; turning filtering off preserves them and
restores plain-shape generation. The
[qualification record](../../qualification/M18A-occlusion-coverage.md#graph-authoring-development)
tracks the focused checks. The SDK API
`OcclusionCoverageBuilder.PreviewGraphs` now evaluates explicit scene triangles,
with frozen center/inset-corner probes, finite opaque segment queries, owned seed
transforms, bounded work, progress/cancellation, and apparently-buried-seed errors.
Coverage operation failures now use the existing shared session-log reporter rather
than a separate dock alert. The SDK's existing segment traversal stops on a definite
blocker for blocked/unblocked queries; nearest-hit traversal remains for buried-seed
diagnosis, with the same budgets and geometric rules. A dense 40,000-triangle
regression reproduces the previous work-limit failure and verifies unchanged cells
after the traversal fix.
The query budget is calibrated to 200 million tests after reproducing a saved
Hoven3 graph with 51,948 candidates and three seeds that required 97.5 million
tests. Matching Host/Electron wire bounds and boundary regressions accompany the
SDK change; cancellation and other work/input limits remain intact.
Ray accounting now uses actual shared queries rather than rejecting a pessimistic
candidate × nine probes × seed count estimate. Cells try the nearest seed first,
retaining the same any-seed acceptance rule and all numeric limits. A copied saved
Hoven3 graph with 16 seeds completes within the original two-million-ray and
200-million-test caps. Generated overlays are usable before visibility baking;
unchanged metadata refreshes do not discard generation, while scene edits still
cancel stale work.
Graph properties also carry Sticky and a configurable distance (on / 8 map units
for new seed graph attachments; existing recipes retain their values and legacy defaults). Source
and seed coordinate fields use initially collapsed Coordinates accordions. The
v1-style fourteen-ray Sticky filter uses the current TIE/tfrag render
surface subset and shared alpha/intersection policy. The same evaluator filters
preview, qualification and build input. Settings persist through normal history,
save/reopen and semantic recipe identity; older graph payloads remain unchanged.
It returns accepted/rejected coordinates per graph and composes accepted cells with
Primary/shapes before final exclusions. Shape-only APIs retain their existing
behavior and identities. See the
[SDK graph record](../../qualification/M18A-occlusion-coverage.md#sdk-graph-preview-development).
`UyaOcclusionSceneSnapshot` composes captured
unsaved editor state in an isolated workspace with verified attached assets and a
read-only catalog. It retains the original project's staging/dirty state and returns
the exact packed-level hash. Tests cover transforms, custom model overrides,
deletion, determinism, bounded copying and cleanup on success/failure/cancellation.
See the [scene capture record](../../qualification/M18A-occlusion-coverage.md#current-scene-snapshot-foundation).
The preview job captures catalog context, composes current unsaved
geometry and extracts calibrated blockers through the SDK without requiring a native
grid or source groups. Responses carry exact packed-scene identity and bounded
ray/query counts. Indexed buried-seed diagnostics identify authored graphs/seeds.
The dock/viewport can inspect accepted/rejected cells per selected graph before
final composition; shape-only wire identities remain unchanged. Synthetic custom
geometry, native-grid independence, wire validation and overlay disposal are tested.
See the [integration record](../../qualification/M18A-occlusion-coverage.md#current-scene-graph-preview-integration).
The API is released and pinned; real-map blocker/probe qualification, broader
accessibility and performance checks, and native runtime gates remain open.

## M18A-007 — Connect coverage regeneration to the existing builder

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Requirements: FR-OCC-AUTH-005, FR-OCC-AUTH-006, NFR-REL-001 through NFR-REL-003  
Depends on: M18A-002 through M18A-005 for box-only delivery; M18A-006 for full delivery

Connect coverage preview and visibility building to one coherent effective-grid
identity. The initial Build occlusion action combines regeneration and visibility
baking in one accepted result; standalone unbaked-grid publication is deferred.

Acceptance:

- Qualified samples and the existing GPU worker consume generated coverage; native
  composition accepts its coordinates without weakening source-mode validation.
- Cache keys, jobs, render-package overlays, sample overlays, octant labels, frozen
  inspection, visibility export, and staged build checks agree on the effective grid.
- Authored changes invalidate dependent output; stale/absent results block authored
  builds. Source-only projects retain current behavior. Last-bake inspection is
  explicitly stale and never becomes an implicit fallback for production output.
- Preflight and phase progress expose counts, size estimates/actual budgets, unmapped
  object fallbacks, cancellation, and corrective actions. Empty publication is gated.
- Stale commits, project close, worker failure, and cancellation at every publication
  boundary preserve prior accepted coverage, masks, and stages atomically.

Verification: host/IPC/worker integration, fingerprint/cache compatibility, coordinate
replacement, overlays/visibility selection, cancellation, and unchanged-source tests.

Implementation: SDK-capable builds expose Build occlusion with Primary disabled.
The existing snapshot, coverage evaluator, camera qualifier, GPU worker and atomic
bake store now form one regeneration/bake job. Accepted metadata binds recipe,
project, algorithm, coordinates, packed scene and object mappings; worker identities
retain pull-in/edge settings. Preflight rejects missing/stale output and packing uses
the SDK's arbitrary-grid path without changing source-mode validation/compaction.
Generated allocations may grow instead of merging masks to fit the initial grid.
Empty coverage remains rejected. Restoring Primary-only ignores the authored cache.
Built octants/IDs/freeze/visibility and qualified samples work independently of
Primary. Tests cover mixed box/cylinder generation, allocation growth, exact packing,
graph evaluation, replacement, cancellation/staleness and Primary restoration. A real
GPU-worker run and host commit passed on the synthetic mixed-shape fixture. See the
[development build record](../../qualification/M18A-occlusion-coverage.md#authored-coverage-build-integration-2026-10-31).
Qualification now returns measured input grid/tree bytes and mask slots, actual
cell/sample counts and work limits, an unshared-mask estimate, and the SDK's
always-visible fallback-group count. The dock shows this alongside qualified samples;
automatic build preparation also reports it before launching the GPU worker. Build
completion reports actual native grid bytes. Estimates do not replace final native
capacity validation or qualify runtime memory limits.
Broader interaction/resource qualification, native-group extensions, streamed-terrain
rebuilding and PCSX2/hardware walkthroughs remain open. SDK release/pin integration
is complete.

The user confirmed on 2026-10-31 that qualifying samples and building occlusion for
a custom cylinder works in-game. This is reported evidence for that map/workflow;
game version, emulator/hardware, allocation sizes and tested routes were not supplied.
It does not close the broader M18A-008 runtime/platform gates.

## M18A-008 — Qualify custom-map coverage end to end

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Requirements: FR-OCC-AUTH-001 through FR-OCC-AUTH-006, NFR-P3-001 through NFR-P3-006  
Depends on: M18A-001 through M18A-007

Acceptance:

- A packaged-app walkthrough disables Primary, authors boxes/cylinders/graphs,
  previews/regenerates/bakes, saves/reopens, builds, and restores Primary through undo.
- Coverage and visibility are checked on interior/exterior, overlapping, sparse,
  disconnected, airborne, and scripted camera routes in PCSX2; parent M18 hardware
  and platform gates remain required before release.
- Runtime reserved-bit/outside/empty behavior is recorded, or explicitly unsupported
  publication cases remain gated and disclosed in the delivered feature.
- Repeated generation produces the same semantic results. Measured work, memory,
  responsiveness, cancellation, and largest native tree/mask sizes are recorded.
- Automated checks and manual evidence contain no proprietary game payloads; all
  negative paths retain the last known-good project and build state.

Verification: the milestone exit-gate walkthrough and updated M18A qualification
record, linked to the parent M18/M19 release evidence.
