# M2 task register — Editor core

Milestone: [M2 — Editor core](../milestones/M2-editor-core.md)

Execution pivot: after M2-005, complete M2-010 through M2-013 before returning to
M2-006. Task IDs remain stable; transforms and snapping should be built against the
real map projection rather than temporary proxy geometry.

## M2-001 — Implement the typed editor runtime

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

## M2-007 — Add snapping and Page Down placement

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

## M2-008 — Complete command history and common edit operations

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

## M2-009 — Implement contextual keybindings and accessibility baseline

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

## M2-010 — Render cached UYA tfrags in the editor

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
