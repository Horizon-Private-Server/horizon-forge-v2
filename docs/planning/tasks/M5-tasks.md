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

Implementation: `UyaTextureInventoryBuilder` in the Ratchet SDK accepts selected
vanilla moby, tie, and shrub PIFs and emits a stable family/class/role/index key,
source hash, dimensions, mip count, texel count, material slots, palette constraints,
all palette entries, and every referenced raw-to-CLUT index with exact PS2 RGBA bytes
and per-mip/total frequency. Referenced, reserved, and unused entries remain distinct.
It validates magic, dimensions, allocation/file sizes, formats, palette bounds,
resolved indexes, duplicates, and cancellation; odd indexed4 texel counts are handled
without dropping the final nibble. `UyaTextureInventoryService` reads only enabled
definitions from validated staging, rejects placeholder-backed assets, and keeps
tfrags out of scope. Synthetic fixtures cover indexed8 CLUT remapping, indexed4,
mips, raw alpha, deterministic ordering, malformed allocations/indexes, duplicates,
and cancellation. A read-only audit passed all 7,224 imported vanilla assets,
21,721 textures, and 356,298,624 texels; two placeholder-tagged assets remain explicit
build blockers when selected. A separate source-header audit found all 13,061 model
definitions use contiguous texture slots, so canonical texture order preserves the
native material-slot mapping without a format migration.

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

Implementation: `UyaPaletteOptimizer` groups textures only when encoding, palette
format/order, capacity, and pinned reserved entries are compatible. It packs exact
PS2 RGBA values, emits every source-to-target index mapping, and never includes
unused source entries. Groups of at most 12 textures use bounded branch-and-bound;
larger or search-budget-limited groups use a labeled deterministic overlap/best-fit
heuristic. Stable choices prefer the fewest added colors, then the fullest result,
the palette serving the most textures, and finally its creation order. Fixtures
cover a known case where the heuristic needs three palettes but exact search proves
two, reserved-index conflicts, incompatible formats, deterministic input ordering,
lossless remaps, cancellation, and infeasible capacity. A read-only worst-case audit
packed all 21,721 globally imported textures into 3,610 palettes in 13.3 seconds;
normal per-map inventories are substantially smaller.

## M5-003 — Write selected vanilla models, optimized palettes, and pixel indices

Requirements: FR-TEX-002, FR-TEX-005, FR-TEX-006
Depends on: M5-002, M3-004, M3-005

Build target palettes and remap every participating vanilla texel index while preserving
the exact decoded color and UYA runtime compatibility. Install every selected
cross-level vanilla tie, shrub, and moby definition into the target asset header/WAD
instead of assuming that its source texture IDs exist in the base level.

Acceptance:

- Each rewritten index resolves to its original packed color and alpha.
- Every enabled instance class has exactly one target model definition whose model
  bytes and texture references resolve to the selected catalog asset.
- Shared palettes are emitted in the locations and formats expected by all three classes.
- Reserved entries, alignment, and target limits are validated before staging commit.
- Texture/palette changes invalidate only declared dependent bake layers.

Verification: exhaustive texel equality checks, cross-level class injection fixtures,
and semantic re-read of model definitions, textures, palettes, and staged output.

Implementation: `UyaStaticAssetComposer` writes selected moby, tie, and shrub model
definitions and bytes, reuses identical family material entries, emits optimized shared
palettes and GS-RAM records, remaps all base/mip texels, and rebuilds material and shrub
billboard references with target alignment and table-limit checks. Canonical UYA assets
now retain normalized native definition metadata; importer revision `forge-uya-v2`
forces legacy caches to refresh while old blobs remain readable for display. Forge pack
composition replaces the asset header, payload, and palette from staged enabled classes,
including classes sourced from another level. Fixtures exhaustively compare decoded
base/mip colors, semantically re-read every generated reference, verify shared material
reuse and a level03-to-level45 class swap, and cover incremental invalidation. A clean
NTSC-U level03 audit composed 334 modeled assets and 758 texture uses into 124 palettes
in 0.7 seconds without exceeding UYA's byte-sized family texture tables.

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

Implementation: each successful bake stores and returns a versioned palette report
containing the optimizer method/proof flag, distinct input and optimized palette counts,
estimated palette upload/VRAM bytes and savings, explicit zero imported quantization
error, violations, every texture assignment, and each referenced old-to-new palette
index mapping. Reports live only in generated staging rather than authoritative project
content, and record one mapping per referenced index instead of per texel. Static-layer
commits invalidate the prior report; every bake deterministically recomputes it, and pack
composition rejects staging unless its report exactly matches the inventory and optimizer
result used to generate the asset payloads. Contract fixtures snapshot the report schema,
recompute its byte estimates, cover all assignments/remaps, and prove that a structurally
valid but stale report cannot produce a successful pack.

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

Qualification: [vanilla texture pipeline checklist](../../qualification/M5-texture-pipeline.md).
The automated installed-ISO color, palette-count, determinism, and preservation gates
are in place; packaged Linux and Windows PCSX2 rendering remains explicitly pending.
