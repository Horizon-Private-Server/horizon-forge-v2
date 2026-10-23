# P1 — Custom asset pipeline

Status: In progress — the automated custom-TIE pipeline and project-local custom-tfrag
extension are implemented; their packaged cross-platform and PCSX2 qualification remains
Priority: P1  
Depends on: stable M3-M5 contracts  
Task register: [P1 tasks](../tasks/P1-tasks.md)

Phase 3 note: unfinished project-local GLB/model deliverables are now scheduled as the
first custom-TIE slice in [M14](../tasks/P3-tasks.md#m14--custom-tie-asset-pipeline).
Completed P1 palette work remains the shared prerequisite; no second custom-asset
store or converter is introduced.

The shared importer now also feeds a project-local custom-tfrag path through the M12
terrain writer. This is an additional authored-asset capability, not the donor-map
tfrag workflow completed by M16-005; its packaged evidence is tracked by M12-008.

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
- Project-local GLB/glTF-to-tfrag partitioning, preview, editing, and native bake.
- End-to-end custom asset fixture and manual in-game evidence.

## Exit gate

The documented fixture completes import-to-game deterministically without
changing vanilla colors, exceeding target limits silently, or depending on its
original external GLB path.
