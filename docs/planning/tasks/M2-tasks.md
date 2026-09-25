# M2 task register — Editor core

Milestone: [M2 — Editor core](../milestones/M2-editor-core.md)

Execution pivot: after M2-005, complete M2-010 through M2-013 before returning to
M2-006. Task IDs remain stable; transforms and snapping should be built against the
real map projection rather than temporary proxy geometry.

## M2-001 — Implement the typed editor runtime

Status: ✅ Complete

Requirements: section 6.4, FR-EDIT-001, FR-COLLAB-001, FR-PLUGIN-001  
Depends on: M1-005

Create one concrete runtime exposing typed commands, queries, Entity-ID selection,
events, capabilities, and background diagnostics without a generic service locator.
Own the active `ForgeProjectWorkspace` session here and connect M1-007's manual
save, configurable idle autosave, and pre-transition recovery flush to its dirty
state.

Acceptance:

- UI features cannot mutate authoritative stores or Three.js objects directly.
- Commands validate before mutation and events are ordered/serializable.
- Runtime can be exercised without Electron UI.
- Future collaboration/plugin boundaries require no P0 implementation.

Verification: command/query/event contract tests with an in-memory project.

Implementation: `EditorRuntime` is the single long-lived owner of the active
`ForgeProjectWorkspace`. It validates serializable selection, rename, and transform
commands before mutation; exposes snapshots, capabilities, tools, diagnostics, and
ordered events; and performs explicit save, idle recovery, and pre-transition
recovery flushes. Electron uses only typed binary bridge operations to access it,
and host shutdown closes stdin gracefully so the runtime can flush dirty state.
The contract is recorded in [editor runtime v0](../../editor-runtime-v0.md).
Undo/redo and common edit operations remain scoped to M2-008.

## M2-002 — Build docking shell and shared Mantine components

Status: ✅ Complete

Requirements: FR-UI-001, FR-UI-002, NFR-UX-001, NFR-UX-002  
Depends on: M2-001

Adopt one maintained docking library and implement shared tree/property/filter,
diagnostic, progress, empty, and error primitives.

Acceptance:

- Panels dock, tab, resize, move, hide, and reset; supported detach persists.
- Layout restore tolerates removed/renamed panels.
- Shared primitives are keyboard accessible and high-DPI usable.
- No custom docking engine is introduced.

Verification: component interaction/accessibility checks and layout round trip.

Implementation: `dockview-react` provides the dock/tab/resize/float engine,
keyboard navigation, announcements, and serialized layout. Forge supplies four
initial panels, compact Mantine tree/property/filter/diagnostic/progress/empty/error
primitives, View-menu reopen/reset actions, and validated debounced persistence in
the machine-specific `ui.editorLayout` setting. Missing panels remain intentionally
hidden; stale IDs or component names reset safely. In-window floating groups are
the supported detachable form for P0. The decision is recorded in
[ADR-0003](../../adr/0003-dockview-editor-shell.md).

## M2-003 — Project authoritative state into Three.js

Status: ✅ Complete

Requirements: FR-SCENE-001, FR-SCENE-002, NFR-PERF-003, NFR-PERF-005  
Depends on: M0-008, M2-001

Implement Entity-ID-based creation/update/removal of scene objects and explicit
ownership/disposal of geometries, materials, textures, workers, and listeners.

Acceptance:

- Re-projection reproduces authoritative state without hidden render mutations.
- Unrelated project changes do not rebuild unaffected geometry.
- Open/close/reload returns GPU/resource counts to a stable baseline.
- Render objects resolve to Entity IDs without storing authoritative data.

Verification: projection diff tests and repeated-load memory/resource soak.

Implementation: `SceneProjection` incrementally creates, updates, and removes one
lightweight render proxy per authoritative project entity. Proxies retain only the
Entity ID needed to resolve interactions; transforms and display state are always
re-applied from runtime snapshots after conversion from PS2 Z-up coordinates to
the Three.js Y-up scene basis. Shared proxy resources have explicit, idempotent
disposal. M2-012 replaces renderable proxies with vanilla asset geometry while
retaining proxies for intentional meshless or failed assets.

## M2-004 — Implement scene tree, properties, status, and diagnostics

Status: ✅ Complete

Requirements: FR-UI-003, FR-UI-004, FR-UI-005  
Depends on: M2-002, M2-003

Build hierarchy/search/multi-select/context UI, common and type-specific property
editors, and workspace status/diagnostic surfaces.

Acceptance:

- Tree displays dirty/hidden/disabled/locked/invalid/missing states.
- Property edits validate and issue commands; compatible multi-edit works.
- Host/task/layer/selection status and corrective diagnostics remain visible.
- Large trees remain interactive on representative fixtures.

Verification: component/state tests and large-tree interaction profile.

Implementation: the scene tree groups and filters authoritative entities, caps
unfiltered rendering at 1,000 rows, synchronizes multi-selection through runtime
commands, and exposes contextual hidden/disabled/locked actions. Properties issue
validated rename, layer, state, and transform commands with compatible multi-edit.
Snapshots derive exact per-entity dirty and missing-asset states; the status bar
keeps host, target, selection, layer, bake, task, and diagnostic state visible.
Diagnostics include corrective save guidance and access to the detailed log folder.

## M2-005 — Implement picking, selection, and entity states

Status: ✅ Complete

Requirements: FR-SCENE-003, FR-SCENE-004, FR-SCENE-007, DR-006  
Depends on: M2-001, M2-003, M2-004

Implement ray selection, additive/toggle behavior, unified selection, and effective
hidden/disabled/locked state across hierarchy.

Acceptance:

- Viewport/tree/properties always agree by Entity ID.
- Ray chooses nearest eligible entity and passes through locked entities.
- Hidden objects are not rendered/picked; disabled objects are excluded from bake;
  locked objects remain visible but immutable.
- Deleting selection leaves a predictable valid fallback.

Verification: ray ordering and state-matrix tests plus synchronized UI checks.

Implementation: viewport pointer clicks raycast against the projected scene and
commit selection through the shared runtime command. Plain clicks replace, Shift
adds, Ctrl/Cmd toggles, and blank clicks clear. Projection state hides hidden and
disabled entities, highlights selection, and skips locked entities while walking
nearest-first intersections. The host rejects mutations of locked entities while
still allowing them to be selected from the tree and explicitly unlocked. Disabled
state is persisted for the M3 baker. Selection fallback after deletion remains with
M2-008, which owns delete commands.

## M2-006 — Add transform manipulation modes

Status: ✅ Complete

Requirements: FR-SCENE-005, FR-SCENE-006, FR-SCENE-008  
Depends on: M2-003, M2-005, M2-012

Implement select/translate/rotate/scale modes, tool overlay scene, world/local
orientation, pivots, numeric entry, multi-selection, and cancel/commit.

Acceptance:

- Mode and orientation are visible and rebindable.
- Gizmos/tools never enter project bake data.
- One drag commits one validated command; cancel restores exact prior state.
- Unsupported scaling is disabled with explanation.

Verification: transform math, multi-selection, cancel/commit, and overlay exclusion.

Implementation: Three.js `TransformControls` renders against a dedicated overlay
scene and manipulates an editor-only bounds-center pivot. World/local move, rotate,
and scale preview selected unlocked entities without mutating project state, then
commit one validated binary batch-transform command on release; Escape restores
the authoritative snapshot. Existing property inputs provide numeric entry. All
current UYA P0 entity records support scale; future unsupported kinds must disable
that mode. Rebindable mode shortcuts remain owned by M2-009's keybinding system.

## M2-007 — Add snapping and Page Down placement

Status: ✅ Complete

Requirements: FR-SCENE-009, FR-SCENE-010, DR-009, NFR-PERF-002  
Depends on: M2-006, M2-010, M2-012

Implement grid/increment, bounds-center, accelerated vertex-to-vertex, surface,
rotation/scale snapping, temporary inversion, and downward placement.

Acceptance:

- Hidden/disabled objects are not targets; locked objects may be targets.
- Vertex snapping is interactive without full-scene per-pointer scans.
- Page Down ignores selection/descendants/tools, preserves group offsets, and is
  one undoable command.
- No-hit behavior does not mutate state and reports briefly.

Verification: deterministic geometry fixtures, latency profile, and undo checks.

Implementation: transform controls provide configurable move, rotation, and scale
increments with Ctrl inversion. Translation can use the selection center, active
origin, or a selected-mesh vertex against the grid, another entity's bounds center,
the ray-hit triangle's nearest vertex, or its visible surface. Selected vertices
are indexed once per drag; target vertices are limited to the ray-hit triangle.
Page Down raycasts beneath the selection, ignores its own geometry, preserves group
offsets, commits one batch transform, and reports no-hit results without mutation.

## M2-008 — Complete command history and common edit operations

Status: ✅ Complete

Requirements: FR-EDIT-001, FR-EDIT-002, FR-EDIT-003, DR-017  
Depends on: M2-001, M2-004, M2-006

Implement coalesced bounded undo/redo, delete/duplicate with reference analysis,
and same-project clipboard graphs.

Acceptance:

- History caps at 100 entries or approximately 128 MiB and evicts oldest complete
  commands.
- Undo/redo restores project, projection, validation, selection, and dirtiness.
- Delete summarizes dependents and cannot silently create broken bake references.
- Paste assigns new Entity IDs and preserves compatible asset references.

Verification: mixed-command sequences, memory eviction, dependency deletion, and
clipboard graph tests.

Implementation: the authoritative host retains structurally shared before/after
project records with fixed 100-entry and approximate 128 MiB eviction limits.
Undo and redo restore content, selection, dirtiness, validation, and all projected
views. Delete chooses a stable neighboring selection; the current P0 schema has
no mapped inbound entity references to summarize. Duplicate
and project-local copy/paste preserve asset/source records, clear vanilla source
provenance, unlock copies, assign new Entity IDs, and commit as one history entry.
The clipboard is cleared on project transitions; cross-project paste remains out
of scope until attached-asset collection is reliable.

## M2-009 — Implement contextual keybindings and accessibility baseline

Status: ✅ Complete

Requirements: FR-EDIT-004, NFR-UX-001, NFR-UX-002  
Depends on: M2-002, M2-005, M2-006, M2-008

Register command shortcuts by context and build search/rebind/clear/reset/conflict
UI, including Page Down and manipulation modes.

Acceptance:

- Text fields suppress destructive scene commands.
- Conflicts are detected before save and defaults can be restored.
- Core menus, panels, tree, properties, and dialogs work by keyboard with visible
  focus and non-color-only states.
- Keybindings persist as user settings without entering project data.

Verification: context/conflict tests and recorded keyboard-only editor walkthrough.

Implementation: one shared command registry resolves defaults plus user overrides
from the flat `keybindings.overrides` setting. Global project/edit commands and
viewport-only mode/Page Down commands dispatch by context, while text inputs and
dialogs retain native keyboard behavior. The Settings keybinding view supports
search, capture, clear, per-command reset, reset-all, and pre-save conflict
detection; the Forge menu reflects changes immediately. Defaults use Ctrl/Cmd for
cross-platform application commands, 1–4 for select/move/rotate/scale, and Page
Down for ground placement.

## M2-010 — Render cached UYA tfrags in the editor

Status: ✅ Complete

Requirements: FR-SCENE-001, FR-SCENE-002, FR-SCENE-011, FR-BRIDGE-004,
NFR-PERF-004, NFR-PERF-005
Depends on: M0-008, M1-006, M2-003

Use the pinned SDK's UYA frontend package builder to materialize a versioned render
package for the project's base level in an app-owned cache. Expose only validated
files from that cache to the sandboxed renderer and load primary and chunk tfrags
into the existing WebGL scene.

Acceptance:

- The host builds from the verified source ISO; the renderer never reads or parses
  the ISO, WAD, or game binary structures.
- Cache identity includes source fingerprint, level, SDK revision, and package
  schema; writes are atomic, progress-reporting, and cancellable.
- A valid cache loads while the source ISO is unavailable; a missing cache/source
  produces a corrective diagnostic instead of a blank viewport.
- Primary and chunk tfrags render with their exported textures/materials, coexist
  with project entities, frame correctly, and dispose on project close/reload.
- No hard-coded development asset path or duplicate render-data bridge copy is
  introduced.

Verification: package identity/validation fixtures, interrupted-build recovery,
multi-level visual checks, and repeated open/close resource counts.

Implementation: the host uses the pinned SDK's terrain-only frontend package,
keys its atomic cache by source MD5, level, schema, and SDK revision, and returns
only validated relative tfrag routes. Electron serves those files through a
cache-confined `forge-asset` protocol; the sandboxed renderer loads their glTF,
buffers, materials, and textures directly into the existing WebGL scene. Primary
and chunk render sections also appear as read-only items in the scene tree. Cache
hits work without reopening the source ISO, failures remain visible in the
viewport, and loaded GPU resources are disposed with the viewport.

## M2-011 — Add tie and shrub entities to base projects

Status: ✅ Complete

Requirements: FR-PROJ-001, FR-PROJ-002, FR-SCENE-002, FR-SCENE-011,
NFR-REL-004
Depends on: M1-004, M1-005, M1-006

Add typed UYA tie/shrub instance decoding to the pinned SDK where required, then
project every supported base instance into Forge with a stable Entity ID,
transform, Asset-ID reference, layer, and source provenance. Keep raw record data
needed for lossless bake without making the renderer authoritative.

Acceptance:

- New base projects include mobys, ties, and shrubs with catalog references and
  source counts checked against the level data.
- The host owns binary parsing and reports unsupported records/classes explicitly;
  no game parser is added to TypeScript.
- Projects created before this capability receive a one-time schema-aware backfill
  that cannot later resurrect user-deleted base entities.
- Missing catalog assets retain valid entities with placeholders/diagnostics rather
  than silently removing instances.
- Save, reopen, recovery, and project moves preserve IDs and instance data.

Verification: authored instance fixtures, representative level counts, old-project
upgrade, deletion/reopen, and cross-platform serialization tests.

Implementation: the pinned SDK decodes typed UYA tie and shrub records while
retaining their raw bytes. New projects persist moby, tie, and shrub entities with
catalog references, source provenance, and decomposed transforms. Existing projects
offer a one-time source-ISO-backed upgrade; its stored entity version prevents
deleted base entities from being recreated on later opens. Missing catalog entries
remain as placeholder-capable entities instead of being dropped.

## M2-012 — Replace entity proxies with vanilla asset meshes

Status: ✅ Complete

Requirements: FR-SCENE-002, FR-SCENE-003, FR-SCENE-004, FR-SCENE-007,
FR-SCENE-011, NFR-PERF-001, NFR-PERF-003, NFR-PERF-005
Depends on: M2-005, M2-010, M2-011

Resolve tie, shrub, and moby Asset IDs through the project/global stores, convert
their canonical blobs to cached render payloads in the host, and project actual
meshes in Three.js. Share immutable class geometry/material resources and retain
per-instance Entity-ID picking and project-driven transforms/states.

Acceptance:

- Renderable mobys no longer use boxes; ties and shrubs render at their project
  transforms with exported vanilla textures/materials.
- Geometry/material data is loaded once per Asset ID and reused across instances;
  the renderer does not duplicate game parsing or one full mesh per instance.
- Ray hits resolve the exact Entity ID, including instanced meshes, and existing
  hidden/disabled/locked/selection behavior remains unchanged.
- Intentional meshless mobys keep a distinct editor-only marker; missing/failed
  assets use a visibly different placeholder and actionable diagnostic.
- One failed asset or family does not blank the rest of the map, and all owned GPU
  resources are released on replacement or close.

Verification: shared-resource counts, instanced-picking fixtures, state matrix,
representative dense-level frame profile, and repeated-load resource soak.

Implementation: the host resolves every unique project Asset ID through the
project/global stores, validates its canonical blob and identity, and uses the
pinned SDK to cache a browser-ready glTF plus textures beside the terrain package.
The renderer loads each Asset ID once, merges compatible primitives by material,
and instances bind-pose geometry across tie, shrub, and moby entities. Instance ray
hits map back to Entity IDs; meshless mobys and failed/missing assets retain
distinct editor markers. A representative UYA Level 1 build exported all 137
referenced assets for 4,589 entities without failures; the projection benchmark
reduced estimated mesh draws from 4,986 to 902. The WebGL viewport uses a neutral
editor light, a fixed 1x pixel ratio, and camera-centered mouse look with right-drag
pan and reduced wheel dolly, augmented by damped viewport-focused WASD, Q/E
vertical movement, and Shift acceleration. Initial framing uses the median entity
position and 75th-percentile radius so distant outliers do not hide the main map.

## M2-013 — Add sky and viewport map-fidelity controls

Status: ✅ Complete

Requirements: FR-SCENE-001, FR-SCENE-011, NFR-PERF-001, NFR-UX-002
Depends on: M2-010, M2-012

Load the UYA sky export from the same package and add compact viewport controls for
terrain, ties, shrubs, mobys, sky, and editor markers. Apply only the UYA fog,
alpha, ambient/vertex-color, and render-order behavior required for a faithful
editing view; keep bake state separate from temporary viewport visibility.

Acceptance:

- A representative level visually contains terrain, sky, ties, shrubs, and mobys
  with correct coordinate basis and recognizable material/alpha behavior.
- Family visibility is session UI state and does not mutate project hidden or
  disabled flags.
- Loading/progress/failure state identifies the affected family and partial scenes
  remain usable.
- Optional statistics expose draw calls, triangles, and frame time without being
  enabled by default.

Verification: representative screenshots, family-failure fixtures, layer-toggle
checks, and light/heavy level performance records.

Implementation: the versioned host render package now includes the UYA sky export
and level-settings environment data alongside terrain and entity assets. The WebGL
viewport applies the exported background, fog, alpha, vertex-color, and source
render-order metadata, while keeping the sky centered on the camera. Compact
session-only controls toggle terrain, ties, shrubs, mobys, sky, and editor markers;
optional FPS, draw-call, and triangle statistics remain off by default. Loading and
failure reporting identify terrain, sky, and affected asset families without
preventing the rest of the scene from rendering.

## Remaining UYA data baseline

M2-014 through M2-020 complete base-level import coverage without making an
unverified writer authoritative. The Ratchet SDK's `UyaGameplayLayout` identifies
39 core pointer slots. Its current typed readers cover level settings, mobys, ties,
shrubs, cuboids, ordinary splines, and areas; Forge currently persists only the
moby/tie/shrub entities and selected base-layer payloads.

The task details below were cross-checked against Map-o-Matic's gameplay geometry
and tie-lighting paths, Wrench's instance schema and GC/UYA gameplay readers/writers,
Deadlocked Level Packer's gameplay and occlusion tooling, and the Ratchet SDK's UYA
layout. Those projects are format evidence, not permission to assume Deadlocked and
UYA layouts are identical. Synthetic fixtures and the local clean UYA corpus remain
the acceptance authority.

## M2-014 — Inventory remaining UYA data and freeze import ownership

Requirements: FR-PROJ-001, FR-BAKE-005, FR-XLT-003, NFR-REL-002
Depends on: M1-006, M2-011

Create a field-level coverage matrix for every UYA gameplay-core slot plus the
separate level-WAD occlusion payload. Classify each section as typed project data,
derived index/cache data, asset metadata, or named opaque pass-through, and define
the minimum versioned SDK and project records needed by M2-015 through M2-020.

Acceptance:

- All 39 core slots and the level-WAD occlusion block have exactly one byte owner;
  decoded read-only views may reference opaque bytes but cannot silently replace them.
- The matrix records counts, record sizes, sentinels, alignment, units, quantization,
  index domains, known fields, retained unknown bytes, and cross-section dependencies.
- Parsers remain in the Ratchet SDK/.NET boundary, validate bounds and checked
  arithmetic before allocation, and never add game-binary parsing to TypeScript.
- Localized help-message blocks remain named opaque content until text editing is in
  scope; model class lists remain asset metadata rather than duplicate scene entities.
- Import/edit/bake capabilities are declared separately so a typed reader does not
  accidentally enable an action without a verified writer.

Verification: authored one-block and malformed fixtures plus a retained, byte-free
coverage report for every populated level in the clean NTSC-U UYA corpus.

## M2-015 — Import UYA volumes, paths, grind paths, and areas

Requirements: FR-PROJ-001, FR-PROJ-002, FR-SCENE-002, FR-SCENE-005,
FR-SCENE-006, FR-SCENE-007, FR-XLT-003
Depends on: M2-001, M2-005, M2-014

Import cuboids, spheres, cylinders, pills, ordinary splines, grind paths, and areas
as stable project data. Preserve the native matrix, inverse-rotation matrix, Euler
rotation, spline point `w` values, grind-path flags, area bounds, and source records.
Translate area membership from source indices to stable Entity-ID references.

Acceptance:

- Every source volume and path receives a deterministic Entity ID, provenance, and
  the correct box/sphere/cylinder/capsule/path overlay without becoming render geometry.
- Area links cover paths, cuboids, spheres, cylinders, and negative cuboids; dangling
  or out-of-range links identify the source area and field instead of being dropped.
- Camera-collision grid flags and scalar parameters attach to their referenced volume;
  the spatial grid is treated as derived data, not thousands of fake entities.
- Empty and reportedly unused pill tables are valid, and non-uniform or mirrored
  transforms are preserved without lossy decomposition.
- Unsupported edits remain disabled with an explanation while raw records retain a
  lossless future bake path.

Verification: per-family parser fixtures, matrix/inverse consistency tests, area-link
fixtures, camera-collision reference checks, and representative all-family counts.

## M2-016 — Import cameras, sound emitters, groups, and shared references

Requirements: FR-PROJ-001, FR-PROJ-002, FR-SCENE-002, FR-SCENE-005,
FR-EDIT-002, FR-XLT-003
Depends on: M2-001, M2-014, M2-015

Import camera and ambient-sound instances with their transforms, class/type fields,
sound range, Pvar indices, and raw records. Import moby, tie, and shrub groups as
stable membership relationships, and preserve shared-data entries plus Pvar link
and relative-pointer tables needed by mobys, cameras, and sounds.

Acceptance:

- Cameras and sounds receive stable Entity IDs and distinct editor markers; the
  viewport's own navigation camera is never confused with a game camera entity.
- Group members resolve to Entity IDs while retaining source order and source indices;
  empty groups and duplicate/invalid membership produce explicit diagnostics.
- Pvar table entries, payloads, fixups, and shared-data pointer records retain exact
  bytes and resolve known owning instances without pretending unknown fields are typed.
- Delete/disable diagnostics report affected groups and known Pvar/shared-data links;
  no index-bearing payload is silently remapped during import.
- Read-only types do not gain transform or bake capabilities merely because they can
  be displayed.

Verification: camera/sound record fixtures, empty and multi-member group fixtures,
shared-data/Pvar boundary cases, broken-reference diagnostics, and save/reopen checks.

## M2-017 — Import UYA lighting, environment, and per-tie ambient data

Requirements: FR-PROJ-001, FR-SCENE-002, FR-SCENE-005, FR-LIGHT-001,
FR-LIGHT-002, FR-LIGHT-003, FR-XLT-003
Depends on: M2-011, M2-012, M2-013, M2-014

Import directional lights, point lights, environment sample points, and environment
transition volumes as typed lighting-layer data. Import the variable-length tie
ambient RGBA stream and associate each entry with its stable tie Entity ID rather
than retaining a fragile parallel source-index array.

Acceptance:

- Directional-light color/direction pairs and point-light position, radius, color,
  1/64-unit quantization, 16-bit channel values, and 128-slot mask bound are preserved.
- The UYA point-light X/Y masks remain derived from the typed lights and are validated
  against source coverage instead of exposed as separate editable objects.
- Environment sample points preserve hero-light selection, hero/fog colors, reverb,
  music, and fog ranges; transitions preserve inverse transforms, endpoint lighting,
  fog values, and enable flags.
- Each tie owns its ambient word stream, directional-light selector, and provenance.
  Preview resolves the tie asset's ambient indices/recipes as Map-o-Matic does and
  avoids applying directional contribution twice.
- Reordering, deleting, or duplicating ties cannot transfer ambient data to a different
  tie accidentally; missing/malformed ambient entries name the affected source index.
- Editor visibility stays separate from bake inclusion, and unsupported mutations are
  capability-gated until native writers and regeneration are verified.

Verification: light-record fixtures, point-mask coverage tests, environment transition
fixtures, per-tie reorder/delete/duplicate tests, and Map-o-Matic parity screenshots.

## M2-018 — Import UYA level settings with stable entity references

Requirements: FR-PROJ-001, FR-PROJ-002, FR-SCENE-001, FR-XLT-003
Depends on: M2-014, M2-015, M2-016

Promote the already decoded UYA level-settings block into versioned project-level
data instead of representing it as a scene entity. Preserve environment values,
world/ship configuration, chunk planes, reference fields, and unknown trailing data.

Acceptance:

- Background/fog colors, distances and intensities, death height, spherical-world
  state and center, ship position/rotation, chunk planes, core-sound count, the UYA
  third-part value, padding, and trailing bytes round-trip through project save/reopen.
- Ship path and ship-camera start/end cuboids resolve to stable Entity IDs while also
  retaining source indices for provenance and exact unchanged output.
- Missing referenced paths/cuboids produce field-specific diagnostics; `-1`/none
  sentinels remain valid and are not converted into broken links.
- Rendering consumes the typed settings snapshot without creating a second mutable
  environment authority.
- Fields without a verified native writer remain read-only and retain their original
  bytes.

Verification: settings boundary fixtures, stable-reference migration tests, chunk-plane
count/terminator cases, and render-environment snapshot parity.

## M2-019 — Decode UYA occlusion octants and instance mappings safely

Requirements: FR-PROJ-001, FR-SCENE-005, FR-BAKE-005, FR-XLT-003,
NFR-REL-001, NFR-REL-002
Depends on: M2-011, M2-014, M2-015

Add a read-only typed view over the level-WAD occlusion tree and gameplay-core
occlusion mappings. Relate 4x4x4 octants and their 128-byte visibility masks to the
tfrag, tie, and moby mapping domains and to per-instance occlusion fields. Record
the coordinate scale/domain and source grid size as explicit baseline settings for
later visibility regeneration.

Acceptance:

- Sparse X/Y/Z tree offsets, shared mask indices, octant coordinates, mask bytes,
  mapping counts, and tfrag/tie/moby records are bounds-checked and preserved.
- Tie and moby mappings resolve to stable Entity IDs; terrain mappings use stable
  terrain-section keys rather than renderer object identity.
- The decoded model distinguishes the gameplay mapping table from the separate
  level-WAD octant tree and reports mismatched counts, IDs, or mask ranges.
- Octant-to-world scale and source memory usage are explicit metadata; editable
  generation budgets or heuristics wait for a scoped UYA visibility writer.
- Deadlocked Level Packer and Wrench behavior is used only as supporting evidence;
  UYA fixtures must confirm every adopted field and limit.
- Source occlusion bytes remain the sole bake authority and byte-exact opaque payload
  until a UYA-specific writer and semantic re-read task explicitly replaces them.

Verification: sparse/shared-mask fixtures, invalid tree/mapping corpus, stable-link
tests, unchanged opaque hashes, and local multi-level octant/mapping statistics.

## M2-020 — Qualify complete UYA base-data import and migration

Requirements: FR-PROJ-001 through FR-PROJ-004, FR-BAKE-005, FR-XLT-003,
NFR-REL-001, NFR-REL-004, NFR-PERF-004
Depends on: M2-015, M2-016, M2-017, M2-018, M2-019

Integrate the new families into base-project creation, project inspection, one-time
schema migration, scene-tree grouping, properties, diagnostics, and capability
reporting. Prove complete data ownership across every readable retail UYA level.

Acceptance:

- A generated coverage report lists source/imported counts and reference failures for
  every typed family and classifies every populated core slot as typed, derived, asset
  metadata, or named opaque content.
- Moving a decoded section out of `UyaOpaqueContentService` happens only after its
  typed capture validates; no bytes disappear and no section has two bake owners.
- Existing projects offer one explicit source-ISO-backed upgrade. The stored import
  version prevents later opens from resurrecting user-deleted entities or overwriting
  accepted project state.
- Cancellation, malformed data, or migration failure preserves the last known-good
  project and reports the exact level, section, record, and field where possible.
- Save/reopen/recovery and Linux/Windows serialization preserve Entity IDs, typed
  values, raw unknowns, and stable references.
- Large-level creation remains off the Electron loops and reports bounded progress
  without one bridge request per instance.

Verification: synthetic full-core fixture, old-project migration/deletion tests,
save/recovery/portability tests, cancellation and malformed-section injection, and a
retained all-level import report containing metrics and hashes but no proprietary data.
