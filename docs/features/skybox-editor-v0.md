# Skybox Editor v0

Status: ✅ Complete

Milestone: M2 — Editor core

Tasks: M2-027 through M2-030

Primary requirements: FR-ASSET-004, FR-UI-004, FR-UI-007, FR-SCENE-002,
FR-SCENE-007, FR-EDIT-001, FR-BAKE-001 through FR-BAKE-004, NFR-REL-001
through NFR-REL-003, NFR-PERF-004, NFR-SEC-003, NFR-UX-001

## Outcome

The editor treats the current sky as an ordered collection of project-owned shell
instances instead of one opaque preview. A user can add a compatible shell from the
Asset Explorer, select and inspect it in the scene tree, edit its initial rotation
and angular velocity in Properties, reorder it, or remove it. Every mutation uses
the normal command, history, save, recovery, bake, and validation paths.

The immutable catalog remains source authority for shell geometry and textures.
Editing a project's composition never mutates a catalog sky asset or another
project. Forge writes a new target-native sky payload only during validated staging.

## Scope

| Capability | v0 |
| --- | --- |
| Add a compatible catalog shell | Yes |
| Remove or reorder a project shell | Yes |
| Add the same source shell more than once | Yes, within target limits |
| Edit initial X/Y/Z rotation | Yes |
| Edit X/Y/Z angular velocity / rotation speed | Yes |
| Select a shell and inspect source metadata | Yes |
| Edit shell geometry, flags, blend mode, materials, or textures | No |
| Replace sky header color, clear mode, sprites, or FX | No |
| Convert a shell from another game | No |

The owning base sky continues to provide header color, clear-screen behavior,
sprites, FX data, and their texture dependencies. A dragged shell contributes only
its shell geometry, source flags, rotation defaults, and referenced textures.

## Existing foundation and required gap

Forge already has:

- cataloged parent sky assets with SDK-produced shell indexes;
- isolated shell previews and persistent thumbnails;
- SDK decoding for shell geometry, flags, textures, initial rotation, and rotation
  deltas;
- renderer support for source blend behavior and animated shell rotation; and
- an independent Sky bake layer that stages the original `sky.bin` byte-for-byte.

The current SDK has a sky reader and glTF exporter but no target-native sky writer or
composer. The editor scene tree therefore exposes loaded glTF shell names as
non-selectable render details, and the retained base-layer sky remains the sole bake
authority. Renderer-only insertion would lose edits on reopen and cannot be used.

## Ownership and project model

Sky shells become ordinary typed project entities so the existing stable Entity ID,
selection, state, command history, diagnostics, save, recovery, and Properties paths
remain authoritative. `ProjectEntity` gains one optional typed sky-shell record with:

- the source shell index within the entity's parent `AssetKind.Sky` reference;
- an explicit composition order;
- initial rotation in normalized project radians; and
- angular velocity in radians per second.

The entity Asset ID identifies the immutable parent sky blob. Provenance identifies
the source game, level, sky section, and source shell index. Geometry, source flags,
blend mode, texture use, cluster count, and triangle count are derived from the
validated asset and are not copied into a generic metadata bag.

Sky-shell entities use identity spatial transforms and do not expose translate,
rotate, scale, snapping, or Page Down tools. Their native initial rotation is edited
through the typed sky-shell properties because it is an ordered Euler field, not an
ordinary world transform. Hidden remains preview-only; disabled shells remain in the
project but are omitted from composed bake output. Deleting the entity removes the
shell from the composition.

Project creation imports every base shell as one typed entity with a stable ID and
the same order, rotation, velocity, parent Asset ID, and source index. Existing
projects receive the same data through the next project-schema migration. Migration
is source-ISO/catalog backed, versioned, and never runs again after user removal.

The base-layer sky asset remains retained as the source for global header, sprite,
and FX data. It is also the exact pass-through output when the project composition
still matches the imported source. Every parent sky Asset ID referenced by a shell
is protected by project asset collection and garbage-collection scans.

## Asset Explorer interaction

A sky-shell result becomes draggable only when its game, region/revision, blob,
shell index, and the active target's sky composer are compatible. Incompatible
results remain previewable and explain why addition is disabled.

Dropping a shell over the viewport means **append to the sky**; pointer position is
irrelevant and no world-space placement ghost is shown. The viewport shows an
“Add sky shell” drop affordance instead. A keyboard-accessible **Add to sky** action
in the Asset Preview dock performs the same command.

The host re-resolves the untrusted Asset ID and shell index, validates the canonical
blob and target, preflights texture and shell-count limits, then appends one new
entity. Success selects the new shell and exposes it in Properties. Failure makes no
project mutation. Adding the same source shell more than once creates distinct Entity
IDs and independent rotation settings.

The target adapter owns shell-count limits. The first UYA implementation supports
the format's validated range of zero through eight composed shells; the renderer
does not hard-code that limit. Removing the last shell leaves the base sky color and
global data with no shell geometry.

## Scene tree, selection, and removal

The Sky group uses project shell Entity IDs rather than `render:sky:<index>` values.
Each row shows its order and label, is keyboard selectable, and participates in the
normal shared selection. Adding selects the new row. Removing selects the next shell,
the prior shell at the end, or clears selection when none remain.

Delete and the standard context action remove unlocked selected shells through the
existing `deleteEntities` command. Selecting the parent Sky row opens a vertical
composition list with cached shell thumbnails; dragging a row issues one reorder
command, with compact earlier/later actions as the keyboard-accessible fallback.
Orders are normalized to contiguous values after each add, delete, duplicate, or
move. Viewport picking of overlapping enclosing sky geometry is not required in v0;
tree selection and selection after addition provide an unambiguous path.

## Properties dock

A single selected shell displays:

- project Entity ID, name, order, state, and compatibility;
- parent sky Asset ID, source game/level/revision, and source shell index;
- read-only source flags, blend behavior, cluster/triangle counts, and referenced
  texture count;
- editable initial rotation X/Y/Z in degrees; and
- editable angular velocity X/Y/Z in degrees per second, labeled as rotation speed.

The editable values are stored in radians and radians per second; degrees are a UI
presentation only. A read-only speed magnitude helps compare shells without becoming
a second rotation authority. **Stop rotation** sets all velocity components to zero.
**Reset to source** restores the selected source shell's initial rotation and
velocity.

Inputs accept finite numeric values only. Enter or blur commits one undoable command;
Escape restores the prior value. The target adapter quantizes values to the nearest
representable native value before commit, and the returned snapshot displays that
effective value. Out-of-range values are rejected with the valid range instead of
being silently clamped.

For UYA NTSC-U, the adapter uses the verified native rotation tick of
`pi / 32768` radians and runtime frame rate of 60 Hz. Unedited source values retain
their exact signed 16-bit ticks. Other targets must provide their own conversion and
limits before advertising edit capability.

Multi-shell property editing is outside v0. Multiple shells may still be selected
for the existing delete operation.

## Command contract

The runtime adds the minimum sky-specific commands needed around existing entity
behavior:

- `addSkyShellFromAsset`: validated parent Asset ID and source shell index;
- `updateSkyShell`: one shell Entity ID plus optional initial rotation or angular
  velocity; and
- `reorderSkyShell`: one shell Entity ID and destination order.

Removal, rename, hidden/disabled/locked state, undo, and redo reuse existing entity
commands. Each command validates the complete proposed composition before replacing
project state. Add/remove/reorder/update creates one history entry; undo and redo
preserve Entity IDs, ordering, exact source references, and effective rotation
values. Command payloads and snapshots remain bounded typed bridge values.

## SDK composition and native writing

Binary parsing, texture remapping, cluster serialization, and native sky writing
belong in the Ratchet PS2 SDK. The first implementation is UYA-specific under
`RatchetPs2.Games.UYA` and is exposed through a game-neutral SDK entry point that
dispatches by target game. No one-implementation interface or renderer-side binary
patcher is introduced.

The composer receives the target base sky plus an ordered list of validated source
shells and effective rotation values. It:

1. preserves the target base header color, clear mode, sprites, FX bytes, and all
   base textures at their original indexes;
2. copies each selected shell's flags, clusters, vertex data, and triangle data;
3. reuses an identical retained base texture when possible, otherwise appends the
   source texture and remaps that shell's triangle texture IDs;
4. applies quantized initial rotation and rotation-delta ticks;
5. rebuilds shell/cluster offsets, counts, alignment, texture tables, and payload
   sizes deterministically; and
6. re-reads the result and compares shell order, geometry counts, flags, rotations,
   texture references, sprites, FX, and header values before returning bytes.

Missing textures, unsupported texture IDs, count overflow, offset overflow, malformed
clusters, or a semantic mismatch fail composition. Forge never patches those bytes
itself. Texture optimization, sprite/FX editing, and material changes wait for their
own evidence and scope.

## Render and cache behavior

The host-generated sky render package is keyed by the ordered composition, parent
Asset IDs and shell indexes, effective rotation values, SDK revision, and render
schema. Add/remove/reorder replaces only the sky render resource after a complete
new package is ready; failure leaves the last known-good sky visible.

Rotation property edits update the selected projected shell from the authoritative
snapshot without regenerating geometry. The renderer uses the same conversion math
for the editor scene and Asset Preview. It never persists rotation by mutating a
Three.js object. Hidden/disabled changes and composition replacement release owned
resources through the existing scene lifecycle.

## Bake, pack, and validation

The Sky bake input fingerprint includes the ordered enabled shell records, every
referenced parent Asset ID, and effective rotation values. Unrelated entity edits do
not dirty Sky. A composition identical to the imported base stages the original
`sky.bin` unchanged; a modified composition invokes the SDK writer.

Staging validates all sources before writing, writes into a temporary snapshot,
semantically re-reads the result, and atomically replaces the prior successful Sky
snapshot only after validation. Cancellation or failure leaves project state,
staging, and the development ISO unchanged. Level packing replaces only the existing
sky asset slice through the established UYA level-asset composer.

Qualification covers save/reopen/recovery, undo/redo, add/remove/reorder, zero and
eight-shell boundaries, duplicate source shells, cross-level texture remapping,
stationary and rotating shells, deterministic repeated bake, WAD pack/re-read, and
recorded PCSX2 checks.

## Non-goals

- No sky mesh, vertex, UV, material, blend-flag, texture, sprite, or FX editor.
- No whole-sky replacement command; shells are composed against the target base sky.
- No cross-game conversion or assumption that UYA limits apply to another game.
- No renderer-authored project state or direct writes to catalog blobs.
- No separate sky hierarchy, history stack, selection authority, or custom cache.

## Implementation tasks

### M2-027 — Add validated UYA sky composition to the SDK

Status: ✅ Complete

Requirements: FR-ASSET-004, FR-BAKE-003, FR-BAKE-004, NFR-REL-001,
NFR-SEC-003

Depends on: M1-009, M2-013, M3A-004

Implement the UYA shell/texture composer and native writer in the SDK, expose it
through the existing game-neutral SDK boundary, and retain exact base global data.

Acceptance:

- Ordered shells from one or several valid UYA sky assets compose deterministically.
- Texture IDs are remapped without changing source assets or breaking retained base
  sprite/FX texture indexes.
- Rotation values round-trip through native signed tick fields.
- Zero/eight-shell, duplicate-shell, untextured, bloom, malformed, overflow, and
  cross-level texture fixtures either re-read semantically or fail actionably.
- No-edit composition can be recognized so Forge may preserve original bytes.

Verification: synthetic binary fixtures, representative retained UYA corpus cases,
determinism, semantic re-read, malformed input, and cancellation checks.

### M2-028 — Promote sky shells into typed project entities and commands

Status: ✅ Complete

Requirements: FR-SCENE-002, FR-SCENE-007, FR-EDIT-001, FR-PROJ-004,
NFR-REL-001, NFR-REL-003

Depends on: M1-005, M2-001, M2-027

Add the typed sky-shell record, project-schema migration, editor snapshots, target
quantization, asset-reference protection, and add/update/reorder command handling.
Reuse existing entity deletion, state, selection, history, save, and recovery.

Acceptance:

- New and migrated projects expose one stable entity per imported base shell.
- Add, update, reorder, delete, undo, and redo preserve stable IDs and validate the
  entire proposed composition before mutation.
- Source parent Asset IDs remain immutable and protected from catalog collection.
- Save/reopen/recovery and Linux/Windows serialization preserve order and effective
  values; migration never resurrects a shell after completion.
- Invalid tokens, missing blobs, unsupported targets, and native-limit failures do
  not dirty the project.

Verification: migration, command/history, malformed bridge payload, catalog
resolution, garbage-collection reference, recovery, and portability fixtures.

### M2-029 — Add sky composition interaction and Properties editing

Status: ✅ Complete

Requirements: FR-UI-004, FR-UI-007, FR-SCENE-007, NFR-UX-001,
NFR-UX-002

Depends on: M2-002, M2-007, M2-023, M2-028

Enable compatible sky-shell drag/add, selectable scene-tree rows, removal and
reordering, shell-specific Properties, and live authoritative rotation preview.

Acceptance:

- Dragging to the viewport or activating **Add to sky** appends and selects one shell;
  no spatial ghost or position is implied.
- The tree, viewport highlight where practical, Properties, and shared selection stay
  synchronized by shell Entity ID.
- Properties show source metadata and read-only shell statistics, and edit initial
  rotation and angular velocity with clear units and keyboard commit/cancel behavior.
- Delete, move, stop rotation, reset to source, undo, and redo update the viewport
  without stale shell nodes or leaked resources.
- Incompatible and over-limit shells remain inspectable with a concrete disabled
  reason.

Verification: keyboard/focus flows, drag/drop validation, properties command tests,
selection/history synchronization, render replacement failure, and repeated-edit
resource soak.

### M2-030 — Bake and qualify edited sky compositions

Status: ✅ Complete

Requirements: FR-BAKE-001 through FR-BAKE-004, NFR-PERF-004,
NFR-REL-001 through NFR-REL-003

Depends on: M2-027, M2-028, M2-029, M3-003, M4-001, M4-006

Wire typed composition into Sky fingerprints, staging, render-package invalidation,
level-WAD replacement, semantic verification, and the PCSX2 qualification loop.

Implementation: Sky fingerprints now include the ordered enabled shell composition
and its source Asset IDs. Dirty Sky staging invokes the SDK composer, validates the
staged native payload, and flows through the existing atomic level-WAD replacement
path. Automated qualification covers isolated invalidation, add/reorder/remove,
editable initial rotation and angular velocity, save/reopen, native rotation ticks,
packing, byte-identical restoration, and injected composed-output validation failure.
The recorded Bakisi4 PCSX2 qualification passed cross-level addition, stationary and
animated rotation edits, reordering, and removal.

Acceptance:

- Unchanged skies remain byte-identical and clean; only composition-affecting edits
  dirty the Sky layer.
- Modified output stages and packs atomically, reopens with the same shell order and
  effective rotations, and does not rewrite unrelated level assets.
- Cancellation and injected writer/validation failures preserve the last good stage
  and development ISO.
- PCSX2 confirms one added cross-level shell, one removed shell, reordered shells,
  a stationary rotation edit, and an animated velocity edit.

Verification: fingerprint isolation, save/reopen/bake/pack/re-read integration,
failure injection, deterministic hashes, and retained PCSX2 evidence. Manual results
are recorded in the [skybox editor qualification](../qualification/M2-skybox-editor.md).
