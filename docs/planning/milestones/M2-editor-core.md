# M2 — Editor core

Priority: P0  
Depends on: M1  
Task register: [M2 tasks](../tasks/M2-tasks.md)

Feature tasks: [Asset Explorer v0](../../features/asset-explorer-v0.md#implementation-tasks),
[Skybox Editor v0](../../features/skybox-editor-v0.md#implementation-tasks)

## Outcome

A user can inspect and safely manipulate supported vanilla scene entities through
a dockable, keyboard-accessible UI backed by one authoritative project model and a
bounded command history while viewing the real UYA level rather than editor proxies.

## Deliverables

- Typed editor runtime, commands, queries, and events.
- Docking shell and reusable Mantine components.
- Deterministic Three.js projection/disposal.
- Versioned host-generated UYA render packages cached outside projects.
- Actual tfrag, tie, shrub, moby, and sky rendering with Entity-ID mapping.
- Typed import of the remaining UYA gameplay instances, volumes, paths, groups,
  environment data, level settings, and their stable cross-references.
- Read-only decoded occlusion coverage with explicit ownership until a verified
  editable UYA writer exists.
- Scene tree, properties, status, diagnostics, picking, and selection.
- Dockable vanilla asset exploration with cursor-backed infinite scrolling, lazy
  previews, exact source variants grouped by class family, and target-compatible
  tie, shrub, and moby placement.
- Typed sky-shell composition with catalog insertion, removal, ordering, and editable
  initial rotation and angular velocity.
- Hidden/disabled/locked behavior.
- Translate/rotate/scale, grid/center/vertex snapping, and Page Down placement.
- Undo/redo, delete/duplicate, clipboard, keybindings, and accessibility basics.

## Exit gate

A representative UYA level renders its terrain, sky, and vanilla instances with
actual assets and can be edited and recovered without direct Three.js or raw-file
mutation; compatible catalog assets can be found, previewed, and placed through the
Asset Explorer; selection remains synchronized; every project mutation is undoable
where specified; every populated UYA gameplay slot is typed, derived, or explicitly
opaque; and the packaged app passes the editor interaction checks.
