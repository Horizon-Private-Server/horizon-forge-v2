# M5 task register — Texture optimization

Milestone: [M5 — Texture optimization](../milestones/M5-texture-optimization.md)

## M5-001 — Inventory every participating vanilla texel

Requirements: FR-TEX-001, FR-TEX-005, FR-TEX-006, NFR-SEC-003
Depends on: M1-004, M3-004, M3-005

Decode tie, shrub, and moby textures into a deterministic inventory of every used
pixel index, exact target color/alpha value, frequency, material use, and constraint.

Acceptance:

- Inventory distinguishes referenced colors, unused entries, and required reserved entries.
- Corrupt dimensions, indices, palette data, or allocation sizes fail safely with context.
- Equivalent inputs produce identical inventories on Linux and Windows.
- Tfrag textures remain excluded until their lifetime rules are separately approved.

Verification: decoder fixtures covering formats, alpha, unused entries, and malformed data.

## M5-002 — Optimize exact-color shared palette assignment

Requirements: FR-TEX-002, FR-TEX-004, FR-TEX-005, FR-TEX-006
Depends on: M5-001

Assign compatible vanilla texture color sets to deterministic shared palettes of at
most 256 exact entries across ties, shrubs, and mobys.

Acceptance:

- No imported color is altered, merged, displaced, or dropped.
- The implementation minimizes palette count under declared compatibility constraints,
  or labels deterministic heuristic output without claiming proof of optimality.
- Equal-cost choices use stable documented tie-breakers.
- Infeasible inputs produce violations rather than invalid output.

Verification: known-optimum small cases, adversarial capacity cases, and repeatability tests.

## M5-003 — Write optimized UYA palettes and pixel indices

Requirements: FR-TEX-002, FR-TEX-005, FR-TEX-006
Depends on: M5-002, M3-004, M3-005

Build target palettes and remap every participating vanilla texel index while preserving
the exact decoded color and UYA runtime compatibility.

Acceptance:

- Each rewritten index resolves to its original packed color and alpha.
- Shared palettes are emitted in the locations and formats expected by all three classes.
- Reserved entries, alignment, and target limits are validated before staging commit.
- Texture/palette changes invalidate only declared dependent bake layers.

Verification: exhaustive texel equality checks and semantic re-read of staged output.

## M5-004 — Expose palette diagnostics and VRAM estimates

Requirements: FR-TEX-003, FR-TEX-004
Depends on: M5-002, M5-003

Report the optimization method, palette assignments, index maps, exactness, violations,
and estimated palette upload/VRAM change in the bake results and manifest.

Acceptance:

- Reports identify input/output palette counts and every texture assignment.
- Imported quantization error is explicitly zero and any heuristic is labeled.
- Old-to-new index mappings can be traced during diagnosis without bloating project files.
- Invalid or incomplete reports cannot accompany a successful optimized staging commit.

Verification: report schema snapshots and recomputation checks against generated output.

## M5-005 — Qualify optimized output end to end

Requirements: FR-TEX-001 through FR-TEX-006, NFR-PERF-004, NFR-REL-001
Depends on: M5-004, M4-006

Run representative vanilla maps through inventory, optimization, bake, WAD pack, ISO
patch, and external PCSX2 loading to establish the P0 texture pipeline gate.

Acceptance:

- All decoded vanilla texels remain exact after the complete pipeline.
- Optimized maps load with correct tie, shrub, and moby textures in PCSX2.
- Palette count and estimated cost do not regress against unoptimized fixtures.
- Repeated runs are deterministic and failures retain last known-good staging and ISO data.

Verification: automated golden suite plus Linux and Windows PCSX2 qualification record.
