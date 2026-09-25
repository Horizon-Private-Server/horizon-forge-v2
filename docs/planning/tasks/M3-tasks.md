# M3 task register — Incremental bake

Milestone: [M3 — Incremental bake](../milestones/M3-incremental-bake.md)

## M3-001 — Build the layer graph and transactional staging writer

Status: ✅ Complete

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

Implementation: the graph declares world, sky, tfrag, collision, tie, shrub,
moby, gameplay, lighting, and opaque layers in dependency order. Canonical layer
content, transitive Asset IDs, target, translator/baker versions, relevant settings,
and dependency fingerprints produce separate content and full-input hashes so plans
can distinguish direct dirtiness from dependency invalidation. Staging writes immutable
content-addressed snapshots first, then atomically replaces the active manifest;
failed or cancelled writes leave the prior manifest and output usable.

## M3-002 — Preserve unsupported content as opaque data

Status: ✅ Complete

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

Implementation: project creation inventories byte-exact UYA sections that Forge
does not parse, including the level code overlay, sound/occlusion payloads, HUD and
transition data, and unclaimed gameplay blocks. A versioned project-local manifest
retains verified source identity, container/header placement, source offset, size,
alignment, SHA-256, and content-addressed blob path. The opaque bake input blocks
on missing, altered, malformed, or linked data; valid content stages unchanged in
an immutable layer snapshot. Parsed gameplay sections are excluded so later bakers
own their supported edits without rewriting unrelated opaque bytes.

## M3-003 — Bake UYA world, sky, tfrag, and collision layers

Status: ✅ Complete

Requirements: FR-BAKE-001, FR-XLT-001, FR-XLT-003, DR-001
Depends on: M3-001, M3-002

Stage source-native NTSC-U UYA world geometry, sky, tfrags, and collision with
semantic validation while the editable serializer/container foundation is built
in M3A.

Acceptance:

- Each layer can be planned, baked, validated, and replaced independently.
- Target-native values retain their units, axes, and indices; non-native inputs
  remain blocked until a supporting SDK serializer exists.
- Unchanged input produces byte-stable output where the format permits it.
- Unsupported source values produce diagnostics instead of silent coercion.

Verification: layer golden files, semantic re-read checks, and unchanged-layer tests.

Implementation: P0 stores vanilla world settings, sky, primary/chunk tfrags, and
collision as globally deduplicated Asset IDs referenced by a versioned project
manifest. These inputs already use native NTSC-U UYA coordinates, units, indices,
and binary encoding, so the initial bake capability is explicitly target-native
pass-through rather than a lossy conversion. World, sky, and tfrag payloads are
semantically re-read with SDK readers before independent transactional staging;
collision remains byte-verified because the pinned SDK has no collision parser.
Missing, malformed, project-local, or non-UYA assets block only their affected
layer. Entity-only edits leave all four base layers clean and repeated output is
byte-stable. Future editable geometry requires SDK serializers before this
capability can accept non-native canonical data.

## M3-004 — Bake UYA tie and shrub layers

Status: ✅ Complete

Requirements: FR-BAKE-001, FR-XLT-001, FR-XLT-003
Depends on: M3-001, M3-002, M3A-005

Write tie and shrub definitions, instances, transforms, references, and associated
vanilla resource links into valid NTSC-U UYA staging data.

Acceptance:

- Definition and instance identities resolve deterministically to target indices.
- Hidden objects remain bakeable, disabled objects are omitted, and locked objects
  bake normally.
- Dangling or incompatible asset references block only affected output with context.
- Re-reading output preserves supported editor-visible values.

Verification: fixture-based round trips and hidden/disabled/locked state matrix tests.

Implementation: project creation retains the native tie and shrub instance-table
headers, raw record templates, and trailing bytes needed for non-lossy edits. Each
layer resolves enabled vanilla UYA assets independently, assigns definitions by
class and Asset ID, assigns instances by stable source/Entity ID order, and stages
a native instance table plus a manifest linking each target definition to its
canonical resource. The SDK writer patches only class and editor-visible transform
fields while preserving unsupported record bytes. Hidden and locked entities bake;
disabled entities do not. Invalid references block only their owning layer.

## M3-005 — Bake UYA moby and gameplay layers

Status: ✅ Complete

Requirements: FR-BAKE-001, FR-XLT-001, FR-XLT-003
Depends on: M3-001, M3-002, M3A-005

Write vanilla mobys and supported gameplay records while preserving stable reference
mapping between editor Entity IDs and target indices.

Acceptance:

- Supported transforms, class data, pvars, and known references serialize correctly.
- Index allocation is deterministic and reference failures identify source entities.
- Unsupported gameplay records are preserved through the opaque-data path.
- Disabled entities and their invalidated dependents are reported before output commit.

Verification: semantic round trips and reference-resolution fixtures.

Implementation: the shared native-instance staging path now includes mobys with
deterministic definition and instance indices. The SDK patches only record size,
class, uniform scale, position, and native ZYX rotation while preserving UID,
Pvar index, class data, and unsupported record fields. Project creation stores the
four Pvar payloads separately from opaque gameplay, validates their table bounds,
and stages them byte-for-byte with an Entity ID-to-target-index manifest. Disabled
mobys are omitted; gameplay blocks before commit when an invalid Pvar index names
its source entity or opaque `pvar_moby_links` data would require unsupported index
remapping. All other unsupported gameplay sections remain in the opaque layer.

## M3-006 — Add the independent lighting bake layer

Status: ✅ Complete

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

Implementation: verified source-native directional lights, point lights, and tie
ambient colors are cataloged and staged under the independent lighting layer rather
than opaque content. A source level with no lighting payloads produces an explicit
empty lighting layer as the supported no-light fallback. Viewport visibility remains
editor-only state and does not affect bake fingerprints. Editable lighting and native
lighting regeneration are deferred until the end-to-end pack/patch path needs them.

## M3-007 — Implement bake validation, capabilities, and diagnostics

Status: ✅ Complete

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

Implementation: the SDK now declares the exact UYA archive target it supports,
and Forge queries that capability during bake preflight. Preflight combines every
layer store into one plan and returns structured diagnostics with layer, Entity ID,
Asset ID, cause, and corrective action where available. Permitted warnings carry
stable codes and block until explicitly acknowledged. Each layer reopens and
validates its temporary files, sizes, checksums, native records, references, and
limits before the shared staging store atomically updates the active manifest;
validation failure preserves the last successful snapshot.

## M3-008 — Qualify the end-to-end incremental bake workflow

Status: ✅ Complete

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

Implementation: `UyaBakeService` runs capability-aware preflight, dispatches only
dirty or dependency-invalidated layers to their existing owners, reopens staging,
and requires a fully clean second plan before reporting success. A clean plan writes
nothing and reports staging as current. Cancellation or failure restores the prior
active manifest while retaining immutable snapshots for safe reuse. The automated
qualification covers a full bake, no-op bake, tie-to-lighting invalidation,
cancellation, injected asset loss, repair, retry, and deterministic repeated output.
