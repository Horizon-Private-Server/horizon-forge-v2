# M18A — Occlusion coverage authoring

Status: ✅ Complete — maintainer sign-off (2026-11-06)

Priority: P3  
Depends on: the integrated M18 existing-grid visibility builder and M7 authoring/history foundation  
Task register: [M18A tasks](../tasks/M18A-tasks.md)  
Native evidence: [M18A coverage qualification](../../qualification/M18A-occlusion-coverage.md)

## Outcome

Users can disable the original occlusion coverage, author boxes and cylinders with
optional seed graph filtering, regenerate the map's octants, and bake their visibility
with the established occlusion builder. Coverage generation decides which octants
exist; visibility baking decides what those octants can see.

This milestone adopts manual coverage authoring as a supported workflow for custom
maps. It supersedes the conditional fallback in M18-009. Automatic navigable-collision
coverage remains separate M18-002 work and is not a prerequisite for this workflow.
The broader M18 solver, mapping, analysis, and platform qualification gates remain.

## Scope

- UYA NTSC-U 1.00 is the initial writable and qualification target. Renderer-facing
  authoring contracts remain neutral; verified target constraints come from the SDK.
- Existing projects retain Primary-only coverage and existing build behavior until
  users edit coverage. No-edit source output remains unchanged.
- Generated octants are derived output. Editable objects are the coverage recipe:
  Primary contribution, shape volumes, optional owned seed graphs, and generation settings.
- Octant generation reuses the visibility builder's scene extraction, qualification,
  worker lifecycle, progress, cancellation, and atomic publication where applicable.
- General graph canvases, connected corridor nodes, per-octant editing, new solver
  backends, variable native cell sizes, and automatic object-group reallocation are
  outside this milestone. Existing conservative object-mapping fallbacks remain.

## Requirements

### FR-OCC-AUTH-001: Reversible coverage sources

Each source has a stable ID, name, enabled contribution, and explicit kind. Primary
represents the project's attached source cells, whether vanilla or imported. Its
coordinates are immutable; its contribution is optional. Start from scratch disables
Primary through an undoable edit and retains the source attachment for restoration.
It does not silently delete authored volumes or the last successful bake.

Boxes, cylinders, their optional graphs, and seeds participate in normal history, save/reopen,
recovery, and project portability. Runtime/gameplay entities are not synthesized for
these editor authoring objects. Preview visibility and selection are session state;
contribution and geometric edits are authoritative project state.

### FR-OCC-AUTH-002: Deterministic shape composition

The effective cell set is:

```text
union(enabled Primary, enabled include volumes after optional graph filtering)
  minus enabled exclusion shapes
```

Overlaps produce one cell. Source order, names, viewport visibility, and selection
do not change coverage. Generation uses canonical coordinate ordering and a versioned
fingerprint; the SDK supplies target cell dimensions and representable bounds.

Boxes support independent positive dimensions and rotation. Cylinders support
positive radius, height, and rotation with circular cross-sections; arbitrary
elliptical scaling is not part of v0. Include shapes retain intersecting cells.
Exclusion shapes remove only fully contained cells and take final precedence over
all inclusion sources. The SDK freezes numeric tolerance and tangency behavior in
fixtures; uncertain inclusion retains a cell and uncertain exclusion keeps it.

Bounds, candidate work, coordinates, counts, and finite transforms are validated
before enumeration. Unsupported negative/out-of-range coordinates fail explicitly;
they are never silently truncated, clamped, or wrapped into the native grid.

### FR-OCC-AUTH-003: Optional seed graphs

An include box or cylinder can own an optional seed graph with markers placed in
the 3D viewport. The volume supplies the candidate boundary; there is no separate
graph shape or size. The graph filters candidate cells by line of sight to its seeds.
This carries forward Forge v1's volume/marker interaction without Unity dependencies
or an executable node network. Seeds belong to their volume; ownership never
depends on a global first-graph search. Moving the volume moves its seeds. Disabling
filtering preserves seeds and settings while restoring ordinary shape generation.
New attachments default to Sticky enabled with distance 8. Existing standalone graph
recipes remain readable as boxes with seed filtering enabled. Changing shape preserves
seed identities; shrinking a volume past a seed is rejected. Cylinder candidates use
the same circular intersection rule as ordinary cylinders, including intersecting edge cells.

The SDK freezes which cell probes, blockers, endpoint tolerances, and alpha/backface
semantics determine marker visibility. Reuse existing scene/query support where it
matches that contract. An enabled graph without markers fails actionably. Markers
inside blocking geometry are identified; uncertain ray/intersection results retain
coverage. Graph filtering is a coverage heuristic, not proof that rejected areas
cannot contain valid gameplay or scripted cameras.

Accepted and rejected candidates can be inspected. Ordinary include shapes restore
coverage for rooms, airborne routes, or scripted cameras missed by the graph. There
are no graph-to-graph references, authored edges, or cycle semantics in this version.

### FR-OCC-AUTH-004: Occlusion dock and gizmos

The existing Occlusion dock owns one coarse tree row for Primary and one row for
each authored volume. Its optional seed graph is edited in the volume properties;
generated octants do not become individual rows.

```text
Coverage
  Primary
  Courtyard — Box, Include
  Tower — Cylinder, Include
  Interior — Box, Seed graph
  Solid foundation — Box, Exclude
```

Contribution toggles and viewport visibility controls are separate and labeled.
Rows expose source kind, role, enabled state, and last evaluated count/staleness.
Counts distinguish per-source coverage from the deduplicated final total.

Creation, selection, focus, rename, duplicate, delete, translation, rotation, sizing,
numeric entry, and snapping reuse existing editor conventions. Marker editing needs
translation; volume transforms use the existing gizmo foundation. A drag
previews locally and creates one history edit on commit; cancelling restores the
previous value. Circular cylinder constraints remain consistent in gizmo and numeric
editing. Controls remain keyboard accessible and understandable without color alone.

### FR-OCC-AUTH-005: Coverage jobs and visibility integration

Preview coverage evaluates a recipe without publishing it. The initial Build occlusion
action combines octant regeneration and visibility baking into one atomic accepted
result, using the established sample planner and visibility worker. A separately
accepted unbaked grid is deferred until there is a concrete workflow that needs it. Authoring inputs and
published coverage have explicit fingerprints; preview is never build authority.

Shape outlines update during interaction. Full coverage enumeration/filtering runs
off interactive loops after an edit or through an explicit preview request. Jobs
report enumeration/filter/validation/publication progress, support cancellation,
bound diagnostic volume, and reject results if their saved inputs changed.

Before expensive visibility work, preflight reports candidate/final cells, qualified
sample counts, tree size, mask-size estimates or measured sizes as appropriate,
native limits, and unmapped/always-visible object fallbacks. Actual masks are checked
again before publication. A smaller cell count does not imply a smaller native tree:
sparse spans and relative-pointer limits are checked separately.

Recipe changes stale generated coverage, sample previews, and baked visibility.
Geometry changes also stale graphs that use it. Viewport inspection may keep the last
successful bake with an explicit stale indicator. Builds reject authored coverage
without matching coverage and visibility results; they never silently substitute
Primary. Primary-only projects keep their existing source-mode compatibility.
Frozen-octant inspection resets or reports a missing cell when the active grid changes.

Fingerprints include semantic recipe inputs, the source coverage identity, relevant
scene inputs, generation/SDK versions, qualification settings, and visibility settings.
Renaming, reordering rows, hiding helpers, or selecting objects does not change the
semantic coverage fingerprint. Safe full invalidation is sufficient initially.

### FR-OCC-AUTH-006: Native validation and preservation

Native lookup-tree creation, mask serialization, alignment, reserved bits, allocation
and archive composition belong in RatchetPs2.Games.UYA behind neutral SDK entry
points. Forge.Host orchestrates project state and jobs through its UYA adapter;
Electron manages privileged integration and workers; React/Three.js presents edits.
Shared host Domain code does not depend on game adapters.

The native contract must establish coordinate/span/pointer/mask limits, sparse gaps,
source-free cell generation, allocation growth, and reserved-bit behavior for cells
without source counterparts. Source-mode cell reserved bits remain preserved; authored
regeneration sets the runtime-forced visible bit 1023. The current
source-allocation compaction policy must not be mistaken for a format/runtime limit.
Unknown native sections and existing mapping families remain preserved.

An empty recipe is a valid authoring state. Native publication of empty coverage is
blocked until game runtime behavior is qualified. The editor's outside-coverage
all-visible fallback does not establish the game's lookup behavior. Newly generated
coordinates and reserved-bit defaults need explicit SDK/runtime evidence before use.

Generated native output is semantically re-read against the planned coordinate and
visibility sets before atomic replacement. Conservative mask compaction may add
visibility but must never remove generated visibility or change reserved-bit policy.
Failure, cancellation, capacity exhaustion, and stale results leave the prior
successful coverage, bake, and stage intact.

## Delivery order

Start with native-contract fixtures, then a box-only path from project edits through
native publication and visibility baking. Add cylinder enumeration/controls and
graph filtering to that same path. Tasks may use synthetic all-visible masks to
exercise native writing independently of the GPU solver; these are test inputs and
never a silent production replacement for baking visibility.

## Exit gate

In packaged Forge, users disable Primary, create overlapping boxes/cylinders, add
optional seed graphs, inspect coverage, regenerate octants, bake visibility, save and
reopen, and build a level using the new coordinates. Restoring Primary is reversible.
Representative interior/exterior/disconnected/airborne/scripted routes are checked in
PCSX2, with supported-platform and selected real-hardware evidence required by the
parent M18 release gate. Cancellation at each phase and malformed/stale/oversized
inputs preserve known-good state. New runtime behavior is not advertised from
synthetic writer/readback evidence alone.
