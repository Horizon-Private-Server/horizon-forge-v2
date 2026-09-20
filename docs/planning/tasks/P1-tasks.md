# P1 task register — Custom assets

Milestone: [P1 — Custom assets](../milestones/P1-custom-assets.md)

## P1-001 — Add project-local custom asset lifecycle

Requirements: FR-ASSET-005, FR-ASSET-006, NFR-PORT-001, DR-005
Depends on: M6-007

Store custom source and derived data within the project, assign stable asset identities,
and integrate copy, rename, replace, delete, validation, and portability behavior.

Acceptance:

- Custom assets travel with the project without machine-specific paths.
- Replacing source content updates identity/fingerprints and invalidates dependents.
- Delete reports references and cannot silently break bakeable content.
- No global custom repository is introduced in this milestone.

Verification: project move/zip round trips and custom-asset lifecycle tests.

## P1-002 — Inspect and canonicalize custom GLBs

Requirements: FR-CUSTOM-001, NFR-SEC-003
Depends on: P1-001

Parse GLB meshes, primitives, attributes, materials, textures, transforms, skins, and
animations into validated canonical inputs while reporting target limitations early.

Acceptance:

- Lengths, offsets, counts, nesting, allocations, and external references are bounded.
- Unsupported features identify their node/material/primitive and corrective action.
- Import is deterministic and keeps enough provenance to diagnose conversions.
- Invalid GLBs cannot partially mutate the project asset set.

Verification: valid feature matrix, malformed corpus, and deterministic import snapshots.

## P1-003 — Port the alpha-aware K-means baseline into the SDK

Requirements: FR-TEX-007, DR-013
Depends on: P1-002

Port deadlocked-level-packer's MIT-licensed `AlphaAwareKMeans` implementation into the
Ratchet .NET SDK with its notice, deterministic behavior, and cross-project golden fixtures.

Acceptance:

- Frequency weighting, premultiplied RGBA distance, alpha weight, transparent reservation,
  seed, iteration bound, sorting, and remapping match the documented baseline.
- Original license attribution is retained in source and distribution notices.
- Linux and Windows produce identical palettes and indices for golden inputs.
- Forge invokes the SDK implementation rather than maintaining a second quantizer.

Verification: golden compatibility suite against deadlocked-level-packer outputs.

## P1-004 — Jointly optimize vanilla and custom texture palettes

Requirements: FR-TEX-002, FR-TEX-003, FR-TEX-005, FR-TEX-007, FR-TEX-008, DR-011, DR-012
Depends on: P1-003, M5-005

Extend shared palette assignment with fixed vanilla centroids, movable custom centroids,
and the versioned 0–100 fidelity-versus-reuse control stored in the project bake profile.

Acceptance:

- Vanilla colors and decoded texels remain exact at every slider value.
- Custom colors reuse compatible vanilla entries within the configured threshold.
- Strength 0–100, default 50, uses `paletteOptimization.v1` and affects bake fingerprints.
- Output and reports are deterministic and include custom error/reuse statistics and warnings.

Verification: mixed vanilla/custom optimum cases, slider boundaries, golden images, and determinism tests.

## P1-005 — Convert supported GLBs to UYA PS2 model structures

Requirements: FR-CUSTOM-002, FR-XLT-001, FR-XLT-003
Depends on: P1-002, P1-004

Convert supported canonical custom models and optimized textures directly into the selected
NTSC-U UYA tie, shrub, or moby structures through the SDK bake pipeline.

Acceptance:

- Packet, material, texture, vertex/index, skin, and animation limits are preflighted.
- Supported content needs no user-run intermediate conversion command.
- Generated structures reopen and preserve supported geometry/material semantics.
- Conversion errors point to the responsible source element and do not commit partial output.

Verification: minimal and boundary GLB fixtures with SDK semantic re-read checks.

## P1-006 — Complete custom-asset preview and end-to-end qualification

Requirements: FR-CUSTOM-003, FR-TEX-003, FR-TEX-008
Depends on: P1-005

Preview canonical custom assets with disclosed PS2 differences and qualify project save,
edit, mixed-palette bake, WAD pack, ISO patch, and PCSX2 rendering.

Acceptance:

- Viewport preview is sufficient for editing and labels material/output mismatches.
- Palette estimates update for the chosen strength without altering vanilla content.
- A project-local GLB survives move/zip, bakes, patches, and renders correctly in PCSX2.
- The workflow records quantization error, palette reuse, and target-limit diagnostics.

Verification: Linux and Windows end-to-end custom asset qualification record.
