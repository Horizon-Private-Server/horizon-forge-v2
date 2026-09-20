# M2 task register — Editor core

Milestone: [M2 — Editor core](../milestones/M2-editor-core.md)

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

## M2-006 — Add transform manipulation modes

Requirements: FR-SCENE-005, FR-SCENE-006, FR-SCENE-008  
Depends on: M2-003, M2-005

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
Depends on: M2-006

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
