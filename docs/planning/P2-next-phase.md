# P2 — Advanced authoring

Status: Draft 0.1
Priority: P2
Depends on: P0 release contracts, P1 project-attached assets where noted
Task register: [P2 tasks](tasks/P2-tasks.md)

This document is the requirements addendum and delivery plan for the phase after
the initial Forge release. The base [Forge v2 specification](../forge-v2-spec.md)
continues to define architecture, safety, portability, and release behavior. This
addendum promotes the previously future reference-enrichment work and specifies the
new authoring surfaces without broadening every game adapter at once.

## Outcome

A UYA project can use portable custom asset overrides, author HUD/FX texture banks,
organize and edit semantic level objects, edit documented moby instance and PVar data,
manipulate native tfrag pieces, visualize otherwise invisible settings, and install a
verified full-package Forge update. Unknown or unsupported source data is still
preserved and disclosed rather than guessed.

## Scope decisions

- **UYA remains the qualification target.** Shared contracts are game-neutral and
  game layouts stay in the SDK/adapters. DL sprite-ID and FX-label rules receive
  fixtures, but writable DL workflows do not block P2 until a DL base-project and
  bake pipeline exists.
- **Extend the existing attached-asset store.** Forge already stores immutable,
  content-addressed project assets with parent Asset IDs. P2 adds explicit override
  bindings and broader asset kinds; it does not create a second cache.
- **References are typed.** Entity references use stable Entity IDs and asset
  references use Asset IDs. Source list indexes remain provenance only. Bake adapters
  deterministically resolve supported references to target-native indexes.
- **No blind index conversion.** A raw integer becomes a reference only when a
  game-specific layout or MobyDex field proves its meaning, null sentinel, target
  kind, and bake encoding. Unknown integers remain opaque.
- **Groups are editor-only organization.** Manual and template groups never enter
  game binaries. A group transform emits ordinary entity transform commands.
- **MobyDex is local in P2.** A versioned built-in dataset ships with Forge and a
  project may carry custom entries. Independent network updates are deferred until
  schema compatibility and artifact signing are proven.
- **PVar editing is fixed-length first.** Structured edits patch validated byte
  ranges in the original blob and preserve every unknown byte. A missing PVar may be
  created only from an exact-length default. Arbitrary resizing and executable schema
  expressions are out of scope.
- **Tfrag editing means native-piece transforms.** P2 splits source tfrags at proven
  native group boundaries and supports piece translate/rotate/scale. It does not add
  vertex-, face-, UV-, or material-authoring tools.
- **Full-package updates precede deltas.** Only Forge-managed, user-writable install
  layouts self-update. Package-manager or protected installs receive a clear handoff.

## Delivery order

```text
M6-007 P0 qualification
  └─ P1-001 attached custom-asset lifecycle
       └─ M7 Authoring foundations
            ├─ M8 HUD and FX texture authoring (+ P1-004 quantization)
            ├─ M9 MobyDex and moby editing
            │    └─ M10 Group engine
            ├─ M11 Scene semantics and level settings
            └─ M12 Editable tfrags

M6-004 update discovery + M6 release package contract
  └─ M13 Full-package updater

M13-007 P2 qualification depends on M8, M9, M10, M11, M12, and M13-006.
```

M7-001 (ribbon cleanup), M8-001 (HUD format inventory), M9-001 (moby layout
inventory), M9-004 (MobyDex schema), M12-001 (tfrag boundary inventory), and M13-001
(install contract) may start in parallel when their stated inputs exist. A whole
milestone does not need to wait merely because another task in its lane is unfinished.

## Functional requirements

### Workspace chrome

#### FR-UI-008: Compact project ribbon

- Remove the HOST badge and the redundant Horizon Forge title/description from the
  workspace ribbon. Host health remains available in the status/diagnostics surface.
- Put Project and Save icon buttons at the left edge with accessible names, tooltips,
  visible focus, and the existing keybindings. Use Phosphor CSR icons.
- Put dirty/read-only/recovery state immediately after those actions. State cannot be
  communicated by color alone.
- Show the project name as the primary ribbon label, truncating safely while exposing
  the complete name to assistive technology and hover/focus.
- Keep the notification action right-aligned and size it to the ribbon without
  reducing its keyboard target or badge legibility.

### Custom assets and overrides

#### FR-ASSET-007: General project-attached assets

The existing content-addressed project asset store MUST accept every asset kind that
has a validated canonicalizer and target writer, including textures and tfrag pieces.
An attached record stores kind, canonical-format version, size, provenance/parent
where applicable, and an integrity-verifiable blob. External paths are optional
reimport hints and never required to open, share, or bake the project.

#### FR-ASSET-008: Explicit asset override bindings

A project MAY bind an exact `(source kind, source Asset ID)` to a compatible
`(replacement kind, replacement Asset ID)`. Resolution is exact, deterministic, and
at most one hop: source to replacement. Chained or cyclic overrides are invalid.
Removing a binding restores the vanilla source. Replacement blobs participate in
reference analysis, undo/redo, recovery retention, cleanup, bake fingerprints, and
portable project packaging.

Brand-new content such as an appended FX texture directly references an attached
Asset ID and does not invent a source override.

### Typed references

#### FR-REF-001: Stable reference model

Forge MUST represent understood relationships as a discriminated reference containing
the domain (`entity` or `asset`), target kind, stable ID, nullability, and source field
identity. Entity targets cover mobys, ties, shrubs, cuboids, spheres, splines, paths,
areas, tfrag pieces, and other project entities as adapters gain verified fields.
Asset targets use an Asset ID and kind. A single untyped numeric/string reference is
not permitted.

#### FR-REF-002: Import, mutation, and bake resolution

- Import adapters resolve known native indexes to stable IDs and retain the original
  raw value for diagnostics.
- Deleting or disabling a target reports inbound references. Delete either blocks,
  clears nullable references through the same undoable command, or includes all
  dependents in an explicit destructive operation.
- Reordering or deleting source lists never changes reference meaning in project
  state. Bake assigns deterministic target indexes and patches every supported field.
- Missing, wrong-kind, ambiguous, or unsupported references block only the affected
  output and identify the owner, field path, expected kind, and target.

#### FR-REF-003: Shared reference UI and navigation

One reusable Mantine reference field renders resolved labels, kind, missing state,
and a picker filtered to compatible targets. Single activation selects/reveals the
target in the active tree; double activation or an explicit Focus action moves the
camera to renderable entity targets. Keyboard users receive equivalent Select, Reveal,
and Focus actions.
Asset references open the relevant asset preview rather than pretending they have a
world position.

The References view exposes incoming and outgoing understood references and clearly
labels coverage as partial when opaque fields remain.

### Billboard gizmos and semantic overlays

#### FR-GIZMO-001: Shared world billboard

A reusable Three.js overlay primitive renders a camera-facing map-pin silhouette at a
world position with a shared texture/material cache, configurable color and center
glyph, selection/hover states, depth policy, and bounded screen size. It maps back to
an Entity ID, follows ordinary picking/selection rules, never enters bake data, and
releases GPU resources on project close. React/UI glyphs use Phosphor CSR imports;
the viewport atlas may rasterize the same icon paths without adding an icon package.

### Moby instances and MobyDex

#### FR-MOBY-001: Per-game moby instance contracts

The Ratchet SDK owns each supported game's binary moby instance layout, validation,
and read/write behavior. Forge.Host exposes a game-neutral typed property sheet with
stable field keys, value kinds, bounds, editability, and raw provenance. Unknown
instance bytes survive edits. Renderer code MUST NOT encode UYA or DL byte offsets.

#### FR-MOBY-002: Moby instance property editing

Selecting a moby displays its identity, OClass, and all supported instance fields in
Properties. Controls match value semantics (integer, finite float, enum/flags, RGB(A),
vector, reference, and read-only raw value), validate before mutation, and use editor
commands. Multi-selection exposes only compatible fields. A semantic SDK re-read
verifies every write.

#### FR-MDEX-001: Dataset identity and precedence

A MobyDex entry is keyed by exact game ID plus moby OClass and carries a schema
version, display name, optional description/tags, supported PVar length, optional
exact-length default bytes, relocation offsets, and field definitions. The built-in
dataset is read-only and versioned with Forge. A project-local custom entry replaces
the whole built-in entry for the same key; partial implicit merges are forbidden.
The UI always identifies the active source and reports duplicate/conflicting entries.

#### FR-MDEX-002: Safe extensible PVar schema

The declarative JSON schema supports fixed little-endian scalar integers/floats,
booleans, enums, flags, RGB/RGBA colors, vectors, fixed-size bytes, fixed-count arrays,
fixed-layout structs, and typed entity/asset references. Every field has a stable key,
label, byte offset, byte length, and optional bounds/help. Arrays declare count and
stride. References declare target kind and null sentinel.

Schemas cannot run code or arbitrary expressions. Validation rejects out-of-bounds or
integer-overflow ranges, invalid defaults, duplicate keys, illegal overlaps outside an
explicit union, invalid relocation offsets, unknown types, and unreasonable nesting,
field, array, or blob sizes.

#### FR-MDEX-003: Contribution workflow

Forge provides a canonical JSON schema, formatting/validation command, small synthetic
fixtures, and contributor documentation. A project can import/export a custom entry
atomically. An in-app entry editor is optional after the schema and structured PVar
editor are stable; P2 does not require a general visual schema programming system.

#### FR-PVAR-001: Lossless structured editing

For a documented moby PVar, Forge reads the original fixed-length blob, decodes only
declared fields, and writes edits back into a copy of that blob. Unknown gaps and
unsupported fields remain byte-identical. Creating a missing PVar requires a valid
MobyDex default of the declared length. Editing, reference changes, save/recovery,
undo/redo, PVar table/relative-pointer updates, bake fingerprints, and native write
all form one validated path.

#### FR-PVAR-002: Structured and hex views

The PVar panel toggles between a typed form and a read-only hex/ASCII view of the same
authoritative bytes. Fields highlight their exact byte ranges; selecting a form field
scrolls/highlights the hex range and activating a highlighted range selects its form
field. Modified bytes are distinguishable without color alone. The implementation may
reuse the geometry/highlight algorithms from ratchet-companion, but must use Forge's
Mantine styling and window rows so DOM work is proportional to visible content.

#### FR-PVAR-003: PVar reference integration

MobyDex reference fields use FR-REF-001 through FR-REF-003. Import converts verified
native indexes to Entity/Asset IDs; bake converts them back after final deterministic
ordering. The raw hex view displays both encoded value and resolved target. Unknown
index-looking integers are never auto-promoted to references.

### Group engine

#### FR-GROUP-001: Manual semantic groups

Users can create, rename, delete, and reorder project-local groups and add/remove any
compatible loose scene entities by Entity ID. An entity may belong to multiple groups.
Groups have stable IDs, preserve member order, diagnose missing members, participate in
save/recovery/undo/redo, and do not change scene hierarchy or bake output.

#### FR-GROUP-002: Parallel group view

The scene panel can switch between the raw layer/entity tree and a semantic group tree.
Selection remains one Entity-ID set in both views. Filtering, missing/locked/hidden
states, keyboard navigation, and context actions behave consistently. Ungrouped
entities remain discoverable.

#### FR-GROUP-003: Group manipulation

Selecting a group selects its current members. Translate/rotate/scale uses the existing
multi-entity transform path, a documented pivot, capability intersection, and one
undoable command while preserving member offsets. Locked or unsupported members are
reported before mutation; Forge never silently applies only part of a transform.

#### FR-GROUP-004: Versioned group templates

Built-in game-specific templates are deterministic host-side evaluators over typed
project data, MobyDex fields, references, class IDs, and spatial indexes. Evaluation
returns a preview with evidence and ambiguities; accepting it stores ordinary group
membership plus template ID/version. Regeneration shows an add/remove diff and never
silently replaces user changes. Initial fixtures cover node-moby clusters and UYA
siege bases. User-authored executable templates are out of scope.

### HUD and FX texture authoring

#### FR-HUD-001: HUD bank inventory and preview

The SDK exposes validated HUD icons, frames, palettes, textures, physical bank/page
placement, sprite IDs, limits, and diagnostics through a game-neutral host contract.
Forge provides a searchable main viewport with lazy texture previews and explicit
invalid/missing-frame states. The adapter, not React, owns UYA/DL encoding rules.

#### FR-HUD-002: Replace and append HUD entries

Replacing an existing HUD texture creates an FR-ASSET-008 override from its exact
source Texture Asset ID to a project-attached custom Texture Asset ID. Adding an entry
directly references a custom texture, assigns a validated unique custom sprite ID, and
updates frame/palette/bank metadata without exceeding adapter limits. UYA `Exxx` and
DL `75xx` display/validation rules are adapter fixtures, not renderer conditionals.

#### FR-HUD-003: HUD rebuild and bake

The Ratchet SDK is the sole HUD writer. It rebuilds affected header/bank payloads,
preflights dimensions, pixel/palette format, counts, offsets, alignment, sprite-ID
uniqueness, bank capacity, and compressed size, then semantically re-reads output.
Unchanged HUD data remains byte-identical where the format permits. A failed rebuild
does not replace staging or project state.

#### FR-FX-001: FX texture inventory and labels

The SDK exposes ordered FX texture definitions and decoded previews. Forge displays
game-specific labels migrated from map-o-matic as reviewed data, including a stable
fallback label for unknown indexes. Labels do not participate in asset identity.

#### FR-FX-002: Replace and append FX textures

Replacement uses an exact texture override. New custom textures append to the list;
P2 does not insert or reorder existing entries because that would invalidate opaque
indexes. Import validates target dimensions, format, palette, and count/size limits
before committing a project command.

#### FR-FX-003: FX rebuild and bake

The Ratchet SDK rebuilds FX definitions/pixels and any affected offsets, semantically
re-reads them, and integrates the result with incremental asset-WAD bake and pack.
Existing indexes remain stable and appended indexes are deterministic.

### Level settings and scene semantics

#### FR-LEVEL-001: Complete supported level settings

The project and editor expose verified target settings including death height, ship
position/rotation, spherical-world center/state, ship path, and ship camera cuboids.
World positions use vector controls and typed references replace understood path or
cuboid indexes. Unsupported fields remain opaque. Every edit is validated, undoable,
saved, fingerprinted, and written by the game adapter.

#### FR-LEVEL-002: Semantic scene aids

- Death height renders as a selectable/editor-toggleable transparent red-striped plane
  with repeating skull markers, sized from scene bounds with a safe fallback.
- Ship position and spherical-world center use FR-GIZMO-001 with distinct icons and
  non-color labels.
- Area entities replace their current wire-sphere mesh with the shared billboard while
  preserving authoritative bounds, picking, selection, and properties.

These aids are renderer projections of project data and never become baked meshes.

### Editable tfrags

#### FR-TFRAG-001: Native piece import

The SDK defines and validates the native UYA tfrag group boundary, then emits one
canonical piece asset plus one stable project entity for every source occurrence in
primary and chunk terrain. Duplicate geometry remains distinct by Entity ID and source
provenance. No-edit import/rebuild retains byte identity or a documented equivalent
semantic identity when deterministic repacking is required.

#### FR-TFRAG-002: Piece selection and transforms

Tfrag pieces appear in the raw scene tree, render with their source materials, map
picking to Entity IDs, and support translate/rotate/scale through ordinary commands.
The adapter preflights transform representability; zero, non-finite, or unsupported
mirrored/non-uniform scales are rejected explicitly. Transform application updates
vertices, normals/winding, bounds, chunk/group metadata, and bake fingerprints as the
native format requires.

#### FR-TFRAG-003: Collision association

The existing collision association approach is extended to rank native collision
pieces against individual tfrag entities in local space. It records an attachment only
when the best candidate passes documented coverage/alignment thresholds with a clear
lead. Ambiguous collision remains independent. Attached collision follows every
supported tfrag transform and is revalidated against native collision budgets.

#### FR-TFRAG-004: Native bake

The Ratchet SDK is the sole tfrag writer. It rebuilds affected primary/chunk terrain,
material/texture references, bounds and indexes, semantically re-reads output, and
integrates with incremental staging/WAD/ISO patching. Invalid geometry, target limits,
or unresolved assets block commit with piece Entity ID and source group diagnostics.

### Full-package updates

#### FR-UPD-004: Download and stage

After explicit consent, Forge downloads the selected channel's full package with
progress and cancellation to an app-owned temporary location, enforcing HTTPS source,
declared maximum size, exact byte count, SHA-256, channel/version/platform/architecture,
and artifact signature policy. Extraction rejects absolute paths, traversal, links,
devices, duplicate paths, unexpected top-level layouts, and decompression limits.
A complete staged install is validated before the current install is touched.

#### FR-UPD-005: Install, restart, and recovery

Forge MUST detect whether the current install is self-managed and writable. For a
supported install, the user chooses Install and restart; dirty projects block exit
until saved/discarded/cancelled. An external handoff replaces the application only
after Forge exits, retains one known-good version, atomically activates the staged
version where the platform permits, launches it, and records success. Failed startup
or interrupted replacement restores or offers the known-good version. Unsupported
package-manager/protected installs receive a platform-appropriate handoff.

The ribbon displays a distinct red **Forge update available** badge with a download
icon, version tooltip, keyboard action, progress, retry, defer, and ready-to-install
states. Red is not the sole indication.

## Non-functional requirements

All base NFRs remain normative, especially NFR-PERF-002 through NFR-PERF-006,
NFR-REL-001 through NFR-REL-004, NFR-SEC-001 through NFR-SEC-004,
NFR-PORT-001, NFR-MAINT-001 through NFR-MAINT-004, and NFR-UX-001 through
NFR-UX-003. P2 adds these refinements:

#### NFR-P2-001: Forward-compatible preservation

Project migrations, MobyDex overlays, PVar edits, HUD/FX writers, tfrag writers, and
updates preserve unknown compatible fields/bytes. Unsupported newer schemas are
rejected before mutation. Every new project, dataset, render-package, and updater
contract has its own version and migration/compatibility fixture.

#### NFR-P2-002: Determinism and reproducibility

Reference index assignment, override resolution, group-template output, PVar encoding,
HUD/FX placement, tfrag rebuild, and update manifest validation are deterministic for
identical inputs. Locale, filesystem enumeration order, and platform path syntax cannot
change output.

#### NFR-P2-003: Interactive scale

Reference resolution uses indexed lookup rather than repeated full-project scans.
Group-template evaluation, large PVar decoding, texture conversion, tfrag conversion,
hashing, extraction, and installation run outside renderer/main interaction loops.
Scene trees, hex views, and texture grids bound DOM/preview work to visible content.
Qualification records interaction p95 and peak memory on the largest supported UYA
fixture; regressions from the retained M6 baseline require an explicit decision.

#### NFR-P2-004: Declarative data safety

MobyDex and label datasets are data, never executable extensions. Parsers bound input
bytes, nesting, entry/field/array counts, decoded allocation, and diagnostic volume.
Project custom data cannot access files, network, processes, or renderer globals.

#### NFR-P2-005: Actionable provenance

Diagnostics for a schema field, reference, asset override, bank entry, FX index, group
template, tfrag piece, or update identify its stable key/ID, owning source, target game,
and corrective action. The UI distinguishes built-in facts, project overrides, inferred
relationships, and opaque data.

## Milestones

### M7 — Authoring foundations and workspace chrome

Deliver the compact ribbon, generalized project-attached assets, exact override
bindings, stable typed references, shared reference UI, and billboard primitive.

Exit: a moved/zipped UYA project resolves an overridden texture and typed entity
reference without source indexes or external paths; undo/recovery/cleanup remain safe;
the compact ribbon and billboard pass keyboard/high-DPI checks.

### M8 — HUD and FX texture authoring

Deliver SDK read/write contracts, lazy main viewports, exact texture overrides, append
flows, per-game labels/IDs, incremental rebuilds, and target-limit diagnostics.

Exit: one replaced and one appended UYA HUD texture and FX texture survive project
transfer, bake/build/patch, and PCSX2 verification with stable existing indexes. DL
display/validation fixtures pass without claiming a writable DL project pipeline.

### M9 — MobyDex and moby editing

Deliver per-game moby instance descriptors, instance editing, the MobyDex schema and
validation/contribution flow, lossless structured PVar editing, reference-aware fields,
and a windowed highlighted hex view.

Exit: documented synthetic and real UYA mobys round-trip instance and PVar edits while
every unknown byte remains identical, references survive list reordering, and malformed
or incompatible definitions cannot mutate a project.

### M10 — Group engine

Deliver persisted manual groups, the parallel group tree, atomic group transforms, a
versioned template evaluator, and initial node/siege templates.

Exit: users can organize a representative level into semantic groups and manipulate a
whole group safely; template evidence and regeneration diffs are deterministic and do
not silently overwrite manual membership.

### M11 — Scene semantics and level settings

Deliver remaining verified level settings, typed ship references, death-height plane,
ship/sphere billboards, and billboard-based area rendering.

Exit: settings edit and round-trip through UYA output, every semantic aid selects the
right authoritative data, and no overlay appears in baked scene geometry.

### M12 — Editable tfrags

Deliver proven native piece boundaries, per-piece entities/rendering/transforms,
collision association, SDK writing, and incremental bake qualification.

Exit: representative primary and chunk tfrag pieces can be moved, rotated, and scaled,
with confidently associated collision following them, then rebuilt and verified in
PCSX2; untouched terrain retains the qualified no-edit identity.

### M13 — Full-package updater and P2 qualification

Deliver an explicit install-layout contract, secure full-package download/extraction,
out-of-process activation/rollback, ribbon update state, and the combined release gate.

Exit: supported Forge-managed Linux and Windows installs update and restart from a
signed/verified package without losing a dirty project or known-good installation;
unsupported layouts hand off clearly; the complete P2 UYA workflow passes in a packaged
application.

## Deferred work

- Network-delivered MobyDex updates and their signing/rollback channel.
- A general in-app schema designer or executable/user-authored group templates.
- Automatic inference of unknown PVar/index meanings.
- Tfrag vertex/face/UV/material authoring and terrain generation.
- Insertion/reordering of existing FX indexes.
- A writable DL level pipeline solely to expose P2 controls.
- Delta application updates.

## Discovery evidence and unresolved gates

- Forge v1's
  [`pvar_overlay.json`](https://github.com/Horizon-Private-Server/horizon-forge/blob/develop/pvar_overlay.json)
  demonstrates game/OClass keys, exact defaults, fixed offsets, arrays, enums, colors,
  primitive values, and moby/cuboid references. P2 keeps those useful concepts but adds
  stable field keys, explicit sizes/endian/nulls, recursive validation, typed stable
  references, source precedence, and strict bounds.
- `ratchet-companion/ui/src/components/hex-byte-view` supplies useful highlight-shape
  and hit-testing reference code. Its current all-row React rendering is not copied;
  M9 windows rows and integrates Forge styling/accessibility.
- `ratchet-map-o-matic/src/features/map-viewer/fxTextureCatalog.ts` is the review source
  for UYA/DL FX labels. M8-006 moved the reviewed catalogs to game-owned SDK data,
  added bounded raw/compressed inventory parsing, and exposed exact Texture Asset IDs
  through a game-neutral Forge bridge contract.
- M8-001 confirmed that the native “five pages” are five physical HUD banks selected
  by cumulative palette/texture counts, not a separate per-entry concept. The frozen
  byte ownership, limits, and game-owned sprite-ID namespaces are documented in
  [`hud-bank-contract-v0.md`](../features/hud-bank-contract-v0.md). M8-002 added the
  bounded symmetric UYA writer and corpus-backed no-edit/replace/append verification;
  M8-003 now imports exact HUD source identities into schema 13, routes replacements
  through asset overrides, stores appended `Exxx` icons as direct attached-texture
  references, and exposes bounded undoable bridge commands.
- M12-001 must prove the source-native tfrag group boundary and writer constraints.
- M13-001 must freeze which packaged install layouts can be replaced and rolled back
  safely before implementation begins.

## P2 release gate

M7 through M12 exit gates and M13-006 pass in packaged Linux and Windows builds. One
portable UYA project containing custom overrides, typed references, MobyDex/PVar edits,
groups, HUD/FX changes, semantic settings, and transformed tfrags moves between the two
platforms, deterministically bakes/builds/patches, and loads in PCSX2. An interrupted
save, asset conversion, bank rebuild, tfrag rebuild, patch, or application update leaves
the last known-good project, development ISO, and Forge installation recoverable.
