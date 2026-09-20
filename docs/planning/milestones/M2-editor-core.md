# M2 — Editor core

Priority: P0  
Depends on: M1  
Task register: [M2 tasks](../tasks/M2-tasks.md)

## Outcome

A user can inspect and safely manipulate supported vanilla scene entities through
a dockable, keyboard-accessible UI backed by one authoritative project model and a
bounded command history.

## Deliverables

- Typed editor runtime, commands, queries, and events.
- Docking shell and reusable Mantine components.
- Deterministic Three.js projection/disposal.
- Scene tree, properties, status, diagnostics, picking, and selection.
- Hidden/disabled/locked behavior.
- Translate/rotate/scale, grid/center/vertex snapping, and Page Down placement.
- Undo/redo, delete/duplicate, clipboard, keybindings, and accessibility basics.

## Exit gate

A representative scene can be edited and recovered without direct Three.js or raw
file mutation; selection remains synchronized; every project mutation is undoable
where specified; and the packaged app passes the editor interaction checks.

