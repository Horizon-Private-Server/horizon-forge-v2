# M5 — Vanilla texture optimization

Priority: P0  
Depends on: M3; integrates with M4  
Task register: [M5 tasks](../tasks/M5-tasks.md)

## Outcome

Forge inventories every referenced vanilla texture color/index and losslessly
packs compatible UYA tie, shrub, and moby textures into fewer shared palettes with
deterministic index remapping and measurable VRAM/upload savings.

## Deliverables

- Complete vanilla texture/color/index inventory.
- Deterministic exact-color palette grouping under the 256-entry limit.
- UYA palette/index writers for tie, shrub, and moby textures.
- Optimization diagnostics, VRAM estimates, and regression fixtures.

## Exit gate

Every rewritten vanilla texel decodes to its original packed color, repeat builds
are byte-identical, known-small fixtures meet their optimum, and optimized output
passes the M4 build/patch/load workflow.

