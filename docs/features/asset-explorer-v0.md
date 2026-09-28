# Asset Explorer v0

Status: In progress

Milestone: M2 — Editor core

Tasks: M2-021 through M2-025

Primary requirements: FR-ASSET-003, FR-ASSET-004, FR-UI-001, FR-UI-002,
FR-UI-007, FR-SCENE-002, FR-SCENE-007, FR-EDIT-001, NFR-PERF-002 through
NFR-PERF-005, NFR-SEC-003

## Outcome

The Asset Explorer is a dockable catalog browser for imported vanilla assets. It
can browse assets from every installed game catalog while an open project remains
bound to one target game. Users can search and filter lightweight metadata, lazily
preview visible results, inspect one asset interactively, and drag a compatible
tie, shrub, or moby into the current scene as one undoable project mutation.

The browse identity is a target-specific class family, not an exact content hash.
One family card may contain several immutable Asset ID variants from different
source levels. The exact hashes remain available for source-faithful preview,
placement, bake, repair, and cache validation.

Here, **import into the scene** means creating a project entity that references an
immutable global Asset ID. It does not copy or mutate the catalog blob.

## Scope

| Category | Browse | Interactive preview | Drop into scene |
| --- | --- | --- | --- |
| Ties | Yes | Yes | Yes, when compatible with the project target |
| Shrubs | Yes | Yes | Yes, when compatible with the project target |
| Mobys | Yes | Yes | Yes, with neutral instance data and stubbed Pvars |
| Sky shells | Yes | Yes, isolated or with the owning sky | No |
| Textures | Yes | Yes, with transparency controls | No |

Cross-game browsing does not imply cross-game conversion. Assets from another game
remain visible and inspectable, but scene placement is disabled unless the current
target adapter explicitly supports them. Sky shells and textures are preview-only
until dedicated sky-composition and material-assignment commands exist.

Custom/project-local assets, remote catalogs, asset editing, favorites,
collections, and cross-project drag-and-drop are outside v0.

## Existing foundation and required gaps

Forge already has a content-addressed catalog with Asset IDs, aliases, tags, source
appearances, game and level filters, and immutable blobs. The UYA importer currently
catalogs tie, shrub, and moby model bundles. Project creation catalogs the whole
base sky payload, while model textures remain embedded in canonical model bundles.

The explorer requires:

- cursor-based, cancellable metadata queries instead of one result capped at 1,000;
- text search over common name, aliases, class IDs, tags, Asset ID, game, and level;
- filter facets for kind, game, level, tags, region, and revision;
- standalone normalized texture entries from M1-009;
- an SDK-produced sky-shell index over existing `AssetKind.Sky` payloads; shells
  remain subresources of their owning sky until independent shell editing exists;
- lazy preview-package and thumbnail caching keyed by immutable source identity;
- one editor command that validates an Asset ID and creates a target-native entity
  plus required supplemental instance data.

### Class-family finding

An all-level UYA catalog showed 7,224 class-backed entries but only 3,453 distinct
kind/class families. Of 974 families with more than one exact Asset ID, 965 retained
identical normalized definitions and model bytes; most differences were small
level-specific palette/index quantization changes. For example, `moby:0x000B` had
32 exact variants with identical geometry and alpha while its worst texture pair
averaged 1.68 RGB values apart on a 0–255 scale.

This does not weaken Asset ID semantics. Asset IDs remain exact hashes of canonical
bytes. Fuzzy hash reuse would be lossy, import-order dependent, and unable to
reconstruct the source level. The explorer instead groups exact variants under a
family key of target game, region/revision, asset kind, and class ID. A project-level
source match is the default representative; other exact variants remain inspectable.

Visual-equivalence analysis is presentation metadata only. A future calibrated
classifier may mark variants as quantization-equivalent when definition/model bytes,
texture topology, and alpha match and every decoded texture stays below retained RGB
error gates. It must never replace an exact Asset ID or discard its blob.

No generic metadata bag is added to the project format. Supplemental data continues
to use typed records such as `ProjectEntitySource` and `ProjectTieLighting`.

## Dock interaction

The panel is registered with Dockview, defaults to the bottom workspace region, can
be hidden or floated in-window, and restores through the existing layout setting.

```text
┌ Asset Explorer ──────────────────────────────────────────────────────┐
│ [Ties] [Shrubs] [Mobys] [Sky shells] [Textures]                     │
│ [ Search assets…                         ] [Filters 3] [Clear]       │
│ Game: UYA ×   Level: 03 ×   Tags: vanilla ×                         │
│                                                                     │
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐                 │
│ │ preview  │ │ preview  │ │ preview  │ │ preview  │   virtual grid │
│ │ name/id  │ │ name/id  │ │ name/id  │ │ name/id  │                 │
│ └──────────┘ └──────────┘ └──────────┘ └──────────┘                 │
│                         Loading more…                               │
└─────────────────────────────────────────────────────────────────────┘

Selecting a card opens the Asset Preview dock grouped with Properties:
┌ interactive orbit preview ┐  name, Asset ID, kind, class aliases,
│ rotate / zoom / reset     │  tags, games, levels, revisions,
└───────────────────────────┘  source appearances and compatibility
```

The preview is an in-window Dockview panel, not a modal or second Electron window.
This preserves the deny-by-default window policy and avoids an independent renderer
lifecycle.

### Search and filters

- Exactly one top-level category is active at a time.
- Search is case-insensitive, debounced, cancellable, and combined with filters.
- Selected tag filters use all-tags semantics, matching the existing catalog.
- Game, level, region, and revision values come from catalog appearances rather
  than hard-coded UI lists.
- Filter state is session UI state and does not dirty the project.
- Results have deterministic ordering and an opaque continuation cursor. Infinite
  scrolling consumes that cursor automatically; there are no page-number or
  previous/next controls. A catalog change invalidates the cursor and refreshes the
  query.
- One target-specific kind/class family is one card even when several source levels
  carry distinct exact Asset IDs. Loaded cursor batches merge into the existing
  family without duplicating cards.
- The current project level's exact variant is preferred for preview and placement.
  Other exact variants and their source appearances remain inspectable in the
  preview dock. If one Asset ID has several class identities, the selected family
  supplies the unambiguous target-game identity.

Each card identifies its category in text, not color alone, and reports missing
blobs or preview failures without removing the catalog entry. Keyboard navigation,
focus, activation, filter removal, and placement remain usable without a pointer.

## Query contract

The renderer receives metadata only; it never receives arbitrary filesystem paths
or reads catalog JSON directly.

A game-neutral query contains one explorer category, optional search text, game,
level, region, revision and tag filters, an opaque cursor, and a bounded batch size
(default 64, maximum 128). Each result contains the Asset ID, category, display
label, aliases, tags, appearances, canonical version, byte size, preview state, and
target compatibility. Sky-shell results also contain their parent sky Asset ID and
shell index.

Exact results are ordered class-first so a client can coalesce adjacent variants
across cursor boundaries without weakening catalog pagination or Asset ID identity.
Pages avoid splitting a class family when the complete family fits the requested
batch bound.
The host validates every field and cursor. Search/filter work runs off Electron's
main and renderer loops. The initial implementation may scan the already in-memory
catalog; add a durable index only if retained profiles miss the interaction gate.

## Preview architecture

Grid cards use cached raster thumbnails. They do not create one WebGL renderer per
card.

1. The grid virtualizes card layout and observes actual visible cards.
2. Only an intersecting card may request its thumbnail. DOM overscan does not start
   preview work.
3. A bounded queue permits at most four thumbnail jobs and cancels work for cards
   that leave the viewport before conversion starts.
4. A cache hit returns through the local `forge-package:` boundary. A cache miss
   asks the host/SDK for a validated preview package, then one shared renderer
   produces the thumbnail.
5. Cache keys include Asset ID, optional subresource, SDK revision, preview schema,
   and view preset. Writes validate before atomic replacement.
6. Interactive inspection uses one canvas and the existing model/sky/material
   loaders, framing, resource tracking, and disposal rules.

Model previews use a neutral, fog-free environment, bounds framing, orbit/zoom,
reset view, and visible loading/error states. Texture previews expose actual
dimensions, format/role metadata, nearest-neighbor zoom, alpha checkerboard, and
channel toggles. Sky-shell previews preserve source blend and rotation metadata.

Preview packages and blobs stay behind validated app protocols; large model data
is not copied through JSON or ordinary renderer IPC payloads.

## Infinite scrolling and performance gates

- The renderer holds only loaded metadata batches and a bounded thumbnail LRU.
- Grid DOM nodes remain proportional to visible rows plus layout overscan.
- Reaching the final loaded row requests the next cursor batch automatically;
  identical requests coalesce and no manual pagination controls are shown.
- Changing category, search, or filters aborts stale page and preview requests.
- Scrolling never waits for previews; unloaded cards show a stable labeled placeholder.
- At most one interactive preview and four thumbnail jobs own decoded resources.
- A large synthetic catalog verifies bounded DOM and concurrency, no off-screen
  preview requests, deterministic cursor traversal, and stable resources after
  repeated use.

## Scene placement

Only tie, shrub, and moby cards advertise draggable placement in v0. Drag data is
an internal opaque Asset ID/category/class-identity token. The host treats it as
untrusted and re-resolves the catalog entry, blob, project target, and identity.

The viewport shows a non-authoritative ghost at the raycast surface hit. If no
surface is hit, project Z=0 is the fallback. Dropping issues one
`CreateEntityFromAsset` editor command which:

1. validates compatibility and resolves canonical data;
2. asks the target-game placement service for the entity and supplemental records;
3. commits nothing if required data generation fails;
4. adds and selects the entity through the normal project/scene path; and
5. creates one undoable history entry whose redo preserves IDs and generated data.

Placement uses identity rotation and scale at the ghost position. The new entity
references the global Asset ID and enters the existing static-layer writer.

The implementation starts as a concrete UYA service under `Forge.Host/Games/UYA`.
The bridge request/result remain game-neutral and dispatch from project target
metadata. Do not add a one-implementation interface or generic supplemental-data
framework; extract a shared contract only after another game proves common behavior.

## UYA neutral instance profiles

The UYA placement service creates fresh records from verified constants and
canonical metadata rather than cloning an unrelated instance.

### Ties

A new tie needs more than its 0x60 instance record:

- class ID, identity transform, conservative draw distance, and unique UID;
- neutral directional-light selection;
- per-instance ambient data sized for the selected tie class;
- no source tie-group membership; and
- a verified always-visible or neutral occlusion mapping that does not borrow an
  unrelated source tie's visibility.

Preliminary format evidence shows that the tie model header's `AmbientSize` is the
authoritative byte count. Ambient indices and remap recipes exported from the model
must fit that count; size must not be guessed from render vertex count. The neutral
packed-color candidate is:

- word 0: base red/green = 128/128 (`0x8080`);
- word 1: base blue = 128 with shift 0 (`0x0080`); and
- remaining words: zero deltas.

This decodes to neutral RGB 128 in Map-o-Matic's UYA decoder. Before becoming bake
authority, an SDK fixture must parse representative tie models, validate every
ambient index/recipe, round-trip the payload, and record a PCSX2 check. Zero-length
ambient classes remain valid.

Forge assigns both tie ID fields from the first unused positive ID, maps inserted
ties to the first unused PVS bit, and marks that bit in every existing visibility
mask. The SDK fixture verifies the mapping and mask rewrite. This is insertion
support, not full occlusion regeneration, which remains deferred in M2-019.

### Shrubs

A new shrub uses a fresh 0x70 template with class ID, identity transform,
conservative draw distance, zeroed unknowns, a verified neutral directional
selector, and neutral RGB96 shade values. An SDK fixture confirms the shade
constant and native re-read. No Pvar or per-instance ambient stream is required.

### Mobys

A new moby uses a fresh 0x88 template with size, class ID, identity transform,
unique UID, no mission/group, neutral color/light fields, and verified default
draw/update/occlusion values. Pvar creation is deliberately stubbed as index `-1`.
Properties and diagnostics disclose **No generated Pvar data** because some classes
may render but cannot behave correctly without class-specific Pvars. This warning
does not prevent initial placement or saving.

### Sky shells and textures

These categories create no scene entity and need no supplemental instance data.
Their cards omit the scene-drag affordance and explain that replacement or
assignment requires a future dedicated command.

## Failure and consistency behavior

- Missing/corrupt blobs keep metadata visible and offer existing repair when possible.
- Incompatible or ambiguous assets cannot begin a valid scene drop.
- Preview failure does not block placement when canonical data remains valid.
- Placement validates canonical data again; a thumbnail is not proof of compatibility.
- Catalog refreshes do not mutate the project.
- Saving, undo/redo, recovery, bake, and reopen preserve generated data and Asset IDs.

## Implementation tasks

### M2-021 — Add cursor-backed Asset Explorer catalog queries

Requirements: FR-ASSET-003, FR-ASSET-004, FR-UI-007, NFR-PERF-002,
NFR-PERF-004, NFR-SEC-003

Depends on: M1-003, M1-004, M2-001, M5-001

Expose bounded, cancellable catalog result batches and facets for ties, shrubs,
mobys, sky shells, and textures. Search common names, aliases, class IDs, tags,
Asset IDs, and source appearances without sending the complete catalog to the
renderer.

Acceptance:

- Queries combine one category with search, tag, game, level, region, and revision
  filters and return deterministic batches with an opaque continuation cursor.
- Batch size defaults to 64 and cannot exceed 128; a catalog revision change rejects
  stale cursors and causes a clean refresh.
- Results disclose every class identity/source appearance and return target
  compatibility plus a placement-disabled reason.
- Standalone textures use M1-009's normalized Asset IDs. Sky shells are indexed as
  preview-only subresources of existing sky assets rather than gaining a second
  bake owner.
- Query fields, cursors, counts, and catalog records are validated at the host
  boundary; work does not block Electron loops.

Verification: search/filter combinations, multi-appearance/ambiguous-class fixtures,
cursor invalidation, malformed requests, cancellation, and a large synthetic catalog.

### M2-022 — Build lazy asset preview and thumbnail infrastructure

Requirements: FR-UI-007, FR-SCENE-001, FR-SCENE-002, NFR-PERF-003 through
NFR-PERF-005, NFR-REL-002

Depends on: M2-003, M2-012, M2-013, M2-021

Generate target-aware preview packages through the host/SDK, render grid thumbnails
through one bounded shared path, and reuse the scene resource lifecycle for one
interactive preview.

Acceptance:

- Grid cards use raster thumbnails and never allocate one WebGL renderer per card.
- Only actually visible cards request previews; at most four thumbnail jobs and one
  interactive preview own decoded resources concurrently.
- Cache keys include Asset ID, subresource, SDK revision, preview schema, and view
  preset; writes validate before atomic replacement.
- Model previews orbit, zoom, reset, auto-frame, and render without fog. Texture and
  sky-shell previews preserve relevant alpha, blend, and rotation metadata.
- Switching filters/assets or closing the panel cancels stale work and returns GPU,
  URL, listener, and host resource counts to baseline.

Verification: off-screen request assertions, cancellation races, corrupt assets,
cache invalidation, repeated-open soak, and representative visual screenshots.

### M2-023 — Implement the Asset Explorer dock

Requirements: FR-UI-001, FR-UI-002, FR-UI-007, NFR-UX-001, NFR-UX-002

Depends on: M2-002, M2-009, M2-021, M2-022

Add the Dockview panel, type selector, search/filter controls, virtual grid,
cursor-backed infinite scrolling, and interactive detail surface.

Acceptance:

- The panel docks, hides, floats in-window, restores, and resets with the workspace.
- Category selection and filters are keyboard accessible and do not dirty the
  project; result cards remain understandable without color.
- The grid keeps DOM work bounded and automatically requests the next result batch
  near its loaded end, without page-number or previous/next controls.
- Stable placeholders and errors never block scrolling.
- Detail metadata includes Asset ID, aliases/class identities, tags, all source
  games/levels/revisions, compatibility, and missing/repair state.
- The grid renders one card per target-specific class family. It shows the exact
  variant count, prefers the current base level's variant, and exposes other exact
  source variants in the Properties-grouped Asset Preview dock.
- Cross-game and preview-only assets remain inspectable while their scene-drag
  affordance is absent or disabled with a reason.

Verification: layout round trip, keyboard/focus checks, query-state tests, virtual
grid profile, infinite-scroll boundary tests, empty/missing/error states, and
narrow/high-DPI layouts.

### M2-024 — Place catalog assets with target-native defaults

Status: 🚧 In progress

Requirements: FR-ASSET-004, FR-UI-007, FR-SCENE-002, FR-SCENE-007,
FR-EDIT-001, NFR-REL-001, NFR-SEC-003

Depends on: M2-007, M2-008, M2-023, M3-004, M5-003

Add one game-neutral `CreateEntityFromAsset` command and a concrete UYA placement
service that creates tie, shrub, and moby entities plus every required supplemental
record. Scene drops use the viewport surface hit or Z=0 fallback and commit atomically.

Acceptance:

- The host re-resolves and validates the opaque drag token, catalog blob, target,
  kind, canonical version, and selected class identity before mutation.
- A successful drop adds and selects one entity; undo/redo preserves its Entity ID,
  generated UID, source template, supplemental data, and Asset ID.
- UYA ties receive model-sized neutral ambient data, a neutral directional selector,
  no group membership, and verified neutral visibility data without borrowing an
  unrelated source tie's occlusion.
- UYA shrubs receive fresh neutral native records and shade values. UYA mobys
  receive fresh neutral records with Pvar index `-1` plus a visible diagnostic that
  class-specific Pvar behavior is not generated.
- Sky shells, textures, incompatible games, ambiguous classes, missing blobs, and
  invalid supplemental data cannot mutate the project.
- Created entities survive save/reopen and complete static bake, WAD pack, and
  semantic re-read. One tie, shrub, and safe model-only moby are checked in PCSX2.

Verification: neutral-record SDK fixtures, tie ambient index/recipe coverage,
UID/group/minimal-occlusion fixtures, command/history/recovery tests, malformed drag
payloads, cross-level asset injection, deterministic bake, and recorded in-game checks.

Implementation: the game-neutral drag token, viewport surface/Z=0 ghost, validated
editor command, atomic add/select, stable undo/redo, and concrete UYA tie/shrub/moby
record generators are wired. New ties receive model-sized neutral ambient data and
share a free visibility bit that Forge marks in every existing occlusion mask; native
tie mappings remain unchanged and new ties inherit no source group membership.

Placement finding: retail UYA shrub records use `0.01` in the homogeneous position
component at record offset `0x4c`. Zero-filled placed records caused camera-dependent
transform/culling failures. New records write the retail value, and bake repairs older
Forge-placed shrubs that still contain zero without changing imported source records.
Forge also used a synthetic draw distance of `1024`; the imported level41 shrubs use
retail values of `32` and `80`, while the native level41 class uses `32` or `128`.
New placements now use the retail-safe `128`, and bake repairs the old Forge-only
`1024` template value. The imported model bounding spheres match their source levels.
Retail shrub instance records are also grouped in ascending class-ID order, matching
`shrub_classes.bin` and the asset model table. Forge previously preserved the native
level41 instances first and appended `0x0BCA` then `0x0480`, producing class runs
`0x0C30, 0x0BCA, 0x0480` against an ascending lookup table. Shrub bake now emits
ascending class blocks and remaps shrub-group member indexes to the new positions.

Packing finding: cross-level placement legitimately recomposes the complete static
asset table so palettes and injected classes can be optimized together. The UYA
composer must retain the terrain-owned prefix of the shared texture address space,
then replace its moby/tie/shrub pixels and static models while preserving and
relocating particle/FX and trailing sequence data plus intentional zero-offset moby
definitions. Starting rebuilt static pixels at the shared base corrupts every
retained terrain texture definition. The pack must also regenerate each family's
sorted class-ID list alongside its instance table; retaining level41's one-entry
shrub class list after injecting three classes makes runtime lookups select unrelated
models. The same replacement rule applies to `palette.bin` and the GS-RAM table:
retaining every old static record before appending replacements duplicates uploads and
inflates the palette payload. Forge retains terrain, chrome/glass, and gadget-stash
records at their required destinations, fills unused primary-address gaps with rebuilt
static data, relocates extra pixel blocks, and emits the primary upload table in address
order. Gadget-WAD mobys are a distinct fixed contract: their type-0 texture definitions,
original moby-table IDs, palette destinations, class list, and extra pixel destinations
must survive composition byte-for-byte because the external gadget WAD refers to those
slots. They do not participate in level palette optimization. Moby textures backed by
embedded team palettes must retain their referenced CLUT indexes during optimization;
Forge now pins only those used indexes and rewrites every embedded team palette against
the optimized base palette. Appending recomposed data
after the complete source asset WAD causes
multi-megabyte growth and stale size metadata. The corrected level41 three-shrub
regression packs to 17,874,944 bytes, retains 160 mobys, 72 ties, and 4 shrubs,
round-trips every shrub material and billboard color exactly, and requires a
full-image replacement because its 8,728 sectors exceed the retail 8,574-sector
allocation.

Archive finding: UYA level-data byte blocks start on `0x40` boundaries. Preserving
the source gap length after growing `asset_header.bin` placed the palette, HUD, and
asset WAD at offset remainder `0x12`, causing a fresh-load trap exception. The shared
writer now identifies zero alignment padding, regenerates it after resized payloads,
and retains only genuinely opaque gap bytes. The two-shrub level41 audit rebuilds all
non-empty level-data blocks at remainder zero and stays inside the retail allocation.

### M2-025 — Group exact source variants by class family

Requirements: FR-ASSET-003, FR-UI-007, NFR-REL-001, NFR-UX-001

Depends on: M2-021, M2-022, M2-023

Present one explorer family per target game, region/revision, kind, and class ID
while retaining every exact content-addressed variant. Prefer the open project's
base-level source for preview and keep source provenance visible in the preview dock.

Acceptance:

- Cursor batches merge into stable family cards without duplicate class cards or
  unbounded DOM growth.
- The family card discloses its exact variant count and aggregated source levels.
- Selecting a family previews the exact variant sourced by the current base level
  when available, with a deterministic fallback and an exact-variant selector.
- Exact Asset IDs, blobs, verification, repair, preview cache keys, and bake inputs
  remain unchanged; grouping never drops or rewrites source variants.
- Multi-class Asset IDs form one family per selected class identity rather than
  remaining placement-ambiguous.
- Visual-equivalence thresholds remain optional presentation metadata and cannot
  become fuzzy Asset IDs.

Verification: split-family cursor fixtures, same-class multi-level fixtures,
base-level representative selection, multi-class fixtures, and exact variant
preview switching.

## Delivery plan

1. Add cursor-backed explorer queries and missing texture/sky-shell metadata.
2. Add lazy preview packages, thumbnail caching, and one interactive preview.
3. Add the dock, virtual grid, search/filter UX, metadata, and failure states.
4. Group exact source variants under class-family cards without changing Asset IDs.
5. Add atomic placement and UYA neutral tie/shrub/moby generators, gated by
   ambient, UID, group, and minimal occlusion fixtures.
6. Qualify performance and run save/reopen/bake/pack/PCSX2 placement checks for one
   asset of each placeable type.

## Acceptance summary

- All supported categories can be browsed across installed games and isolated by
  search, tags, game, level, region, and revision.
- Metadata and previews load through infinite scrolling; off-screen cards request
  no previews.
- One selected asset can be orbited and inspected with full provenance and
  compatibility information.
- Compatible tie, shrub, and moby drops create selected, undoable, saveable,
  bakeable entities with deterministic target-native defaults.
- Tie ambient data fits the selected model and decodes to neutral light; inserted
  ties inherit no unrelated ambient or visibility data.
- Cross-game, sky-shell, texture, ambiguous, missing, and malformed cases remain
  visible without silently creating invalid project state.
