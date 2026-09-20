# M3 task register — Incremental bake

Milestone: [M3 — Incremental bake](../milestones/M3-incremental-bake.md)

## M3-001 — Build the layer graph and transactional staging writer

Requirements: section 8.2, FR-BAKE-001, FR-BAKE-002, FR-BAKE-003, NFR-REL-001
Depends on: M1-005, M2-001

Model bakeable content as explicit layers with input fingerprints, dependencies,
stable snapshots, and atomic replacement of staging output.

Acceptance:

- Every P0 content category has a declared layer and dependency set.
- A bake plan distinguishes dirty, dependency-invalidated, clean, and blocked layers.
- Successful output replaces the prior layer atomically; cancellation or failure
  leaves the last successful staging state usable.
- Fingerprints are deterministic for identical project state and tool versions.

Verification: dependency/fingerprint unit tests and interrupted-write integration tests.

## M3-002 — Preserve unsupported content as opaque data

Requirements: FR-BAKE-005, DR-014
Depends on: M3-001

Capture unsupported level sections, including code overlays, as immutable opaque
payloads and carry them through staging without interpretation.

Acceptance:

- Opaque sections retain source identity, placement metadata, bytes, and checksum.
- Editing supported content does not rewrite an unrelated opaque payload.
- Missing or altered opaque data blocks bake with a corrective diagnostic.
- A no-edit round trip reproduces every opaque section byte-for-byte.

Verification: golden checksum tests against representative NTSC-U UYA fixtures.

## M3-003 — Bake UYA world, sky, tfrag, and collision layers

Requirements: FR-BAKE-001, FR-XLT-001, FR-XLT-003, DR-001
Depends on: M3-001, M3-002

Connect the SDK writers for NTSC-U UYA world geometry, sky, tfrags, and collision
to the canonical project model and staging layout.

Acceptance:

- Each layer can be planned, baked, validated, and replaced independently.
- Canonical values are converted explicitly to target units, axes, and indices.
- Unchanged input produces byte-stable output where the format permits it.
- Unsupported source values produce diagnostics instead of silent coercion.

Verification: layer golden files, semantic re-read checks, and unchanged-layer tests.

## M3-004 — Bake UYA tie and shrub layers

Requirements: FR-BAKE-001, FR-XLT-001, FR-XLT-003
Depends on: M3-001, M3-002

Write tie and shrub definitions, instances, transforms, references, and associated
vanilla resource links into valid NTSC-U UYA staging data.

Acceptance:

- Definition and instance identities resolve deterministically to target indices.
- Hidden objects remain bakeable, disabled objects are omitted, and locked objects
  bake normally.
- Dangling or incompatible asset references block only affected output with context.
- Re-reading output preserves supported editor-visible values.

Verification: fixture-based round trips and hidden/disabled/locked state matrix tests.

## M3-005 — Bake UYA moby and gameplay layers

Requirements: FR-BAKE-001, FR-XLT-001, FR-XLT-003
Depends on: M3-001, M3-002

Write vanilla mobys and supported gameplay records while preserving stable reference
mapping between editor Entity IDs and target indices.

Acceptance:

- Supported transforms, class data, pvars, and known references serialize correctly.
- Index allocation is deterministic and reference failures identify source entities.
- Unsupported gameplay records are preserved through the opaque-data path.
- Disabled entities and their invalidated dependents are reported before output commit.

Verification: semantic round trips and reference-resolution fixtures.

## M3-006 — Add the independent lighting bake layer

Requirements: FR-LIGHT-001, FR-LIGHT-002, FR-LIGHT-003
Depends on: M3-001, M3-003, M3-004, M3-005

Represent lighting as an independently enabled and invalidated layer whose derived
artifacts can be rebuilt without forcing unrelated content output.

Acceptance:

- Lighting can be shown/hidden for editing without changing bake inclusion.
- Geometry or lighting input changes invalidate lighting through declared dependencies.
- Re-baking only lighting leaves unrelated successful staging artifacts untouched.
- Disabled lighting uses an explicit supported fallback or blocks with a diagnostic.

Verification: dependency invalidation tests and before/after staging checksums.

## M3-007 — Implement bake validation, capabilities, and diagnostics

Requirements: FR-BAKE-003, FR-BAKE-004, FR-XLT-003, NFR-REL-002
Depends on: M3-003, M3-004, M3-005, M3-006

Run preflight and post-write validation through SDK capability checks and return
structured, entity-aware diagnostics to the editor.

Acceptance:

- Target support is queried rather than inferred from game/version strings.
- Errors identify layer, entity/asset when applicable, cause, and corrective action.
- Post-write validation reopens staged data and checks sizes, references, and limits.
- Warnings require explicit acknowledgment when policy permits output to continue.

Verification: invalid fixture matrix and diagnostic contract tests.

## M3-008 — Qualify the end-to-end incremental bake workflow

Requirements: FR-BAKE-001 through FR-BAKE-005, NFR-PERF-004, NFR-REL-001
Depends on: M3-002, M3-003, M3-004, M3-005, M3-006, M3-007

Exercise import, edit, save, dirty planning, staged bake, cancellation, failure recovery,
and no-op re-bake using representative NTSC-U UYA projects.

Acceptance:

- Editing one content category rebuilds only it and declared dependents.
- A no-op bake writes no layer payload and reports that staging is current.
- Cancellation and injected failures preserve the prior successful staging snapshot.
- Repeated identical bakes produce the same manifest and semantic output.

Verification: automated workflow suite plus a recorded milestone exit run.
