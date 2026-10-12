# P1 — Custom asset pipeline

Priority: P1  
Depends on: stable M3-M5 contracts  
Task register: [P1 tasks](../tasks/P1-tasks.md)

Phase 3 note: unfinished project-local GLB/model deliverables are now scheduled as the
first custom-TIE slice in [M14](../tasks/P3-tasks.md#m14--custom-tie-asset-pipeline).
Completed P1 palette work remains the shared prerequisite; no second custom-asset
store or converter is introduced.

## Outcome

A constrained custom GLB can be attached to a project, converted into supported
UYA PS2 structures, palette-optimized alongside vanilla assets, baked, patched,
and inspected in-game with every conversion compromise reported.

## Deliverables

- Project-local custom asset lifecycle.
- Validated GLB-to-canonical import.
- Ported alpha-aware K-means with compatibility fixtures.
- Fixed vanilla centroids, joint palette optimization, and fidelity/VRAM slider.
- UYA PS2 model conversion and preview diagnostics.
- End-to-end custom asset fixture and manual in-game evidence.

## Exit gate

The documented fixture completes import-to-game deterministically without
changing vanilla colors, exceeding target limits silently, or depending on its
original external GLB path.
