# Instanced TIE Collision v0

Status: Proposed

Milestone: M2 — Editor core

Tasks: M2-036 through M2-040

Primary requirements: FR-COLL-001 through FR-COLL-004, FR-SDK-002,
FR-ASSET-001, FR-ASSET-004, FR-ASSET-006, FR-PROJ-004, FR-UI-004,
FR-UI-005, FR-UI-007, FR-SCENE-002, FR-SCENE-004, FR-SCENE-011,
FR-EDIT-001, FR-BAKE-001 through FR-BAKE-004, NFR-PERF-004,
NFR-REL-001 through NFR-REL-003, NFR-SEC-003, NFR-UX-001 through
NFR-UX-003

## Outcome

A user can generate a reusable solid-collision proxy for a compatible vanilla TIE,
compare bounded candidates over the source mesh, inspect their exact native-octant
cost, and apply one proxy to that exact TIE asset within the project. Every enabled
instance of the asset reuses the same local-space proxy and follows its position,
rotation, and scale. Individual instances may opt out.

Forge expands the proxies into ordinary world-space UYA collision during bake. It
combines them with the retained source collision, validates the complete transformed
result against native and evidence-backed runtime budgets, deterministically writes
and re-reads the payload, and publishes nothing on failure or cancellation.

"Instanced collision" describes Forge's authoring model. The UYA collision payload
does not retain a native proxy or instance reference: every enabled placement is
expanded into target-native faces at bake time.

## Scope

| Capability | v0 |
| --- | --- |
| UYA NTSC-U 1.00 vanilla TIE assets | Yes |
| Reuse one proxy across every matching TIE instance | Yes |
| Per-instance collision opt-out | Yes |
| Topology-preserving decimated-mesh candidates | Yes |
| Smooth sectioned outer-hull candidates with configurable detail | Yes |
| Disposable viewport preview and wireframe | Yes |
| Exact per-octant and combined-project diagnostics | Yes |
| One raw collision/sound type per proxy | Yes |
| Automatic independent collision entities for every instance | No |
| Per-face type painting or mesh editing | No |
| Player-barrier proxy generation | No |
| Automatic chunk streaming assignment | No |
| Arbitrary glTF/GLB collision import | No |
| Shrub, moby, tfrag, or cross-game proxy generation | No |

Custom GLB collision waits for the P1 custom-asset pipeline. The v0 input is the
validated canonical geometry of an exact vanilla TIE Asset ID already supported by
the Asset Explorer and UYA target adapter.

## Terms and ownership

- A **collision proxy** is immutable, local-space solid-collision geometry derived
  from one exact TIE Asset ID and one versioned generation recipe.
- A **proxy binding** associates that exact TIE Asset ID with one proxy Asset ID in
  the current project. It is not attached to a class-family card whose exact source
  variants may differ.
- A **proxy instance** is the transient transformed geometry produced from a binding
  and an enabled project TIE entity during preview or bake.
- **Octant pressure** is the target-native cost of all faces assigned to a collision
  octant after transform, quantization, face intersection, and duplication.

The TIE entity remains the owner. A proxy is not a free-standing collision entity
with a second transform, because that would allow visible geometry and collision to
drift apart. Moving, rotating, scaling, disabling, deleting, undoing, or restoring
the TIE produces the same ownership result for its proxy. Hidden remains preview-only
and does not remove collision from bake.

Generated geometry is stored once as a content-addressed project-attached asset,
never inline in project JSON and never once per instance. Its parent is the exact
source TIE Asset ID. The binding stores the proxy Asset ID, generator and recipe
versions, user-facing parameters, and selected raw collision type. Derived glTF and
octant visualization data remain disposable render-cache content.

Applying a proxy affects matching references only within the current project, in
line with project-isolated asset edits. Undo restores the previous binding; an
unreferenced generated blob becomes eligible for ordinary safe attached-asset
collection. Save, recovery, project move/zip, and missing-asset repair preserve the
binding without depending on a machine-specific temporary path.

## Candidate generation

Game geometry decoding, proxy generation, quantization, and collision analysis live
in the Ratchet PS2 SDK. Forge.Host supplies validated assets and settings, schedules
bounded cancellable work, and stores results. Electron and the renderer do not parse
TIE or collision binaries and do not author authoritative mesh data.

Generation is deterministic for the same source Asset ID, target, generator version,
and recipe. Inputs are bounded before allocation, and candidates are rejected when
their grid, face, vertex, or output limits would exceed the target profile.

### Decimated mesh

The Decimated mesh candidate selects the coarsest usable authored TIE LOD rather than
blindly copying the densest render mesh or running shrinkwrap on a multipart asset.
It retains the internal Surface recipe kind for project compatibility. The SDK:

- transforms decoded packet topology into one canonical local-space triangle set;
- quantizes positions to target-representable precision;
- removes degenerate and exact duplicate faces;
- welds target-equivalent vertices;
- may remove connected components below the recipe's explicit minimum feature size;
- merges compatible coplanar triangle pairs into native quads where this lowers cost;
  and
- runs native octant analysis after cleanup.

This mode is intended for fences, arches, rails, and other assets whose deliberate
openings matter. It does not claim to remove every internal surface; the preview and
diagnostics disclose its result.

### Sectioned outer hull

The general-purpose candidate is a smooth sectioned outer hull. It discards internal
geometry, fills small holes, and retains major vertical profile changes without an
axis-aligned voxel boundary. One section is a conventional convex hull; increasing
**Hull detail** up to sixteen sections lets the proxy follow waists and insets more
closely. Concavities within a horizontal section remain filled. Each height contour is
reduced to at most twelve convex boundary vertices before neighboring contours are
joined, bounding radial face density and preventing source-triangulation fans from
leaking into collision.

The candidate set contains **Decimated mesh** and **Shrinkwrap**. Decimated mesh
preserves authored openings and separate pieces; Shrinkwrap favors a simple exterior
shape with bounded smooth geometry.

### Collision type

Every v0 proxy uses one target-game raw collision byte across all faces. The UI shows
the UYA-scoped collision nibble, sound nibble, combined raw value, and verified label
where available. The target adapter may provide a verified default but must never
infer a type from render material names or reuse another game's numeric meaning.
Per-face type painting is defined separately by the
[Collision Type Painting v0 specification](collision-type-painting-v0.md).

## Native octant analysis and recommendation

The analyzer shares the writer's exact quantization, face/octant intersection,
vertex deduplication, face duplication, and encoded-size calculations. A second
approximate implementation in Forge is not acceptable. For every candidate it
returns at least:

- logical face and vertex counts;
- occupied octant count and duplicate-face count;
- face, unique-vertex, quad, and encoded-byte counts for each octant;
- hard native-limit failures with the responsible octant and limit;
- pressure against the current evidence-backed runtime-safe profile; and
- maximum or sampled surface deviation from the source mesh.

Native field capacity is a hard failure, not a warning. The lower runtime-safe
threshold at which UYA may ignore crowded collision is established from retained
all-level vanilla metrics and focused PCSX2 boundary checks. The threshold and its
evidence are versioned with the UYA collision budget profile. Forge must not present
an unverified guess as a safe limit.

Asset-only analysis is useful for comparing recipes but cannot prove a placement is
safe. Before Apply and during bake, Forge transforms every enabled proxy instance,
combines it with the retained source collision and other generated instances, and
analyzes the complete affected payload. Several individually safe instances sharing
one octant may therefore produce a blocking project diagnostic.

The UI recommends the lowest-pressure candidate that stays inside hard and qualified
soft budgets while satisfying the selected measured-deviation threshold. A
recommendation never applies automatically and does not hide the other candidates.

## Preview and interaction

**Generate collision** is available from the Asset Preview panel for a compatible
TIE and from Properties for a placed TIE. Opening the workflow does not dirty the
project. Candidate generation runs off the renderer and Electron main loops with
progress, cancellation, bounded concurrency, and cache reuse keyed by exact inputs.

The preview overlays the selected candidate on the source TIE and supports:

- switching among available Decimated mesh LODs and Shrinkwrap;
- source-only, proxy-only, and combined visibility;
- a spatial octant-pressure heatmap that remains understandable without color alone;
- face, vertex, occupied-octant, worst-octant, encoded-size, and deviation summaries;
- clear hard-limit and combined-project diagnostics with the responsible Asset ID or
  Entity IDs; and
- keyboard-accessible controls, visible focus, cancel, and Apply actions.

Apply is one editor command. It stores the chosen proxy asset and project binding,
then updates all matching instances in one history entry. Existing and future
placements of the exact TIE Asset ID use the binding by default. Properties exposes
an individual **Collision enabled** override without copying proxy geometry.

## Bake and validation

The Collision layer depends on bound TIE instance transforms and enabled state in
addition to the retained source collision pieces. A TIE without a proxy binding does
not dirty Collision when it moves. A binding, raw-type, generation-recipe, proxy
Asset ID, transform, enabled state, or per-instance opt-out change invalidates
Collision and its existing downstream layers.

During bake the target adapter:

1. Resolves and verifies each proxy and exact parent TIE asset.
2. Applies the full finite instance transform in the correct coordinate basis.
3. Rejects degenerate or unrepresentable scale and reverses winding when a supported
   mirrored transform requires it.
4. Quantizes transformed vertices and expands each enabled proxy into solid faces.
5. Combines additions with surviving edited source-collision pieces.
6. Runs complete octant analysis and blocks unsafe output before staging.
7. Writes deterministically, re-reads the native payload, and compares retained and
   generated face/type semantics plus budget metrics.
8. Atomically publishes the validated Collision snapshot.

V0 inserts generated proxies into the primary collision payload. It does not assign
them to streamed chunk collision until chunk lifetime and spatial ownership are
verified. If primary capacity or the combined budget cannot accept the result, bake
fails actionably rather than silently moving faces to a chunk or dropping them.

When no proxy is bound, existing collision no-edit byte identity remains unchanged.
When proxies are present, source pieces retain their requested geometry and raw type
semantics even though the containing payload must be rebuilt. Equal project state,
assets, target, SDK revision, generator version, and recipe produce byte-identical
output.

## Performance, reliability, and security

- TIE decoding, generation, surface extraction, simplification, instance expansion,
  octant analysis, writing, and semantic re-read are cancellable background work.
- Bridge payloads carry compact recipes, IDs, progress, diagnostics, and cache paths;
  they do not repeatedly copy full mesh arrays through Electron.
- Voxel dimensions, candidate count, faces, vertices, octants, temporary bytes, and
  output bytes have target-owned limits validated before allocation.
- Preview caches are disposable and atomically replaced. The last valid proxy and
  project state survive generation failure.
- Apply, save, recovery, bake, and staging use existing atomic project and layer
  publication paths.
- Asset IDs, recipes, paths, candidate indexes, and generated blobs are validated at
  their owning trust boundary.

## Non-goals

- No claim that UYA map collision has a native instance facility.
- No automatic collision generation on every asset placement.
- No renderer-authored collision or glTF-to-native round trip.
- No arbitrary vertex, edge, face, topology, or per-face type editor.
- No collision inferred from texture opacity, render material names, or unverified
  class conventions.
- No silent proxy regeneration after a generator-version change; regeneration is an
  explicit project mutation producing a new Asset ID.
- No automatic distribution into chunk collision.
- No shared cross-game generation parameters or collision-type meanings.
- No generated player barriers, dynamic moby collision, or physics simulation.

## Implementation tasks

### M2-036 — Expose collision additions and exact octant budgets in the SDK

Requirements: FR-COLL-003, FR-COLL-004, FR-SDK-002, FR-BAKE-003,
FR-BAKE-004, NFR-REL-001, NFR-SEC-003

Depends on: M2-032, M3A-006

Extend the game-neutral collision SDK boundary with validated solid-mesh additions
and structured analysis that shares UYA writer calculations. Retain all-level metrics
and qualify a conservative runtime-safe budget instead of equating native field
capacity with gameplay reliability.

Acceptance:

- The public SDK accepts bounded local/world solid faces with one raw type and can
  compose them with source-piece edits without a Forge-side binary model.
- Analysis and writing share quantization, face/octant intersection, duplicate-face,
  vertex-deduplication, quad, and encoded-size logic.
- Results identify every occupied octant, exact cost, hard limit, and responsible
  addition; equal inputs have stable ordering and output.
- A retained vanilla report records observed distributions without proprietary data.
- A versioned runtime-safe profile is backed by focused PCSX2 evidence; absent
  evidence is reported as unqualified rather than guessed.
- Malformed, excessive, cancelled, or semantically mismatched additions publish no
  bytes.

Verification: synthetic transform/quantization and hard-limit fixtures, writer/analyzer
parity tests, determinism and cancellation tests, retained all-level metrics, and a
PCSX2 budget qualification record.

Progress: the UYA SDK now accepts bounded typed solid additions, preflights proposed
compositions without writing, attributes exact shared-writer octant costs and hard
violations to addition IDs, and rejects unsafe output. Synthetic quantization,
parity, determinism, validation, cancellation, and hard-limit checks pass. Retained
schema-2 metrics cover 62 payloads across 51/51 retail levels with byte-identical
no-op composition; the reconstructed writer layout reports 25 hard diagnostics in
8 payloads (maximum 287 faces, 276 vertices, 206 quads, and 2,320 encoded bytes in
one octant). The versioned runtime-safe profile and focused PCSX2 evidence remain
open and no soft threshold is inferred from these retail observations.

### M2-037 — Generate deterministic TIE collision candidates

Requirements: FR-COLL-002, FR-COLL-003, FR-SDK-002, FR-ASSET-001,
NFR-PERF-004, NFR-SEC-003

Depends on: M2-024, M2-036

Expose validated TIE collision-source topology through the SDK and generate bounded
Decimated mesh and smooth sectioned hull candidates with versioned recipes.

Acceptance:

- Decimated mesh generation selects the coarsest usable authored LOD, removes
  degenerate/duplicate faces, welds target-equivalent vertices, and merges safe
  coplanar pairs.
- Solid hull generation deterministically clips the source into bounded vertical
  sections, reduces each section boundary to a bounded convex contour, follows the
  resulting outer planes, ignores enclosed geometry, retains slopes, and falls back
  safely for planar input.
- Hull profile sections, selected LOD, raw type, generator version, and limits are
  explicit recipe inputs.
- Every candidate carries exact octant diagnostics and source-deviation metrics.
- A candidate that already violates a native structural limit is omitted before
  standalone encoding and cannot prevent other valid candidates from previewing.
- Repeated Linux and Windows generation from equal canonical input is byte-identical.

Verification: authored openings, vertical insets, dense internal geometry, mirrored,
malformed, limit, determinism, and cancellation fixtures.

Progress: the SDK now exposes a versioned UYA Surface v2 candidate generated directly
from validated decoded TIE LOD topology in native coordinates. Generation applies
target quantization, welds target-equivalent vertices, removes collapsed and
orientation-independent duplicate triangles, greedily merges only consistently wound,
exactly coplanar, strictly convex pairs into native quads, preserves one selected raw
type, enforces an explicit maximum-face recipe budget, and reports source/generated
counts plus maximum target-quantization displacement. Synthetic checks cover safe and rejected
merges, native quad round trip, filtering, quantization, determinism, invalid LOD,
unsupported target, cancellation, and explicit face-budget rejection. The browser
binding reduced a real 40-triangle TIE to 19 faces including 13 quads, then composed
and semantically re-read it against retained level collision with addition ownership
and no hard violations. A local corpus pass generated 36,894 faces including 23,945
quads across every available decoded LOD (153 LODs from 86 TIEs) without unexpected
failure.

Every generated Surface and Hull candidate carries the native writer's exact
standalone `CollisionAnalysis`: logical and duplicated counts, occupied octants,
per-octant faces, vertices, quads, encoded bytes, violations, and addition ownership.
The analyzer runs on the final emitted candidate and shares the same code used by
composition preflight. These intrinsic local candidate costs are useful for preview,
but final scene composition analysis remains authoritative because placement and
interaction with retained collision can change octant pressure.

Hull candidates report the exact maximum Euclidean distance from every source vertex
referenced by a non-degenerate, target-quantized triangle to the nearest emitted
collision triangle, plus the sample count. Quads are measured as their two native
triangles. Unused and collapsed strip vertices do not distort mesh bounds or the
metric, cancellation is checked per sample, and measurement rejects recipes requiring
more than 25 million vertex-face tests. This is a vertex-sampled source-to-proxy
metric, not a claim of a continuous bidirectional Hausdorff distance.

Minimum-component filtering and retained corpus evidence remain open for Surface.
Windows execution of the portable golden-byte gate remains open for hull generation.
Further simplification is deferred until qualification finds actual octant pressure.

### M2-038 — Persist reusable project proxy bindings

Requirements: FR-COLL-001, FR-ASSET-004, FR-ASSET-006, FR-PROJ-004,
FR-EDIT-001, NFR-REL-001 through NFR-REL-003

Depends on: M1-005, M1-007, M2-024, M2-037

Add content-addressed project-attached proxy assets, exact TIE Asset-ID bindings,
per-instance opt-out, validation, commands, history, save/recovery, and collection
protection without embedding or duplicating mesh geometry in project content.

Acceptance:

- One proxy blob is stored for any number of matching TIE entities and future
  matching placements inherit the binding.
- Binding applies to one exact TIE Asset ID, not every variant in a class family.
- Apply, replace, remove, and per-instance enable/disable are undoable and preserve
  the TIE as transform owner.
- Undo and replacement leave unreferenced blobs eligible for safe collection but do
  not delete shared or still-referenced data.
- Save/reopen, recovery, project move/zip, migration, and missing-asset repair retain
  the binding and resolve without temporary absolute paths.
- Corrupt recipes, mismatched parent IDs, missing blobs, or unsupported targets block
  mutation or bake without replacing the last known-good state.

Verification: multi-instance deduplication, future-placement inheritance, history,
collection, save/recovery, portability, migration, missing-asset, and corruption tests.

Progress: project schema v5 now persists one validated collision-proxy binding per
exact TIE Asset ID. Each binding references one content-addressed project-attached
Collision asset whose parent is that exact TIE, plus a typed Surface/Hull recipe with
generator and recipe versions, LOD, raw type, and hull profile sections. Legacy Wrap
recipes remain readable for project compatibility. Applying
equal geometry deduplicates its blob; replacement leaves the prior blob unreferenced,
and bound blobs participate in the existing collection-protection query. A nullable
per-entity override records only an explicit TIE collision opt-out, so matching and
future placements inherit the binding without per-instance geometry or repeated JSON.

Domain checks cover shared matching instances, future placement, exact parent
metadata, duplicate application, invalid-recipe rollback, opt-out, save/reopen,
project-relative blob resolution, replacement, removal, recovery, and v4 migration.
The editor runtime now wraps host-owned Apply/Replace mutations in the existing
project-state history and exposes validated Remove and per-instance enable/disable
commands through the IPC command codec. Binding changes mark every matching TIE
dirty; apply, replace, remove, and opt-out all survive undo/redo, including undo after
a save, without deleting their immutable blobs. Explicit attached-blob collection
now removes only assets unused by the saved project and every retained recovery,
including transitive attached parents; it refuses dirty projects. Moved/zip repair
coverage now round-trips the full project through a ZIP, reopens its project-relative
proxy blob, repairs the exact source TIE into a fresh catalog, and retains the binding.
Preview-driven Apply transport is complete, so the automated M2-038 acceptance is complete.
Opening a project now verifies each bound proxy blob by size and content address,
reports actionable missing or corrupt diagnostics, and retains the binding and prior
project state for repair or regeneration.

### M2-039 — Build collision candidate preview and selection

Requirements: FR-COLL-002, FR-COLL-003, FR-UI-004, FR-UI-005,
FR-UI-007, FR-SCENE-002, FR-EDIT-001, NFR-PERF-003 through NFR-PERF-005,
NFR-UX-001 through NFR-UX-003

Depends on: M2-022, M2-034, M2-037, M2-038

Add the Instanced collision workflow to compatible TIE previews and Properties, with
cancellable generation, automatic apply, overlay inspection, exact statistics, and
accessible octant-pressure visualization.

Acceptance:

- Enabling Instanced collision generates and applies a safe default as one history
  entry; failed or cancelled generation preserves the prior binding.
- Users can choose an available Decimated mesh LOD or Shrinkwrap, and changes apply
  automatically without separate Generate or Apply actions.
- Preview shows source/proxy visibility, numeric cost/deviation, hard failures,
  qualified soft pressure, and current combined-project impact.
- The heatmap identifies octants without relying on color alone and remains usable
  with keyboard navigation, focus indication, and display scaling.
- Stale, failed, cancelled, or superseded jobs cannot replace a valid preview or
  project binding, and renderer resources return to baseline after repeated use.

Verification: panel and command tests, candidate/cache race tests, accessibility
checks, render-resource soak, and representative light/heavy TIE interaction captures.

Progress: selecting a TIE now exposes an Instanced collision checkbox in Properties.
Enabling it runs cancellable host-side Decimated mesh generation from the exact
canonical catalog asset, stores the verified native collision blob once, and creates
the exact-asset binding as one undoable history entry. Existing bindings expose their
recipe, per-instance enable toggle, method, available authored LODs, hull detail, and
remove control. Method and collision-ID changes regenerate or update the binding
automatically. A failed or cancelled regeneration leaves the prior valid binding
usable, while open/close and successful regeneration bound cache lifetime. Collision
ID and Sound ID selectors pack the UYA low/high nibbles into the
raw face type used by every generated preset, preview, persisted recipe, and bake.
The selectors reuse the verified collision names and sound labels shown by Collision
Properties and Settings. New proxies default to collision ID `0x0F` (walkable).
Maximum hull detail selects up to 1–16 persisted vertical profile sections (six by
default). Higher values retain large-scale waists and insets, while the generator
removes section boundaries that do not improve measured fit so simple slopes stay
smooth rather than becoming an accordion of quantized bands.
Selecting a candidate now materializes its native collision as a disposable
render-cache glTF overlay at the owning TIE transform. Source and proxy visibility
can be toggled independently, the proxy can be switched to triangle wireframe, and
optional octant boxes show relative encoded-byte pressure alongside a numeric
highest-pressure octant list so the comparison does not depend on color alone. The
wireframe choice is retained while settings or generated candidates change. The panel
keeps generation settings collapsed by default and applies changes automatically.
Every candidate is also transformed across the enabled matching
instances and analyzed with other bound proxies plus edited primary collision. The
panel reports combined faces, octants, worst pressure, responsible Entity IDs, and
blocks Apply on combined hard failures or analysis errors. The three highest candidate
and combined octants are now keyboard-operable
accordions exposing coordinates, faces, vertices, quads, encoded bytes, owners, and
violations without relying on color. The panel explicitly labels soft pressure as
unqualified until the retained PCSX2 gate supplies evidence. Cancellation retains the
prior valid preview, repeated generation keeps only the latest host cache, and repeated
render preparation reuses one cache entry. Interaction captures and runtime-qualified
soft pressure remain open.

### M2-040 — Bake and qualify transformed proxy instances

Requirements: FR-COLL-001, FR-COLL-003, FR-COLL-004, FR-BAKE-001 through
FR-BAKE-004, NFR-PERF-004, NFR-REL-001 through NFR-REL-003

Depends on: M2-035, M2-036, M2-038, M2-039, M3-003, M4-001, M4-006

Expand enabled bindings through their owning TIE transforms, compose them into
primary solid collision, validate the combined payload, and qualify the result in
PCSX2.

Acceptance:

- Position, rotation, supported scale, disabled state, deletion, and per-instance
  opt-out produce the expected transformed additions; mirrored winding is handled or
  rejected explicitly.
- A bound TIE transform invalidates Collision; an unbound TIE transform does not.
- Source collision edits and generated additions compose together deterministically,
  while a project with no bindings retains existing no-edit byte identity.
- Combined analysis includes source faces and every transformed proxy instance, so
  overlapping placements cannot bypass per-octant limits.
- Unsafe primary capacity, hard/soft pressure, invalid transforms, missing assets,
  cancellation, corruption, or semantic re-read failure preserve the prior stage and
  development ISO.
- Reopen and repeated bake retain equal face/type semantics and byte-identical output
  for equal inputs.
- PCSX2 confirms blocking for rotated/scaled copies, open-base behavior, sealed versus
  retained openings, removal/disable behavior, and unaffected source collision.

Verification: end-to-end save/bake/pack/re-read tests, overlap and boundary fixtures,
fingerprint isolation, deterministic hashes, failure injection, and retained PCSX2
qualification evidence.

Progress: the neutral SDK now expands a solid addition through finite position,
normalized rotation, nonzero scale, target-native quantization, and mirrored-winding
correction with cancellation. Preview uses that path for complete prospective primary
collision analysis. Collision bake fingerprints now include exact bindings, recipes,
proxy blobs, enabled matching instances, and their full transforms; unbound TIE
transforms remain isolated. Bake resolves each stored proxy once, expands enabled
instances in stable Entity-ID order, and composes them only into primary
`collision.bin` alongside source edits through the cancellable native writer and its
semantic re-read gate. Automated bake/pack/re-read coverage verifies one face per
enabled instance, per-instance opt-out, proxy-only collision invalidation, and
byte-identical output after the final binding is removed. Boundary analysis now
charges a crossing face to both native octants, while a bake fixture proves that two
individually valid overlapping dense instances fail combined limits without replacing
the last good manifest or collision bytes. An unchanged repeated proxy bake writes no
layers and packs byte-identically. Exact parent TIE and proxy blobs are verified after
preflight; corruption or disappearance fails without replacing the last good stage.
The end-to-end fixture now re-reads packed collision to verify translation, rotation,
non-uniform scale, and mirrored winding, and separately proves that entity disable
and deletion each remove exactly one enabled proxy instance.
The retained [PCSX2 qualification matrix](../qualification/M2-instanced-tie-collision.md)
defines the remaining live gameplay and runtime-pressure evidence gate.
