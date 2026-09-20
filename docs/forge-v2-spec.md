# Horizon Forge v2 product and technical specification

Status: Draft 0.7

Last updated: 2026-09-19

Primary target: Linux desktop

Secondary target: Windows desktop

Possible later target: macOS desktop

## 1. Purpose

Horizon Forge v2 is a cross-platform desktop editor for Ratchet & Clank
PlayStation 2 levels. It should let a user import assets from games they own,
create a project from a base level, edit the level visually, bake only changed
content, and patch a configured development ISO with a short, safe test loop.

This document is the product and engineering source of truth. Requirement IDs
should be copied into issues, pull requests, tests, and release notes so work can
be traced back to its intended outcome.

The words **MUST**, **SHOULD**, and **MAY** are normative:

- **MUST** is required for the applicable release gate.
- **SHOULD** is expected unless a documented reason prevents it.
- **MAY** is optional.

Items marked **P1** or **Future** influence today's boundaries but do not authorize
building the feature in P0. **P0** and **MVP** are synonyms: the first usable,
vanilla-asset-only Forge release assembled through milestones M0-M6. P0 is a
priority, not milestone M0.

## 2. Product goals

1. Make the edit-to-test loop feel native: edit, bake, patch, then reload the
   development ISO in the user's externally managed PCSX2 installation.
2. Reuse `RatchetPs2.Core`, `RatchetPs2.Sdk`, and the per-game libraries from
   ratchet-ps2-cli rather than duplicating game logic in JavaScript.
3. Migrate compatible React, Mantine, Three.js, renderer, and map-package work
   from ratchet-map-o-matic.
4. Keep projects small and portable by referring to common imported game assets
   by content identity rather than copying them into every project.
5. Preserve work through undo/redo, autosave, crash recovery, validation, schema
   migration, and recoverable ISO patching.
6. Support game-specific formats without spreading their quirks throughout the
   editor.
7. Leave deliberate boundaries for future collaboration and plugins without
   implementing either system prematurely.

## 3. Non-goals for the first usable release

- Shipping copyrighted game assets with Forge or inside ordinary shared
  projects.
- Replacing PCSX2 or emulating gameplay inside Forge.
- Installing, configuring, launching, pausing, reloading, or otherwise directly
  controlling PCSX2.
- A browser-hosted edition of the editor.
- Real-time multi-user collaboration.
- A third-party plugin marketplace or untrusted plugin execution.
- A native C#/Vulkan/DirectX viewport.
- Custom GLB import, custom-texture quantization, or other authored non-vanilla
  asset pipelines; these are P1.
- Lossless editing of every unknown field in every game from day one.
- Delta application updates before full-package updates are reliable.
- macOS distribution before Linux and Windows pipelines are stable.

### 3.1 P0/MVP boundary

P0 proves the complete workflow using vanilla NTSC-U UYA content: install the
pinned SDK, import the shared UYA asset catalog, create a project from a UYA base
level, inspect and edit supported content, preserve unsupported data opaquely,
save/undo/recover, incrementally bake a map WAD, and patch a separate development
ISO. Linux is the lead development platform and Windows is included before P0 is
released.

P0 does not import custom GLBs or quantize custom textures. RC1, Going Commando,
Deadlocked, PAL UYA, and custom content may shape schemas and capability checks but
do not expand the P0 pipeline.

## 4. Terminology

| Term | Meaning |
| --- | --- |
| Clean source ISO | A user-owned, validated game ISO used read-only for global imports and base levels. Forge never writes to it. |
| Development ISO | A separate working copy that Forge creates from a clean source ISO in a user-selected development directory and may patch for testing. |
| Asset | Immutable reusable content such as a texture, material, model, animation, or sound. |
| Asset ID | A content-derived SHA-256 identity for an immutable canonical asset. |
| Entity ID | A UUID identifying an editable instance in a project independently of its referenced asset. |
| Layer | A category of project content such as sky, tfrags, ties, shrubs, mobys, gameplay, collision, or lighting. |
| Bake | Convert editable project state into validated, game-specific loose output in staging. |
| Build | Pack baked output into a WAD or other target archives. |
| Patch | Apply built output to the configured development ISO. |
| Staging | Generated, disposable bake/build output; never authoritative project data. |
| Translation | Conversion from a source game's representation to the target game's representation. |
| Editor command | A serializable user action that validates and changes project state and can normally be undone. |

## 5. Core workflows

### 5.1 First run

1. Forge displays a welcome/setup wizard.
2. The user chooses a default projects directory.
3. In P0, the user locates a clean NTSC-U Up Your Arsenal source ISO. Later
   releases may add RC1, Going Commando, Deadlocked, and PAL UYA sources.
4. Forge identifies each ISO's game, region, revision, and checksum where known.
5. Forge globally imports reusable UYA ties, shrubs, mobys, and other supported
   asset classes across its maps,
   deduplicating them by Asset ID and tagging every source appearance.
6. The user selects a development-ISO directory. For the initial UYA target,
   Forge validates available space and creates a separate development copy there
   using progress-reporting, cancellable, temporary-file-and-replace semantics.
7. Forge records the clean ISO as permanently read-only and the generated copy as
   the only writable patch target.
8. Forge opens the project hub. Asset import may be deferred or resumed.

### 5.2 Create a project

1. The user chooses a project name, directory, target game/version, and base
   level.
2. Forge verifies that required source content exists locally.
3. Forge creates lightweight project data referencing immutable assets in the
   global catalog by Asset ID.
4. Forge opens the populated scene. Base content can be modified, disabled, or
   deleted without changing the global asset store. Editing an imported resource
   creates a project-local derived asset on first write.

### 5.3 Edit a level

1. The user navigates the Three.js viewport or scene tree.
2. Selection is synchronized between viewport, tree, and properties.
3. Property edits and transform gizmos issue editor commands.
4. Commands participate in undo/redo and mark affected layers dirty.
5. Forge autosaves authoritative state and maintains crash recovery data.

### 5.4 Bake and test

1. The user invokes **Bake**, **Build**, or **Bake, Build & Patch**.
2. Forge validates the project and presents actionable blocking errors.
3. Forge fingerprints layer inputs and skips unchanged outputs.
4. The .NET host translates and bakes dirty content into staging, then packs it.
5. Forge validates the development ISO and applies a recoverable patch.
6. Forge reports success so the user can reload the development ISO in their
   externally managed emulator.

### 5.5 Share a project

1. Machine-specific source ISO, development ISO, cache, and staging paths
   remain outside project data.
2. A shared project contains authored data, asset references, and attached custom
   assets only.
3. On another machine, Forge resolves common assets from that user's catalog and
   reports missing Asset IDs with an import action.

## 6. Architecture

```text
┌─────────────────────────────────────────────────────────────┐
│ Electron application                                       │
│                                                             │
│  React + Mantine renderer                                   │
│  ├─ docked editor UI                                        │
│  ├─ Three.js viewport                                       │
│  └─ commands, queries, selection, notifications             │
│             │ narrow typed preload API                      │
│  Electron main process                                      │
│  ├─ windows, dialogs, menus, updater                         │
│  ├─ settings and project-path coordination                  │
│  └─ .NET host lifecycle and binary framing                  │
└─────────────┬───────────────────────────────────────────────┘
              │ versioned binary stdin/stdout protocol
┌─────────────▼───────────────────────────────────────────────┐
│ Forge .NET host (self-contained .NET 10 executable)         │
│  ├─ RatchetPs2.Sdk / Core / Games.*                         │
│  ├─ imports, translation, baking, WAD building              │
│  ├─ source/development ISO validation and patching          │
│  └─ content-addressed asset storage                         │
└─────────────┬───────────────────────────────────────────────┘
              │
             filesystem / development ISO
```

### 6.1 Responsibilities

**React renderer**

- Owns panels, docking, shortcuts, scene presentation, and direct-manipulation UX.
- Renders the scene with Three.js.
- Has no unrestricted Node.js or filesystem access.
- Does not duplicate SDK parsing, conversion, build, or patch logic.

**Electron main process**

- Owns app lifecycle and privileged desktop APIs.
- Exposes a small operation-specific API through a context-isolated preload.
- Starts one .NET host for the application session, restarts it after a crash,
  and fails pending work clearly.
- Does not become a second domain backend.

**Forge .NET host**

- References `RatchetPs2.Sdk` and game libraries directly; ordinary operations do
  not shell out to the CLI.
- Owns CPU-heavy and filesystem-heavy game operations.
- Uses stream/byte-oriented SDK APIs and adds editor orchestration only.
- Writes protocol frames to stdout and diagnostics to stderr.

### 6.2 Ratchet SDK bootstrap

#### FR-SDK-001: Pinned source installation

Forge MUST NOT depend on a NuGet-published Ratchet SDK or a Git submodule. A
repository-owned version file pins an exact ratchet-ps2-cli tag or commit, and a
bootstrap script installs that revision into a local ignored dependency directory.

The bootstrap MUST support both local development and GitHub Actions. It MUST:

- fetch the configured ratchet-ps2-cli repository/revision or accept an explicit
  already-downloaded source directory for offline development;
- verify the checked-out revision against the version file;
- restore and build the required `RatchetPs2.Sdk`, Core, and UYA game projects;
- expose their project/output paths to the Forge host build without copying SDK
  source into this repository; and
- fail clearly when the dependency is absent or mismatched.

Publish workflows MUST run this bootstrap before building Forge and bundle the
compiled SDK dependencies with the self-contained .NET host. Credentials for a
private SDK repository, if required, come from protected GitHub secrets and are
never embedded in artifacts.

### 6.3 Binary bridge

#### FR-BRIDGE-001: Transport

Electron main MUST spawn the bundled host as a normal child process and use raw
stdin/stdout byte streams. The initial bridge requires no WASM runtime, HTTP
server, JSON RPC, gRPC, Protobuf, or native Node addon.

#### FR-BRIDGE-002: Framing

Every message MUST have a fixed little-endian header containing:

| Field | Purpose |
| --- | --- |
| Magic | Reject accidental or corrupt input. |
| Protocol version | Negotiate compatibility. |
| Message kind/opcode | Identify request, result, progress, cancellation, or error. |
| Flags/status | Carry frame options and outcome state. |
| Request ID | Correlate results/progress to requests. |
| Payload length | Delimit payloads regardless of pipe chunking. |

The exact layout belongs in a protocol document with golden vectors before
implementation. Payloads SHOULD use explicit length-prefixed fields and MUST not
rely on C# or JavaScript object memory layout.

#### FR-BRIDGE-003: Lifecycle and errors

- Startup MUST include a handshake reporting host, protocol, and SDK versions,
  supported games, and capabilities.
- Unknown opcodes, invalid lengths, incompatible versions, and malformed payloads
  MUST fail safely without unbounded allocation.
- Noticeable operations MUST support progress and cancellation.
- Errors MUST include stable codes and human-readable messages.
- stdout MUST contain protocol bytes only; logs use stderr or a log file.
- Electron MUST continuously drain output and apply write backpressure.

#### FR-BRIDGE-004: Large data

- ISOs remain on disk; only validated paths and operation metadata cross the
  bridge.
- Map/render packages MAY cross as raw binary payloads.
- Payloads causing repeated high-memory copies SHOULD use an app-owned temporary
  file with explicit lifetime instead.
- Host paths MUST be normalized and restricted to appropriate source, project,
  staging, temporary, or target locations.

### 6.4 Editor runtime and scene bus

Forge MUST expose one typed editor runtime to UI features. It is a controlled
boundary, not an unrestricted bag of mutable globals. It provides:

- `execute(command)` for validated state changes;
- queries for current project/scene reads;
- shared selection and focus state;
- typed state, progress, and diagnostic events;
- registered tools and capability checks.

React components, scene tree, properties, keybindings, viewport tools, and future
trusted plugins MUST use the same commands and queries. Three.js objects MUST NOT
be the authoritative project model.

Each selectable render object MUST resolve to an Entity ID. Selection stores IDs,
not render object references, so it survives scene re-projection.

Commands SHOULD remain serializable and ordered for future collaboration.
Networking, conflict resolution, presence, and authority are deferred.

## 7. Project and asset data

### 7.1 Logical project layout

```text
MyLevel/
├── forge-project.json       version, target, base, content index
├── content/                 authoritative editable layers/entities
├── assets/                  attached custom assets by Asset ID
├── recovery/                bounded local recovery data
└── staging/                 generated and safely disposable
```

The manifest MUST be human-readable and versioned. Exact content encoding remains
an implementation decision until representative levels are measured; large scene
data MAY use deterministic binary files if text is a proven performance problem.

Project rules:

- Authoritative files use project-relative references.
- Machine-specific absolute paths never enter shared project data.
- Staging/recovery are unnecessary for sharing or reopening a clean project.
- Saves use temporary-file-and-replace semantics where supported.
- Every schema has an explicit version and tested forward migrations.
- A newer unsupported schema opens read-only or is rejected, never overwritten.
- Project serialization SHOULD be deterministic.

### 7.2 Identity

#### FR-ASSET-001: Asset IDs

An Asset ID MUST be SHA-256 over a domain-separated canonical representation:

```text
sha256(asset-kind || canonical-format-version || canonical-bytes)
```

Canonicalization MUST be deterministic and versioned. Meaningful content changes
produce a new ID. Mutable names/tags are not part of identity. Imported source,
canonical form, and target-game derivatives are separate linked records.

#### FR-ASSET-002: Entity IDs

Every project entity MUST have a stable UUID. Copying creates a new Entity ID but
may retain the same Asset IDs. IDs survive save/load, bake, undo/redo, and sharing.

#### FR-ASSET-003: Catalog and tags

The per-user catalog MUST support:

- asset kinds including texture, material, tie, shrub, tfrag, moby, animation,
  sound, sky, and gameplay resources;
- source game, region/revision, source level(s), archive, and source index;
- user tags, display aliases, provenance, import time, and importer version;
- queries by ID, kind, game, level, and tags;
- metadata re-indexing without changing immutable blobs.

Game and level tags are many-to-many. A tie used by three levels is stored once
and tagged with all three appearances.

#### FR-ASSET-004: Storage and resolution

- Common imported assets live once in a per-user content-addressed store.
- Projects reference common assets by ID and do not copy them on save.
- Resolution order is project-attached asset, then per-user asset store.
- Missing assets report ID, kind, known provenance, and a guided import action.
- Deleting a project never deletes shared catalog assets.
- Catalog garbage collection is a separate explicit maintenance operation.

The global import is shared by every map project. It is the immutable source of
truth for assets extracted from clean ISOs; a project never mutates these records
or blobs in place.

#### FR-ASSET-005: Custom assets

- **Priority: P1.** This requirement does not block P0/MVP.
- Import creates a canonical asset with provenance.
- In P1, every custom asset initially belongs to one project and is copied into that
  project's `assets/` directory by Asset ID.
- Project custom assets are stored by ID and deduplicated within that project.
- External source paths MAY be remembered for reimport, but an attached project
  must bake without that arbitrary path.
- A global custom-asset repository mirroring the vanilla asset catalog is Future;
  P1 project files and references MUST remain migratable to it.

#### FR-ASSET-006: Project-isolated asset edits

- Imported assets use copy-on-write. The first content edit creates a derived
  asset in the project's `assets/` directory and records its parent Asset ID.
- The project is redirected to the derived Asset ID; the global asset and every
  other project remain unchanged.
- Entity-only edits such as position or rotation do not copy the referenced
  model, material, or texture.
- By default, editing a shared resource affects all references to that resource
  within the current project. An explicit **Make unique** action creates an
  override for only the selected reference when that distinction is supported.
- Undoing the first edit restores the global reference and makes the unreferenced
  project asset eligible for safe cleanup.
- Saving, sharing, and baking a project MUST resolve entirely from its global
  Asset IDs plus attached project assets; it must not depend on mutable data from
  another project.

### 7.3 Content layers

Initial logical layers are:

- project/world metadata;
- sky;
- tfrags;
- ties;
- shrubs;
- mobys;
- gameplay entities and volumes;
- collision;
- lighting;
- textures/material assignments;
- audio when supported by the target workflow.

Each layer MUST expose a stable ID, schema version, dependencies, current content
fingerprint, last successful bake fingerprint, diagnostics, and bake state
(`clean`, `dirty`, `baking`, or `failed`).

## 8. Functional requirements

### 8.1 Application, setup, and projects

#### FR-APP-001: Desktop shell

Forge MUST be an Electron app with a React renderer, Mantine design system,
Three.js viewport, and bundled self-contained .NET 10 host.

#### FR-APP-002: Platforms

- Linux x64 is the first development/release target.
- Windows x64 is second.
- macOS x64/arm64 is Future and needs signing/notarization plus CI ownership.
- Projects MUST move between supported OSes without path edits.

#### FR-APP-003: Setup wizard

The P0 wizard MUST select the projects directory; locate and verify an NTSC-U UYA
ISO; choose global imports; select a development-ISO directory; estimate import
and copy disk space; show progress; cancel safely; and resume incomplete imports.
It MUST create the UYA development ISO as a distinct copy of the selected clean
source. Later game importers extend the same wizard. All paths remain editable in
Settings.

#### FR-APP-004: ISO records

Source records MUST store game, region, revision, size, fingerprint, and per-user
path. Moved paths are repairable. A clean source ISO can never be redesignated as
a development ISO. Forge MUST reject configurations where clean and development
paths resolve to the same file.

#### FR-APP-005: Global asset import

- Each selected game is imported once into a cache shared by all map projects.
- The importer MUST scan supported assets across every selected map, deduplicate
  identical canonical content, and retain all game/level/source tags.
- At minimum, the catalog model supports ties, shrubs, and mobys; additional
  supported classes use the same identity/provenance rules.
- Re-import MUST be incremental by clean-ISO fingerprint and importer version.
- Removing or moving a clean ISO does not invalidate already imported assets, but
  prevents repair/re-import until the source path is restored.

#### FR-PROJ-001: Project hub

The hub MUST list recent projects with target, base, modified time, and
missing-asset status. Users can create, open, rename, remove from recents, and
reveal projects in the file manager.

#### FR-PROJ-002: Base level

Creation MUST offer a base level from imported supported games and populate the
scene using references to the shared global catalog while retaining provenance.
Resource edits use the project-isolated copy-on-write behavior in FR-ASSET-006;
they never mutate the globally imported source asset.

#### FR-PROJ-003: Target profile

Projects declare target game, required version/region, and bake profile. A user's
development ISO path stays in per-user settings.

#### FR-PROJ-004: Save and recovery

- Dirty state is always visible.
- Manual save has menu and keybinding access.
- Autosave runs after a configurable idle interval and before destructive project
  transitions when possible.
- Crash recovery never silently replaces the last explicit save.
- Recovery history has count and disk-size bounds.

### 8.2 Workspace UI

#### FR-UI-001: Docking

The workspace MUST support docked/tabbed/resized/moved/hidden panels and detached
panels where the chosen library/platform supports it. Layout persists per user
and has **Reset layout**. Use a maintained docking library rather than building
docking from scratch.

#### FR-UI-002: Reusable components

Tree views, property rows, asset pickers, diagnostics, filters, progress displays,
and empty/error states MUST be shared Mantine-based components rather than
lookalike feature-local copies.

#### FR-UI-003: Scene tree

The tree MUST support hierarchy, filter/search, multi-selection, permitted rename
and reparenting, context actions, and states for dirty, hidden, disabled, locked,
invalid, and missing-asset entities.

#### FR-UI-004: Properties

Selection MUST display identity, type, transform, layer, state, asset/provenance,
validation, and type-specific properties. Multi-selection SHOULD expose compatible
shared properties. Edits validate and use commands.

#### FR-UI-005: Status and diagnostics

UI MUST show project dirty state, target, host connection, selection, task
progress, layer bake state, warnings/errors, and detailed-log location. Known
errors offer corrective actions.

#### FR-UI-006: Reference enrichment (Future)

An enrichment or **References** view should explain how selected objects relate to
the rest of the level. It should show incoming and outgoing references, the
referencing entity and field path, the referenced entity, and the decoded meaning;
for example, a moby pvar field referencing a cuboid index. Entries should navigate
to and select either side of the relationship.

This is Future because useful coverage requires versioned knowledge of pvar and
other game-specific structures. Partial decoders MUST disclose coverage and
unknown fields rather than imply that the reference graph is complete. Stable
Entity IDs and typed reference fields in P0 provide the foundation.

### 8.3 Scene and editing

#### FR-SCENE-001: Viewport

The viewport MUST use Three.js and migrate compatible map-o-matic rendering and
package code. It supports perspective navigation, focus selection, configurable
speed, resize/high-DPI, and optional render statistics.

#### FR-SCENE-002: Projection

Project state is authoritative. Render objects are projected, updated, and
disposed from it. Reopen, undo/redo, and bake never depend on hidden Three.js
mutations.

#### FR-SCENE-003: Picking

Clicking casts a ray and selects the nearest eligible entity. Additive/toggle
selection uses keybindings. Locked entities are skipped so the ray may hit an
eligible entity behind them.

#### FR-SCENE-004: Entity states

- **Hidden:** not rendered or pickable in-editor, but still baked.
- **Disabled:** excluded from bake/gameplay output; may be shown in an
  editor-distinct state on request and is not interactive by default.
- **Locked:** normally rendered but not selectable, editable, transformable, or
  reparentable.

The tree and properties control these states. Parent state has a visible,
predictable effective result for descendants.

#### FR-SCENE-005: Tools and gizmos

Transforms, bounds, splines, volumes, lights, spawns, and other aids render in a
dedicated tool/overlay scene. Tool entities remain separate from level entities,
are excluded from bake, and are pickable only by the active tool.

#### FR-SCENE-006: Simple shapes

Tools MAY represent points, lines, boxes, spheres, capsules, planes, paths,
splines, and frustums. Only shapes required by implemented tools need production
support.

#### FR-SCENE-007: Selection synchronization

Tree, viewport, properties, focus, and relevant asset views share one
Entity-ID-based selection. Deleting selection chooses a predictable fallback and
leaves no stale references.

#### FR-SCENE-008: Manipulation modes

The viewport MUST provide explicit select, translate, rotate, and scale modes.
The current mode MUST be visible in a toolbar and switchable through rebindable
hotkeys. Transform gizmos MUST support world and local orientation, a defined
selection pivot, numeric property entry, multi-selection where valid, and
cancel/commit behavior. A completed drag is one undoable editor command.

Scaling MUST be disabled with an explanation for entity types or target formats
that cannot represent it safely.

#### FR-SCENE-009: Transform snapping

Snapping MUST be independently configurable for translate, rotate, and scale:

- translation grid/increment;
- rotation angle increment;
- scale increment;
- object reference point: origin/pivot, bounding-box center, or an active/nearest
  mesh vertex for edge snapping;
- target: grid, another object's bounding-box center, another visible mesh vertex,
  or a visible scene surface.

Users MUST be able to toggle snapping, temporarily invert it with a modifier, and
edit the increments without leaving the viewport. Snapped manipulation still uses
the ordinary command/undo path. Hidden and disabled objects are not snap targets;
locked objects MAY be targets even though they cannot be selected or changed.

P0 edge snapping is vertex-to-vertex: it transforms the selected entity so the
chosen source vertex coincides with the chosen target vertex without modifying
either mesh. Vertex lookup MUST use spatial acceleration suitable for interactive
dragging rather than scanning every scene vertex on every pointer event.

#### FR-SCENE-010: Snap to ground

The default **Page Down** command MUST move the selection downward until its
lowest applicable point contacts the nearest visible, enabled level surface or
object immediately beneath it. It MUST:

- ignore selected entities, their descendants, and editor-only overlays as
  targets;
- preserve relative offsets for a multi-selection;
- use one undoable command;
- perform no mutation and show a brief status when no surface is found; and
- remain rebindable through the normal keybinding system.

#### FR-SCENE-011: Complete map representation

The editor viewport MUST represent the selected UYA base level with actual vanilla
geometry and materials rather than permanent proxy boxes. P0 coverage includes the
primary and chunk tfrags, tie instances, shrub instances, renderable moby instances,
and sky. Tie, shrub, and moby render objects retain a reversible mapping to their
stable project Entity IDs so picking and project state use the same authority.

The .NET host uses the pinned SDK to produce versioned render data. The renderer
MUST NOT parse game archives or read the clean ISO. Large immutable render files are
served from an app-owned cache keyed by source fingerprint, level, SDK revision,
and render-package schema, without copying them through JSON or into each project.
Cache writes are atomic and cancellable; a valid cache remains usable if the source
ISO is temporarily unavailable.

Project transforms and entity states override package instance records after base
creation. Renderable assets use their actual meshes; intentional meshless mobys use
a distinct editor-only marker, and missing or failed assets use an explicit
placeholder plus a diagnostic. Failure in one content family MUST NOT prevent other
valid families from rendering.

#### FR-EDIT-001: Undo/redo

- Every project mutation uses an editor command except documented UI-only state.
- Commands retain enough prior state to undo/redo safely.
- Continuous interactions such as gizmo drags coalesce into one entry.
- History updates dirtiness, validation, viewport, tree, and properties.
- P0 retains at most 100 coalesced entries or approximately 128 MiB of command
  history, whichever limit is reached first, evicting the oldest complete entries.
- P0 history is session-local and need not survive restart.

#### FR-EDIT-002: Delete, duplicate, references

Delete/duplicate MUST account for children and inbound references. Destructive
commands summarize affected dependents. Bake never silently emits broken refs.

#### FR-EDIT-003: Clipboard

Copy/paste within Forge SHOULD preserve compatible entity graphs and asset refs
while assigning new Entity IDs. Cross-project paste waits until attached-asset
collection is reliable.

#### FR-EDIT-004: Keybindings

Commands declare defaults and context (`global`, `viewport`, `tree`, `text-input`,
or another explicit context). Users can search, rebind, clear, and reset. Conflicts
are detected before save. Text entry cannot trigger destructive scene commands.

### 8.4 Lighting

#### FR-LIGHT-001: Independent layer

Lighting MUST be a separate layer with editor visibility and enabled state.
Hiding is preview-only; disabling changes bake output and warns if required target
data would be removed.

#### FR-LIGHT-002: Dependencies

Changes to lights, light-affecting geometry/materials, target rules, or bake
settings invalidate relevant lighting outputs. Unrelated changes do not.

#### FR-LIGHT-003: Rebake

Users can rebake lighting alone. It reports progress, cancels at safe boundaries,
keeps the last successful output on failure, and reports clipping/unsupported data.

### 8.5 Baking, translation, and custom content

#### FR-BAKE-001: Staging

Baking writes loose, inspectable target output to staging. Failure/cancellation
does not replace the last successful output. Staging is safely regenerable and
excluded from portable archives by default.

#### FR-BAKE-002: Incremental bake

Each output fingerprints:

- authoritative layer content;
- transitive Asset IDs;
- target game/version;
- translator and baker versions;
- relevant settings;
- declared cross-layer dependencies.

Reuse is allowed only when the full fingerprint matches the successful bake
manifest. Users also get **Rebuild all** and **Clean staging**.

#### FR-BAKE-003: Determinism

Equal supported inputs, versions, and settings MUST produce byte-identical output
unless a format has an unavoidable nondeterministic field. Exceptions are
documented and normalized for comparison where possible.

#### FR-BAKE-004: Validation

Bake stops before patching on invalid/unsafe output. Warnings may continue but are
summarized. Diagnostics identify layer plus Entity ID or Asset ID where possible.

#### FR-BAKE-005: Opaque pass-through content

Content that Forge cannot safely parse or edit MUST be preserved as opaque data.
This includes code overlays and any unsupported WAD entry or substructure.

- Import records the payload bytes, container identity/location, size, and hash in
  the shared source cache or base-level provenance.
- Project operations cannot mutate an opaque payload. They may retain, omit, or
  replace it only through an explicit future capability that understands it.
- Bake/build copies or repacks the payload bytes exactly; container offsets,
  alignment, indexes, and checksums MAY change only as required by the surrounding
  archive format.
- Validation compares the emitted opaque payload hash with its imported source
  hash and blocks patching on a mismatch.
- Adding a parser later requires round-trip fixtures before the content stops
  using opaque pass-through behavior.

#### FR-XLT-001: Translation boundary

Game translation lives in .NET game modules/SDK services, not React or generic
editor state. It consumes canonical data plus source/target context and returns
target data with diagnostics.

#### FR-XLT-002: Known differences

**Priority: P1.** Cross-game asset translation does not block P0/MVP.

Translation MUST explicitly handle known differences such as RC1/GC tie packet
layouts and Deadlocked's tie vertex-color header/padding versus UYA. Unsupported
fields produce diagnostics and are never silently dropped.

#### FR-XLT-003: Capabilities

The handshake/project target exposes import, edit, translation, and bake support
per content type/game. Unsupported UI actions are disabled with an explanation.

#### FR-CUSTOM-001: GLB import

**Priority: P1.** Custom GLB support does not block P0/MVP.

The GLB importer inspects meshes, primitives, vertices, indices, UVs, normals,
vertex colors, materials, textures, transforms, skins, and animations and reports
target limitations before bake.

#### FR-CUSTOM-002: Target conversion

**Priority: P1.**

Supported custom models bake directly to the selected game's PS2 structures with
actionable errors for packet, material, texture, vertex/index, skin, or animation
limits. No separate conversion command is required.

#### FR-CUSTOM-003: Preview parity

**Priority: P1.**

The viewport SHOULD represent canonical content accurately enough to edit while
disclosing meaningful differences from baked PS2 output. A baked-output preview
is optional after the converter is stable.

### 8.6 Texture and palette optimization

#### FR-TEX-001: Inventory

Before bake, Forge MUST decode and inventory every texel/pixel index of every
participating texture. The inventory records texture class, dimensions, indexed
pixel format, current palette, every referenced palette index, resolved color and
alpha value, usage frequency, material usage, and target constraints. Unused
palette entries MUST be distinguishable from colors actually referenced by texels.

#### FR-TEX-002: Shared palettes

The P0 optimizer considers imported vanilla textures only and treats their colors
as fixed exact values. P1 extends the same optimizer with custom weighted color
samples that may reuse sufficiently close fixed colors or create quantized colors.
In either phase, the optimizer combines compatible textures into the minimum
number of PS2 palettes, with no more than 256 entries per palette.

A palette may satisfy any number of textures when their format, alpha, runtime
lifetime, exact imported colors, and permitted custom-color mappings fit the
limit. Imported color values are immutable anchors: custom content may reuse them,
but may never alter, merge, or displace a color required by an imported texel.

After assignment, Forge MUST build each shared palette and rewrite every
participating texture's pixel indices. Imported indices point to the same exact
color; custom indices point to their selected existing or generated quantized
color. The primary objective is the fewest palettes within all loss/error limits;
deterministic tie-breakers SHOULD favor the assignment satisfying the most
textures, reusing the most anchored colors, and then producing the least weighted
color-distance error.

"Minimum" means minimum under the declared target policy. The purpose is to
reduce distinct palette/CLUT data uploaded to and retained in PS2 VRAM, not merely
to deduplicate source files.

#### FR-TEX-006: Imported texture losslessness

Vanilla imported textures are already quantized and MUST NOT be quantized again.
Only colors referenced by their texels, plus required reserved entries, participate
in packing. Color identity uses the exact target packed color/alpha representation.
Palette rearrangement may change indices, but decoding the rewritten texture
through its assigned optimized palette MUST reproduce every original texel exactly.

#### FR-TEX-007: Custom texture quantization

**Priority: P1.** This requirement does not block P0/MVP.

Custom texture quantization MUST participate in shared-palette optimization rather
than run as an isolated preprocessing pass. P1 MUST port and reuse the
`AlphaAwareKMeans` implementation from deadlocked-level-packer's
`DL.Level/helpers/PngQuantizer.cs` as its baseline. That implementation provides
frequency-weighted clustering, premultiplied RGBA distance, an alpha weight of
2.5, a reserved fully transparent entry, deterministic seed `1337`, 12 iterations,
and deterministic palette sorting/index remapping.

Forge MUST extend that baseline for P1 palette-aware optimization:

- identical source RGBA colors are grouped and weighted by texel frequency;
- required transparent/reserved entries are preserved according to UYA rules;
- exact imported colors already assigned to a candidate palette act as fixed
  centroids;
- a custom color within the configured distance threshold of a compatible fixed
  centroid MUST prefer that existing entry instead of adding a near-duplicate;
- custom colors that cannot reuse a fixed entry generate movable centroids only
  in the candidate palette's remaining slots;
- existing deterministic weighted centroid seeding is retained unless tests show
  a repeatable quality or performance improvement from a replacement;
- convergence limits and equal-distance tie-breaking are fixed so every platform
  produces the same result; and
- each texel is rewritten to the nearest resulting target color/index.

Palette assignment and custom centroid selection MUST be evaluated as one overall
strategy across compatible tie, shrub, and moby textures. If no existing palette
can accept a custom texture within capacity and error limits, the optimizer adds
a palette rather than exceeding either limit. A custom texture may therefore reuse
vanilla palette entries even when it has fewer than 256 source colors.

#### FR-TEX-008: Quality versus palette-reuse control

**Priority: P1.** This requirement does not block P0/MVP.

The project bake settings MUST expose a slider controlling custom-texture
quantization and palette reuse. Its endpoints MUST be labeled by outcome rather
than algorithm detail:

- **Visual fidelity:** lower tolerance for mapping custom colors to existing
  entries and greater willingness to allocate new colors or palettes.
- **VRAM savings:** stronger reuse of sufficiently close existing entries and
  greater pressure to reduce the palette count within a bounded error limit.

The slider MUST NOT alter imported texture colors at any value. Custom textures
that exceed 256 required entries still need quantization at the visual-fidelity
end. The slider uses integer values from 0 through 100 and initially defaults to
50. Its first version, `paletteOptimization.v1`, maps strength `s` to the existing
normalized premultiplied-RGBA squared-distance metric using:

```text
reuseThreshold = 0.05 * (s / 100)^2
```

These values are deliberately provisional and MAY be calibrated from test images
without changing the 0-100 project-facing setting; changing the mapping requires a
new mapping version. The slider value and mapping version MUST be stored in the
project bake profile, included in incremental-bake fingerprints, and produce
deterministic output across supported platforms.

The UI SHOULD preview estimated palette count, palette VRAM/upload cost, and
custom-texture quantization error before a full bake. It MUST warn when an
aggressive setting reaches the configured maximum error bound. Changing the slider
dirties only outputs that depend on texture palettes or remapped pixel indices.

#### FR-TEX-003: Reporting

The optimizer never silently loses colors or changes pixel meaning. It reports
palette count, per-texture palette assignment, old-to-new pixel-index mappings,
reused and quantized colors, error metrics, estimated palette VRAM/upload savings,
violations, and whether the result is proven optimal or heuristic.

For custom textures, reports MUST include the pre/post distinct-color count and
quantization error statistics, how many texels/colors reused imported palette
entries, and how many new quantized entries were introduced. For imported
textures, the reported quantization error MUST be zero.

#### FR-TEX-004: Fallback

If an exact solver is impractical, Forge MAY use a deterministic heuristic, but
the UI/manifest identifies it and retains comparison diagnostics. Valid target
output matters more than an unproven optimality claim.

#### FR-TEX-005: Scope

Tie, shrub, and moby texture palettes are explicitly shareable across those asset
classes and MUST be considered together by the optimizer when they coexist in the
same applicable runtime palette/VRAM scope. This applies to imported and custom
textures after any required custom quantization. Tfrag and later texture classes
MAY join only after their loading and lifetime rules are verified.

### 8.7 Build, patch, and test

#### FR-BUILD-001: Pack

Build consumes the last successful bake manifest and packs through the .NET SDK.
It rejects stale/failed inputs unless a supported partial workflow is selected.

#### FR-PATCH-001: Development target

Patching requires the separate development ISO created from the matching clean
source. Forge validates game, region/revision, size, base structure, path, and file
identity before every patch. If it resolves to a clean source file, patching MUST
be rejected with no override.

#### FR-PATCH-002: Patch plan

Before writing, the SDK produces a plan listing affected files/byte ranges,
preconditions, output sizes, free-space needs, and whether the operation is
in-place or requires rebuilding.

#### FR-PATCH-003: Recoverability

- In-place writes journal original affected ranges.
- Writes flush and verify before retiring the journal.
- Failure restores or offers recovery to the prior state.
- Full rebuild writes a sibling temporary image and replaces only after validation.
- Forge never writes to a clean source ISO under any mode or advanced setting.
- Development ISO creation MUST use a real copy or copy-on-write filesystem clone,
  never a hard link that could expose the clean source to writes.

#### FR-PATCH-004: Patch attempt and file-access errors

Forge MUST attempt the validated development-ISO patch without trying to detect,
pause, reload, or coordinate with PCSX2. Linux normally permits the write while
PCSX2 has the image open. If Windows or another platform rejects access, Forge
MUST surface the original file-I/O error with guidance to stop emulation and retry.
A patch is not successful until final verification completes.

#### FR-PATCH-005: One-click loop

After one-time setup, **Bake, Build & Patch** runs validation, incremental bake,
pack, patch, and verification without intermediate file dialogs, then presents a
completion state suitable for reloading in an externally managed emulator.

### 8.8 Settings and updates

#### FR-SET-001: Flat settings schema

User settings MUST have stable flat keys grouped by prefix, for example:

```text
appearance.theme
editor.autosaveSeconds
editor.cameraSpeed
paths.projects
paths.developmentIsos
sources.rc1.iso
targets.uya.developmentIso
updates.channel
```

Values declare type, default, validation, description, and restart needs. Unknown
keys SHOULD survive reads/writes for forward compatibility. Future secrets use OS
credential storage, not this schema.

#### FR-SET-002: Settings UI

Settings are searchable, presentation-grouped without changing flat key identity,
resettable per item/globally, and exportable without machine paths or secrets
unless explicitly requested.

#### FR-UPD-001: Checking

Forge supports manual checks and SHOULD check periodically. It presents version,
channel, notes, download size, and trusted source before installation.

#### FR-UPD-002: Consent

Forge never installs/restarts without explicit acceptance. It warns about unsaved
work and allows deferral.

#### FR-UPD-003: Platform behavior

- Windows and future macOS SHOULD use signed platform-appropriate packages.
- Linux supports discovery, but installation MAY use its package manager or
  package-specific updater because Electron has no built-in Linux auto-updater.
- Delta downloads are Future and require full-package fallback.
- Stable installations MUST never receive nightly updates unless the user
  explicitly changes update channels.

### 8.9 Build and release automation

#### FR-RELENG-001: Continuous integration

GitHub Actions MUST build and test the React/Electron application and .NET host on
pull requests and pushes. The workflow MUST exercise the supported Linux and
Windows release matrices, use locked dependencies, cache only safe dependency
inputs, and upload useful logs when a job fails. Pull requests and non-main
branches do not publish installable releases.

Each job MUST install the exact Ratchet SDK revision through FR-SDK-001 before
restoring/building Forge. No workflow may rely on a preinstalled runner SDK,
NuGet-published Ratchet package, moving branch, or unverified cached checkout.

#### FR-RELENG-002: Nightly channel

Every successful commit pushed to `main` MUST publish Linux and Windows nightly
packages. Each package gets a unique SemVer-compatible prerelease version such as
`2.0.0-nightly.123.a1b2c3d4`, plus the full source commit in its metadata. Nightly
packages MUST be clearly labeled prerelease/unsupported, publish nightly-channel
update metadata, and remain isolated from stable users.

Historical nightly artifacts MAY follow a documented retention limit; the latest
nightly and the artifact for each retained commit must be identifiable without
overwriting another commit's version.

#### FR-RELENG-003: Stable channel

Pushing a manually created tag matching `vMAJOR.MINOR.PATCH` MUST trigger the
stable workflow. It MUST verify that the tag's version matches application/package
metadata, run the complete release checks, build supported platform packages,
produce checksums and provenance, publish a non-prerelease GitHub Release, and
publish stable-channel update metadata. Re-running the same tag MUST not create
different version identities.

#### FR-RELENG-004: Artifact contract

Every published package MUST identify Forge version, channel, source commit,
Ratchet SDK version, bridge protocol version, platform, and architecture. Release
jobs SHOULD generate release notes from commits or a maintained changelog while
allowing a maintainer to edit the final stable notes.

Signing credentials, when introduced, MUST come from protected GitHub environments
or repository secrets and MUST not be available to pull-request workflows.

### 8.10 Future collaboration and plugins

#### FR-COLLAB-001: Foundation (Future)

Stable project/Entity/Asset IDs, serializable commands, and deterministic inputs
are established in P0. Presence, transport, permissions, conflict handling, and
authority are Future.

#### FR-COLLAB-002: Direction (Future)

A future session should support cameras/cursors, remote presence, shared selection
indicators, ordered commands, asset negotiation, reconnect, and conflicts through
a separately developed relay-server project hosted by Horizon. Forge contains the
client boundary, not the relay implementation. Exact authority and conflict rules
belong to that future protocol and have no P0 acceptance gate.

#### FR-PLUGIN-001: Extension boundary (Future)

First-party features SHOULD consume the typed editor runtime so future
capability-based plugins can wrap commands, queries, panels, tools, importers, and
bakers. Raw mutable stores are not a plugin API. Loading third-party code requires
an explicit trust/security model.

## 9. Non-functional requirements

### 9.1 Performance

#### NFR-PERF-001: Viewport

Forge MUST stay interactive on representative largest-supported levels. Release
benchmarks define low-end and recommended hardware and record median/1% low frame
times. Provisional goals are 60 FPS recommended and usable 30 FPS low-end; actual
fixtures define the final gate.

#### NFR-PERF-002: Interaction

Selection, properties, and simple transforms SHOULD respond within one frame when
resources are free. Longer work becomes an async task with feedback.

#### NFR-PERF-003: Rendering

Use culling, instancing, merged primitives, texture reuse, disposal, and LOD when
measurements justify them. React changes MUST not rebuild unrelated geometry.

#### NFR-PERF-004: Isolation

Import, translate, bake, pack, hash, and patch work MUST not block Electron's main
or renderer loops. Background work SHOULD throttle if it harms interaction.

#### NFR-PERF-005: Memory

Avoid unnecessary renderer/main/host copies. Repeated project/level opens release
GPU and host resources. A soak test detects unbounded growth.

### 9.2 Reliability

#### NFR-REL-001: No silent data loss

Save, migrate, bake, build, patch, import, and update failures retain the last
known-good user data and produce actionable diagnostics.

#### NFR-REL-002: Atomic generated state

Generated manifests/output commit only after completion and validation. Temporary
partial output is identifiable and safe to clean at next launch.

#### NFR-REL-003: Cancellation

Long work honors cancellation at safe checkpoints and never marks partial catalog,
project, staging, or ISO state successful.

#### NFR-REL-004: Compatibility

Project schema, asset canonicalization, bake recipes, and bridge protocol are
independently versioned. Failures say what needs upgrade, migration, or reimport.

### 9.3 Security

#### NFR-SEC-001: Electron isolation

The renderer uses context isolation and sandboxing with Node integration disabled.
Preload exposes named validated operations, never raw IPC/filesystem/shell/process.

#### NFR-SEC-002: Local content

The editor loads packaged local content through a secure app protocol with a
restrictive CSP. Navigation, new windows, permissions, and external links are
allowlisted.

#### NFR-SEC-003: Input validation

Projects, GLBs, game archives, protocol frames, updates, and paths are untrusted.
Parsers validate lengths, counts, offsets, integer overflow, nesting, allocation,
and write boundaries.

#### NFR-SEC-004: Updates

Artifacts/metadata use HTTPS and platform/package signatures. Unpresented
downgrades or channel changes are rejected.

#### NFR-SEC-005: Release workflows

GitHub workflows MUST use least-privilege permissions, pin third-party actions to
reviewed immutable revisions, and keep publishing/signing credentials unavailable
to untrusted pull-request code.

### 9.4 Portability and legal boundaries

#### NFR-PORT-001: Portable projects

Shared content is independent of path separators, install path, username, source
and development ISO paths.

#### NFR-PORT-002: User-owned content

Forge neither downloads nor bundles proprietary assets. Imports come from local
user-selected sources. Default packaging omits common imported content and
includes references plus explicitly attached custom assets.

### 9.5 Maintainability

#### NFR-MAINT-001: Dependency direction

Parsing/transformation belongs in `RatchetPs2.Core`, `RatchetPs2.Sdk`, or
`RatchetPs2.Games.*`; desktop orchestration in the Forge host; lifecycle in
Electron; presentation in React/Three. Reverse dependencies are prohibited.

#### NFR-MAINT-002: Cohesion and file size

Classes/modules SHOULD be focused and ideally under 500 logical lines. Larger
files require review for separable responsibilities, but line count must not force
one-use abstractions or fragment cohesive code.

#### NFR-MAINT-003: Dependencies

Prefer platform, standard library, existing SDK, Mantine, React, and Three.js
before adding packages. Runtime dependencies need a clear use/owner, compatible
license, active maintenance, and lockfile entry.

#### NFR-MAINT-004: Diagnostics

Logs include timestamp, component, severity, operation/request ID, and stable code
where applicable. Support bundles preview/redact paths/source metadata. Any future
telemetry is opt-in.

### 9.6 Accessibility and usability

#### NFR-UX-001: Keyboard and focus

Menus, tabs, tree, properties, dialogs, and commands are keyboard accessible with
visible focus. Text-input context suppresses scene shortcuts.

#### NFR-UX-002: Displays

UI remains usable with OS scaling, high DPI, and reasonable text scaling. Color
is never the sole state/error indicator.

#### NFR-UX-003: Destructive actions

Delete, clean, overwrite, detach, patch, and migration state their scope and
recovery. Routine undoable edits avoid confirmation fatigue.

## 10. State, save, and bake model

```text
User input
   │
   ▼
Editor command ──validate──► authoritative project state
   │                              │
   ├─ undo record                 ├─ autosave/manual save
   ├─ selection/UI events         └─ affected layer fingerprints
   │                                      │
   ▼                                      ▼
Three.js projection                 incremental bake graph
                                             │
                                             ▼
                                      staging manifest
                                             │
                                      build and validate
                                             │
                                      recoverable ISO patch
```

Invariants:

1. Saved, baked, built, and patched are distinct states.
2. UI/Three.js state is never the only copy of a mutation.
3. Dirtiness derives from fingerprints, not manually cleared flags alone.
4. A failed newer bake does not invalidate last successful artifacts.
5. Undoing to a previous fingerprint may make a layer clean again.
6. A bake may use a snapshot while editing continues, but records its snapshot
   and remains dirty when later edits exist.

## 11. Verification strategy

### 11.1 Automated checks

- Binary bridge golden frames, fragmentation, malformed lengths, cancellation,
  version mismatch, host crash, and backpressure.
- SDK bootstrap tests for pinned revision verification, local-source override,
  missing dependency, and packaged host dependency completeness.
- Project round-trip and migrations for every supported schema.
- Stable Asset ID fixtures and canonicalizer version changes.
- Catalog deduplication and tag/provenance queries.
- Command apply/undo/redo, coalescing, references, and layer fingerprints.
- Selection synchronization and hidden/disabled/locked picking.
- Translate/rotate/scale commands, bounding-center and vertex-to-vertex snapping,
  snapping increments, multi-selection, and Page Down surface snapping with
  undo/redo.
- Deterministic bake fixtures per supported game/layer.
- Opaque code-overlay and unsupported-entry fixtures proving payload hashes remain
  byte-identical after build/repack.
- Incremental invalidation for every cross-layer dependency.
- **P1:** cross-game fixtures for each known format difference.
- **P1:** GLB boundaries and target-limit errors.
- Full texel/index inventory, cross-class palette validity, byte-exact vanilla
  decode after index remapping, and small known palette-packing optima.
- **P1:** deterministic extended `AlphaAwareKMeans` output, reuse of sufficiently
  close vanilla entries, deterministic slider behavior, and quantization-error
  bounds.
- **P1:** golden fixtures proving the SDK port matches deadlocked-level-packer's existing
  quantizer before Forge-specific fixed-centroid extensions are enabled.
- WAD/ISO patch plans, journal recovery, wrong-ISO rejection, disk-full behavior,
  and final verification.
- Linux/Windows packaged smoke tests starting the host and loading a project.
- GitHub workflow tests for nightly version generation, stable-tag validation,
  channel isolation, artifact metadata, and non-publishing pull requests.

### 11.2 Manual release checks

- First-run/import on a clean account.
- Docking, shortcut conflicts, keyboard-only use, and high DPI.
- Large-level profiling and memory soak.
- Edit/bake/build/patch followed by manual reload in an externally managed PCSX2.
- Interrupted import, bake, patch, and update.
- Project zip transfer between Linux and Windows with asset re-resolution.

## 12. Milestones

### M0: Decisions and walking skeleton

- Resolve blocking decisions in section 14.
- Bootstrap and verify the pinned Ratchet SDK without NuGet or a submodule.
- Electron starts the self-contained host and completes a binary handshake.
- Audit map-o-matic's renderer and migrate only the reusable scene/package code.
- Benchmark Three.js `WebGLRenderer` against `WebGPURenderer` on the same
  representative light and heavy UYA scenes, recording visual correctness,
  compatibility, startup, median/1% low frame time, CPU/GPU cost, and memory.
- Select one P0 renderer backend from evidence rather than assuming WebGPU wins;
  do not maintain two production backends unless the results require it.
- Packaged Linux app opens the selected viewport backend.
- One request supports progress, cancellation, and forced-host-crash recovery.
- Project schema and Asset ID canonicalization get version-zero fixtures.
- GitHub Actions builds/tests Linux and Windows on pull requests and pushes.

Exit: the architecture works in a packaged Linux app, not only a dev server, and
the renderer decision is backed by a reproducible benchmark report.

### M1: Project foundation

- Wizard/settings and source ISO records.
- One global UYA import across all maps, initially covering reusable ties, shrubs,
  and mobys with cross-level deduplication and tags.
- Separate UYA development ISO creation in the selected directory.
- Project hub/create/base/load/save/autosave/recovery.
- Shared asset store, tags, missing assets, portable paths.

Exit: create from a base, close, move, reopen, and render without copying common
assets into the project.

### M2: Editor core

- Docked workspace, tree, properties, diagnostics.
- Entity IDs and synchronized selection.
- Cached host-generated render packages and a complete UYA map viewport with
  tfrags, ties, shrubs, mobys, and sky.
- Picking, camera, transforms, tools, and entity states.
- Translate/rotate/scale modes, snapping controls, and Page Down ground snapping.
- Commands, undo/redo, keybindings, delete/duplicate, and layer dirtiness.

Exit: safely edit/recover a representative scene without raw-file changes.

### M3: Incremental bake

- Dependencies, fingerprints, staging, deterministic manifests, validation.
- UYA NTSC-U map WAD output with the selected initial content coverage.
- Lighting layer and isolated rebake.
- Byte-exact opaque pass-through for code overlays and unsupported entries.
- Required canonical-to-UYA bake translations for supported P0 content.

Exit: changing one entity rebakes only required outputs; clean rebuild is
byte-equivalent.

### M4: Build and rapid test loop

- Archive build from successful staging.
- ISO validation, patch plan, journaling/recovery, verification.
- One-click **Bake, Build & Patch**.
- Clear success/file-access failure behavior for an externally managed emulator.

Exit: repeated edits require no manual WAD/ISO management and interrupted patches
are recoverable.

### M5: Vanilla texture optimization

- Inventory every referenced color/index in supported vanilla textures.
- Losslessly share palettes across compatible UYA tie, shrub, and moby textures.
- Deterministic index remapping, palette reports, and VRAM/upload estimates.

Exit: optimized vanilla textures decode byte-for-byte to their original colors,
use no more palettes than the selected deterministic strategy requires, and pass
the complete bake/build/patch workflow.

### M6: Windows and distribution

- Windows packaged build and smoke tests.
- Per-`main`-commit nightly publishing with generated prerelease versions.
- Stable GitHub Releases from manual `vMAJOR.MINOR.PATCH` tags.
- Signed release/update publishing and Linux update discovery.
- Performance baselines, accessibility pass, support bundle, user docs.

Exit: Linux and Windows satisfy the same project compatibility and edit-to-test
contract.

### P1: Custom asset pipeline

- GLB inspection, canonical import, project attachment, and initial PS2 model
  conversion.
- Port deadlocked-level-packer's `AlphaAwareKMeans` into the Ratchet SDK with
  license attribution and golden compatibility fixtures.
- Add fixed vanilla centroids, custom/vanilla palette sharing, quantization
  reports, and the project-level fidelity/VRAM slider.
- Add custom baked-output validation and preview-parity diagnostics.

Exit: a constrained documented GLB imports, bakes, patches, and appears in-game
with every conversion compromise reported.

### Future

- Broader four-game/content coverage after the first target proves the pipeline.
- macOS packaging, signing, notarization, and host builds.
- Separate Horizon relay server plus Forge collaboration client, authority,
  presence, permissions, and conflict resolution.
- Global reusable custom-asset repository.
- Capability-secured third-party plugins.
- Delta updates when measurements justify the infrastructure.

## 13. Tracking rules

1. Every implementation issue names requirement IDs.
2. A requirement becomes **verified** only with an automated check or linked
   manual verification.
3. Bugs link to the violated requirement.
4. Schema/protocol/canonicalizer changes include migration/compatibility notes.
5. New layers declare dependencies and incremental-bake tests.
6. Milestones close only when exit gates pass in a packaged app.
7. Future requirements do not block P0 unless explicitly promoted.

Suggested issue states: `proposed`, `accepted`, `in-progress`, `verified`,
`deferred`, and `rejected`. This spec records behavior; the issue tracker records
implementation status.

## 14. Decisions and open clarifications

### Resolved decisions

#### DR-001: First writable target

The first complete authoring, bake, build, and patch target is an NTSC-U Up Your
Arsenal map WAD. Global importing may support other games before they become
writable targets.

#### DR-002: Global assets and project isolation

Forge imports supported reusable assets, initially including ties, shrubs, and
mobys, once into a cache shared by all map projects. These immutable imports are
the source of truth. Base maps reference them by Asset ID, and the first resource
edit creates a project-local derived asset so no other project changes.

#### DR-003: Clean and development ISOs

Clean source ISOs and development ISOs are always separate. The setup wizard asks
for a clean source ISO and a destination directory, then creates the development
copy. Forge never writes to a clean source and provides no override.

#### DR-004: Disc variants

P0 targets NTSC-U. PAL is a possible later build target for UYA only and is not
required by P0. NTSC-J and other disc variants are not planned.

#### DR-005: P1 custom assets

Custom assets are P1 and are unique to and fully stored in a project's `assets/`
directory. A future global custom-asset repository may expose them by Asset ID in
the same way as vanilla assets.

#### DR-006: Disabled entities

Disabled means excluded from bake into staging. It is not an in-game disabled
flag. Hidden and locked remain editor-only states with the behavior in
FR-SCENE-004.

#### DR-007: PCSX2 boundary

PCSX2 is configured and controlled outside Forge. Forge attempts the ISO patch
without emulator coordination. Normal file-I/O errors, including Windows sharing
violations, are reported for the user to resolve.

#### DR-008: Collaboration service boundary

Network collaboration is Future and uses a separately developed relay-server
repository hosted by Horizon. P0 Forge provides stable IDs, serializable commands,
and a client-friendly boundary but no relay or network collaboration behavior.

#### DR-009: Center and edge snapping

Center snapping uses object bounding-box centers. Edge snapping in P0 is
vertex-to-vertex mesh snapping rather than bounding-box-edge snapping.

#### DR-010: Nightly publication scope

Every successful `main` commit publishes a uniquely versioned nightly. Branch and
pull-request commits build and test but do not publish nightly packages.

#### DR-011: Palette loss and custom quantization

Imported vanilla textures are merged losslessly by rearranging exact colors into
shared palettes of at most 256 entries and remapping texture indices in P0. P1
custom textures participate in the same optimization and use deterministic,
frequency-weighted, alpha-aware K-means based on deadlocked-level-packer's existing
implementation. Imported colors are fixed centroids: custom colors within the
approved distance threshold reuse those entries, while colors that cannot do so
create quantized entries in remaining palette slots.

#### DR-012: User-controlled optimization strength

In P1, the project bake profile exposes a user-configurable slider between visual
fidelity and palette reuse/VRAM savings. It affects only custom-texture error and
palette reuse; imported textures remain exact. Its value is portable project data,
not a machine-local user preference. P1 initially uses integer range 0-100, default 50, and
the versioned provisional mapping in FR-TEX-008.

#### DR-013: Quantizer implementation source

For P1, Forge ports the MIT-licensed `AlphaAwareKMeans` implementation from
deadlocked-level-packer into the Ratchet .NET SDK rather than creating a second
quantizer. The port keeps golden compatibility fixtures and the original license
notice, then adds fixed vanilla centroids, configurable reuse distance, and
cross-texture palette assignment.

#### DR-014: Unsupported content preservation

P0 preserves unsupported content, including code overlays, as opaque byte-exact
payloads. Forge may update the surrounding container's offsets, alignment,
indexes, and checksums but does not interpret or regenerate the payload.

#### DR-015: Ratchet SDK dependency delivery

Forge does not use NuGet or a Git submodule for the Ratchet SDK. A checked-in
version file and bootstrap script install/build the pinned ratchet-ps2-cli revision
for local development and GitHub Actions; published Forge packages bundle the
compiled dependencies.

#### DR-016: P0 and MVP scope

P0 and MVP mean the same priority/release. It proves the end-to-end editor using
vanilla NTSC-U UYA assets only. Custom GLBs and custom texture quantization are P1.

#### DR-017: P0 undo bounds

P0 uses a fixed 100-entry and approximately 128 MiB session-history limit,
evicting the oldest complete commands when either limit is reached. Configuration
can be added later if real projects demonstrate a need.

### Open decisions

#### OD-005: Palette constraints

Before P1 custom-texture work, define UYA reserved-entry and alpha rules, supported
indexed pixel formats, runtime palette-loading/lifetime scope, and the hard maximum
acceptable custom-texture error. These do not block P0's lossless vanilla palette
packing. The provisional slider range, default, and version-one curve are resolved
and may be calibrated later under a new mapping version.

#### OD-013: Viewport backend

WebGL versus WebGPU is intentionally unresolved until the M0 benchmark. P0 should
ship the faster and more compatible measured backend without maintaining a
second renderer speculatively.

## 15. References

- Electron security checklist:
  <https://www.electronjs.org/docs/latest/tutorial/security>
- Electron context isolation:
  <https://www.electronjs.org/docs/latest/tutorial/context-isolation>
- Electron built-in updater platform support:
  <https://www.electronjs.org/docs/latest/api/auto-updater>
- Electron application updates:
  <https://www.electronjs.org/docs/latest/tutorial/updates>
- Three.js WebGPU renderer and WebGL 2 fallback:
  <https://threejs.org/docs/pages/WebGPURenderer.html>
- Ratchet SDK architecture:
  `../ratchet-ps2-cli/docs/ARCHITECTURE.md` in the current development workspace.
- Existing alpha-aware K-means implementation to port and extend:
  `../deadlocked-level-packer/DL.Level/helpers/PngQuantizer.cs` (introduced in
  commit `694b0f8`, MIT licensed).
