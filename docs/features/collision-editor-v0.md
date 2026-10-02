# Collision Editor v0

Status: 🚧 In progress

Milestone: M2 — Editor core

Tasks: M2-031 through M2-035

Primary requirements: FR-SDK-002, FR-PROJ-004, FR-UI-003, FR-UI-004,
FR-SCENE-002 through FR-SCENE-004, FR-SCENE-007, FR-SCENE-008, FR-SCENE-011,
FR-EDIT-001, FR-EDIT-002, FR-BAKE-001 through FR-BAKE-004, FR-SET-001,
FR-SET-002, NFR-PERF-004, NFR-REL-001 through NFR-REL-003, NFR-SEC-003,
NFR-UX-001 through NFR-UX-003

## Outcome

Forge renders every supported UYA level-collision payload as selectable project
entities. A user can identify solid collision and player-only barriers, select a
logical piece in the viewport or scene tree, translate it, delete it, undo or redo
the change, save and reopen the project, and build a validated target-native level.

An untouched collision payload is passed through byte-for-byte. Edited payloads are
rebuilt deterministically by the Ratchet PS2 SDK and semantically re-read before
Forge can stage or pack them.

## Scope

| Capability | v0 |
| --- | --- |
| UYA NTSC-U primary collision | Yes |
| UYA NTSC-U populated chunk collision | Yes |
| Solid/main collision | Yes |
| Player-only barrier/hero collision | Yes |
| Scene-tree and viewport selection | Yes |
| Multi-selection and translation | Yes |
| Delete, undo, redo, save, and recovery | Yes |
| Read-only collision and sound type inspection | Yes |
| Game-scoped collision/sound IDs and visual defaults | Yes |
| User-configurable collision, sound-accent, and barrier colors | Yes |
| Rotate, scale, duplicate, add, or reshape collision | No |
| Edit face collision or sound types | No |
| User-authored or unverified ID-specific decorators | No |
| Import arbitrary collision from glTF | No |
| GC, RC1, or DL collision writing | No |

## Existing foundation and format evidence

Forge already extracts the primary `collision.bin` as an immutable Collision base
asset, stages it unchanged, and can relocate it while composing the level asset WAD.
The editor already has authoritative entities, shared Entity-ID selection, translate
commands, delete/history/save/recovery behavior, glTF loading, atomic render caches,
and validated staging. These paths are extended rather than replaced.

The standalone SDK now decodes primary and chunk collision, exports stable glTF
piece nodes, and deterministically rebuilds edited payloads. Forge imports those
pieces as source-backed entities, carries them through its render cache, and stages
primary and chunk replacements through the SDK composer.

Format work starts from independent SDK tests and user-owned UYA samples. Wrench commit
`d80ca3a0b70c756c90f727faafc5513bd14def60` is useful GPL-3.0 reference evidence for
the broad layout—4×4×4 solid-collision octants and bounded player-only triangle
groups—but its implementation is not copied into the SDK. Every claimed field,
limit, alignment rule, and chunk-capacity rule must be established by SDK fixtures
and retained all-level metrics before write support is advertised.

## Terms and editable unit

The UI uses two plain-language layer names:

- **Solid collision** is the main world-space mesh used by fully solid level
  geometry. Native files duplicate a logical face into each intersected 4×4×4
  lookup octant. Faces may be triangles or quads and retain one raw type byte.
- **Player barriers** are the native player-only, or hero, triangle groups. Each
  group has its own bounding sphere and no per-face collision type byte.

A native solid octant is not an editor entity: its duplicated faces would make a
move or delete affect arbitrary copies of the same surface. The SDK first collapses
exact duplicate logical faces, then partitions the solid mesh into deterministic
edge-connected components. One component is one editable solid piece. One native
player-barrier group is one editable barrier piece.

Component ordering is stable for identical source bytes: components are sorted by a
canonical key derived from their lowest typed face and quantized world-space
vertices. The stable piece key is the collision payload Asset ID, piece kind, and
source piece index. The SDK retains triangle/quad topology and the complete raw type
byte in its typed model even though glTF rendering triangulates quads.

This boundary is deliberately coarse. Per-face or vertex editing would require a
mesh-editing workflow and is not implied by selecting a rendered triangle.

## SDK reader, glTF, and writer

Map-collision binary parsing and writing live in the game library under
`RatchetPs2.Games.UYA`. Game-neutral byte-oriented SDK entry points dispatch from
`GameId`; the CLI only supplies paths and diagnostics. No collision binary parser is
added to Forge, Electron, or the renderer.

The reader validates before allocating:

- top-level solid-mesh and player-barrier offsets, ordering, and alignment;
- Z/Y/X lookup ranges, relative offsets, octant sizes, counts, and bounds;
- packed vertex ranges, face/quad counts, vertex indexes, and raw type bytes;
- barrier group counts, data offsets, vertex/triangle indexes, zero padding, and
  bounding values; and
- complete range ownership, so malformed or overlapping regions fail actionably.

The glTF exporter emits one named node per logical piece beneath `solid_collision`
or `player_barriers`. Node extras contain a versioned collision schema, piece kind,
source piece index, counts, bounds, and a raw-type histogram. Geometry uses the
existing PS2-to-glTF coordinate basis. Names and extras are diagnostics and mapping
data, not project authority.

The thin CLI command is:

```text
ratchet-ps2 collision export-gltf --game UYA --input collision.bin --output collision.gltf
```

The editor does not round-trip through glTF. The SDK composer instead accepts the
validated source bytes plus a bounded list of piece edits: source key, effective
translation, or removal. It applies edits to logical geometry, rebuilds the solid
lookup tree and required face duplication, recomputes barrier bounding spheres,
quantizes native fields, enforces native count/offset limits, writes deterministically,
and re-reads the result.

If no effective edit exists, the composer returns the original bytes without
serializing. Byte identity is therefore required for every untouched primary and
chunk payload; modified output is required to be deterministic and semantically
equivalent to the requested edits, not byte-identical to the original packing.

Chunk support includes reading the compressed collision member, replacing it while
preserving unrelated chunk members, and updating offsets and compression metadata.
Archive composition receives all primary and chunk collision outputs together so it
can preserve or recompute the verified primary collision capacity needed by UYA's
chunk-loading layout. Unchanged members and chunks remain byte-identical.

## Game-scoped type and material encoding

Solid faces retain the complete native type byte. The high nibble is the sound
layer and the low nibble is the collision type:

```text
raw = (soundType << 4) | collisionType
```

Both values are exposed as `0x0` through `0xF`; the combined byte is exposed as
`0x00` through `0xFF`. Their semantics are scoped to the source game. A value from
UYA does not inherit the name, behavior, color, or decoration used by the same
numeric value in RC1, GC, or DL. Verified game-specific names may supplement the
numeric values, but an unknown value is never guessed, normalized, or discarded.

Game-specific collision code owns ID labels, default palettes, and material
decorations; there is no shared cross-game enum. UYA's currently decoded low-nibble
labels include water, hazards, magnet walls, mud, walkable surfaces, and sliding
surfaces. Type `0xA` remains explicitly unconfirmed.

The SDK writes collision type as the portable `COLOR_0` base and preserves both raw
nibbles as `_COLLISION_TYPE` and `_SOUND_TYPE` vertex attributes. Forge keeps the
collision color dominant and applies the sound color as a subtle full-surface tint
that remains visible on small pieces. Forge derives face normals in the shader for
gentle view-relative shading without expanding the collision geometry. This retains one shared material rather than creating 256
materials and draw-state groups. Ordinary glTF viewers still show the collision
base color when they ignore the custom attributes.

Forge Settings exposes all 16 collision base colors, all 16 sound accent colors,
and the player-barrier color under a game-specific Collision visualization group.
It reuses the existing accessible color picker and reset behavior. Overrides are
validated `#RRGGBB` values, machine-local, and preview-only: they are not stored in
projects, do not alter raw IDs, and never dirty or change baked collision. Resetting
restores that game's defaults, and overrides are never silently reused for another
game. Invalid settings fall back to the affected default with a diagnostic.

The SDK's generic palette remains an explicit glTF export input. Forge applies its
machine-local palette through material uniforms, so a color change updates the live
viewport without regenerating glTF, reloading terrain, or changing render-cache
identity. Entity moves, states, and deletion also do not regenerate glTF.

Color is not the only carrier of meaning. Selection, tree rows, the Properties
panel, and the collision legend display `Sound 0xS · Type 0xC · Raw 0xSC`; a piece
with several values shows a count-sorted histogram.

Player barriers use their configurable color in a separate high-contrast translucent
material with a portable flat glTF fallback. Forge enhances it with subtle diagonal
object-space stripes whose brighter band is fully transparent, without duplicating
geometry. This stripe indicates the barrier layer; it is not a meaning assigned to a
numeric collision ID. The material is double-sided, remains pickable, and has a
selected outline that is visible against both light and dark maps. Stripes are
decorative; losing the enhancement cannot hide the barrier or remove its label.

## Project ownership and migration

Every collision piece becomes an ordinary typed `ProjectEntity` with an identity
transform, Collision asset reference, provenance, state, and a small collision-piece
record containing only its kind and source piece index. Project files do not copy
collision vertices, glTF, native octants, or barrier bytes.

New projects import all pieces from the primary payload and every populated chunk.
Existing projects get one explicit source-ISO/catalog-backed migration. The base
entity version records completion so deleted pieces are never resurrected on later
opens. Missing or changed source blobs block migration or bake without replacing
the last known-good project.

Only translation is representable in v0. The target adapter quantizes solid-piece
translation to the verified packed-vertex precision and barrier translation to its
native precision before command commit; snapshots and preview show that effective
value. Rotation and scale remain identity and their controls are disabled with an
explanation. Invalid, non-finite, or out-of-range movement is rejected before
project mutation.

Hidden and locked retain their normal editor-only meanings. Disabled pieces are
omitted from rebuilt collision. Deleting a piece removes its entity and has the same
bake result as disabling it, but participates in the normal deletion fallback and
migration rules. Collision pieces cannot be duplicated, copied, pasted, or changed
to an unrelated layer in v0.

## Render package and scene interaction

The host-generated render package gains collision glTF paths and validated mapping
metadata keyed by payload Asset ID, piece kind, and source piece index. Cache keys
include every collision Asset ID, SDK revision, render schema, and effective visual
profile fingerprint. Entity transforms and state are projected onto those immutable
source nodes, so moving, hiding, or deleting a piece does not regenerate glTF. A
complete new cache replaces the old cache atomically; failure leaves the last
known-good scene available.

The scene tree groups rows without inventing a second hierarchy authority:

```text
Collision
  Primary
    Solid collision
    Player barriers
  Chunk 1
    Solid collision
    Player barriers
  Chunk 2
    Solid collision
    Player barriers
```

Empty payload/layer groups are omitted. Child rows use project Entity IDs and show
piece index, face count, and dominant or mixed raw type. Group rows provide
show/hide controls and counts but are not fake entities.

Tree selection, viewport picking, Properties, focus, and selection outlines use the
existing shared selection. A translate gizmo or numeric position edit issues the
ordinary `updateTransforms` command; a completed multi-piece drag is one history
entry. Delete uses `deleteEntities` after the standard confirmation policy and
selects the next row, previous row, or nothing. Undo and redo restore the same Entity
IDs, source keys, effective translations, and selection.

Collision visibility defaults on, with independent toggles for solid collision and
player barriers. Visibility toggles are view state, not project mutations. Hidden,
disabled, and deleted pieces are not pick or snap targets. A moved visible piece may
be used by existing center, vertex, surface, and Page Down snapping where the shared
tools already support it; no collision-specific snapping engine is added.

## Bake, pack, and validation

The Collision bake fingerprint includes ordered enabled piece keys and effective
translations, plus all primary/chunk source Asset IDs. It ignores selection, tree
expansion, view toggles, visualization colors, names, hidden state, and locked state.
A collision edit dirties Collision and its existing downstream graph only; unrelated
entity edits do not regenerate collision.

Staging resolves every source Asset ID, validates all edits as one proposed collision
set, invokes the SDK composer off the host and renderer loops, and writes a temporary
snapshot. Validation re-reads each output and compares:

- surviving logical pieces, topology, raw type bytes, and effective positions;
- solid octant coverage and duplicate-face ownership;
- barrier group order, triangle indexes, and recomputed bounding spheres;
- native counts, offsets, alignment, compressed chunk members, and primary capacity;
  and
- hashes for every untouched collision and non-collision payload.

Only a fully validated snapshot atomically replaces the prior Collision stage.
Cancellation, malformed source data, native overflow, compression failure, or a
semantic mismatch leaves project state, prior staging, and the development ISO
unchanged.

Qualification includes untouched byte identity, deterministic repeated edits,
primary and chunk pieces, both collision layers, mixed type bytes, zero-piece
boundaries, save/reopen/recovery, undo/redo, level-WAD pack/re-read, and recorded
PCSX2 checks for a moved and deleted solid piece plus a moved and deleted player
barrier. The game check confirms physical blocking and at least two distinguishable
sound-type surfaces, not only visual output.

## Performance and limits

Parsing, component construction, octant rebuilding, glTF creation, compression, and
validation run in cancellable background work with bounded progress. Bridge payloads
contain paths and compact metadata, never whole collision binaries or vertex arrays.

The renderer shares geometry/material resources per loaded collision glTF, projects
only Entity-ID transforms and state, and disposes superseded resources. Solid faces
use one material; barriers use one material. Large groups in the scene tree use the
existing bounded/virtualized behavior. A representative heavy UYA level must remain
interactive during selection and translation and return resource counts to baseline
after repeated load/unload.

## Non-goals

- No arbitrary vertex, edge, face, quad, topology, or bounding-sphere editor.
- No collision/sound type painting; unknown or unconfirmed meanings remain numeric.
- No shared cross-game collision-ID meanings, palettes, or decorators.
- No user-authored decorator editor; the first pass exposes color overrides only.
- No automatic regeneration from moved tfrags, ties, shrubs, or mobys.
- No collision insertion, duplication, clipboard transfer, or cross-level reuse.
- No glTF-to-native collision import path.
- No renderer-authored project state or direct patching of native bytes in Forge.
- No claim that the first verified UYA layout applies to another Ratchet game.

## Implementation tasks

### M2-031 — Decode and export UYA map collision in the SDK

Status: ✅ Complete

Requirements: FR-SDK-002, FR-SCENE-011, NFR-SEC-003, NFR-MAINT-001

Depends on: M3A-004

Implement the validated UYA primary/chunk collision readers, typed logical model,
duplicate-face collapse, deterministic solid-component partitioning, material
palette inputs and generic defaults, glTF exporter, and thin `collision export-gltf`
CLI command.

Acceptance:

- Primary and compressed chunk fixtures expose every valid solid component and
  player-barrier group as stable named glTF nodes.
- Triangles, quads, world coordinates, raw type bytes, nibble histograms, barrier
  bounds, and node extras match the decoded typed model.
- Raw ID interpretation, labels, and optional decorators remain game-scoped; UYA's
  high sound nibble and low collision nibble are preserved as vertex attributes,
  `COLOR_0` uses the low-nibble base, and barrier nodes use the portable translucent
  material.
- Malformed offsets, counts, indexes, overlaps, padding, compression, and native
  limits fail with section/offset diagnostics before unbounded allocation.
- The game library and SDK own reusable logic; the CLI contains only path and
  presentation orchestration, and no GPL implementation is copied.

Verification: synthetic binary fixtures for empty/min/max/sparse/mixed layouts,
default/override palette goldens, malformed-input cases, deterministic glTF hashes,
CLI smoke tests, and retained all-level decode metrics without proprietary bytes.

### M2-032 — Compose and validate edited native collision

Status: ✅ Complete

Requirements: FR-SDK-002, FR-BAKE-003, FR-BAKE-004, FR-BUILD-002 through
FR-BUILD-004, NFR-REL-001, NFR-REL-003

Depends on: M2-031, M3A-006

Add the SDK collision composer and extend level/archive composition to replace
primary and chunk collision together while preserving unrelated payloads.

Acceptance:

- Translation and removal rebuild solid octants and player-barrier groups
  deterministically, recompute bounds, preserve raw type bytes, and re-read to the
  requested logical result.
- An empty solid layer, empty barrier layer, and all-pieces-removed payload either
  produce the verified native empty form or fail with an evidence-backed target
  limit before publication.
- A no-op returns each original payload byte-for-byte; repeated modified composition
  has stable hashes.
- Chunk offsets/compression and any verified primary collision capacity are updated
  from all outputs; untouched chunk members and level assets retain their hashes.
- Overflow, out-of-range quantization, malformed sources, cancellation, and semantic
  mismatch publish no partial result.

Verification: writer/re-reader fixtures, no-op and determinism hashes, native-limit
boundaries, primary/chunk relocation tests, cancellation/failure injection, and a
local all-level round-trip report.

### M2-033 — Promote collision pieces into project entities

Status: 🚧 In progress

Requirements: FR-PROJ-004, FR-SCENE-002, FR-SCENE-004, FR-EDIT-001, FR-EDIT-002,
NFR-REL-001, NFR-REL-003

Depends on: M1-005, M2-001, M2-008, M2-031

Add the typed collision-piece record, source-backed creation/migration, target
translation quantization, command validation, asset-reference protection, and
Collision bake-input projection. Reuse existing transform, delete, state, history,
save, and recovery behavior.

Acceptance:

- New and migrated projects contain one stable entity for every primary/chunk solid
  component and player barrier without embedding geometry or native bytes.
- Translate, multi-translate, delete, disable, undo, and redo retain stable source
  keys and effective target-representable values.
- Rotate, scale, duplicate, clipboard, layer changes, invalid source keys, and
  out-of-range transforms are rejected without dirtying the project.
- Save/reopen/recovery and Linux/Windows serialization preserve collision edits;
  completed migration never resurrects a deleted piece.
- Collision fingerprints change only for enabled composition or effective
  translation changes and protect every referenced source Asset ID from collection.

Verification: creation/migration, command/history, quantization, malformed bridge
payload, fingerprint isolation, asset collection, recovery, and portability tests.

### M2-034 — Render and edit collision in the Forge scene

Status: 🚧 In progress

Requirements: FR-UI-003, FR-UI-004, FR-SCENE-003, FR-SCENE-007,
FR-SCENE-008, FR-SET-001, FR-SET-002, NFR-PERF-003, NFR-PERF-005,
NFR-UX-001 through NFR-UX-003

Depends on: M2-002 through M2-005, M2-007, M2-009, M2-033

Extend Settings, the atomic render package, scene projection, tree, viewport,
Properties, and visibility controls for collision entities. Reuse the existing color
picker, shared selection, transforms, delete, snapping, focus, and resource ownership.

Acceptance:

- Primary/chunk Solid collision and Player barriers appear in the specified tree
  groups with Entity-ID rows, counts, raw nibble labels, filtering, and keyboard
  selection.
- Picking, tree selection, focus, Properties, outlines, translation, numeric entry,
  deletion, undo, and redo remain synchronized without direct Three.js mutation.
- With defaults, combined solid colors are identical to CLI output. Per-game color
  overrides update solid and barrier rendering without changing project dirtiness or
  bake fingerprints; reset restores generic defaults.
- Barriers are translucent, double-sided, pickable, and striped where supported with
  a visible flat fallback; no unverified ID-specific decorator is shown.
- Layer visibility is non-persistent view state; hidden/disabled/deleted pieces do
  not remain pickable or selectable through stale render nodes.
- Cache replacement failure keeps the prior collision visible, and repeated
  load/edit/unload returns GPU and URL ownership to baseline.

Verification: render-package validation, scene projection/picking tests, tree and
keyboard flows, transform/delete/history integration, settings validation/reset,
default and override material goldens, cache failure injection, and a representative
heavy-level interaction/resource soak.

Progress: collision pieces now load as selectable batched scene nodes with synchronized
translation, deletion, snapping, Properties data, parent/tree visibility, and separate
non-persistent Solid/Player-barrier viewport toggles. UYA settings expose the generic
16×16 color profile and barrier color; barriers use a translucent striped material.
The projection suite covers picking, hidden-state exclusion, snapping vertices, and a
3,036-piece representative load/update/dispose case. Palette reloads retain the last
visible package until its atomic replacement succeeds. Collision-only cache reuse and
interactive failure/resource qualification remain open.

### M2-035 — Bake, pack, and qualify collision edits

Status: 🚧 In progress

Requirements: FR-BAKE-001 through FR-BAKE-004, FR-BUILD-001 through
FR-BUILD-004, NFR-PERF-004, NFR-REL-001 through NFR-REL-003

Depends on: M2-032, M2-033, M2-034, M3-003, M4-001, M4-006

Wire collision entities into staging, primary/chunk archive replacement, semantic
validation, progress/cancellation, and the PCSX2 qualification loop.

Acceptance:

- An untouched project stages and packs every collision payload byte-identically;
  edited output is deterministic and reopens with the requested surviving pieces,
  effective translations, raw types, and barrier bounds.
- Only Collision and its declared dependents invalidate; unrelated entity or view
  changes do not rebuild collision or rewrite unrelated WAD members.
- Save/reopen/recovery, undo/redo, primary/chunk edits, mixed types, and zero-layer
  boundaries survive bake and level-WAD pack/re-read.
- Cancellation and injected decode/write/compress/validation failures preserve the
  last good stage and development ISO with actionable diagnostics.
- PCSX2 confirms moved/deleted solid collision, moved/deleted player barriers, and
  distinguishable behavior on at least two sound-type surfaces.

Verification: end-to-end fingerprint/stage/pack/re-read tests, deterministic hashes,
failure injection, all-level no-op audit, and a retained collision-editor PCSX2
qualification report.

Progress: the automated gate now covers collision-only invalidation, save/bake/pack/
native re-read for moved and removed Solid and Player-barrier pieces, cancellation,
injected staged-output corruption, and a retained 51-level/62-payload byte-identical
audit with per-payload SHA-256 checks. Results and the pending manual PCSX2 rows are
recorded in the [collision editor qualification](../qualification/M2-collision-editor.md).
The first in-game run passed moved/deleted Solid collision and confirmed unrelated
collision retained its source position; Player-barrier and sound-type rows remain.
