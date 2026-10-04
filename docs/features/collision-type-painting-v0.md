# Collision Type Painting v0

Status: 🚧 In progress

Milestone: M2 — Editor core

Tasks: M2-041 through M2-044

Primary requirements: FR-COLL-001 through FR-COLL-005, FR-PROJ-004,
FR-UI-004, FR-UI-005, FR-SCENE-002, FR-SCENE-004, FR-EDIT-001,
FR-BAKE-001 through FR-BAKE-004, NFR-PERF-003 through NFR-PERF-005,
NFR-REL-001 through NFR-REL-003, NFR-SEC-003, NFR-UX-001 through
NFR-UX-003

## Outcome

A user can paint target-game collision and sound IDs directly onto the native faces
of an applied shared TIE collision proxy. Painting is previewed immediately over the
owning TIE, committed once per stroke through normal command history, retained across
save and recovery, reused by every matching TIE instance, and expanded into validated
target-native collision during bake.

Painting is authoritative. Render materials do not determine collision behavior.
Material selection may later accelerate selection where a source-to-proxy mapping is
proven, but shrinkwrap geometry and native collision faces remain independent of the
render material layout.

## Scope

| Capability | v0 |
| --- | --- |
| Applied shared TIE solid-collision proxies | Yes |
| Full UYA raw type byte per native face | Yes |
| Click-drag face painting | Yes |
| Reset face or all faces to the binding default | Yes |
| Eyedropper from a proxy face | Yes |
| One undo entry per completed stroke | Yes |
| Shared result across every matching TIE instance | Yes |
| Paint existing primary or chunk collision pieces | No |
| Paint player-only barriers | No |
| Material-driven automatic assignment | No |
| Brush-radius, flood-fill, or topology subdivision | No |
| Vertex, edge, or geometry editing | No |

## Authoring unit and face identity

UYA stores one raw type byte per solid collision triangle or quad. The high nibble
remains the sound ID and the low nibble remains the collision ID. A rendered quad is
two triangles, but both triangles are one authoring face and must always receive the
same value.

The SDK assigns every face in a decoded proxy a zero-based ID in deterministic face
order and exports that ID as `_COLLISION_FACE_ID` on every vertex of the face. Both
render triangles of a native quad repeat the same ID. Face identity is scoped to the
exact immutable proxy Asset ID; Forge never assumes that a face ID survives proxy
replacement or regeneration.

The collision glTF continues to carry `_COLLISION_TYPE` and `_SOUND_TYPE`. Its
face-separated vertex layout lets the renderer update the affected attributes for an
immediate preview without changing geometry, materials, or authoritative project
state.

## Project ownership and commands

The exact TIE Asset-ID binding remains the owner. It stores an ordered sparse list of
`faceIndex` and `rawType` overrides next to the proxy Asset ID and generation recipe.
The proxy blob remains immutable and content-addressed; a brush stroke does not
create another collision asset.

An override equal to the recipe's default raw type is omitted. Entries are unique,
sorted by face index, bounded by the decoded proxy face count, and valid only while
the binding references the same proxy Asset ID. Migrating an older project produces
an empty override list and therefore preserves its current one-type behavior.

The renderer sends one bounded `setTieCollisionFaceTypes` command at pointer release,
not one bridge request per pointer event. The command includes the selected TIE
Entity ID, expected proxy Asset ID, and unique face assignments. The host resolves
the exact shared binding, rejects stale or invalid input before mutation, normalizes
the sparse list, and records the whole stroke as one history entry. Undo and redo
restore the previous overrides and every matching projected instance.

Changing the binding's default Collision or Sound ID affects unpainted faces only.
**Reset painted faces** explicitly clears all overrides. Removing or replacing the
proxy removes its old face-ID domain rather than attempting an unsafe geometric
remap.

## Painting interaction

Properties exposes **Paint collision types** only for an editable TIE with an applied
shared proxy. Entering the mode loads one disposable, raycastable proxy overlay at the
selected instance transform, renders it above the source mesh, disables transform manipulation,
and presents Collision ID, Sound ID, Reset, Eyedropper, wireframe, and source/proxy
visibility controls.

Primary-pointer drag samples the visible proxy and accumulates every crossed native
face once. The overlay recolors immediately from renderer-local pending values. On
release, one command commits the distinct assignments; a rejected command restores
the authoritative snapshot. Escape cancels an active uncommitted stroke, and exiting
the tool disposes the overlay. Middle-button drag rotates the camera while the primary
pointer remains dedicated to painting; Alt-drag remains available as a fallback.

The tool highlights the exact native face under the cursor and states that edits are
shared by all instances of the exact TIE asset. Selection, labels, and numeric IDs
remain available without depending on color. Controls are keyboard accessible and
retain visible focus at supported display scaling.

V0 intentionally has no radius heuristic. Pointer interpolation prevents skipped
faces during a quick stroke, but only ray-hit visible faces change. Add connected
radius or fill behavior only if representative use shows that direct strokes are too
slow.

## Resolution and topology boundary

Paint resolution is proxy topology resolution. Forge cannot assign two behaviors to
different regions of one native triangle or quad. The cursor and wireframe make that
boundary visible, and Properties recommends higher hull detail or a Decimated mesh
when the generated faces are too coarse.

Forge does not silently split a face to match a brush path. Subdivision changes face
and vertex counts, octant duplication, encoded size, deviation, and possibly runtime
safety, so it belongs in a future SDK generation recipe with ordinary candidate
analysis and explicit application.

## Preview, bake, and validation

The applied proxy model is loaded from its verified project-attached asset through a
bounded host endpoint and disposable render cache. The renderer applies persisted
overrides to the exported base attributes, then layers pending stroke values over
them. Normal scene collision batching remains unchanged in v0.

Before transforming a proxy instance, the UYA adapter assigns each decoded face its
sparse override or the recipe default. Every enabled matching instance therefore
uses the same local-space painting under position, rotation, supported scale, and
mirroring. Raw-type edits do not alter topology or encoded record width, but the
complete collision composition still runs normal octant analysis, deterministic
write, semantic re-read, and atomic publication.

The Collision fingerprint includes the ordered face overrides. Validation compares
the effective raw type of every painted face after transform and re-read while also
proving that painting did not change proxy topology. Missing assets, stale face IDs,
malformed overrides, unsupported transforms, cancellation, writer failure, or a
semantic mismatch preserves the last known-good project, stage, and development ISO.

## Performance, reliability, and security

- Pointer movement mutates only disposable overlay attributes and a bounded in-memory
  set; it does not call the host or rebuild collision.
- One completed stroke has at most one entry per proxy face and one command/history
  entry.
- The host validates Entity ID, expected proxy Asset ID, duplicate indexes, decoded
  face bounds, raw bytes, command size, and target support before mutation.
- Proxy decode, glTF export, composition, and semantic re-read remain off Electron
  and renderer loops and retain existing cancellation and atomic cache behavior.
- Repeated enter, cancel, commit, selection change, project close, and failed load
  return overlay GPU resources and listeners to baseline.

## Non-goals

- No inference from material names, texture slots, opacity, or another game's IDs.
- No claim that painted regions correspond to original render submeshes.
- No new generic mesh-editing framework or renderer-authored native geometry.
- No persistent duplicate proxy per TIE instance.
- No automatic face remap when generation settings or proxy geometry change.
- No paint-through, hidden-face brush, pressure sensitivity, or GPU picking buffer.

## Implementation tasks

### M2-041 — Expose deterministic collision face identity

Status: ✅ Complete

Requirements: FR-COLL-002, FR-COLL-005, FR-SDK-002, NFR-REL-002

Depends on: M2-037

Add explicit zero-based face identity to solid-collision glTF exports and expose the
decoded proxy face count needed to validate a binding's face domain.

Acceptance:

- Every solid collision vertex carries `_COLLISION_FACE_ID`; a native quad's two
  triangles resolve to the same ID.
- IDs cover exactly `0..faceCount-1` in deterministic decoded face order.
- Repeated exports of equal canonical bytes produce equal face IDs and bytes.
- Player barriers do not advertise editable face IDs.
- Existing collision type and sound attributes and ordinary glTF rendering remain
  unchanged.

Verification: triangle/quad attribute fixtures, repeated-export byte comparison,
malformed proxy rejection, and renderer face-pick mapping test.

Progress: the UYA collision exporter now emits deterministic global
`_COLLISION_FACE_ID` values in decoded solid-piece/face order. Every vertex of a
native triangle or quad repeats its face ID, player barriers omit the attribute, and
the renderer rejects missing, non-integer, or inconsistent triangle IDs. Targeted
synthetic SDK and renderer mapping checks cover disconnected pieces and both triangles
of a rendered native quad. The existing neutral decoded-addition API exposes the same
ordered face list and count for host-side validation without another SDK abstraction.

### M2-042 — Persist and command sparse face-type overrides

Status: ✅ Complete

Requirements: FR-COLL-001, FR-COLL-005, FR-PROJ-004, FR-EDIT-001,
NFR-REL-001 through NFR-REL-003, NFR-SEC-003

Depends on: M2-038, M2-041

Extend exact-asset proxy bindings with normalized sparse overrides and add one bounded
host command that commits a complete brush stroke against an expected proxy Asset ID.

Acceptance:

- Older projects migrate to an empty override list without changing bake behavior.
- Commands reject stale assets, duplicate or out-of-range face IDs, oversized lists,
  missing bindings, locked source entities, and unsupported targets before mutation.
- Applying the default removes an override; persisted entries are unique and sorted.
- One stroke is one undo/redo entry and updates every matching TIE snapshot.
- Save/reopen, recovery, project move, binding replacement, and removal preserve or
  discard overrides according to their exact proxy ownership.

Verification: schema migration, validation rollback, normalization, shared-instance,
history, save/recovery, portability, replacement, and command-codec tests.

Progress: project schema v7 stores normalized sparse `faceIndex`/`rawType` pairs on
the exact-asset proxy binding and migrates older bindings to an empty list. A bounded
`setTieCollisionFaceTypes` command verifies the current proxy, decodes its attached
blob for the authoritative face count, and commits a complete stroke as one history
entry shared by every matching TIE. Bridge codecs and snapshots carry the new data;
tests cover migration, default-value removal, ordering, stale and malformed commands,
locked and unsupported sources, undo/redo, save/recovery, project transfer, proxy
replacement/removal, TypeScript boundary validation, and the C# command codec.

### M2-043 — Build the freehand collision-type painter

Status: ✅ Complete

Requirements: FR-COLL-005, FR-UI-004, FR-UI-005, FR-SCENE-002,
FR-SCENE-004, NFR-PERF-003, NFR-PERF-005, NFR-UX-001 through NFR-UX-003

Depends on: M2-039, M2-041, M2-042

Add a focused Properties and viewport workflow that paints visible native proxy faces
with immediate local feedback and commits once per completed pointer stroke.

Acceptance:

- The tool shows the exact proxy topology, hovered face, effective IDs, shared-asset
  scope, and source/proxy visibility without relying on color alone.
- Drag interpolation does not skip crossed faces at normal interaction speed and does
  not issue per-pointer host calls.
- Reset, eyedropper, cancel, navigation modifier, wireframe, and keyboard focus work
  without mutating geometry.
- A failed/stale command restores authoritative colors and reports an actionable
  diagnostic.
- Repeated tool entry, exit, cancellation, project close, and failed model load leave
  no overlay resources or listeners behind.

Verification: pointer-stroke and quad mapping tests, one-command assertion,
accessibility checks, stale-response race tests, and render-resource soak.

Progress: Properties now opens a focused painter for the verified applied proxy,
with separate collision/sound selectors, face reset, reset-all, eyedropper, source and
proxy visibility, wireframe, explicit shared-instance scope, and numeric hovered-face
feedback. The viewport disables transforms while painting, uses middle-drag or Alt-drag
for camera navigation, interpolates ray samples across fast pointer movement, previews attribute
changes locally, cancels an active stroke with Escape, and sends one bounded command
only on pointer release. Stale or failed commits restore snapshot colors through the
existing error path, while model requests, listeners, geometry, and materials follow
the existing cancellation and disposal lifecycle. Applied proxy models are read from
the current exact binding with content-integrity verification and share the
content-addressed render cache with generated previews.

### M2-044 — Bake and qualify painted shared proxies

Status: 🚧 In progress

Requirements: FR-COLL-003 through FR-COLL-005, FR-BAKE-001 through
FR-BAKE-004, NFR-PERF-004, NFR-REL-001 through NFR-REL-003

Depends on: M2-040, M2-042, M2-043, M4-001, M4-006

Apply effective per-face types before instance transformation, include overrides in
Collision fingerprints, and qualify mixed-type proxies through native write/re-read
and PCSX2.

Acceptance:

- All enabled matching instances receive identical local face semantics under
  translation, rotation, supported scale, and mirroring.
- Unpainted faces retain the binding default and Reset restores the prior one-type
  behavior without regenerating geometry.
- Paint-only changes dirty Collision, preserve topology and cost, and rebuild no
  unrelated layer.
- Equal projects produce byte-identical collision; semantic re-read proves every
  effective raw byte and unchanged geometry.
- Invalid overrides, missing assets, cancellation, native failures, or semantic
  mismatch preserve the prior valid stage and development ISO.
- A retained PCSX2 case demonstrates a mixed stone/magnet shrinkwrap surface on more
  than one transformed instance.

Verification: composition and fingerprint fixtures, transform/mirroring cases,
save/bake/pack/re-read tests, failure injection, and retained PCSX2 evidence.

Progress: the UYA collision composition adapter now applies each binding's effective
per-face raw type before instance transformation. Ordered overrides participate in
the Collision fingerprint, so paint-only changes rebuild Collision and no unrelated
layer. Bake/pack/re-read coverage proves painted bytes reach every matching instance,
topology is unchanged, and Reset restores byte-identical uniform collision. External
PCSX2 qualification remains pending with M4-006.
