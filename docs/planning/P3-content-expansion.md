# P3 — Content expansion and runtime fidelity

Status: In progress — M14-001 through M14-006, M15-001 through M15-005,
M16-001 through M16-008, and M17-001, M17-002, and M17-004 are complete; the corrected
M17-003 ocean renderer awaits visual validation. Safe whole-family Moby model, PVar,
stable-reference, native-group, sound-closure, and explicit cross-family reference
import are implemented. The import workflow now has the apply UI, explicit external Moby-reference
mapping, and entity, group, property, asset-use, and sound origin projection; singleton
replacement now supports atomic whole-composition skybox swaps, target-native level-
settings replacement with explicit reference rebinding, and selector-safe whole-array
directional-light replacement. Whole-bank HUD replacement now attaches the donor source
and preview textures for portable deterministic baking. Whole-bank FX replacement now
attaches every donor texture and rebuilds the exact donor index count while relocating
unrelated target assets. The Imports panel now provides batch-report filtering,
live-content selection, entity focus, Scene filtering from origin links, and dependency-
safe removal previews with stale-plan-guarded atomic apply and undo/redo. Collision now
supports portable whole-family add/replace for solid pieces and player barriers while
preserving native face types and explicit static-instance links. Final packaged/PCSX2
qualification remains.
Priority: P3
Depends on: P2 authoring contracts by task; P2 release qualification before P3 release
Task register: [P3 tasks](tasks/P3-tasks.md)

This addendum turns the Phase 3 notes into an ordered delivery plan. The base
[Forge v2 specification](../forge-v2-spec.md) and the
[P2 advanced-authoring addendum](P2-next-phase.md) continue to define project safety,
stable references, game boundaries, incremental bake behavior, and portability.

## Outcome

A UYA project can import and place project-local custom TIEs and tfrag terrain, merge
supported content from another verified vanilla map, inspect and carry moby sound
dependencies, preview the `0x0B37` ocean-plane simulation, and rebuild native occlusion
with interactive visibility/performance diagnostics. Every imported or generated
artifact remains portable, attributable, bounded, deterministic, and removable without
mutating the vanilla catalog or source maps.

## Scope decisions

- **UYA remains the writable qualification target.** Contracts that genuinely apply
  to other games remain neutral, but P3 does not add speculative cross-game writers.
- **The first custom model is a static TIE.** Shrub and moby conversion can follow the
  canonical pipeline after the TIE round trip proves material, texture, geometry,
  placement, and native writer contracts. Skins and animation do not block M14.
- **Project-local custom tfrags remain distinct from donor import.** Authored terrain
  uses the bounded static-model pipeline; M16 donor terrain instead retains attached
  raw primary/streamed owners, canonical three-LOD pieces, terrain textures, and batch
  provenance for lossless add/replace composition.
- **P3 completes, rather than duplicates, unfinished P1 model work.** P1-001 and
  P1-002 become M14 foundations; P1-005 and P1-006 are satisfied first for the
  constrained TIE slice. Completed P1 texture quantization remains a dependency.
- **Custom class IDs are project-local native slots.** UYA class IDs at or above
  `0x9000` are reserved for custom definitions. A content Asset ID remains the stable
  identity; the allocated class ID is persisted target metadata and may be remapped
  atomically during map import conflict resolution.
- **Cross-map import is a project overlay.** Source maps and the vanilla asset cache
  remain immutable. Imported definitions, textures, sounds, and other required bytes
  are copied into project-attached assets, so reopening or sharing the project never
  requires the donor map.
- **Import capabilities are explicit per family.** TIEs, shrubs, and tfrags are the
  default-selected core. Skybox, level settings, directional lights, HUD, and FX are
  replace-only. Opaque/unsupported payloads cannot be imported over the base.
  Other families are initially unchecked and enabled only when their dependency
  closure and writer are proven.
- **A sound is not assumed to be one PCM blob.** 989snd banks contain playable sound
  entries, grains/program behavior, sample payloads, and metadata. P3 assigns identity
  at the playable-entry level while retaining/deduplicating its sample dependencies.
- **Sound support starts with read, preview, dependency import, and deterministic bank
  composition.** General WAV/VAG authoring is deferred until the retail bank writer is
  proven by cross-map sound merges.
- **Simulations are editor-only visualizations.** They consume authoritative project
  data but never rewrite PVars, FX, entity transforms, or bake output merely by
  running.
- **Occlusion generation is conservative.** A visible object may be retained when
  uncertain; a potentially visible object must not be culled. A bounded CPU reference
  establishes semantics before a GPU backend is accepted.
- **GPU acceleration is a measured implementation choice.** M18 compares raster,
  ray/cone, readback, and available API constraints before freezing a backend. It does
  not hard-code an attractive but unproven technique into the project schema.

## Delivery order

```text
P1-003/P1-004 + M7 attached assets + M12/M3 native writers
  └─ M14 Custom TIE pipeline

M9 moby/PVar references + existing UYA sound remap composer
  └─ M15 Sound banks and moby sound dependencies

M7 stable references + M10 groups + M12 terrain + M14 custom IDs + M15 sounds
  └─ M16 Cross-map content import

M8 FX + M9 PVars/references + M7 scene services
  └─ M17 Moby simulation framework and ocean plane

M2 scene inventory + SDK occlusion readers/writers + M16 provenance
  └─ M18 Occlusion generation and analysis

M14 + M15 + M16 + M17 + M18 + M13-007
  └─ M19 P3 qualification
```

M14-001, M15-001, M16-001, M17-001, and M18-001 are independent discovery tasks and
may start in parallel. M16 can deliver its static-family path before moby import, but
M16 cannot close until M15 supplies transitive sound dependency handling.

## Functional requirements

### Custom TIE assets

#### FR-CUSTOM-004: Stable custom identity and class allocation

- Each imported custom model has a content-addressed Asset ID independent from any
  target-native class ID. Derived textures and native outputs retain parent Asset IDs
  and canonicalizer/converter versions.
- Each project persists a collision-free custom class allocation registry. For UYA,
  `0x9000` through the adapter-proven maximum is custom space; allocation is
  deterministic and never silently reuses a live slot for different content.
- A donor-map definition at or above `0x9000` is custom content. Exact content may be
  deduplicated; conflicting slots are remapped with all understood references in one
  transaction.
- Deleting/replacing a definition uses the indexed inbound-reference graph. Missing,
  exhausted, duplicate, or wrong-kind allocations block bake with the owning asset and
  corrective action.

#### FR-CUSTOM-005: Bounded GLB/glTF canonical import

The SDK MUST parse supported `.glb` and `.gltf` scenes into one versioned canonical
static-model contract. It validates and bounds containers, accessors, buffers, images,
nodes, transforms, meshes, primitives, indices, attributes, materials, and texture
references before project mutation. External local resources are copied into the
project import transaction; remote URIs, path traversal, oversized data URIs, cycles,
and unsupported extensions fail explicitly.

The initial TIE profile supports finite static triangle geometry, documented vertex
attributes, one documented coordinate/unit conversion, bounded material/texture
features, and deterministic primitive ordering. Unsupported topology, morph targets,
skins, animations, shader extensions, and target-limit violations identify the exact
source element. Source bytes remain attached for reimport/diagnostics but canonical
data, not the original path, is authoritative.

#### FR-CUSTOM-006: Native TIE conversion and diagnostic round trip

- The SDK converts the canonical static model and texture assets into validated UYA
  TIE model/material/texture structures, including required alignment, packet/index
  limits, bounds, LOD policy, draw-state semantics, and palette assignment.
- Custom textures are ordinary content-addressed texture assets. Multiple models reuse
  identical assets and the bake allocator resolves them without duplicate native data.
- Generated native bytes MUST semantically re-read before attachment or staging.
  A debug-only native-to-GLB export makes conversion loss visible; it is never bake
  authority and is labeled as an approximation where PS2 state has no glTF equivalent.
- Asset Preview can compare source/canonical geometry with the re-read target result,
  select matching primitives/materials, and show geometry, texture, palette, bound,
  and conversion diagnostics.

#### FR-CUSTOM-007: Placement, collision, occlusion, and bake

Import creates an asset definition, not a hidden world instance. Users explicitly add
TIE instances that use normal entity transforms, groups, undo/redo, references, save,
recovery, and incremental bake. Collision is explicit: an imported TIE may have no
collision or a user-generated/assigned collision asset; Forge never silently treats
the render mesh as retail collision. Until M18 generates occlusion for the project,
new custom TIEs use the adapter's disclosed conservative always-visible behavior.

### Sound banks

#### FR-SOUND-001: Bounded bank inventory and sound identity

The Ratchet SDK owns UYA sound-bank layouts, relocation/alignment rules, sound/grain
metadata, sample payloads, decode behavior, and semantic validation. Readers distinguish
playable sound entries from shared samples and reject malformed offsets/counts/opcodes,
integer overflow, cycles, excessive graphs, and decoded-allocation bombs.

Each playable entry receives a canonical Sound Asset ID derived from every byte and
semantic parameter that can change playback. Shared sample payloads are separately
content-addressed so they can deduplicate without conflating distinct programs. The
original encoded representation and provenance remain available for exact preservation.

#### FR-SOUND-002: Sound remaps and moby dependency closure

- Forge imports the map sound-remap table as typed links from owner/class slots to
  Sound Asset IDs while retaining native indexes as provenance only.
- Moby asset definitions list their referenced Sound Asset IDs. Adding/importing a
  moby definition carries the transitive sound-entry, sample, and cross-sound
  dependencies needed for playback and bake.
- Missing, recursive, unsupported, or ambiguous dependencies identify the owner,
  remap slot, native sound index, and failing graph edge. Forge never substitutes a
  similarly numbered sound from the target map.
- Deletion and replacement use the shared inbound-reference rules; a referenced sound
  is retained or changed only through an explicit dependent operation.

#### FR-SOUND-003: Sound/remap view and safe preview

A sound workspace lists all map sound entries, Asset IDs, native/remap indexes, source,
dependents, sample/loop/rate/channel metadata, validation state, and import batch. It
can filter or navigate from a moby/remap link and preview supported entries without
auto-playing. Decode/playback is cancellable, cached with a bounded budget, performed
off interactive loops, and disposed on project close. Unsupported grain behavior is
shown rather than approximated silently.

#### FR-SOUND-004: Deterministic sound-bank composition

The SDK can append/reuse the exact playable entries and dependencies needed by imported
content, assign deterministic native slots, rewrite the remap table, preserve untouched
entries and opaque bytes, and rebuild aligned bank sections. A no-edit composition
meets its frozen identity contract; a changed bank semantically re-reads to the planned
entry graph and sample hashes before atomic staging. Capacity/format failures occur in
preflight and leave the last known-good stage intact.

#### FR-SOUND-005: Preview volume preferences

Forge stores global, machine-local direct-sound and ambient-sound preview volume
sliders with mute, clamping, keyboard access, and immediate effect on active previews.
They do not enter project files or game output. Direct sound preview uses the first;
the second is available for ambient-sound preview/simulation when implemented.

### Cross-map content import

#### FR-IMPORT-001: Verified source and capability matrix

Users select a donor game/map through the existing verified source/catalog flow. The
adapter reports, per family, whether add, replace, both, or neither is supported and
why. The prompt provides one keyboard-accessible checkbox and supported mode control
per family. UYA initially presents:

- TIEs, shrubs, and tfrags checked by default with add/replace where validated;
- other validated entity/asset families unchecked by default;
- skybox, level settings, directional lights, HUD, and FX as replace-only; and
- opaque payloads and target-incompatible families as unavailable.

The UI never enables a family merely because source bytes were discovered. A reader,
dependency contract, project representation, native writer, and qualification fixture
are required.

#### FR-IMPORT-002: Dry-run import plan

Before mutation, Forge builds a cancellable plan containing source fingerprint, import
batch ID, selected mode per family, exact definitions/entities/assets to add, reuse,
remap, protect, or remove, transitive dependencies, ID/index conflicts, capacity
changes, estimated attached bytes, warnings, and blockers. Users can inspect why an
item is included and which retained target references protect it.

#### FR-IMPORT-003: Add and replace semantics

- **Add** keeps existing target content, imports selected source content, remaps every
  native index/class conflict, and preserves understood relationships among imported
  entities.
- **Replace** removes the selected target family except items required by unselected
  retained content. Protected survivors are listed with inbound owners/fields and a
  clear message that they may be removed only after those references change.
- Replace-only singleton families atomically swap the supported authoritative record;
  no duplicate singleton is created.
- Exact already-present dependencies are reused. A donor definition whose vanilla
  numeric ID collides with different target content receives a project custom slot;
  Forge never overwrites a different definition merely because its source ID matches.

#### FR-IMPORT-004: Dependency-complete family import

The adapter computes a typed closure across model definitions, instances, textures,
materials, sounds, PVars, entity references, map groups/areas, collision, and other
proven dependencies. Tfrag import appends/rebuilds complete native owner groups and
their texture/material records rather than copying display triangles alone. Moby
import preserves raw PVars, resolves supported entity references within the batch or
target, and includes M15 sound dependencies. Unsupported edges block the affected
selection instead of writing null or stale native indexes.

#### FR-IMPORT-005: Atomic and reversible import batches

An import batch stages all project records and attached blobs, validates the resulting
reference/allocation graph, and commits as one history operation. Cancellation or
failure commits nothing. The batch records source identity and added/reused/replaced/
protected items so undo/recovery is complete and a later Remove import action can
delete unreferenced batch-owned content after a fresh dependency analysis. Removal
never deletes deduplicated content still used elsewhere.

#### FR-IMPORT-006: Origin-aware presentation

Scene, Groups, Asset Explorer, Properties, Sound, and relevant bank views distinguish
base, project-authored, and imported content without color alone. Imported rows expose
the donor map and batch, support filtering, and navigate to the batch report. Base and
imported entities otherwise use the same editing tools and selection model; Forge does
not create a parallel scene authority.

### Moby simulations

#### FR-SIM-001: Class-driven simulation framework

The renderer owns a disposable, game-and-class-keyed simulation registry. A simulation
receives read-only entity/PVar/reference snapshots, resolved FX/texture services,
camera state, and deterministic fixed-step time. It may create derived render objects
and hide the ordinary proxy while enabled, but it cannot mutate project state.

Simulation enable, pause, reset, error isolation, project-change invalidation, resource
cleanup, fixed-step catch-up bounds, and per-simulation timing are shared. Missing or
unsupported data falls back to the ordinary scene representation with an actionable
diagnostic.

#### FR-SIM-002: UYA ocean plane `0x0B37`

The first simulator reproduces the local map-o-matic behavior for UYA class `0x0B37`
from verified game/PVar evidence: layered FX textures, colors/blending, wave motion,
tiling/direction/speed, authored height, camera-relative coverage, and hidden-cuboid
behavior. It reacts to committed PVar, transform, reference, and FX override changes
without reopening the project and preserves the regular selection/Properties identity.

#### FR-SIM-003: Simulation authoring UX and fidelity

A level/project toggle and small simulation control surface disclose that the result is
an editor preview. Selection, focus, visibility, layer controls, and picking remain
consistent. A comparison fixture records map-o-matic, Forge, and PCSX2 behavior for
representative camera positions/times; known approximations are documented. Simulation
work is demand-driven and cannot impose its tick/render cost while disabled or outside
the active project.

### Occlusion generation and analysis

#### FR-OCC-001: Frozen native occlusion contract

Before generation, the SDK inventory freezes UYA octant/grid encoding, world-space
partitioning, tie/tfrag/moby mapping, bit limits, ordering, alignment, empty/always
visible behavior, and writer identity. Source and generated occlusion are distinct
project states with explicit provenance. Unknown mapping families remain preserved and
cannot be silently regenerated.

#### FR-OCC-002: Automatic coverage with bounded overrides

Forge derives candidate camera octants/samples automatically from navigable collision,
level bounds, and verified native constraints. Optional tagged cuboids may include,
exclude, or increase/decrease sampling density for interiors, airborne routes, scripted
cameras, or unsupported navigation. Inputs and settings are versioned project data and
produce a deterministic generation fingerprint. A representative coverage audit must
decide whether this is sufficient. Manual occlusion volumes and a node graph are a
documented fallback only if automatic coverage plus cuboid overrides cannot represent
proven camera routes; Forge does not build both authoring systems speculatively.

#### FR-OCC-003: Conservative visibility semantics

For each sample and documented camera orientation/FOV set, generation evaluates ties,
tfrags, and mobys using canonical bounds/geometry and native grouping. Transparent,
cutout, translucent, non-depth-writing, and explicitly non-occluding materials do not
become solid blockers merely because their geometry intersects a ray/view. Feathering
and numeric tolerances retain boundary objects; uncertainty resolves visible.

#### FR-OCC-004: Reference and accelerated generation backends

A bounded CPU reference solver defines correctness on synthetic and small real
fixtures. Production generation uses a separately qualified GPU backend, with the CPU
reference retained for bounded verification and disclosed diagnostics. The GPU backend
may use raster/depth, ray/cone, or a measured hybrid, but must produce results within
the conservative equivalence contract across supported vendors. Jobs run off the
renderer/main loops, report phase/progress,
support safe cancellation, bound GPU/CPU memory and readback, avoid driver watchdog
work units, and cache/incrementally recompute by fingerprint where valid.

#### FR-OCC-005: Occlusion preview and point-of-view probe

Users can compare source and generated visibility, select an octant/sample, or place
and rotate a transient POV pin with adjustable supported FOV. The scene dynamically
shows what the selected native mask would retain and can isolate false-positive/false-
negative diffs without altering entity Hidden state or project history. Probe state is
editor layout/session state, not baked map content.

#### FR-OCC-006: Performance analysis

Forge computes a disclosed visibility-cost estimate per octant/view from visible
triangles plus material pass count, double-sided work, and other verified multipass
costs. A graph/heatmap ranks worst views, links them to the probe, and distinguishes an
estimate from measured GS time. Level 45 multiplayer is the initial calibration map;
recorded PCSX2/real-hardware evidence is used to tune weights rather than presented as
portable timing fact.

#### FR-OCC-007: Native write and build integration

Generated octants/mappings compose through the SDK, satisfy native limits, re-read to
the planned visibility sets, and stage atomically as an independently fingerprinted
bake layer. No-edit source mode preserves source output. Build diagnostics report
over-capacity octants, unmapped entities, conservative fallbacks, timings, cache use,
and corrective actions. A failed or cancelled rebuild retains the last successful
occlusion stage.

## Non-functional requirements

#### NFR-P3-001: Deterministic identity and preservation

Canonical asset IDs, custom class allocation, import plans, native slot assignment,
sound composition, and CPU occlusion output are deterministic for the same versioned
inputs. Unselected families, unknown bytes, opaque payloads, and protected referenced
content remain byte/semantically unchanged under their frozen contracts.

#### NFR-P3-002: Responsive bounded work

GLB conversion, donor inventory, import closure, sound decoding/composition, simulation
resource creation, and occlusion generation stay off Electron/React interactive loops.
Long jobs expose progress and safe cancellation. M18-001 records Forge v1 and current
baseline time/memory before setting the production budget; M18 must materially beat
that baseline and prevent repeated scene construction and unbounded or synchronous
per-sample readback.

#### NFR-P3-003: Untrusted input and provenance

GLB/glTF, donor maps, sound banks, imported project records, and generator settings are
untrusted. Boundary code validates paths, offsets, counts, graph depth, decoded sizes,
allocation arithmetic, and diagnostic volume. Every generated/imported artifact records
source fingerprint, converter/schema version, parent Asset IDs, and target game.

#### NFR-P3-004: Portable self-contained projects

After a successful import, neither original GLB/glTF sidecars, donor ISO/map, nor sound
bank path is required to open, preview, rebuild, move, zip, or share the project.
Machine-local sound volume and transient simulation/occlusion probe state are excluded.

#### NFR-P3-005: Actionable qualification

Failures identify the family/entity/asset/sound/octant, stable ID, source/batch, native
slot or field, violated limit, and corrective action. P3 features include synthetic CI
fixtures and local proprietary-corpus procedures; no retail payload enters the repo or
public artifacts.

#### NFR-P3-006: Accessible dense tooling

Import plans, sound tables, comparison previews, simulation controls, graphs, heatmaps,
and probes remain keyboard operable, visibly focused, scalable, and understandable
without color alone. Large lists and graphs virtualize or aggregate rather than
creating one DOM component per source item/octant.

## Milestones

### M14 — Custom TIE asset pipeline

Deliver project-local custom identities and class allocation, bounded GLB/glTF import,
UYA TIE conversion, shared texture assets, diagnostic native-to-GLB round trip, preview
comparison, placement, and native bake.

Exit: a constrained static GLB with shared textures can be imported from either Linux
or Windows, its source and re-read target compared, two instances placed, the project
moved without original paths, and the TIE deterministically built/seen in PCSX2 with
explicit collision and conservative occlusion behavior.

Delivered extension: the same bounded GLB/glTF source flow now creates project-local
custom tfrag imports. Forge partitions them into native primary terrain groups, attaches
their sources/sidecars/textures, previews and edits them as one parent with ordinary
piece transforms, reimports or deletes them atomically, and rebuilds terrain, texture,
occlusion, render, and packed level outputs. This is not the donor-map workflow owned by
M16; its packaged-app and PCSX2 evidence is tracked by M12-008.

### M15 — Sound banks and moby sound dependencies

Deliver the UYA 989snd inventory, playable-entry/sample identities, remap/dependency
graph, sound workspace and preview, volume preferences, and deterministic merge/write.

Exit: representative direct sounds preview safely, a moby sound dependency closure
moves into another project, the composed bank/remap semantically re-read, and PCSX2
plays the intended sound without changing untouched entries.

### M16 — Cross-map content import

Deliver per-family capabilities, dry-run planning, provenance, custom-ID/index remap,
atomic add/replace batches, dependency-complete static/terrain/moby import, protected
survivors, origin-aware UI, and safe batch removal.

Exit: a level 41 project imports selected level 40 TIE/tfrag/shrub content, then an
optional moby with sounds, remains self-contained, builds deterministically, and can
remove/undo the batch without damaging base or shared referenced content.

Qualification status and the retained packaged/runtime matrix are tracked in the
[M16 cross-map import record](../qualification/M16-cross-map-import.md).

### M17 — Moby simulation framework and ocean plane

Deliver the shared fixed-step simulation lifecycle and the UYA `0x0B37` ocean plane
using authoritative PVars, FX overrides, references, transforms, and hidden cuboids.

Exit: a representative ocean map previews with stable performance and selection,
responds to committed edits without reload, resets deterministically, disposes cleanly,
and records expected parity/differences against map-o-matic and PCSX2.

### M18 — Occlusion generation and analysis

The integrated builder rebuilds existing UYA octants and native groups with an
isolated WebGPU raster worker. Shared center/corner samples, local wall qualification,
LOD0 alpha-aware blockers, nonblocking moby bounds and ocean barriers define the
current profile. Every sample contributes to its cell; neighbor feathering and center
parallax dilation are disabled. The Occlusion panel exposes rebuilding, qualification
previews, sample pull-in and pixel-edge controls, viewport visibility and octant freeze.

The [current qualification record](../qualification/M18-occlusion.json) consolidates
saved Hoven/Outpost comparisons, defaults, resource limits and regression coverage.
Historical experiments are archived locally under ignored artifacts. Existing native
mapping/primary-terrain integration is working; continuous camera coverage, automatic
new coverage/custom groups, streamed chunks and broader hardware qualification remain
open. Source-mask agreement alone does not establish visible-pixel correctness.

Deliver frozen native contracts, automatic/cuboid-controlled sampling, conservative
CPU and accelerated visibility generation, caching/cancellation, source/generated diff,
POV probing, cost heatmaps, native writing, and build integration. Add manual
volume/node coverage authoring only if the M18 coverage audit activates that fallback.

Exit: representative maps plus level 45 multiplayer rebuild within the frozen resource
budget, native output re-reads and runs in PCSX2/real hardware, no known visible object
is incorrectly culled in the qualification routes, and worst views are inspectable.

### M19 — P3 integration and release qualification

Close cross-platform, portability, accessibility, performance, interruption, and
combined build/patch evidence across all P3 features.

Exit: one portable UYA project containing a custom TIE, donor-map import, imported moby
sound closure, ocean simulation, and generated occlusion moves between packaged Linux
and Windows, deterministically builds/patches, and passes PCSX2 plus selected real-
hardware checks while failures retain the last known-good project and staging output.

## Deferred work

- Custom shrub/moby conversion beyond reuse of the proven canonical/native pipeline.
- Skinning, animation, morph targets, arbitrary shader graphs, and a model editor.
- A global custom-asset repository or publishing marketplace.
- Cross-game donor import or automatic semantic translation between incompatible games.
- General WAV/VAG authoring, music/stream editing, external 989snd plug-ins/cross-bank
  named dispatch, and hardware-exact SPU envelope/reverb emulation.
- Gameplay-accurate moby simulation, physics, scripts, networking, or simulation bake.
- User-authored GPU shaders/occlusion algorithms and cloud/distributed generation.
- Claiming PCSX2 or estimated GS cost as identical to every real PS2 workload.

## Discovery evidence and open gates

- The local map-o-matic simulation registry already demonstrates a class-driven,
  fixed-step lifecycle with disposal and per-game factory maps. Its UYA registry
  inherits class `0x0B37` and the ocean implementation consumes layered FX textures,
  wave/tiling/color/blend PVars, authored height, and hidden cuboids. M17 ports the
  proven behavior into Forge services; it does not couple Forge to map-o-matic runtime
  modules.
- The Ratchet SDK already owns UYA sound-remap composition and UYA occlusion grid/
  mapping readers and writers. M15 and M18 extend those boundaries rather than parsing
  either format in Electron or React.
- We already have a decompilation of the PS2 runtime for 989snd. It documents file chunks, block/sound/grain
  structures, relocation, VAG/sample transfer, and playback behavior, but it is not a
  desktop decoder library and the repository contains no root license file. M15-001
  must record provenance/reuse terms; until then it is behavioral reference material,
  not source copied into Forge or the SDK.
- The runtime's chunk loader makes `sound.bnk` structural similarity plausible, not
  proven. M15-001 compares actual UYA containers and does not label the layouts
  identical until offsets, versions, relocations, and playback graphs agree.
- M14-001 proved that the adapter accepts `0xFFFF`, froze all four class-ID rewrite
  locations, and found no `0x9000+` use across the hashed 45-level NTSC-U corpus. The
  versioned project registry and deterministic donor conflict rules are recorded in
  [`uya-custom-tie-class-contract-v1.md`](../features/uya-custom-tie-class-contract-v1.md).
- M16-001 must freeze each family closure and native append/remove capability. A family
  remains disabled when Forge can display it but cannot reconstruct it safely.
- M18-001 must freeze native visibility semantics and performance baselines before a
  GPU backend or production budget is selected. The reported Forge v1 observation of
  ten-plus-minute generation on an RTX 5090 and level 45 multiplayer's roughly 8 ms GS
  latency versus a 2–3 ms target are hypotheses to reproduce with recorded settings,
  not portable performance guarantees.

## P3 release gate

M14 through M18 exit gates and M19 pass in packaged Linux and Windows builds. The
portable qualification project opens without original import paths or donor media;
all Asset IDs, custom class allocations, import batches, sound remaps, and occlusion
fingerprints validate. Repeated clean builds are deterministic under the documented
contract. Interrupting GLB conversion, donor import, sound composition, occlusion
generation, save, pack, or patch leaves the prior project and staging output usable.
