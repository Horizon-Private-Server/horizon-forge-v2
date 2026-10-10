# P2 task register — Advanced authoring

Plan and requirements: [P2 — Advanced authoring](../P2-next-phase.md)

Tasks are ordered by dependency within each milestone. The phase plan's graph allows
independent milestone lanes to run in parallel. Discovery tasks freeze their contracts
before downstream project schemas or UI are implemented.

## M7 — Authoring foundations and workspace chrome

### M7-001 — Streamline the workspace ribbon

Status: 🚧 In progress

Requirements: FR-UI-008, NFR-UX-001, NFR-UX-002
Depends on: M2-009, M6-004

Replace the current title/description, text actions, and HOST badge with the compact
project ribbon while retaining host diagnostics and notification behavior.

Acceptance:

- Project and Save are left-aligned Phosphor icon buttons with tooltips, accessible
  names, visible focus, existing actions, disabled/busy state, and keyboard shortcuts.
- Project name is prominent; dirty/read-only/recovery state follows the actions and
  remains understandable without color.
- The larger notification action stays right-aligned and does not move as the project
  name grows; narrow/high-DPI layouts truncate rather than hide critical actions.
- Host disconnected/version detail remains in status/diagnostics but no HOST badge is
  rendered in the ribbon.

Verification: component/state tests plus keyboard, 125–200% scaling, narrow-window,
dirty/save/error, and host-disconnect walkthroughs.

### M7-002 — Generalize the project-attached asset lifecycle

Status: 🚧 In progress

Requirements: FR-ASSET-007, FR-ASSET-005, FR-ASSET-006, NFR-P2-001
Depends on: P1-001

Extend the existing attached asset records, content-addressed layout, validation,
cleanup, recovery retention, and packaging to each newly supported canonical kind.

Acceptance:

- Texture and tfrag-piece fixtures attach, deduplicate, resolve, move/zip, reopen, and
  pass integrity checks without an external source path.
- Import validates kind, canonical version, size, hash, and bounded bytes before the
  project record changes; cancellation leaves no committed partial blob.
- Reference collection includes direct refs, override targets, group/template-owned
  data where applicable, and every retained recovery snapshot.
- The global catalog and another project are never mutated by attached-asset actions.

Verification: lifecycle/integrity/malformed/cancellation tests and Linux↔Windows ZIP
round trip with synthetic assets.

### M7-003 — Add exact asset override bindings

Status: 🚧 In progress

Requirements: FR-ASSET-008, NFR-P2-001, NFR-P2-002, NFR-PORT-001
Depends on: M7-002, M2-008

Add versioned project bindings from an exact source Asset ID/kind to one compatible
replacement Asset ID/kind and route resolution/bake fingerprints through them.

Acceptance:

- Add, replace, remove, undo, redo, save, recover, and migrate are atomic and preserve
  the source identity needed to restore vanilla behavior.
- Resolution rejects kind mismatches, missing replacement blobs, self/cyclic/chained
  mappings, duplicate sources, and unknown newer binding schemas.
- Source entity references need not be rewritten when an override changes; all current
  project consumers resolve the same replacement.
- Cleanup protects live and recovery-referenced replacements, then can collect an
  unreferenced replacement after the project is explicitly saved.

Verification: resolution precedence, invalid graph, command history, fingerprint,
recovery, cleanup, and portable-project fixtures.

### M7-004 — Introduce typed stable project references

Status: ✅ Complete

Requirements: FR-REF-001, FR-REF-002, FR-EDIT-002, NFR-P2-001
Depends on: M2-008, M2-020, M3-005

Define game-neutral entity/asset reference records and migrate the verified existing
geometry links without changing unknown numeric fields.

Acceptance:

- Reference JSON/bridge contracts discriminate domain and target kind and validate
  stable IDs, nullability, owner field keys, duplicates, and wrong-kind targets.
- Existing area links and other already-understood links migrate losslessly; old
  source indexes remain provenance, not authority.
- Import and bake adapter helpers map native indexes↔Entity IDs deterministically and
  report owner Entity ID, field path, raw value, and target on failure.
- Delete analysis uses an indexed inbound-reference graph and blocks or explicitly
  clears nullable refs in the same undoable command.

Verification: migration, list reorder/delete, wrong-kind/dangling, deterministic bake
ordering, delete/undo, and 25k-entity indexed-lookup tests.

### M7-005 — Build shared reference fields and the References view

Status: 🚧 In progress

Requirements: FR-REF-003, FR-UI-002, FR-UI-006, NFR-UX-001
Depends on: M7-004, M2-004

Implement one reusable property editor/picker and one incoming/outgoing reference view
over the host-provided reference graph.

Acceptance:

- Pickers query only compatible target kinds, support search and clear when nullable,
  and render resolved label, kind, missing state, and partial-coverage disclosure.
- Select/Reveal/Focus work from mouse and keyboard; assets open Asset Preview while
  renderable entities can focus the camera.
- Single activation selects/reveals; double activation focuses the camera, and the
  explicit keyboard-accessible Focus action provides the same behavior.
- Large inbound/outbound result sets are windowed or paged and never scan the whole
  project during React render.

Verification: component accessibility/navigation tests, dangling targets, partial
coverage, large graph profile, and camera/asset routing tests.

### M7-006 — Add the shared scene billboard gizmo

Status: ✅ Complete

Requirements: FR-GIZMO-001, FR-SCENE-002, FR-SCENE-007, NFR-PERF-005
Depends on: M2-003, M2-005

Create the reusable camera-facing map-pin overlay used by meshless objects and level
settings, with a shared icon texture/material cache.

Acceptance:

- Color, glyph, Entity ID, world position, depth policy, selection/hover state, and
  min/max screen size are configurable without per-instance textures/materials.
- Picking selects the authoritative entity and hidden/disabled/locked behavior matches
  existing scene rules; the gizmo is absent from bake and ground-snap targets.
- Glyphs have accessible tree/property labels and remain recognizable without color.
- Repeated open/close and icon changes return geometry/material/texture counts to the
  baseline with no late async texture updates.

Verification: projection/picking/size/cache/disposal tests and high-DPI visual check.

### M7-007 — Qualify the authoring foundation migration

Status: 🚧 In progress

Requirements: M7 exit gate, NFR-REL-001, NFR-REL-004, NFR-PORT-001
Depends on: M7-001, M7-002, M7-003, M7-004, M7-005, M7-006

Run the combined schema/bridge/UI portability gate before dependent features persist
P2 records.

Acceptance:

- Project schemas v0 through current open in memory and require only the normal
  journaled save to upgrade; an unsupported future schema is never overwritten.
- A synthetic overridden texture and typed cuboid/path reference survive save,
  recovery, undo/redo, ZIP transfer, and deterministic bake resolution.
- The packaged Linux and Windows app render the compact ribbon and billboard and pass
  keyboard/high-DPI checks.

Verification: versioned fixture matrix and packaged-app qualification record.

Qualification: [M7 authoring foundation](../../qualification/M7-authoring-foundation.md).

## M8 — HUD and FX texture authoring

### M8-001 — Freeze HUD bank/page and sprite-ID contracts

Status: ✅ Complete

Requirements: FR-HUD-001, FR-HUD-002, FR-SDK-002, NFR-P2-005
Depends on: M3A-006

Inventory the pinned SDK's HUD header, five banks, icons, frames, palettes, textures,
compression, alignment, runtime pointer area, and UYA/DL sprite-ID encodings before
defining editable project state.

Acceptance:

- Evidence resolves whether “maximum five pages” means physical banks or another
  native per-entry concept and names limits in SDK/game terminology.
- Every header/bank byte is typed, derived, or explicitly opaque; writer ownership,
  limits, compression, alignment, and runtime-pointer behavior are documented.
- UYA `Exxx` and DL `75xx` parse/format/valid/reserved/uniqueness rules have synthetic
  fixtures independent of the renderer.
- No-edit read/rebuild expectations and unsupported cases are frozen before UI work.

Verification: format note, boundary fixtures, all-five-bank reads, and sprite-ID tests.

Contract: [HUD bank and sprite-ID contract v0](../../features/hud-bank-contract-v0.md).

### M8-002 — Implement the SDK HUD writer

Status: ✅ Complete

Requirements: FR-HUD-003, FR-SDK-002, NFR-REL-001, NFR-PERF-006
Depends on: M8-001, M3A-006

Add the smallest symmetric SDK composition API required to replace/append normalized
HUD texture/frame/icon data while preserving opaque header and bank content.

Acceptance:

- Writer preflights counts, dimensions/logs, palette/pixel format, offsets, alignment,
  bank capacity, sprite IDs, compressed size, and integer overflow before allocation.
- Unchanged input meets the frozen byte/semantic identity; changed output re-reads to
  the requested entries with unaffected entries/opaque bytes preserved.
- Bank placement is deterministic and does not renumber existing icons/frames/textures.
- Cancellation/failure yields no successful output and reports the precise entry/bank.

Verification: no-edit, replace, append, boundary/malformed, determinism, re-read, and
largest-bank allocation/throughput tests in the SDK.

Implementation: `RatchetPs2.Sdk.HudComposer` dispatches UYA validation into the
bounded `RatchetPs2.Core.Hud.HudBankComposer`. The focused `--hud-contract` suite
covers all available extracted UYA HUD fixtures plus synthetic replace, append,
failure, cancellation, deterministic serialization, and 4 MiB capacity cases.

### M8-003 — Add authoritative HUD project state and overrides

Status: ✅ Complete

Requirements: FR-HUD-001, FR-HUD-002, FR-ASSET-008, NFR-P2-001
Depends on: M8-002, M7-003, P1-004

Import HUD inventory into game-neutral project records and bind replacements/appends to
canonical texture assets without copying vanilla payloads into the project.

Acceptance:

- Existing entries retain stable source identity/index and exact Texture Asset ID;
  replacements are exact overrides and new entries directly reference attached assets.
- Replace/add/remove commands validate adapter capabilities and limits and participate
  in undo/redo/recovery/save/fingerprints/cleanup.
- Custom source images convert through the shared P1 texture pipeline; project output
  is independent of the original image path.
- Migration and reference collection prevent missing assets or duplicate sprite IDs
  from being silently baked.

Verification: project command, override/cleanup, conversion, missing asset, migration,
and ZIP round-trip tests.

Implementation: project schema 13 stores stable source icon/frame/table/bank metadata
and exact canonical Texture Asset IDs while leaving vanilla bytes in the verified
opaque source. Existing-entry edits use the shared exact override table; appended
icons directly reference attached canonical PIF assets. Undoable bridge commands
replace/remove overrides and add/remove UYA `Exxx` icons after bounded PNG/PIF
conversion through the SDK alpha-aware quantizer. Validation, migration, recovery,
catalog/project cleanup, snapshots, fingerprints, and portable ZIP tests cover the
new state and reject missing blobs, duplicate IDs, invalid banks, and malformed input.

### M8-004 — Build the HUD bank main viewport

Status: ✅ Complete

Requirements: FR-HUD-001, FR-UI-001, FR-UI-002, NFR-P2-003
Depends on: M8-003

Add a dockable/main workspace for bank/icon/frame browsing, preview, search, diagnostics,
replace, and supported append actions.

Acceptance:

- The view distinguishes icons, frames, texture/palette indices, physical placement,
  sprite ID, dimensions, override/new state, and invalid/missing dependencies.
- Only visible thumbnails load; decoding is cancellable/cached and disposed on close.
- Replace and append use accessible file actions, show conversion/limit diagnostics,
  and commit only after preview validation.
- Selection can open the source/replacement Asset Preview without coupling HUD state to
  Three.js scene selection.

Verification: component/state/accessibility tests, large-grid profile, decode failure,
and replace/append walkthrough.

Implementation: the dockable HUD Bank main tab presents a searchable, virtualized
icon/frame grid with native indices, physical-bank placement, dimensions, state, and
dependency diagnostics. It reuses the cancellable cached texture-thumbnail runtime,
including project-attached replacement resolution, and disposes preview work when the
panel closes. Accessible PNG file actions fully decode and dimension-check a local
preview before enabling replace/append; the host snapshot supplies adapter-owned
sprite-ID and bank/count limits. Source and effective textures open in Asset Preview
without changing scene selection. State/layout fixtures bound a 1,024-entry grid,
exercise search/filter and PNG failures, and host fixtures cover attached preview
integrity plus corrupt PIF diagnostics.

### M8-005 — Rebuild, bake, and qualify HUD changes

Status: 🚧 In progress

Requirements: FR-HUD-003, FR-BAKE-002, NFR-REL-002
Depends on: M8-003, M8-004, M4-005

Add the HUD layer dependencies/fingerprint/staging composition and qualify an existing
replacement plus a custom appended sprite end to end.

Acceptance:

- HUD-only changes rebuild only required header/bank/archive outputs; a failed semantic
  re-read leaves last successful staging intact.
- Existing IDs/indexes are stable and the new sprite ID resolves to the intended
  texture; untouched banks meet the M8-001 identity contract.
- Clean rebuilds are deterministic on Linux and Windows and project transfer retains
  only custom attached assets.
- Patched UYA loads in PCSX2 and the selected existing/new sprite renders correctly.

Verification: incremental/determinism tests plus packaged build/patch and PCSX2 record.

Implementation: HUD is an independent bake layer whose fingerprint contains the
imported HUD tables, relevant exact override bindings, custom attached Texture Asset
IDs, and verified source HUD hashes. Staging composes through the SDK, writes and
semantically rereads a complete candidate before atomic commit, and records only the
header/banks that differ from source for archive replacement. Packing supplies
decompressed payloads to compression-aware source slots, excludes only replaced HUD
sections from opaque identity checks, rereads the packed bank set, and retains the
previous staged snapshot on any write or validation failure. Synthetic end-to-end
coverage replaces an existing texture, appends `E001` into bank 2, verifies stable
source indexes and untouched banks, proves HUD-only invalidation and repeatable output,
and transfers custom HUD assets with the project. Automated Linux qualification is
recorded in [M8 HUD bake qualification](../../qualification/M8-hud-bake.md); packaged
Windows and PCSX2 visual rows remain pending.

### M8-006 — Move FX labels and inventory into game-owned contracts

Status: ✅ Complete

Requirements: FR-FX-001, NFR-MAINT-001, NFR-P2-005
Depends on: M3A-006

Review map-o-matic's UYA/DL label catalogs and expose labels plus ordered validated FX
texture definitions from the SDK/host rather than a renderer enum.

Acceptance:

- UYA/DL known and special negative labels match reviewed fixtures; unknown indexes
  use a deterministic fallback and labels do not affect Asset IDs.
- Inventory reports source index/identity, dimensions, format/palette, offset/length,
  validity, and adapter capabilities without renderer archive parsing.
- Malformed counts/offsets/dimensions are bounded and diagnosed per index.
- Game-neutral bridge DTOs contain no UYA/DL conditionals in React.

Verification: label snapshots, inventory/malformed/bounds tests, and bridge codec tests.

Implementation: the SDK's game-neutral `FxTextureCatalog` dispatches label lookup to
the UYA/DL game libraries and bounded definition/payload parsing to Core. Forge maps
valid canonical PIFs to exact Texture Asset IDs and a neutral bridge payload. The
focused `--fx-contract` suite also reads every available UYA extraction.

Contract: [FX texture inventory and label contract v0](../../features/fx-texture-contract-v0.md).

### M8-007 — Implement FX writer, project state, and viewport

Status: ✅ Complete

Requirements: FR-FX-002, FR-FX-003, FR-ASSET-008, NFR-P2-003
Depends on: M8-006, M7-003, P1-004

Add SDK replace/append composition, authoritative project commands, exact overrides,
and a lazy main viewport using shared texture-card/asset-preview behavior.

Acceptance:

- Existing replacements preserve source indexes; append assigns deterministic trailing
  indexes and cannot insert/reorder existing entries.
- Writer validates dimensions/format/palette/count/offset/alignment/size, preserves
  unrelated definitions/data, and semantically re-reads deterministic output.
- View supports label/index search, visible-only previews, replace/add diagnostics,
  keyboard access, cancellation, and resource disposal.
- Commands participate in undo/recovery/save/fingerprints/cleanup and remain portable.

Verification: writer no-edit/replace/append/boundary tests, component tests, and ZIP
round trip with a custom FX texture.

### M8-008 — Bake/qualify FX changes and close M8

Requirements: FR-FX-003, M8 exit gate, NFR-REL-002
Depends on: M8-005, M8-007, M4-005

Integrate FX changes with the asset-WAD layer and qualify one replacement and append
alongside the HUD fixture.

Acceptance:

- FX-only edits invalidate only required outputs and retain last successful staging on
  failure; existing references keep their indexes and appended IDs are deterministic.
- UYA clean rebuilds match across Linux and Windows and semantically re-read all
  existing/replaced/appended entries.
- HUD and FX overrides coexist without asset identity or palette ownership collisions.
- The combined portable project builds/patches and renders the exercised textures in
  PCSX2; DL label/sprite fixtures pass without claiming writable DL support.

Verification: incremental/determinism matrix and packaged-app/PCSX2 qualification.

## M9 — MobyDex and moby editing

### M9-001 — Inventory and freeze UYA moby instance ownership

Status: ✅ Complete

Requirements: FR-MOBY-001, FR-SDK-002, NFR-MAINT-001
Depends on: M3-005

Document the UYA moby instance byte layout, SDK-owned fields, opaque bytes, limits,
native index fields, and writer coverage before exposing new editable controls.

Acceptance:

- Every byte in representative 0x88 records is classified as typed, derived, or opaque
  with evidence and fixtures; no renderer offset is introduced.
- Supported fields have stable neutral keys, value kinds, units, bounds, and write
  semantics; unsafe/unknown values remain read-only.
- No-edit SDK read/write is byte-identical across representative and boundary records.
- The document explains how another game adapter supplies a different layout without
  a shared-domain dependency on `Forge.Host.Games.*`.

Verification: checked-in synthetic/golden record fixtures and SDK semantic/no-edit
round-trip tests.

Contract: [UYA moby instance contract v0](../../features/uya-moby-instance-contract-v0.md).

Implementation: the UYA game library owns the bounded 0x10-byte table header and
0x88-byte record parser/writer. Parsed records retain all source bytes; the no-edit
writer reproduces representative and boundary fixtures byte-for-byte, while the
existing transform writer patches only OClass, position, native ZYX rotation, and
uniform scale. The contract classifies every range as typed, derived, or opaque and
reserves neutral field keys without exposing native offsets outside the adapter.

### M9-002 — Expose game-neutral moby instance descriptors and commands

Status: ✅ Complete

Requirements: FR-MOBY-001, FR-MOBY-002, NFR-SEC-003
Depends on: M9-001, M2-001

Have the UYA adapter project instance bytes into bounded typed property descriptors and
apply field-key/value commands through the SDK writer.

Acceptance:

- Bridge DTOs use a closed value union and bound strings, collections, and numeric
  values; Forge rejects unknown field keys and client-supplied layout metadata.
- Each edit validates capabilities, entity/OClass expectations, value range, and
  finite numeric input before project mutation.
- One edit is undoable and updates dirty state, recovery, diagnostics, layer
  fingerprint, and projected properties.
- Semantic re-read proves the selected field changed and all non-owned bytes stayed
  identical.

Verification: descriptor codec, command/history, malicious payload, field boundary,
and per-field semantic round-trip tests.

Implementation: the UYA SDK library owns a native field enum, validation bounds,
semantic parsing, and byte-range writes. Forge's UYA adapter owns stable bridge keys,
labels, editability, and read-only explanations, mapping them to a closed game-neutral
bridge union that accepts only field key, expected OClass, and typed value from clients.
Runtime edits participate in the existing atomic history, dirty/recovery, event, save,
and bake-fingerprint paths; SDK tests cover every writable field and prove all
non-owned record bytes remain unchanged.

### M9-003 — Build the moby instance properties UI

Status: ✅ Complete

Requirements: FR-MOBY-002, FR-UI-004, FR-UI-002, NFR-UX-001
Depends on: M9-002, M7-005

Render supported instance properties in the existing Properties dock using the shared
property and reference controls.

Acceptance:

- Integer, float, enum/flags, RGB(A), vector, reference, and read-only values use
  appropriate compact controls with units/help and inline actionable validation.
- OClass and stable identity remain visible; unsupported fields say why they are
  read-only rather than disappearing.
- Multi-selection exposes only fields with compatible descriptors and applies one
  atomic command or no change.
- Keyboard interaction, focus return, scaling, and screen-reader labels pass the
  existing properties-panel baseline.

Verification: component/state tests and representative single/multi-moby walkthrough.

Implementation: the Properties dock renders adapter descriptors with bounded integer
and finite-float inputs, boolean and RGB controls, units/help, and visible explanations
for preserved read-only fields. OClass is formatted alongside its numeric value and
the stable Entity ID remains in Source. Same-OClass multi-selection intersects complete
descriptor metadata and sends one batch command; the host stages every native record
before one workspace replacement, so failure changes none of the selection and one undo
restores all of it. Existing shared transform and reference controls continue to render
those semantic field families without introducing UYA layout knowledge in React.

### M9-004 — Define and validate MobyDex schema v1

Status: ✅ Complete

Requirements: FR-MDEX-001, FR-MDEX-002, NFR-P2-004, NFR-P2-005
Depends on: M7-004

Define the canonical JSON schema, bounded parser, stable diagnostics, formatter, and
synthetic fixtures without runtime code generation or executable expressions.

Acceptance:

- Schema covers the fixed primitives, structs/arrays, explicit unions, enums/flags,
  colors/vectors, raw bytes, typed refs, defaults, relocations, labels/help, and bounds
  listed in FR-MDEX-002.
- Validation detects invalid game/OClass/length/defaults, overflow/out-of-range spans,
  illegal overlap, duplicate keys/options, bad strides/counts/nulls/relocations, and
  bounded-complexity violations with entry/field paths.
- Canonical formatting and parsing are deterministic across Linux and Windows.
- Fixtures translate representative Forge v1 float, byte, enum, color, array,
  MobyRef, and CuboidRef concepts without accepting its ambiguous defaults silently.

Verification: schema corpus, malformed/fuzz corpus, deterministic snapshots, and
legacy-concept conversion fixtures.

Schema: [MobyDex entry schema v1](../../features/mobydex-v1.md) and its
[machine-readable JSON Schema](../../schemas/mobydex-v1.schema.json).

Implementation: game-neutral Domain records represent fixed PVar entry metadata and
recursive declarative field definitions. A 4 MiB bounded parser rejects unknown or
duplicate JSON properties before validation; stable code/path diagnostics cover
identity, exact defaults, spans/overlaps, arrays, options, references, relocations,
and aggregate complexity. The canonical formatter fixes property ordering, casing,
UTF-8/LF output, tag/relocation ordering, and final newline while retaining authored
field/option order. A synthetic snapshot translates representative Forge v1 concepts,
and deterministic malformed-byte fuzzing exercises the untrusted parser boundary.

### M9-005 — Resolve built-in and project MobyDex entries

Status: ✅ Complete

Requirements: FR-MDEX-001, FR-MDEX-003, NFR-PORT-001, NFR-P2-001
Depends on: M9-004, M7-002

Ship a small reviewed built-in UYA dataset and add atomic project-local custom entry
import/export with whole-entry precedence.

Acceptance:

- Exact `(game ID, OClass)` lookup selects project custom over built-in and reports
  source/dataset/schema version; duplicate entries in one source are invalid.
- Import validates completely before replacing an entry and participates in
  undo/recovery/save; export is canonical and contains no machine paths.
- Missing/invalid entries leave moby bytes accessible in raw mode and cannot corrupt
  project state.
- Contributor docs show one minimal primitive entry, one reference entry, validation,
  fixtures, review expectations, and how to avoid proprietary bytes.

Verification: precedence/conflict/import rollback/ZIP tests and documentation dry run.

Implementation: a game-neutral catalog resolves exact `(game, OClass)` keys and reports
built-in/project source plus dataset and entry-schema versions. The versioned UYA
dataset contains the 75 unique, default-free UYA entries translated from the Forge v1
overlay; duplicate rows are collapsed and DL rows are excluded by source game version. Project content
stores complete validated custom entries; canonical bounded import, export, and removal
participate in editor history, recovery, deterministic save, project migration, and ZIP
portability. Invalid replacement data is rejected before mutation, duplicate keys are
invalid within either source, and missing definitions leave source moby bytes unchanged.
The bundled dataset can be replaced for the current host session from a local JSON file;
validation and target-game checks complete before the active catalog is swapped.

### M9-006 — Add lossless PVar storage and SDK write integration

Status: ✅ Complete

Requirements: FR-PVAR-001, FR-PVAR-003, FR-REF-002, NFR-REL-001
Depends on: M9-004, M9-005, M7-004, M3-005

Promote every source moby's raw PVar blob and relocation metadata into authoritative
project state independently of MobyDex coverage, while retaining native table ownership
in the SDK.

Acceptance:

- Import associates a fixed-length blob with the moby Entity ID and resolves only
  schema-declared references; multiple mobys may retain independent identical blobs.
- A missing PVar is creatable only from a matching valid default; ordinary field edits
  cannot resize the blob.
- Save/recovery/undo/redo and PVar table/link/relative-pointer rebuilds remain atomic
  and deterministic; deletion uses the reference rules from M7.
- No-edit and one-field edits semantically re-read, with byte masks proving every
  unknown byte stayed unchanged.

Verification: no-PVar/default, shared/independent blob, relocation, reorder/delete,
unknown-byte mask, malformed table, and native round-trip tests.

Implementation: project content owns fixed-length raw PVar blobs by moby Entity ID while
retaining the original native table slot, relocation offsets, moby-link offsets, and
schema-resolved stable references. All source blobs import independently of the active
MobyDex; older projects hydrate missing blobs from their retained gameplay source when
opened, and the active schema is evaluated as an editor overlay. Shared source slots import as independent blobs;
copies and verified defaults receive deterministic appended slots. The SDK validates,
reads, and writes native PVar tables and both fixup tables, and UYA bake regenerates
them together with patched moby instance indices. Nullable deletion, opaque-link
blockers, fixed-length replacement, initialization history, save/recovery state, and
legacy projects without semantic PVar state all preserve last-known-good data.

### M9-007 — Build the structured PVar editor

Status: ✅ Complete

Requirements: FR-PVAR-001, FR-PVAR-003, FR-UI-002, NFR-UX-001
Depends on: M9-006, M9-003, M7-005

Render MobyDex fields recursively with shared property/reference controls and issue
bounded byte-patch commands through the host.

Acceptance:

- Struct/array hierarchy is searchable/collapsible and shows field byte ranges,
  schema source, help, current value, invalid state, and reference resolution.
- A commit sends field key plus typed value, never a client-selected offset; host
  re-resolves the active schema and rejects stale dataset/blob versions.
- Each field edit is one undoable mutation; compatible continuous numeric edits
  coalesce and rejected edits leave bytes/focus intact.
- Unknown gaps and entries without a valid schema remain inspectable without an
  editable control that implies understanding.

Verification: component/command/stale-schema tests and nested/array/reference fixtures.

Implementation: entity snapshots expose recursive, typed PVar descriptors from the
active MobyDex entry, including byte ranges, source and schema fingerprints, help,
invalid values, resolved references, and explicit read-only unknown gaps. The renderer
provides searchable/collapsible struct and array controls, bounded opaque previews,
reference navigation, and local numeric drafts that commit once. Field commands carry
only a stable path, typed value, and dataset/schema/blob guards; the UYA adapter
re-resolves the current definition and patches its bounded span atomically. Schema or
blob changes reject without mutation, while successful edits participate in normal
undo/redo/save behavior. Missing schemas degrade to a read-only raw PVar descriptor.

### M9-008 — Add the windowed highlighted PVar hex view

Status: ✅ Complete

Requirements: FR-PVAR-002, NFR-P2-003, NFR-UX-001
Depends on: M9-006, M9-007

Adapt the useful ratchet-companion highlight geometry/hit-testing concepts to a
Forge-native read-only hex/ASCII view with windowed rows.

Acceptance:

- Address, hex, ASCII, field highlights, hover/focus tooltip, and form↔hex navigation
  agree at row boundaries and overlapping explicit union fields.
- Modified bytes use color plus a marker/label, and keyboard navigation can reach
  highlighted fields without traversing every byte cell.
- Rendering work and DOM nodes stay bounded to visible rows plus overscan; scrolling a
  large synthetic blob does not rebuild unrelated property controls.
- The view never becomes a second byte authority and never edits bytes directly.

Verification: layout/hit/highlight signature tests, accessibility check, and large-blob
scroll/heap profile against the ratchet-companion baseline.

Implementation: selected moby snapshots expose the current fixed-length PVar bytes and
a bounded one-bit-per-byte mask derived from the project-owned import/default baseline.
The read-only view renders address, little-endian grouped hex, ASCII, schema highlights,
hover/focus details, explicit color-plus-dot modified-byte markers, and bidirectional
field navigation. Rows are windowed to the 320-pixel viewport plus overscan; tests cover
row bounds, group alignment, overlapping fields, mask tail bounds, and a maximum-size
1 MiB PVar without allocating per-byte DOM state. Baselines participate in normal
save/recovery/history state, while legacy draft PVar state safely adopts its current
bytes as the initial baseline.

### M9-009 — Integrate moby/PVar reference enrichment and qualify M9

Status: 🚧 In progress

Requirements: FR-UI-006, FR-PVAR-003, M9 exit gate, NFR-P2-005
Depends on: M9-002, M9-003, M9-004, M9-005, M9-006, M9-007, M9-008, M7-007

Expose MobyDex references in the shared graph/navigation UI and run the combined
instance/PVar round-trip gate.

Acceptance:

- References view names owner moby, stable field path, raw encoded index, resolved
  target, dataset source, and partial coverage; navigation selects either side.
- Reordering/deleting referenced mobys/cuboids produces deterministic remaps or a
  blocking diagnostic according to nullability.
- Representative UYA mobys with primitives, colors, arrays, references, relocations,
  and unknown gaps save, recover, bake, pack, patch, and semantically re-read.
- Malformed custom schemas/PVars and missing targets never partially mutate or stage.

Verification: synthetic automated matrix plus local proprietary-data-free qualification
record and a maintainer PCSX2 result reference.

Implementation: PVar references use the existing project reference graph and now carry
their active MobyDex provenance through the editor snapshot. The shared view identifies
owner and resolved target, stable field path, signed encoded native index, null/missing
state, and source, with endpoint navigation in either direction. Raw hex field details
show the same encoded index and resolved target. Synthetic qualification covers stable
Entity-ID remapping, primitives, nested fields, arrays, RGB color, references,
relocations, unknown-byte preservation, failure atomicity, and staged native-table
semantic re-read.

Qualification: [M9 MobyDex and moby editing](../../qualification/M9-moby-editing.md).
The automated gate passes; real-level pack/patch, packaged Windows, and maintainer
PCSX2 evidence remain pending, so this task and the M9 exit gate are not complete.

## M10 — Group engine

### M10-001 — Add manual group project state and commands

Status: ✅ Complete

Requirements: FR-GROUP-001, NFR-P2-001, NFR-PORT-001
Depends on: M7-004, M2-008

Add stable project group IDs, ordered Entity-ID membership, and create/rename/delete/
reorder/add/remove commands without changing entity parentage or bake layers.

Acceptance:

- Entities may belong to multiple groups; duplicate membership in one group is
  rejected and missing members remain visible diagnostics rather than being dropped.
- All mutations validate first, use undo/redo/recovery/save, and preserve deterministic
  ordering through project transfer.
- Deleting a group never deletes members; deleting a member updates or explicitly
  retains a diagnosed missing reference according to the chosen command.
- Bake fingerprints and output are unchanged by group-only changes.

Verification: schema migration, command/history, duplicate/missing, portability, and
before/after bake-fingerprint tests.

Implementation: project schema v16 stores ordered manual groups with independent UUIDs
and ordered Entity-ID membership. The shared editor protocol exposes group snapshots,
missing-member diagnostics, capabilities, and atomic create/rename/delete/reorder/add/
remove commands through the existing history, recovery, and save paths. Entities may
belong to multiple groups; duplicate membership is rejected without mutation, normal
entity deletion removes membership, and unresolved imported members remain visible and
removable. Automated qualification covers migration, undo/redo, autosave recovery, ZIP
transfer, deterministic ordering, and identical HUD bake input/output fingerprints
before and after group-only edits.

Forge-only groups remain project metadata and are labeled as Custom groups separately
from the read-only moby, tie, and shrub Map groups parsed from the preserved level data.

### M10-002 — Build the parallel semantic group tree

Status: ✅ Complete

Requirements: FR-GROUP-002, FR-UI-003, NFR-UX-001, NFR-P2-003
Depends on: M10-001, M2-004

Add separate dockable Scene and Groups panels using existing tree primitives and the
same selection state, allowing both trees to remain open for cross-panel drag/drop.

Acceptance:

- Groups expand to members with consistent hidden/disabled/locked/invalid/missing
  states; an entity selected in either view is selected in viewport/properties/other
  view without duplicated authority.
- Search covers group and member labels.
- Keyboard navigation, multi-selection, rename/context actions, and focus restoration
  match the raw tree.
- Representative large projects remain interactive without rendering all rows.

Verification: tree state/accessibility tests and large-fixture interaction profile.

Implementation: separate Scene and Groups docks can remain open side by side without
introducing another selection authority. Group and member rows share the project
Entity-ID selection, including entities present in more than one group; group-row
selection resolves to all known members. Search matches group names, entity
labels/IDs/state, and missing IDs. Scene entities can be
dragged across docks onto any visible part of a group, with the full group highlighted
as the drop target. Rows reuse the raw tree's keyboard, range-selection, visibility,
disabled-state, focus, and camera-focus behavior, with accessible group
create/rename/reorder/delete and membership actions. Missing members remain labeled and
removable. Large branches page at 250 rows, and the automated interaction profile covers
25,000 entities without constructing every member row at once.

Map groups share the authoritative entity selection and can be expanded, filtered,
focused, and used as drag sources, while only Custom groups accept membership edits.

### M10-003 — Transform groups atomically

Status: ✅ Complete

Requirements: FR-GROUP-003, FR-SCENE-008, FR-EDIT-001
Depends on: M10-002, M2-006

Route group manipulation through the existing multi-entity transform command using a
documented bounds-center pivot and capability intersection.

Acceptance:

- Translate/rotate/scale preview preserves relative offsets and commits exactly one
  command; cancel restores authoritative transforms.
- Locked, read-only, missing, collision-linked, or transform-incompatible members are
  summarized before interaction and prevent a partial commit.
- Duplicate entities reached through nested UI selection are transformed once.
- Undo/redo, Page Down, snapping, and property numeric entry remain consistent with
  ordinary multi-selection.

Verification: transform math/capability/cancel/history tests and mixed-kind walkthrough.

Implementation: group rows continue to resolve into the authoritative Entity-ID
selection, so the existing bounds-center multi-transform path preserves member offsets,
snapping, preview/cancel behavior, and one-command history semantics. A shared transform
eligibility preflight now deduplicates nested selections and rejects the whole selection
when any member is locked, read-only, hidden, disabled, invalid, missing an asset,
collision-linked, or incompatible with the active transform mode. Unresolved Custom and
Map group members participate in the same preflight, and the persistent viewport notice
summarizes every blocking category before the gizmo is attached. Page Down uses the same
atomic preflight instead of silently placing only the eligible subset.

### M10-004 — Implement deterministic group-template evaluation

Requirements: FR-GROUP-004, NFR-P2-002, NFR-P2-003, NFR-MAINT-001
Depends on: M9-009, M10-001

Add a host-side, game-adapter-owned template contract with spatial queries, typed
field/reference queries, evidence, ambiguity reporting, preview, accept, and diffed
regeneration.

Acceptance:

- Template ID/version and normalized parameters fully determine sorted candidates,
  evidence, membership, and diagnostics for a project fingerprint.
- Evaluation beyond an interaction frame is cancellable background work and uses a
  spatial index rather than all-pairs scans.
- Accept stores ordinary membership plus provenance; regeneration shows add/remove/
  ambiguous sets and requires confirmation before replacing prior generated members.
- Templates cannot execute project/user code or bypass adapter boundaries.

Verification: determinism, cancellation, stale-preview, spatial scale, ambiguity, and
regeneration-diff tests.

### M10-005 — Add node/siege templates and qualify M10

Requirements: FR-GROUP-004, M10 exit gate, NFR-P2-005
Depends on: M10-003, M10-004

Implement initial UYA node-moby and siege-base templates from reviewed OClasses,
MobyDex fields/references, TIE classes, and proximity evidence.

Acceptance:

- Synthetic red/blue base and hallway/node fixtures produce expected memberships with
  evidence for every included/excluded ambiguous candidate.
- Thresholds are explicit versioned parameters with safe defaults; changing them
  previews a diff rather than mutating groups immediately.
- Manual edits after acceptance remain distinguishable and are not silently erased by
  regeneration.
- A representative level supports group selection/transform/save/reopen without any
  game-binary change caused solely by grouping.

Verification: template golden fixtures and packaged-app manual group workflow.

## M11 — Scene semantics and level settings

### M11-001 — Promote verified remaining UYA level settings

Requirements: FR-LEVEL-001, FR-MOBY-001, NFR-P2-001
Depends on: M2-018, M3-005, M7-004

Extend SDK/host/project/bridge contracts for death height, ship transform,
spherical-world center/state, ship path, and ship camera cuboids while preserving
unknown settings bytes.

Acceptance:

- SDK evidence defines units, coordinate conversions, sentinels, bounds, path/cuboid
  index semantics, and writable bytes; unsupported fields remain opaque/read-only.
- Path and camera cuboid fields import as typed Entity-ID refs and bake back after
  deterministic ordering.
- Commands validate finite/range/reference/capability input and update history,
  recovery, dirty state, diagnostics, and layer fingerprints.
- No-edit and individual setting edits re-read semantically with all unknown bytes
  unchanged.

Verification: SDK byte-mask fixtures, ref reorder/delete, command/migration, and native
round-trip tests.

### M11-002 — Complete the level settings property UI

Requirements: FR-LEVEL-001, FR-UI-004, FR-REF-003, NFR-UX-001
Depends on: M11-001, M7-005

Replace read-only settings text with appropriate vector/number/toggle/reference controls
and route edits through validated commands.

Acceptance:

- Death height, ship position/rotation, sphere center/state, path, and camera cuboids
  show units, target capability, inline errors, and accessible labels.
- Reference controls reveal/select/focus valid path/cuboid targets and diagnose missing
  or wrong-kind values.
- Invalid partial numeric input does not mutate authoritative state; successful related
  edits can be committed atomically where invariants require it.
- Existing fog/background controls remain functional and use the same command path.

Verification: component/command/accessibility tests and full settings walkthrough.

### M11-003 — Render death, ship, and sphere semantic aids

Requirements: FR-LEVEL-002, FR-GIZMO-001, NFR-PERF-003
Depends on: M11-001, M7-006

Project level settings into a death-height plane and distinct ship/spherical-center
billboards in the tool overlay scene.

Acceptance:

- Death plane follows the authoritative height, uses transparent red stripes plus
  repeating skull markers, sizes from scene bounds with a bounded fallback, and can be
  toggled without changing project state.
- Ship and sphere-center pins use distinct glyphs/labels, track property edits, and
  select the corresponding settings context; ship position can use existing translate
  behavior only if the command path remains authoritative.
- Overlays neither bake nor become ground/vertex snap targets and disclose when a
  setting is unsupported.
- Materials/textures/geometries are shared and disposed with the project.

Verification: projection/update/picking/exclusion/disposal tests and visual check.

### M11-004 — Replace area meshes with billboard gizmos

Requirements: FR-LEVEL-002, FR-SCENE-002, FR-SCENE-007
Depends on: M7-006, M2-015

Use the shared billboard for area identity while retaining its authoritative bounding
sphere and typed spline/cuboid/sphere/cylinder links for properties and bake.

Acceptance:

- Area pins appear at the documented center, select the original area Entity ID, and
  expose type/name/link status without rendering the old area sphere as its identity.
- Optional bounds visualization remains an editor overlay and can be toggled separately
  for inspection without affecting picking authority or bake.
- Tree selection/focus, viewport selection, properties, hide/disable/lock, and reference
  navigation remain synchronized.
- Large area counts reuse billboard resources and do not create per-area icon textures.

Verification: scene projection/picking/resource tests and representative-level check.

### M11-005 — Qualify level settings and scene semantics

Requirements: M11 exit gate, NFR-P2-002, NFR-P2-005
Depends on: M11-002, M11-003, M11-004

Run deterministic save/recovery/bake and packaged UI gates for all promoted settings
and semantic overlays.

Acceptance:

- Each supported setting survives save/recovery/undo/reorder/build and SDK semantic
  re-read; missing refs produce actionable blockers.
- Death/ship/sphere/area aids remain visually and keyboard distinguishable at supported
  scaling and never change baked geometry.
- Clean Linux/Windows rebuilds agree and patched UYA observes ship/death/sphere settings
  exercised by the fixture.

Verification: automated matrix, packaged interaction record, and PCSX2 result.

## M12 — Editable tfrags

### M12-001 — Prove native UYA tfrag piece boundaries and limits

Requirements: FR-TFRAG-001, FR-TFRAG-004, FR-SDK-002
Depends on: M3-003, M3A-006

Inventory primary/chunk tfrag structures and choose the smallest source-native grouped
mesh unit that can be independently transformed and deterministically rebuilt.

Acceptance:

- The format note classifies group tables, packets, vertices/indices, materials,
  textures, bounds, chunk ownership, links, alignment, limits, and unknown bytes.
- Representative repeated/duplicate geometry proves why occurrence Entity ID and
  provenance are distinct from content-derived piece Asset ID.
- Supported uniform/non-uniform/mirrored transform policy is explicit; unsupported
  cases have preflight diagnostics.
- No-edit byte/semantic identity fixtures and largest-level group counts/sizes are
  recorded before the project schema freezes.

Verification: synthetic/boundary parser fixtures and all-level local inventory report
without committed proprietary payloads.

### M12-002 — Add canonical tfrag-piece extraction in the SDK

Requirements: FR-TFRAG-001, FR-ASSET-007, NFR-P2-002
Depends on: M12-001, M7-002

Have the SDK emit validated canonical geometry/material dependencies and stable source
provenance for each primary/chunk native group.

Acceptance:

- Extraction bounds all counts/offsets/allocations and reports payload/chunk/group on
  failure; cancellation commits no partial cache/project assets.
- Identical canonical pieces deduplicate by Asset ID while every occurrence retains a
  unique project Entity ID and source group provenance.
- Canonical data contains only information required for rendering, transform/bake, and
  diagnostics; raw format ownership stays in the SDK.
- Output and ordering are deterministic across platforms.

Verification: malformed corpus, duplicate occurrence, deterministic snapshot, and
largest-level extraction profile.

### M12-003 — Import tfrag pieces as authoritative project entities

Requirements: FR-TFRAG-001, FR-REF-001, NFR-P2-001
Depends on: M12-002, M7-007

Replace render-package-only terrain identity with source-backed tfrag entities that
reference canonical piece assets and retain primary/chunk/group provenance.

Acceptance:

- Project creation/migration produces one entity per source occurrence with correct
  layer, transform, material refs, state, and stable save/reopen Entity ID.
- Existing projects migrate only with verified source/catalog data; otherwise their
  legacy terrain remains usable and migration reports the prerequisite.
- Delete/duplicate/disable rules and capabilities are explicit before commands are
  exposed; unsupported operations are not advertised.
- Entity-only transforms do not copy immutable canonical piece data.

Verification: creation/migration/missing-source, entity identity, dedup, and portability
tests.

### M12-004 — Render, select, and inspect individual tfrag pieces

Requirements: FR-TFRAG-002, FR-SCENE-011, NFR-PERF-001, NFR-PERF-003
Depends on: M12-003, M2-012

Project tfrag entities into the current terrain renderer with Entity-ID picking and raw
tree/property coverage while preserving batching where measurements justify it.

Acceptance:

- Visual output/materials match the pre-split source within the existing renderer
  qualification, and a pick resolves the exact source occurrence.
- Tree search/grouping identifies primary/chunk/group; Properties show asset,
  provenance, bounds, materials, transform capabilities, and diagnostics.
- Selection highlight and transform preview can isolate a piece without permanently
  de-batching all terrain or rebuilding unrelated geometry.
- Largest-level frame/memory/selection baselines are recorded and resources dispose on
  repeated project opens.

Verification: render/pick/projection/disposal tests and M6-baseline comparison.

### M12-005 — Support validated tfrag piece transforms

Requirements: FR-TFRAG-002, FR-EDIT-001, NFR-P2-002
Depends on: M12-004, M2-006

Enable translate/rotate/scale through ordinary commands and SDK preflight, including
native-space vertex/normal/winding/bounds updates required at bake.

Acceptance:

- Transform controls advertise only adapter-supported modes/scales and reject
  non-finite, zero, overflow, or unsupported mirror/non-uniform cases before mutation.
- Preview, numeric entry, snapping, group transforms, cancel, undo/redo, recovery, and
  save retain exact authoritative transforms.
- Bake transform math handles coordinate conversion, normals, winding, bounds, and
  deterministic rounding with boundary fixtures.
- One piece transform does not rewrite immutable source assets or unrelated entities.

Verification: transform math/boundary/capability/history tests and visual preview check.

### M12-006 — Associate collision with tfrag pieces

Requirements: FR-TFRAG-003, FR-COLL-003, NFR-P2-005
Depends on: M12-003, M12-005

Extend collision recovery to score each source collision component against individual
tfrag entities in local space and store only confident Entity-ID attachments.

Acceptance:

- Scoring documents bounds coverage, center/alignment terms, minimum confidence, clear
  lead, deterministic tie breaking, and why rejected candidates remain independent.
- Attached collision follows supported translate/rotate/scale and reverse manipulation
  remains coherent; source geometry/type bytes are not regenerated from render meshes.
- Ambiguous/weak/no-match cases retain editable world collision with diagnostics and no
  silent association.
- Transformed combined collision passes native octant/size validation before staging.

Verification: strong/ambiguous/no-match golden fixtures, transform/reverse-link tests,
and collision budget failure cases.

### M12-007 — Implement target-native tfrag composition and staging

Requirements: FR-TFRAG-004, FR-BAKE-001 through FR-BAKE-004, NFR-REL-002
Depends on: M12-005, M12-006, M3-003

Add the SDK writer/composer for changed native pieces and integrate deterministic
primary/chunk terrain and associated collision output with incremental staging.

Acceptance:

- Writer preflights all M12-001 limits and unresolved asset/material refs, applies
  transforms once, rebuilds tables/packets/bounds/indexes, and semantically re-reads.
- No-edit output meets the frozen identity contract; a one-piece change leaves
  unaffected payloads/groups semantically or byte identical as specified.
- Fingerprints include piece assets/transforms/material refs/collision attachments and
  writer version; unrelated layers remain clean.
- Failure/cancellation retains last successful staging and identifies Entity ID plus
  payload/chunk/group.

Verification: no-edit/one-piece/multi-chunk/determinism/re-read/incremental/cancellation
tests and largest-level allocation/throughput profile.

### M12-008 — Qualify editable tfrags end to end

Requirements: M12 exit gate, FR-BUILD-001, FR-PATCH-005
Depends on: M12-007, M4-006

Exercise representative primary and chunk pieces through translation, rotation, each
supported scale mode, collision following, save/recovery, build, patch, and PCSX2.

Acceptance:

- Renderer preview and game output agree within documented coordinate/rounding limits;
  moved collision aligns and retains exact raw types.
- Reopen and clean rebuild are deterministic on Linux and Windows; untouched terrain
  meets no-edit identity and project transfer resolves every piece asset.
- Invalid target limits block before the development ISO is touched and recovery can
  return to the last saved transforms.
- Performance/memory remain within an accepted delta from the M6 baseline.

Verification: packaged-app qualification report and PCSX2 evidence reference.

## M13 — Full-package updater and P2 qualification

### M13-001 — Freeze the install and update package contract

Requirements: FR-UPD-003 through FR-UPD-005, FR-RELENG-004, NFR-SEC-004
Depends on: M6-001, M6-002, M6-003, M6-004

Define archive/layout/version/signature/ownership/launcher/rollback contracts and the
exact Linux/Windows install types Forge can safely replace.

Acceptance:

- Release artifacts declare package schema, compression, top-level layout, executable,
  file manifest/hashes, channel/version/platform/architecture, signature/provenance,
  uncompressed size/count limits, and minimum updater version.
- Detection distinguishes Forge-managed writable installs from package-manager,
  read-only, development, unknown, and already-staged layouts without a write probe in
  protected locations.
- Atomic activation/rollback mechanisms and their platform limitations are documented;
  unsupported layouts have explicit handoffs.
- Existing stable/nightly channel isolation and downgrade rules remain normative;
  delta patches stay out of scope.

Verification: contract fixtures, threat review, packaging dry runs, and clean-VM layout
matrix.

### M13-002 — Download verified full packages with progress/cancellation

Requirements: FR-UPD-004, FR-UPD-002, NFR-REL-003, NFR-SEC-004
Depends on: M13-001, M6-004

Extend update discovery into an explicit app-owned download state machine while keeping
automatic checks passive and consent-based.

Acceptance:

- Download streams to a unique temporary file, reports bounded progress, supports safe
  cancellation/retry, limits time/size, and commits only after exact size/hash/signature
  and manifest identity checks.
- Channel switches, downgrades, redirects/untrusted URLs, platform/architecture
  mismatch, stale metadata, disk exhaustion, and duplicate concurrent requests fail
  clearly and clean partial files.
- Project state is untouched and the main/renderer loops remain responsive.
- Restarting Forge recognizes and revalidates a complete staged download but never
  trusts a partial or changed file.

Verification: local fake-release server tests for success, cancellation, corruption,
redirect, timeout, oversize, disk error, concurrency, and resume-state discovery.

### M13-003 — Extract and validate a staged installation safely

Requirements: FR-UPD-004, NFR-SEC-003, NFR-REL-002, NFR-P2-004
Depends on: M13-002

Extract the verified full package into a new app-owned staging directory using the
standard library/platform facilities selected by M13-001 and validate its complete
file manifest before activation.

Acceptance:

- Extraction rejects absolute/traversal/duplicate/case-colliding paths, symlinks,
  hardlinks, devices, special files, unexpected roots, excessive file count/expanded
  size/ratio, and output escaping through existing links.
- Every required file, executable identity, embedded version/channel/commit/SDK/bridge
  value, length, and hash matches the signed manifest before “ready” state.
- Cancellation/crash leaves the active install untouched and recognizable staged
  debris is cleaned safely on next launch.
- Validation runs off interaction loops with progress and bounded diagnostics.

Verification: malicious archive corpus, manifest mismatch, cancellation/crash injection,
and Linux/Windows clean-package tests.

### M13-004 — Activate updates out of process with rollback

Requirements: FR-UPD-005, FR-UPD-002, NFR-REL-001, NFR-REL-002
Depends on: M13-003

Implement the minimal external handoff selected in M13-001 to wait for Forge exit,
activate a validated staged install, retain one known-good version, relaunch, and record
health or recovery.

Acceptance:

- Dirty projects block restart until the user saves, explicitly discards, or cancels;
  Forge never closes during download/staging.
- The handoff verifies exact source/staged targets and a nonce before replacement,
  cannot target arbitrary paths, and refuses if install state changed since staging.
- Power loss/process kill/file lock/permission/startup-health failure at each activation
  checkpoint retains or restores a launchable known-good version.
- Successful startup marks the new version healthy and later cleanup retains exactly
  the documented rollback copy; unsupported layouts use their handoff instead.

Verification: state-machine tests, checkpoint fault injection, file-lock/permission
matrix, rollback launch, and clean Linux/Windows VM upgrades.

### M13-005 — Add update badge and lifecycle UI

Requirements: FR-UPD-004, FR-UPD-005, FR-UI-008, NFR-UX-001
Depends on: M7-001, M13-002, M13-003, M13-004

Integrate a distinct update badge/action into the compact ribbon and notification
center for available, downloading, verifying, ready, deferred, failed, and rollback
states.

Acceptance:

- Available state says **Forge update available**, uses a red badge plus text/icon, and
  provides version/channel/size/source tooltip/details and a Phosphor download icon.
- Mouse and keyboard can download, cancel, retry, defer, install/restart, or open the
  platform handoff; focus and announcements survive async state changes.
- Progress/error/ready state is shared with Notifications and cannot dispatch duplicate
  downloads or installs.
- Dirty-project and unsupported-install guidance is accurate and never implies an
  automatic restart occurred.

Verification: state/component/accessibility tests and packaged update walkthrough.

### M13-006 — Qualify secure full-package updates

Requirements: FR-UPD-004, FR-UPD-005, NFR-SEC-004, NFR-REL-001
Depends on: M13-004, M13-005

Run channel, platform, threat, interruption, rollback, and user-state matrices against
published-like signed fixtures.

Acceptance:

- Stable consumes only newer stable; nightly consumes the selected channel; downgrade,
  replay, tamper, wrong platform/architecture, and signature failures are rejected.
- Supported Forge-managed Linux and Windows installs update, relaunch, report the new
  identity, preserve settings/catalog/projects, and can roll back after injected health
  failure.
- Interruption at every download/extract/activate checkpoint leaves a recoverable old
  or new install and no project/data mutation.
- Package-manager/protected installs never self-modify and open the documented trusted
  handoff.

Verification: clean VM matrix, malicious fixture suite, checkpoint fault injection,
and signed qualification report.

### M13-007 — Run the P2 release qualification gate

Requirements: P2 release gate, all P2 functional requirements and NFR refinements
Depends on: M7-007, M8-008, M9-009, M10-005, M11-005, M12-008, M13-006

Exercise the complete portable advanced-authoring project and application update on
packaged Linux and Windows releases.

Acceptance:

- One UYA project contains an exact custom override, typed refs, moby instance/PVar
  edits with unknown-byte preservation, manual/template groups, HUD/FX replace+append,
  semantic settings, and transformed tfrag/collision pieces.
- Linux↔Windows ZIP transfer resolves only global Asset IDs plus project attachments;
  clean builds are deterministic and patch a development ISO that loads in PCSX2.
- Interrupted save/conversion/HUD/FX/tfrag/bake/patch/update operations retain the last
  known-good project, staging, development ISO, and Forge install.
- Every P2 requirement has automated or linked manual evidence; limitations (including
  DL writable support and partial reference coverage) are explicit.

Verification: signed P2 qualification report linked to the release candidate.
