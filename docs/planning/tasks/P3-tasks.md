# P3 task register — Content expansion and runtime fidelity

Plan and requirements: [P3 — Content expansion and runtime fidelity](../P3-content-expansion.md)

Tasks are ordered by dependency within each milestone. Discovery tasks freeze binary,
identity, ownership, and performance contracts before project schemas or UI depend on
them. Unless marked complete, every task is `not-started`.

## Current milestone status

| Milestone | Status | Gate |
| --- | --- | --- |
| M14 — Custom TIE pipeline | 🚧 In progress | M14-007 |
| M15 — Sound banks | ⏳ Not started | M15-001 through M15-006 |
| M16 — Cross-map import | ⏳ Not started | M16-001 through M16-009 |
| M17 — Moby simulations | ⏳ Not started | M17-001 through M17-005 |
| M18 — Occlusion rebuild | ⏳ Not started | M18-001 through M18-010 |
| M19 — P3 qualification | ⏳ Not started | M19-001 through M19-003 |

## M14 — Custom TIE asset pipeline

### M14-001 — Freeze custom identity, class-ID, and P1 migration contracts

Status: ✅ Complete

Requirements: FR-CUSTOM-004, NFR-P3-001, NFR-P3-003
Depends on: M7-002, M7-004, P1-004

Inventory every UYA TIE class-ID field, table, reference, width, sentinel, limit, and
writer. Define the project custom allocation record and reconcile P1-001/P1-002/
P1-005/P1-006 with M14 so one implementation owns each deliverable.

Acceptance:

- A checked-in contract proves whether `0x9000` through the adapter maximum is unused
  by the supported vanilla corpus and identifies every native location to rewrite.
- The registry schema separates Asset ID from target class ID, persists allocation
  version/provenance, and defines deterministic allocate/reuse/rebase/exhaustion rules.
- Donor custom-ID conflicts and identical-content reuse have exact atomic remap rules.
- P1 tasks link to M14 as their constrained TIE completion path without losing the
  already-complete quantization history.

Verification: corpus inventory hashes, boundary/sentinel/duplicate/exhaustion fixtures,
schema round trip, and planning-link check.

Implementation: [`uya-custom-tie-class-contract-v1.md`](../../features/uya-custom-tie-class-contract-v1.md)
freezes the four native rewrite locations and the `0x9000`–`0xFFFF` namespace. Project
schema 20 persists a versioned Asset-ID-to-class registry with origin provenance;
deterministic allocation reuses identical content, preserves free donor slots, remaps
conflicts to the lowest free slot, and rejects ambiguous donors or exhaustion atomically.
The hashed 45-level NTSC-U corpus inventory contains 1,662 distinct TIE classes through
`0x1FFE` and no custom-range use. Fixtures cover `0xFFFF`, reuse, donor conflicts,
input-order determinism, exhaustion, validation, cleanup retention, migration, and
save/reopen round trip.

### M14-002 — Implement bounded GLB/glTF canonical import in the SDK

Status: ✅ Complete

Requirements: FR-CUSTOM-005, NFR-P3-003, NFR-P3-004
Depends on: M14-001

Add a versioned SDK static-model canonicalizer for the supported GLB/glTF profile and
local sidecars. Keep path access and project attachment in Forge.Host.

Acceptance:

- Supported buffers/accessors/nodes/primitives/materials/images and transforms produce
  deterministic canonical geometry, material, texture, bounds, and provenance records.
- Every count, range, stride, component conversion, URI, decoded image, node depth, and
  total allocation is bounded before allocation or project mutation.
- Local sidecars are returned as explicit dependencies; remote/path-escaping/cyclic or
  unsupported inputs fail with source element and corrective action.
- Static triangles and documented attributes pass; unsupported topology, extensions,
  skins, morphs, and animations are rejected explicitly.

Verification: authored valid matrix, malformed/adversarial corpus, sidecar traversal,
allocation-bound, cancellation, platform-determinism, and canonical snapshot tests.

Implementation: the linked Ratchet PS2 SDK now provides a browser-safe, byte-oriented
`StaticModelGltfImporter` and versioned `HFSM` canonical codec. It supports the
documented static triangle profile, explicit local sidecars, baked node transforms,
PNG-to-RGBA textures, deterministic bounds/material/provenance records, and bounded
container/accessor/image/allocation checks without filesystem or network access. The
focused .NET corpus covers GLB and glTF equivalence, traversal and malformed input,
limits, cancellation, transforms, materials, and the frozen canonical SHA-256
`a053be57678ce6ca4954d1e3b0e6741aa1afff85fc62b13728301c2f0c5a26eb`; a checked-in
generated-TypeScript test produces the same hash. The supported profile and host flow
are recorded in the SDK's `docs/STATIC_MODEL_IMPORT.md`.

### M14-003 — Convert canonical static models into UYA TIE structures

Status: ✅ Complete

Requirements: FR-CUSTOM-006, NFR-P3-001
Depends on: M14-002, P1-004, M3A-006

Implement the smallest UYA SDK composer that converts canonical static geometry,
materials, textures, bounds, and the documented LOD policy into target-native TIE data.

Acceptance:

- Preflight covers vertex/index/primitive/packet/material/texture/palette/bounds/LOD,
  alignment, integer, and archive-capacity limits before writing.
- Texture inputs use canonical Texture Asset IDs and shared palette optimization;
  identical textures are not encoded twice merely because two materials use them.
- Native output re-reads to equivalent supported geometry/material semantics and
  stable primitive/material mappings; repeated conversion is deterministic.
- A failure identifies the source mesh/primitive/material/texture and emits no usable
  partial output.

Verification: minimal, shared-texture, multi-primitive, transformed-node, boundary,
malformed, cancellation, determinism, and semantic re-read SDK tests.

Implementation: the linked Ratchet PS2 SDK now exposes the game-neutral
`StaticModelConverter`, dispatching UYA conversion to a game-owned composer. The
composer preflights native limits, emits retail-shaped packet/shader/normal/remap and
quantized-bound structures, jointly optimizes indexed8 palettes, content-addresses and
deduplicates effective PIF textures, and returns stable source-to-native primitive and
material bindings. Only native LOD0 contains authored geometry; LOD1 and LOD2 remain
disabled until the later LOD work. Adjacent triangles normally share the exact retail
four-token strip shape; models near the runtime packet/remap ceiling use a compact
six-token fallback. Unpaired triangles use a degenerate final slot, up to 15 strips
are batched per retail-shaped packet, and normal/RGBA remaps are split into
packet-sized groups with a terminating zero header entry. Packet vertex rows follow retail reverse-strip storage order.
Normal remap targets follow physical upload rows and stored lighting vectors use the
runtime's inward-facing convention.
Fixed bounds storage and RGBA cache sizing
are retained. Before returning, the SDK semantically re-reads
every generated TIE and PIF and checks topology, quantized positions/UVs, normal
remaps, draw-state/material mappings, and byte stability. Focused fixtures cover the
required happy paths, boundaries, malformed inputs, cancellation, determinism, and
the frozen native SHA-256
`39CBC7CCBB1B6EEF16D9D86729D8E9E02DC81299A7427BF325CBA138867B3EA0`.

### M14-004 — Attach custom models and allocate project-native classes

Status: ✅ Complete

Requirements: FR-CUSTOM-004, FR-CUSTOM-005, FR-ASSET-007, NFR-P3-004
Depends on: M14-001, M14-002, M14-003

Add custom model/source/dependency records and undoable import/reimport/delete commands
to the existing project-attached asset store and custom class registry.

Acceptance:

- One import transaction copies source/sidecars, canonical model, textures, and derived
  metadata; validates every hash/parent/kind; then allocates one stable custom class.
- Import/reimport failure or cancellation commits neither blobs nor registry records.
- Reimport creates new immutable identities, retargets only explicit dependents, and
  retains history/recovery blobs until ordinary cleanup can safely collect them.
- Move/zip/reopen works without the original model or texture paths; delete reports all
  class instances and asset dependents.

Verification: command/history/recovery/cleanup, shared texture, path removal,
integrity-corruption, reimport, delete blocker, and Linux↔Windows ZIP fixtures.

Implementation: project schema 20 now persists bounded custom-model source,
sidecar, canonical TIE, native-envelope, texture, material-binding, and diagnostic
records in the existing attached-asset store. Import and reimport perform conversion
before one rollback-safe blob/metadata commit, allocate stable `0x9000+` classes, and
retarget only explicitly named instances. Import/reimport/delete are undoable editor
commands carried by the validated bridge payload. Delete produces an exact instance
and asset-dependent report; imported class tombstones and immutable blobs remain
protected by active recovery snapshots and are released by ordinary cleanup only
after those references disappear. Focused fixtures cover cancellation/failure
atomicity, undo/redo, selective reimport, shared texture reuse, source-path removal,
ZIP reopen, native-envelope integrity corruption, delete blockers, recovery retention,
and final cleanup.

### M14-005 — Add source-versus-target model preview

Status: ✅ Complete

Requirements: FR-CUSTOM-006, NFR-P3-005, NFR-P3-006
Depends on: M14-003, M14-004

Extend Asset Preview with source/canonical and semantically re-read target views plus a
debug native-to-GLB export for external inspection.

Acceptance:

- Users can switch or compare source/canonical and target representations without
  confusing the debug GLB with authoritative project data.
- Primitive/material selection maps between views and exposes counts, bounds, LOD,
  palette/texture, dropped/approximated feature, and target-limit diagnostics.
- Preview loading is cancellable, cached under a bounded GPU/CPU budget, and releases
  all resources on replacement, panel close, or project close.
- Exported diagnostic GLB is deterministic and labels semantics with no glTF equivalent.

Verification: component/accessibility tests, mapping snapshots, visual authored
fixtures, repeated-open resource profile, cancellation, and debug export re-open.

Implementation: project custom-model metadata now retains source bounds, native
primitive/material/triangle/packet mappings, target counts, palette counts, LOD policy,
and conversion diagnostics. Asset Preview exposes keyboard-accessible source/canonical
and native-target views; the target is rebuilt by semantically reading the stored native
TIE through the SDK frontend exporter. Preview packages are cancellation-aware,
content-keyed, atomically cached without retaining model bytes in process, and reuse the
existing bounded preview scheduler and explicit Three.js/GPU disposal lifecycle. A
deterministic self-contained diagnostic GLB embeds the mapping and non-authoritative
semantic notice. Tests cover metadata boundary validation, both view packages, cache
reuse, mapping serialization, deterministic GLB bytes, and GLB JSON re-open; existing
preview cancellation and repeated-disposal resource tests remain green.

### M14-006 — Place, edit, and bake custom TIE instances

Status: ✅ Complete

Requirements: FR-CUSTOM-007, FR-CUSTOM-004, FR-BAKE-002, NFR-P3-001
Depends on: M14-004, M14-005, M12-007

Expose a place-instance action and route custom TIE definitions through scene editing,
explicit collision, conservative occlusion, bake fingerprints, and UYA composition.

Acceptance:

- Placed instances use ordinary TIE entity transforms, selection, groups, lock/hide,
  duplicate/delete, undo/redo, save, and recovery behavior.
- Properties disclose custom class/Asset IDs, source, target limits, no-collision or
  assigned/generated collision, and always-visible-until-rebuild occlusion status.
- The SDK writes the custom definition/textures once, assigns persisted native slots,
  writes all instances/references, and semantically validates staged output.
- Custom-only changes invalidate only required asset/TIE/archive layers; a failed bake
  retains the prior successful stage.

Verification: two-instance transform/group/collision fixtures, fingerprint tests,
class conflict/capacity failures, deterministic composition, and packed semantic read.

Implementation: project custom models appear as placeable TIE families in Asset Explorer,
with a native GLB/glTF chooser that embeds bounded local sidecars. Placement uses the
persisted custom class allocation and the ordinary entity/history/group/state lifecycle;
scene preview and collision inspection read the verified converted native TIE. Properties
show the source and stable IDs, converted counts against native limits, collision state,
and conservative always-visible status. Static staging materializes one transient canonical
definition per custom class, reuses it for every instance, participates in TIE fingerprints,
and flows through the existing SDK static-asset composer and semantic validation. Reimport
atomically rewrites explicitly selected dependents to the new persisted class and compatible
lighting layout. Tests cover two placed/edited/grouped instances, collision source inspection,
single-definition staging, isolated static-layer invalidation, deterministic output, failed
stage retention, class capacity/conflicts, SDK composition, and packed definition re-read.

Delivered extension (2026-10-18): the shared bounded GLB/glTF import path and M12
terrain writer now also support project-local custom tfrags. One atomic command attaches
the source, local sidecars, converted textures, and canonical partitions; creates a
contiguous primary-owner group range; and preserves reusable piece identities and
authored offsets on reimport. Custom imports preview/render as grouped terrain, use the
ordinary tfrag transform/history lifecycle, and rebuild native terrain, texture tables,
occlusion mappings, render packages, and packed level output. Deleting one child removes
the complete import and undo/redo restores it. These capabilities extend the shipped
custom-asset path but do not change M14-007's custom-TIE qualification gate; their
packaged-app and PCSX2 evidence is tracked by M12-008.

### M14-007 — Qualify the custom TIE pipeline end to end

Requirements: FR-CUSTOM-004 through FR-CUSTOM-007, NFR-P3-001 through NFR-P3-006
Depends on: M14-006

Close the constrained P1 custom-model goal for TIEs in packaged Forge.

Acceptance:

- The authored qualification GLB imports on Linux and Windows to identical canonical
  Asset IDs and documented target semantics.
- Its project moves platforms without source paths, reopens, builds deterministically,
  patches, and renders both instances correctly in PCSX2.
- Shared textures, preview comparison, explicit collision state, class allocation,
  conservative occlusion, diagnostics, and interruption recovery are recorded.
- P1 plan/tasks accurately state what M14 completed and what remains deferred for
  shrubs, mobys, skins, and animation.

Verification: packaged cross-platform record, project artifact hash manifest, PCSX2
captures, interrupted import/build walkthrough, and documentation review.

## M15 — Sound banks and moby sound dependencies

### M15-001 — Freeze UYA 989snd bank, remap, identity, and provenance contracts

Requirements: FR-SOUND-001, FR-SOUND-002, NFR-P3-003
Depends on: M9-009

Inventory representative UYA `sound.bnk` payloads and the current sound-remap composer.
Use the reviewed 989snd runtime only as behavioral/layout evidence until its reuse
terms are established.

Acceptance:

- A checked-in contract identifies container chunks, headers, relative pointers,
  playable entries, grains/opcodes, samples, names/user data, loop/rate metadata,
  alignment, remap slots, limits, and opaque regions.
- The contract separates playable-entry identity from shared sample identity and lists
  every parameter included in each canonical hash.
- No-edit byte/semantic identity and changed-output semantic identity are defined from
  at least two structurally distinct levels without committing retail bytes.
- A provenance note records the 989snd system and implementation of sub-tasks

Verification: corpus structural reports/hashes, synthetic fixture builder, remap
cross-checks, malformed table, and provenance review.

### M15-002 — Implement bounded sound-bank inventory and preview decode in the SDK

Requirements: FR-SOUND-001, FR-SOUND-003, NFR-P3-002, NFR-P3-003
Depends on: M15-001

Add SDK readers for the frozen bank structures and a deterministic supported-entry
render/decode path suitable for preview PCM.

Acceptance:

- Inventory returns typed entries, grains, sample dependencies, playback metadata,
  opaque/unsupported state, provenance ranges, and canonical identities.
- Offset/count/relocation/opcode/graph/decode bounds reject overflow, overlap, cycles,
  excessive expansion, and malformed ADPCM before unsafe allocation.
- Supported direct entries render bounded PCM with documented loop/rate/pan behavior;
  unsupported behavior is reported rather than silently dropped.
- Decode is cancellable and independent from Electron/renderer APIs.

Verification: synthetic direct/shared/looped entries, retail structural hashes,
malformed/adversarial corpus, PCM golden vectors, cancellation, and resource limits.

### M15-003 — Import sounds and remaps as typed project assets

Requirements: FR-SOUND-001, FR-SOUND-002, FR-ASSET-007, FR-REF-001
Depends on: M15-002, M7-004

Project the source bank/remap into game-neutral playable Sound Asset records, shared
sample dependencies, and typed moby/class sound links while retaining source bytes in
the verified base layer.

Acceptance:

- Base entries have stable Sound Asset IDs and provenance without duplicating all base
  bytes into the project; imported/custom dependencies attach required encoded blobs.
- Moby asset metadata resolves every understood remap slot to a Sound Asset ID and
  preserves unsupported/raw slots without guessing.
- Reference indexing powers inbound navigation, delete protection, cleanup, recovery,
  fingerprints, and portable project packaging.
- Missing/wrong-kind/recursive dependencies identify the owner and exact graph edge.

Verification: inventory-to-project snapshots, shared sample deduplication, remap
reorder, missing/cycle/wrong-kind, cleanup/recovery, and ZIP portability tests.

### M15-004 — Build the sound/remap workspace and preview mixer

Requirements: FR-SOUND-003, FR-SOUND-005, NFR-P3-002, NFR-P3-006
Depends on: M15-002, M15-003

Add a virtualized sound workspace, dependency navigation, safe playback controls, and
global direct/ambient preview volume preferences.

Acceptance:

- Rows show stable/native/remap identity, source/batch, moby dependents, sample/loop/
  rate/channel metadata, supported state, and actionable diagnostics.
- Play/stop/seek where supported never auto-plays, has keyboard-accessible state, and
  isolates failure to the selected entry.
- Decode jobs are cancellable/deduplicated and cached under a measured memory budget;
  switching/closing projects stops audio and frees buffers/nodes.
- Direct and ambient sliders persist machine-locally, clamp/mute safely, affect active
  preview immediately, and never dirty or serialize the project.

Verification: component/accessibility tests, large-bank virtualization profile,
preference persistence, rapid-selection cancellation, unsupported entry, and leak test.

### M15-005 — Compose imported sound entries and remap tables

Requirements: FR-SOUND-002, FR-SOUND-004, NFR-P3-001
Depends on: M15-003

Extend the SDK and bake adapter to reuse/append a dependency-complete sound graph,
assign deterministic slots, rebuild aligned sections, and rewrite map remaps.

Acceptance:

- Preflight computes exact retained/reused/appended entries/samples, slot assignment,
  aligned size/capacity, remap changes, and unsupported dependencies before writing.
- Identical content reuses one slot; distinct programs sharing a sample stay distinct;
  all cross-sound references are rewritten to final slots.
- No-edit output meets M15-001 identity and changed output re-reads to planned hashes,
  graphs, loops/rates, and remaps while untouched/opaque content remains preserved.
- Composition is deterministic/cancellable and commits staging atomically.

Verification: no-edit, append, shared sample, cross-sound, conflict/capacity, alignment,
determinism, cancellation, semantic re-read, and bake-fingerprint tests.

### M15-006 — Qualify sound import and playback end to end

Requirements: FR-SOUND-001 through FR-SOUND-005, NFR-P3-001 through NFR-P3-006
Depends on: M15-004, M15-005

Qualify one direct sound and one moby dependency closure through packaged Forge and UYA.

Acceptance:

- Linux and Windows inventory/identity agree and preview the supported direct entry
  consistently without retaining source paths.
- The selected moby sound graph composes into a target level, remaps to the intended
  slot, survives project transfer, and plays in PCSX2 with expected loop/rate behavior.
- Untouched sound/remap entries meet the frozen preservation contract; interrupted or
  invalid composition retains the prior stage.
- Known unsupported grains/streams and the boundary of ambient preview are documented.

Verification: packaged cross-platform record, deterministic hashes, PCM comparison,
PCSX2 audio capture/checklist, and interruption/corruption walkthrough.

## M16 — Cross-map content import

### M16-001 — Freeze per-family import and dependency contracts

Requirements: FR-IMPORT-001, FR-IMPORT-004, NFR-P3-001, NFR-P3-003
Depends on: M7-004, M10-005, M12-007

Inventory every candidate UYA family against its reader, project representation,
references/dependencies, add/remove/replace writer, limits, and qualification evidence.

Acceptance:

- A versioned matrix marks `add`, `replace`, `add+replace`, or `unsupported` with the
  exact reason for TIEs, shrubs, tfrags, mobys, collision, cuboids, spheres, splines,
  paths, areas/map groups, skybox, level settings, directional lights, HUD, FX, and
  opaque content.
- TIE/shrub/tfrag are default-selected only when their closure is writable; skybox,
  level settings, and directional lights are replace-only; HUD/FX/opaque are disabled.
- Each enabled family defines ownership, dependency edges, conflict namespace, native
  append/remove semantics, protected-survivor behavior, and no-edit identity.
- The matrix comes from the selected game adapter rather than renderer conditionals.

Verification: adapter capability snapshots, source maps with absent/empty/malformed
families, dependency fixtures, and UI contract serialization.

### M16-002 — Build donor preflight and dry-run planning

Requirements: FR-IMPORT-001, FR-IMPORT-002, NFR-P3-002, NFR-P3-003
Depends on: M16-001, M1-006

Use the verified project/source flow to inventory a donor map and produce a complete,
cancellable import plan without changing the active project.

Acceptance:

- Game/region/version/source hashes are verified and incompatible sources fail before
  family enumeration; the donor catalog and files remain read-only.
- The plan records a stable batch ID, family modes, exact add/reuse/remap/remove/protect
  sets, dependency reasons, conflicts, capacity/byte estimates, warnings, and blockers.
- Default check state follows FR-IMPORT-001; unavailable choices explain the missing
  reader/project/writer/qualification capability.
- Planning runs off interactive loops, reports progress, bounds graph traversal and
  diagnostics, and cancellation leaves no project/blob changes.

Verification: level40→level41 structural fixture, incompatible source, family default,
large graph, cycle, capacity, cancellation, and deterministic-plan tests.

### M16-003 — Add import-batch provenance and atomic project commands

Requirements: FR-IMPORT-005, FR-IMPORT-006, NFR-P3-004
Depends on: M16-002, M7-002, M7-004

Define import batch records and one transactional command that commits attached blobs,
entities/assets, custom allocations, provenance, and reference changes after validation.

Acceptance:

- A batch records source fingerprint/map, plan/schema version, timestamp, family modes,
  and added/reused/replaced/protected stable IDs without storing donor machine paths.
- Blob staging validates hash/kind/size/parent before one project commit; cancellation,
  crash, or validation failure leaves no visible partial batch.
- Undo/redo/recovery restore every entity, asset, reference, allocation, and provenance
  change as one logical operation while deduplicated blobs follow normal retention.
- Batch records and unknown newer fields migrate/preserve according to project policy.

Verification: transaction fault injection at each phase, history/recovery, schema
migration, blob deduplication, integrity failure, and ZIP round trip.

### M16-004 — Import TIE and shrub families with native conflict remapping

Requirements: FR-IMPORT-003, FR-IMPORT-004, FR-CUSTOM-004
Depends on: M16-003, M14-006

Implement dependency-complete add/replace for static TIE and shrub definitions,
instances, textures/materials, group membership, and supported references.

Acceptance:

- Exact definitions/textures reuse content; different definitions with the same donor/
  target native ID receive deterministic project custom IDs and rewritten instances.
- Add preserves target content and source relationships; replace removes unprotected
  target family content and reports retained items with every inbound owner/field.
- Imported native/custom definitions become project-attached and remain bakeable after
  donor media is removed; the global vanilla catalog is unchanged.
- Native composition re-reads every imported definition/instance/reference and keeps
  untouched target definitions stable.

Verification: identical/colliding class, shared texture, add/replace/protected survivor,
group/reference, donor removal, deterministic bake, and PCSX2 static-content fixtures.

### M16-005 — Append and replace dependency-complete tfrag families

Requirements: FR-IMPORT-003, FR-IMPORT-004, FR-TFRAG-004
Depends on: M16-003, M12-008

Extend native tfrag composition from transforming existing pieces to appending/removing
complete donor owner groups with all texture/material and chunk dependencies.

Acceptance:

- The plan carries whole native owner/group payloads needed for each selected piece and
  never copies display geometry without its reconstructable source structures.
- Texture/material/native group/index conflicts are deterministically allocated and
  every following aligned section/offset is rebuilt rather than space-preserved by luck.
- Add/replace obey protected relationships and preserve untouched primary/chunk data
  under the M12 identity contract.
- Changed output semantically re-reads topology, materials, LODs, bounds, textures, and
  native ownership for imported and retained pieces.

Verification: primary plus nonzero chunk imports, texture conflict/growth, add/replace,
alignment/relocation, determinism, semantic re-read, and PCSX2 rendering fixtures.

Existing foundation (2026-10-18): project-local custom-tfrag import now proves bounded
GLB/glTF-to-native partitioning, attached source/sidecar/texture lifecycle, deterministic
primary-group append/replacement/removal, texture allocation, section relocation,
semantic re-read, and conservative occlusion-map growth. M16-005 remains not started:
it still requires M16 planning/batch provenance, complete donor owner payloads, nonzero
chunk import, protected-survivor rules, and donor add/replace qualification.

### M16-006 — Import remaining validated entity and collision families

Requirements: FR-IMPORT-001, FR-IMPORT-003, FR-IMPORT-004
Depends on: M16-003, M16-001

Enable only the second-wave families whose M16-001 contracts prove complete, such as
collision, cuboids, spheres, splines, paths, areas, or native map groups.

Acceptance:

- Each enabled family has an adapter-owned add/replace implementation, dependency
  closure, stable-ID remap, limits, semantic re-read, and explicit UI capability.
- Cross-family entity/group references are rewritten to imported or retained targets;
  unsupported/missing edges block the affected family rather than becoming null.
- Collision material/sound semantics and group/area membership survive where proven;
  no inference creates ownership absent from source data.
- Families not meeting the gate remain disabled with reasons and do not block the core
  TIE/shrub/tfrag workflow.

Verification: one focused fixture per enabled family, mixed dependency plan, wrong-kind/
missing target, add/replace, packed semantic re-read, and capability regression.

### M16-007 — Import mobys with PVars, references, and sound closure

Requirements: FR-IMPORT-004, FR-SOUND-002, FR-PVAR-001
Depends on: M16-003, M15-006, M9-009

Add dependency-complete moby definition/instance import using project MobyDex overlays,
raw PVars, stable references, model dependencies, and composed sound assets/remaps.

Acceptance:

- Imported raw instance/PVar bytes remain authoritative; known entity/asset references
  resolve within the batch or target and unknown bytes stay exact.
- Moby definitions/models and their transitive Sound Asset graphs attach/deduplicate;
  native class, instance, PVar relocation, entity index, sound slot, and remap conflicts
  are all resolved after final ordering.
- Missing external entity targets present an explicit map/clear/block choice only when
  the verified field is nullable; Forge never silently writes null.
- Replace protects mobys required by retained content and semantic re-read verifies
  every changed instance/PVar/remap/model dependency.

Verification: internal/cross-family/null reference, raw unknown PVar, model class
collision, shared sound graph, add/replace/protected, donor removal, and PCSX2 fixture.

### M16-008 — Add replace-only singletons, origin UI, and batch removal

Requirements: FR-IMPORT-003, FR-IMPORT-005, FR-IMPORT-006, NFR-P3-006
Depends on: M16-004, M16-005, M16-006, M16-007

Complete the user workflow for singleton replacement, origin-aware browsing, batch
reports, and dependency-safe removal.

Acceptance:

- Skybox, level settings, and directional lights expose Replace only and atomically
  replace their exact supported project records with semantic validation.
- Scene/Groups/Asset/Properties/Sound rows label base/project/import origin, donor map,
  and batch without color alone and can filter/navigate to the batch report.
- Remove import recomputes live dependencies, previews delete/retain effects, deletes
  only unreferenced batch-owned content, and reports shared/protected survivors.
- Add/replace/remove dialogs are keyboard accessible, virtualize large plans, and keep
  one authoritative scene/selection state.

Verification: singleton replacement, origin component/accessibility tests, 25k-item
plan profile, shared dependency retention, remove/undo/redo, and stale-plan rejection.

### M16-009 — Qualify cross-map import end to end

Requirements: FR-IMPORT-001 through FR-IMPORT-006, NFR-P3-001 through NFR-P3-006
Depends on: M16-008

Qualify the target scenario from the Phase 3 notes plus a moby dependency case.

Acceptance:

- A level 41 project dry-runs then adds/replaces selected level 40 TIE/tfrag/shrub
  content with the documented defaults and accurate retained-item report.
- A separate selection imports one moby and its PVar/reference/model/sound closure.
- After donor files are unavailable, the project moves Linux↔Windows, reopens, builds
  deterministically, patches, and renders/plays intended content in PCSX2.
- Remove batch, undo/redo, cancellation, capacity failure, and interrupted save/build
  preserve base/shared content and the last known-good state.

Verification: packaged cross-platform record, plan/batch hash manifests, PCSX2 capture,
interruption/recovery checklist, and adapter capability snapshot.

## M17 — Moby simulation framework and ocean plane

### M17-001 — Freeze the renderer simulation lifecycle and service boundary

Requirements: FR-SIM-001, NFR-P3-002, NFR-MAINT-001
Depends on: M7-006, M9-009

Review map-o-matic's fixed-step class registry and define the smaller Forge lifecycle,
immutable input snapshot, renderer services, error boundary, and performance telemetry.

Acceptance:

- The contract keys simulators by game/class and defines create/enable/fixed-update/
  render-update/reset/disable/dispose plus bounded catch-up and invalidation events.
- Inputs are read-only project/entity/PVar/reference snapshots and resolved asset/FX
  services; simulation code has no command/save/bake access.
- Proxy hiding, selection identity, errors, unsupported data, project switch, WebGL
  context loss, and resource ownership have explicit behavior.
- Adding a class does not require a game-specific conditional in shared scene code.

Verification: lifecycle state-machine tests, mock simulator, fixed-step determinism,
error isolation, invalidation, context loss, and resource counter checks.

### M17-002 — Implement the simulation controller and controls

Requirements: FR-SIM-001, FR-SIM-003, NFR-P3-002, NFR-P3-006
Depends on: M17-001

Build the renderer controller/registry, project-level enable state, and compact
play/pause/reset/timing controls without introducing project mutation.

Acceptance:

- Disabled simulations allocate/tick nothing; enabled instances share immutable
  assets, run bounded fixed steps, update rendering, and dispose deterministically.
- Pause/reset and project/scene edits produce deterministic time/state; backlog is
  capped rather than creating a catch-up spiral after a stall.
- Failure falls back to the ordinary proxy with a per-entity actionable diagnostic and
  does not stop other simulators or the editor.
- Controls are keyboard accessible and clearly label preview-only state and timing.

Verification: controller/unit/component tests, 100-instance performance profile,
background/foreground stall, edit invalidation, failure isolation, and leak checks.

### M17-003 — Implement UYA ocean-plane simulation for class `0x0B37`

Requirements: FR-SIM-002, FR-SIM-001
Depends on: M17-002, M8-008, M9-009

Port the verified ocean behavior into Forge-native Three.js/services using an exact
MobyDex/PVar contract and resolved FX assets.

Acceptance:

- The simulator reads verified overlay/underlay FX IDs, wave, tiling/direction/speed,
  colors/blend, height, and hidden-cuboid fields without renderer-owned byte offsets.
- It renders stable camera-relative coverage with documented blending and motion,
  follows entity/height/reference edits, and honors hidden cuboids.
- FX overrides and committed PVar/reference/transform changes invalidate only the
  needed simulation resources and appear without reopening the project.
- Missing/invalid FX, PVar, or cuboid references fall back safely and identify the
  owner field/dependency.

Verification: field golden fixtures, FX override/update, camera/time snapshots,
hidden-cuboid cases, missing dependencies, bounded geometry, and resource reuse.

### M17-004 — Preserve scene interaction and simulation performance

Requirements: FR-SIM-003, NFR-P3-002, NFR-P3-006
Depends on: M17-003

Integrate the derived ocean render objects with ordinary selection, visibility, focus,
layer controls, picking, outline, and renderer performance paths.

Acceptance:

- Picking the simulated surface selects the authoritative moby; Properties/tree names,
  Focus, hide/disable/lock, and keyboard selection remain consistent.
- Derived geometry is not duplicated by outline/selection passes and does not enter
  scene entity counts, project serialization, collision, or bake.
- Camera-following patch density and update frequency are bounded and profiled at
  representative resolutions; disabled/offscreen work is skipped where valid.
- Repeated toggle/edit/project-close cycles return GPU/CPU counters to baseline.

Verification: interaction/component tests, outline/triangle counters, FPS/frame profile,
offscreen/disabled checks, context loss, and repeated-cycle leak test.

### M17-005 — Qualify ocean simulation fidelity

Requirements: FR-SIM-001 through FR-SIM-003, NFR-P3-001 through NFR-P3-006
Depends on: M17-004

Record behavioral and visual evidence against the local map-o-matic implementation and
PCSX2 for representative UYA ocean content.

Acceptance:

- Forge, map-o-matic, and PCSX2 comparisons cover multiple camera positions, times,
  PVar values, FX overrides, blend modes, and hidden cuboids.
- Differences are either corrected or documented as disclosed editor-preview limits.
- Packaged Linux/Windows behavior, fixed-step reset, live edit, selection, and resource
  budgets pass with no effect on output when only the simulation toggle changes.
- The framework documentation explains how the next verified class is added.

Verification: packaged comparison record, visual captures, timing/resource report,
project-dirty/bake-fingerprint non-mutation tests, and extension guide review.

## M18 — Occlusion generation and analysis

### M18-001 — Freeze native occlusion semantics and performance baselines

Requirements: FR-OCC-001, NFR-P3-001, NFR-P3-002
Depends on: M2-009, M3A-006

Inventory the existing SDK grid/mapping readers/writers and representative UYA maps.
Measure Forge v1's volume/node workflow and the current Forge scene on the same local
reference hardware before selecting an algorithm or production budget.

Acceptance:

- A checked-in contract defines octant/grid coordinates, indexes, masks, TIE/tfrag/moby
  mappings, bit/capacity limits, ordering, alignment, empty/always-visible behavior,
  and exact source/native writer ownership.
- Synthetic fixtures prove source no-edit byte/semantic identity and changed mapping
  semantic identity; unsupported families/unknown fields are enumerated.
- The benchmark procedure records map, settings, CPU/GPU/driver, time, peak CPU/GPU
  memory, readback/work counts, and output cardinality, including level 45 multiplayer.
- A production performance budget and minimum improvement over Forge v1 are adopted
  from evidence rather than guessed; correctness remains the primary gate.

Verification: contract tests, corpus structural hashes, no-edit writer tests, repeatable
benchmark script/procedure, and reviewed baseline report.

### M18-002 — Add versioned generation inputs and automatic sample coverage

Requirements: FR-OCC-002, NFR-P3-001, NFR-P3-003
Depends on: M18-001, M11-004

Define game-neutral generation settings and derive deterministic candidate camera
cells/samples from collision, level bounds, and adapter limits, with tagged cuboid
include/exclude/density overrides.

Acceptance:

- Project settings version cell/sample density, height/clearance, FOV/orientation set,
  feathering/tolerances, target families, and override cuboid roles with bounded ranges.
- Automatic coverage handles exterior/interior/airborne/scripted gaps conservatively;
  override cuboids have visible provenance and deterministic precedence.
- A representative coverage audit records missed valid camera routes. It either closes
  the fallback as unnecessary or activates M18-009 with concrete failing fixtures and
  a frozen minimal manual-volume/node-graph contract.
- Identical authoritative inputs produce the same ordered candidate set/fingerprint
  independent of thread count/platform.
- Empty/unbounded/malformed collision and excessive candidate counts fail or require an
  explicit bounded override before generation, never allocate unbounded work.

Verification: exterior/interior/air path synthetic scenes, cuboid precedence, empty/
huge bounds, platform/thread determinism, settings migration, and fingerprint tests.

### M18-003 — Implement the conservative CPU reference solver

Requirements: FR-OCC-003, FR-OCC-004, NFR-P3-001, NFR-P3-003
Depends on: M18-002

Build a bounded SDK/reference implementation for small fixtures that classifies
occluders and computes conservative tie/tfrag/moby visibility for each sample/view.

Acceptance:

- Material classification distinguishes opaque occluders from transparent/cutout/
  translucent/non-depth-writing surfaces while still allowing those objects to be
  visible occludees.
- Frustum/FOV/orientation, bounds/geometry intersection, backface/double-sided state,
  feathering, numeric tolerances, and uncertainty-visible behavior are specified and
  tested.
- Results are deterministic across Linux/Windows and expose per-object reasons useful
  for later GPU diffs; work/memory/diagnostics are bounded and cancellable.
- Synthetic ground truth contains thin blockers, holes, boundary objects, transparent
  walls, layered/multipass materials, interiors, and large coordinates.

Verification: ground-truth suite, cross-platform result hashes, cancellation/bounds,
precision stress, and reviewed real-map sample diffs against source masks.

### M18-004 — Spike and select the accelerated visibility backend

Requirements: FR-OCC-004, NFR-P3-002
Depends on: M18-001, M18-003

Prototype the smallest viable raster/depth-ID, ray/cone, or hybrid GPU approaches using
Forge's packaged renderer constraints, then write an architecture decision from data.

Acceptance:

- Candidates run the same representative sample set and report setup, draw/dispatch,
  readback, total time, CPU/GPU memory, driver/API support, cancellation granularity,
  and mismatch reasons against the CPU reference.
- Tests include supported Linux/Windows GPU paths, integrated/discrete hardware where
  available, context/device loss, and a software/headless fallback story.
- The selected backend meets the conservative equivalence rule or documents the exact
  CPU verification/postprocess that makes it safe.
- Rejected prototypes do not remain as production abstractions or dependencies; the
  decision fixes only interfaces justified by the selected implementation.

Verification: benchmark/diff report, packaged capability probes, context/device-loss
test, architecture decision record, and dependency/license review.

### M18-005 — Implement production generation, progress, cancellation, and cache

Requirements: FR-OCC-004, NFR-P3-001, NFR-P3-002
Depends on: M18-004

Implement the selected accelerated backend behind the frozen solver contract with
bounded work units, conservative canonicalization, and fingerprinted cache/invalidation.

Acceptance:

- Generation stays off renderer/Electron loops, reports candidate/visibility/verify/
  compose phases, and cancels at safe checkpoints without replacing good results.
- Work chunks bound GPU time, CPU/GPU memory, readback, and diagnostic volume to avoid
  driver watchdogs and the Forge v1 per-octant scene-rebuild pattern.
- GPU results are canonicalized to stable native sets and checked against reference
  fixtures; uncertainty/mismatch retains visibility and reports the reason.
- Cache keys include all geometry/material/settings/backend-contract inputs; local edits
  invalidate only provably affected work, otherwise trigger a safe full rebuild.

Verification: responsive UI profile, cancel at every phase, cache hit/miss/invalidation,
device loss, cross-vendor result comparison, memory ceiling, and level45 benchmark.

### M18-006 — Build source/generated diff and POV probe tooling

Requirements: FR-OCC-005, NFR-P3-006
Depends on: M18-005

Add an occlusion workspace plus transient scene probe for selecting octants/samples,
placing/rotating a POV pin, adjusting supported FOV, and visualizing native visibility.

Acceptance:

- Source/generated modes and added/removed/unchanged visibility sets are distinct
  without color alone and link rows/objects by stable entity/mapping ID.
- Selecting an octant or moving/rotating the POV pin updates scene visibility from the
  corresponding precomputed mask without changing entity Hidden state or history.
- Probe controls support keyboard manipulation, visible focus, camera synchronization,
  selection/focus actions, and an explicit reset/exit that restores ordinary rendering.
- Large octant/object sets use indexed masks/instancing/aggregation and do not duplicate
  normal geometry or selection outlines.

Verification: component/accessibility tests, mask correctness, hidden-state invariance,
selection/outline counters, 25k-object/large-grid profile, and reset/project-close tests.

### M18-007 — Add visibility cost graph and heatmap

Requirements: FR-OCC-006, NFR-P3-002, NFR-P3-006
Depends on: M18-006

Compute and present a disclosed relative rendering-cost estimate for each generated
octant/view, calibrated initially with level 45 multiplayer evidence.

Acceptance:

- The model counts visible triangles plus verified material pass, double-sided, and
  multipass weights; unknown costs are labeled and do not masquerade as measured time.
- Graph/heatmap rank worst views, aggregate large datasets, filter source/generated,
  and activate the corresponding probe/sample.
- Recorded PCSX2 GS and selected real-hardware measurements can be attached to a
  calibration report without entering deterministic project output.
- UI remains keyboard accessible, scalable, and bounded in DOM/GPU work.

Verification: cost math fixtures, ranking/filter/navigation components, large-grid
profile, color-independent legend, and level45 calibration walkthrough.

### M18-008 — Compose and stage generated native occlusion

Requirements: FR-OCC-007, FR-OCC-001, NFR-P3-001
Depends on: M18-005, M18-006, M16-009

Extend the SDK writer/bake adapter to compose generated grids and mappings, validate
native capacity, and stage an independent occlusion layer atomically.

Acceptance:

- Preflight reports grid/mapping sizes, per-family/object coverage, unmapped entities,
  always-visible fallbacks, native limit violations, cache/backend provenance, and
  exact sections that will change.
- Source mode remains byte/semantically unchanged; generated mode deterministically
  assigns mapping indexes and re-reads to the planned visibility sets.
- Custom/imported entities are included through stable IDs; removed/reordered native
  lists cannot redirect a mask to the wrong object.
- Cancellation/failure retains the prior successful stage and only occlusion-dependent
  layers invalidate for a settings/visibility-only change.

Verification: source no-edit, generated compose, list reorder, custom/imported entity,
capacity/alignment, determinism, semantic re-read, cancellation, and fingerprint tests.

### M18-009 — Add fallback manual coverage authoring if the audit requires it

Requirements: FR-OCC-002, NFR-P3-001, NFR-P3-006
Depends on: M18-002

This task is conditional. Implement the minimal Forge v1-style volume/node coverage
authoring contract only when M18-002 records camera routes that automatic collision
coverage plus cuboid overrides cannot represent safely. Otherwise close it with the
audit evidence and no code.

Acceptance when activated:

- Users place/edit bounded coverage volumes and connect a validated acyclic or otherwise
  explicitly supported node graph that augments sample coverage, not visibility truth.
- The graph has stable IDs, undo/redo/save/recovery, clear invalid/missing/cycle
  diagnostics, keyboard-accessible editing, and deterministic generation fingerprints.
- Automatic coverage remains the default; manual data is opt-in, visible, and reports
  exactly which samples/routes it adds.
- Import, validation, and generation are bounded; no Unity/Forge v1 runtime dependency
  or opaque graph execution is introduced.

Verification when activated: graph command/validation tests, failing-route fixtures,
automatic-versus-augmented diff, determinism, accessibility, and malformed graph tests.

### M18-010 — Qualify occlusion correctness and performance

Requirements: FR-OCC-001 through FR-OCC-007, NFR-P3-001 through NFR-P3-006
Depends on: M18-007, M18-008, M18-009

Close the milestone on representative exterior/interior maps and level 45 multiplayer
using packaged Forge, PCSX2, and selected real hardware.

Acceptance:

- Generation meets the M18-001 time/memory/responsiveness budget on reference hardware
  and records CPU fallback behavior; cancellation/interruption keeps good output.
- Qualification routes and camera probes find no known potentially visible object
  incorrectly culled; conservative extras and source/generated diffs are documented.
- Packed output respects native limits, loads in PCSX2/real hardware, and the level45
  comparison records visibility and GS/performance evidence without overstating it.
- Linux/Windows deterministic contract, cache invalidation, heatmap/probe accessibility,
  and custom/imported entity coverage pass.

Verification: packaged benchmark/route record, native output semantic hashes, PCSX2 and
hardware captures, cross-platform run, interruption test, and accessibility review.

## M19 — P3 integration and release qualification

### M19-001 — Assemble the portable P3 qualification project

Requirements: NFR-P3-001, NFR-P3-004, NFR-P3-005
Depends on: M14-007, M15-006, M16-009, M17-005, M18-010

Create a documented local qualification procedure/project manifest combining one custom
TIE, cross-map static/terrain content, imported moby sound closure, ocean simulation,
and generated occlusion without committing proprietary payloads.

Acceptance:

- The manifest records only redistributable authored fixture data plus hashes/instructions
  for local retail-derived evidence; donor/source absolute paths are absent after import.
- Stable Asset IDs, custom class allocation, import batches, sound remaps, simulation
  expectations, generation settings, and occlusion fingerprints are auditable.
- The project moves Linux↔Windows, reopens, validates, and produces deterministic output
  under the documented contract.

Verification: clean-machine/package walkthrough, project ZIP manifest, cross-platform
hash comparison, integrity scan, and data-policy review.

### M19-002 — Run combined safety, performance, and accessibility regression

Requirements: NFR-P3-002, NFR-P3-003, NFR-P3-006
Depends on: M19-001

Exercise P3's dense views and long-running operations together rather than relying only
on isolated milestone profiles.

Acceptance:

- Import planning/commit, preview decode, simulation, and occlusion generation remain
  responsive within milestone budgets and do not leak CPU/GPU/audio/file resources.
- Malformed GLB, donor map, sound bank, project graph, and generation settings stay
  bounded and cannot escape project/source path policy.
- Keyboard/focus/scaling/color-independent checks cover model compare, import plan,
  sound workspace, simulation controls, occlusion diff/probe, and heatmap.
- Fault injection/cancellation at every long-operation commit boundary retains the last
  known-good project, attached assets, stage, and development ISO.

Verification: packaged stress/resource profile, adversarial suite, accessibility matrix,
and interruption/recovery report on Linux and Windows.

### M19-003 — Close the P3 release gate

Requirements: all P3 requirements
Depends on: M19-002, M13-007

Complete combined build/patch/runtime evidence and reconcile planning/docs with the
implemented contracts, capabilities, deferrals, and known limitations.

Acceptance:

- The portable project deterministically bakes/packs/patches and loads in PCSX2; selected
  real-hardware custom/imported/sound/occlusion checks pass.
- Every M14-M18 exit gate and required packaged Linux/Windows record is linked; open
  limitations have actionable diagnostics and an explicit later milestone/defer status.
- User/contributor docs explain custom TIEs, donor import modes/protection, sound preview,
  simulation preview limits, occlusion generation/probing, portability, and recovery.
- Requirements/tasks/status tables match reality and no incomplete item is marked done.

Verification: release checklist, CI/package results, PCSX2/hardware record, documentation
link audit, `git diff --check`, and final milestone review.
