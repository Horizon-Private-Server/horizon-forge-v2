# P3 task register — Content expansion and runtime fidelity

Plan and requirements: [P3 — Content expansion and runtime fidelity](../P3-content-expansion.md)

Tasks are ordered by dependency within each milestone. Discovery tasks freeze binary,
identity, ownership, and performance contracts before project schemas or UI depend on
them. Unless marked complete, every task is `not-started`.

## Current milestone status

| Milestone | Status | Gate |
| --- | --- | --- |
| M14 — Custom TIE pipeline | 🚧 In progress | M14-007 |
| M15 — Sound banks | 🚧 In progress | M15-006 |
| M16 — Cross-map import | 🚧 In progress | M16-009 |
| M17 — Moby simulations | 🚧 In progress | M17-003 visual validation |
| M18 — Occlusion rebuild | 🚧 Existing-grid GPU builder + viewport inspection | M18-001 through M18-010 |
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

Status: ✅ Complete

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
- A provenance note records the 989snd system and the implementation boundary for
  later subtasks.

Verification: corpus structural reports/hashes, synthetic fixture builder, remap
cross-checks, malformed table, and provenance review.

Implementation: [`UYA 989snd bank and remap contract v1`](../../features/uya-989snd-contract-v1.md)
freezes the observed type-3/two-chunk container, `SBlk` v3 tables and relocations,
playable sound/grain/tone records, PS2 ADPCM sample and loop boundaries, optional
name/user-data flags, asset-header remap slots, opaque regions, native/operational
limits, and separate canonical program/sample identities. It defines byte-exact
no-edit preservation and semantic changed-output re-read without committing retail
payloads. The SDK contract fixture builds one direct synthetic bank/remap pair, rejects
bad chunk arithmetic, grain windows, and unterminated samples, and cross-checks hashes,
structure, sample termination, and remap indexes for all ten locally available UYA
levels. The provenance boundary records the reviewed `dec989snd` commit and runtime
hash, notes the missing root license, and permits behavioral/layout evidence only—no
source reuse or redistribution.

### M15-002 — Implement bounded sound-bank inventory and preview decode in the SDK

Status: ✅ Complete

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

Implementation: the game-neutral SDK `SoundBankReader` dispatches UYA banks to an
independent bounded `SBlk` v3 reader, PS2 SPU ADPCM decoder, and recovered 240 Hz grain
interpreter. Inventory reports typed programs, grains, tone metadata, canonical
program/sample identities, dependency and provenance ranges, loops, and exact preview
blockers. Static single tones retain bounded mono PCM16 with effective rate, gain/pan,
and loop metadata; compound production programs render bounded stereo PCM16 with
layering, timing, random/cycle selection, registers, control flow, voice controls,
pitch, and LFO state. Synthetic PCM/control fixtures, malformed bounds, cancellation,
all ten local UYA banks, and one real program per observed opcode qualify the boundary.

### M15-003 — Import sounds and remaps as typed project assets

Status: ✅ Complete

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

Implementation: project schema 23 stores game-neutral sound programs, their raw typed
grain parameter payloads, separately content-addressed shared sample assets, source
ranges, playback metadata, global remaps,
and ordered moby/class slot links. The UYA adapter projects the bounded SDK inventory,
resolves only qualified programs, retains native indexes as provenance, and records
unsupported slots with actionable blockers. Base bank and remap bytes remain in the
verified opaque layer; imported sample payloads use ordinary integrity-checked project
attachments. Asset-to-asset reference indexes cover program/sample and moby/program
edges, and sound assets participate in delete protection, cleanup, recovery,
fingerprints, and portable ZIP projects. Validation reports the exact owner edge for
missing, wrong-kind, or recursive dependencies.

### M15-004 — Build the sound/remap workspace and preview mixer

Status: ✅ Complete

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
  authored random previews retain only their active seek buffer and regenerate on replay;
  switching/closing projects stops audio and frees buffers/nodes.
- Direct and ambient sliders persist machine-locally, clamp/mute safely, affect active
  preview immediately, and never dirty or serialize the project.

Verification: component/accessibility tests, large-bank virtualization profile,
preference persistence, rapid-selection cancellation, unsupported entry, and leak test.

Implementation: Forge exposes the active project's typed sound/remap inventory through
bounded bridge payloads and a virtualized Sounds panel available from View → Panels.
Rows retain stable and native identity, source/batch, global and moby remap slots,
dependency navigation, sample/rate/channel/loop metadata, preview support, and isolated
diagnostics. User-initiated play/stop/seek requests use the cancellable SDK decode path,
asynchronous Web Audio PCM conversion, gain/pan/loop metadata, deduplicated in-flight
loads, and a measured 32 MiB decoded-buffer LRU. Project switches and closes cancel
work, stop sources, disconnect nodes, close the audio context, and clear the cache.
Machine-local direct and ambient volume/mute preferences are clamped and excluded from
project state; direct changes affect active bank playback immediately, while ambient
preferences are reserved for the specified later ambient
simulation path. Bridge round trips, large-bank windowing, preference validation,
rapid-selection cancellation, cache bounds, seek/mute behavior, and disposal tests
qualify the workflow.

The preview transport accepts mono direct clips and interleaved stereo compound mixes.
Projects created before compound preview support refresh stale support/identity labels
from their verified retained bank without mutating project data.

### M15-005 — Compose imported sound entries and remap tables

Status: ✅ Complete

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

Implementation: the game-neutral SDK `SoundBankComposer` dispatches UYA/DL 989snd
composition to a bounded writer that retains the source grain-data and sample-chunk
opaque bytes, reuses existing samples, appends dependency-complete programs/samples,
rewrites same-bank program edges, enforces native/SPU/24-bit limits, sector-aligns the
container, and semantically re-reads the result. Compound identities use canonical
child identities rather than donor-native slots. The UYA remap writer preserves opaque
other records, emits Moby/global lists against final program slots, appends the verified
block to the rebuilt asset header, and semantically re-reads it.

Forge persists raw typed grain parameter payloads in project schema 23. Its UYA
composition preflight reports retained/reused/appended programs, appended samples,
final slots, and output size; base programs retain their native order, while new assets
append in stable Asset-ID order. Unchanged banks remain byte-exact. Changed bank/header
payloads enter the existing in-memory level archive replacement transaction, participate
in the Moby bake fingerprint, and replace the opaque sound sections only after bounded
composition succeeds. Synthetic and ten-level corpus tests cover no-edit identity,
append/reuse, shared samples, child-slot and Moby-remap rewriting, opaque retention,
capacity/conflict rejection, alignment, determinism, cancellation, and semantic re-read.

### M15-006 — Qualify sound import and playback end to end

Status: 🚧 In progress — automated and packaged-Linux startup gates pass;
packaged sound, Windows parity, and PCSX2 playback evidence remain

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
- Known external plug-in/cross-bank/stream limits and the boundary of ambient preview
  are documented.

Verification: packaged cross-platform record, deterministic hashes, PCM comparison,
PCSX2 audio capture/checklist, and interruption/corruption walkthrough.

Qualification record: [`M15 sound import and playback qualification`](../../qualification/M15-sound-import.md).
The ten-level UYA/50-bank Deadlocked SDK corpus, corpus-backed level 41 to level 03
composition/remap fixture, Forge portability/recovery tests, and self-contained Linux
package startup smoke pass. The task remains open for the recorded packaged sound UI,
Windows, and listen-in-PCSX2 rows. M16-007 supplies the final one-action Moby closure
workflow and may proceed on the completed M15-005 writer boundary; M15-006 remains a
qualification gate for M16-009 and the P3 release.

## M16 — Cross-map content import

### M16-001 — Freeze per-family import and dependency contracts

Status: ✅ Complete

Requirements: FR-IMPORT-001, FR-IMPORT-004, NFR-P3-001, NFR-P3-003
Depends on: M7-004, M10-005, M12-007

Inventory every candidate UYA family against its reader, project representation,
references/dependencies, add/remove/replace writer, limits, and qualification evidence.

Acceptance:

- A versioned matrix marks `add`, `replace`, `add+replace`, or `unsupported` with the
  exact reason for TIEs, shrubs, tfrags, mobys, collision, cuboids, spheres, splines,
  paths, areas/map groups, skybox, level settings, directional lights, HUD, FX,
  occlusion, and
  opaque content.
- TIE/shrub/tfrag are default-selected only when their closure is writable; skybox,
  level settings, directional lights, HUD, FX, and occlusion are replace-only; opaque content is disabled.
- Each enabled family defines ownership, dependency edges, conflict namespace, native
  append/remove semantics, protected-survivor behavior, and no-edit identity.
- The matrix comes from the selected game adapter rather than renderer conditionals.

Verification: adapter capability snapshots, source maps with absent/empty/malformed
families, dependency fixtures, and UI contract serialization.

Implementation: the versioned
[`UYA content-import capability contract v1`](../../features/uya-content-import-contract-v1.md)
defines all 18 stable family IDs, intended modes, readiness/default-selection rules,
ownership, transitive dependencies, conflict namespaces, native composition,
protected-survivor behavior, no-edit identity, limits, and evidence. The UYA adapter is
the sole matrix owner. Its bounded source-state evaluator distinguishes unknown,
absent, empty, present, and malformed families while keeping every unfinished M16
family non-selectable with its exact owning-task blocker. Bridge opcode
`GetContentImportCapabilities` carries the complete game-neutral matrix through matching
C# and TypeScript codecs. Synthetic snapshots cover the full ordered matrix, core
defaults, singleton modes, unsupported opaque content, malformed diagnostics,
target rejection, and deterministic serialization without proprietary donor bytes.

### M16-002 — Build donor preflight and dry-run planning

Status: ✅ Complete

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

Implementation: [`UYA content-import planning v1`](../../features/uya-content-import-planning-v1.md)
adds a cancellable `PlanContentImport` host operation over the existing verified-source
and base-level reader. It verifies the clean disc and target source identity before
inventory, records both disc and level hashes, opens the catalog without write-side
setup or cleanup, and derives a stable versioned batch ID plus all 18 family plans from
one target snapshot. Plans carry stable add/reuse/remap/remove/protect sets, dependency
reasons, conflicts, capacity and byte estimates, warnings, and exact capability blockers.
Synthetic level40→level41, incompatible-profile, reference-cycle, protected-survivor,
large-capacity, cancellation, read-only catalog, and deterministic C#/TypeScript codec
checks run without proprietary disc bytes.

### M16-003 — Add import-batch provenance and atomic project commands

Status: ✅ Complete

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

Implementation: [`Content-import batches v1`](../../features/content-import-batches-v1.md)
adds the schema-21 append-only provenance ledger and one stale-plan-guarded project
transaction. It computes and verifies all staged Asset IDs and metadata, integrity-checks
deduplicated blobs, validates the complete prospective content graph, and publishes it
with one state swap. The editor records that swap as one undo/redo entry; recovery,
save, and ZIP transfer retain the same entities, references, allocations, assets, and
batch provenance. Boundary fault injection, cancellation, corrupt existing blobs,
schema migration/unknown-field policy, history, recovery, deduplication, and portable
round trips are covered by the host qualification suite.

### M16-004 — Import TIE and shrub families with native conflict remapping

Status: ✅ Complete

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

Implementation: the UYA static-family importer commits reviewed TIE/shrub plans through
the M16-003 transaction, attaches verified canonical definitions and their complete
model/material/texture payloads, assigns deterministic stable entity/group identities,
and rewrites colliding instances through per-family `0x9000` custom allocations. Exact
content reuses retained target classes; add preserves target content, while replace
removes only unprotected entities and reports the retaining owner and field. Imported
static instances own their linked collision: replace previews and removes exclusively
attached pieces, while shared pieces retain only surviving instance links. Imported
groups retain stable membership for the structural map-group composer.
Static staging and final asset composition read project-attached donor definitions after
the donor catalog is unavailable, conservatively remap inserted TIE visibility, remove
unused definitions only for replaced families, and preserve untouched families. The
host qualification covers deterministic preparation, TIE/shrub add and replace,
identical/colliding classes, protected group survivors, donor removal, native instance
re-read, and deterministic staged output. M16-008 owns the user-facing apply workflow
and M16-009 owns packaged Forge/PCSX2 qualification.

### M16-005 — Append and replace dependency-complete tfrag families

Status: ✅ Complete

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

Implementation: tfrag planning now selects complete donor owners rather than display
pieces, expands protection across each target owner, and blocks streamed owners without
a matching target chunk. The atomic M16 transaction attaches each raw native owner,
canonical three-LOD pieces, used terrain textures, stable piece/owner IDs, and batch
provenance, so later baking does not require the donor ISO or catalog. Add appends after
retained target groups; replace removes only complete unprotected owners. The SDK
exposes its existing validated raw-group copier as a game-neutral native-owner append,
rebuilding aligned records and offsets while preserving donor payload bytes. Forge
applies edits against the attached donor owner, allocates global terrain textures, and
finalizes both primary and nonzero chunk groups. Synthetic primary/chunk add/replace,
determinism, portability, native relocation, semantic re-read, and the SDK's extracted
terrain fixture suite cover the implementation; M16-009 owns packaged Forge/PCSX2
workflow qualification.

### M16-006 — Import remaining validated entity and collision families

Status: ✅ Complete

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

Implementation: the M16-001 gate now enables collision, cuboids, spheres, splines,
grind paths, areas, and replace-only native map groups. The UYA geometry importer commits reviewed add/replace plans
through the shared atomic transaction,
assigns deterministic stable IDs, preserves protected targets named by groups, level
settings, PVars, collision bindings, and other typed inbound references, and never
clears an unsupported edge. Complete shape and variable path sections are rebuilt in
final project order through the SDK, including retained/imported transforms, path
points, bounds, opaque native record headers, and remapped camera-collision entries.
Project schema 24 keeps imported path metadata portable, so projects bake
deterministically after save/reopen without donor media. Capability, mixed-plan,
protected-survivor, add/replace, deterministic commit, native semantic re-read, and
camera-collision remap and area-link fixtures cover all five enabled families. Area composition rebuilds all five
typed spline/shape link tables after final geometry ordering, automatically maps selected
donor geometry, and requires an explicit Map/Clear choice for other nullable links.
The collision importer attaches each selected donor payload, assigns deterministic
stable IDs, preserves raw face types and source component identity, imports both solid
pieces and player barriers, and remaps explicit TIE/shrub associations only when their
owning families are selected. Complete composition rebuilds native indexes and octants,
retains untouched target pieces in Add mode, removes the reviewed target family in
Replace mode, and remains bakeable after donor catalog removal. Synthetic add, replace,
batch removal, portability, solid, and player-barrier fixtures plus a real level 05 to
level 46 preparation cover the path. Map groups store ordered stable member IDs and
rebuild native indexes after final Moby/TIE/shrub ordering; the plan requires every
represented member family to be replaced in the same batch. M16-008 owns the user-facing
apply workflow and M16-009 owns packaged Forge/PCSX2 qualification.

### M16-007 — Import mobys with PVars, references, and sound closure

Status: ✅ Complete

Requirements: FR-IMPORT-004, FR-SOUND-002, FR-PVAR-001
Depends on: M16-003, M15-005, M9-009

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

Implementation to date: the UYA adapter now prepares one atomic Moby-family commit,
attaches verified canonical definitions, deterministically rebases class and UID
collisions, preserves native records and unknown PVar bytes, remaps typed internal Moby
links after final ordering, clones built-in MobyDex schemas for rebased classes, and
imports each exact-source transitive program/sample/remap closure. Replace protects
planned survivors, removes obsolete Moby sound maps, and participates in full static-
asset recomposition. Imported model/PVar output semantically re-reads and remains
bakeable from project attachments after donor removal. Native Moby groups are validated
against instance records, collision-rebased into stable project groups, rebuilt after
final instance ordering, and packed from staged output. The capability planner enables
only donors whose entire Moby family is safe. Native Moby-link relocation entries now
become schema-independent typed references, so they remap through stable Entity IDs
even when MobyDex does not name the field; genuinely missing relocation metadata and
malformed group membership remain blockers. Typed external references are exposed as
bounded requirements; apply must explicitly map each one to a compatible retained
stable Entity ID or clear a verified nullable field. Bake resolves mapped cuboid/spline
and other supported gameplay targets back to their final native indexes while preserving
unrelated PVar bytes. Missing, stale, duplicate, incompatible, and unused choices fail
atomically. The M16-008 workflow owns presenting those choices.

### M16-008 — Add replace-only singletons, origin UI, and batch removal

Status: ✅ Complete

Requirements: FR-IMPORT-003, FR-IMPORT-005, FR-IMPORT-006, NFR-P3-006
Depends on: M16-004, M16-005, M16-006, M16-007

Complete the user workflow for singleton replacement, origin-aware browsing, batch
reports, and dependency-safe removal.

Progress (2026-10-22): Forge now exposes the host's authoritative content-import
dry-run from **File → Import Map Content…**. The dialog discovers donor levels,
selects add/replace modes, reports add/reuse/remap/remove/retain effects and blockers,
supports cancellation, and bounds rendered item details for large plans. Approved
TIE, shrub, tfrag, cuboid, sphere, and Moby selections now revalidate against live
editor state and commit as one undoable atomic batch. Plans expose every typed external
Moby PVar requirement and compatible retained target; the preview requires an explicit
Map or nullable Clear choice and the host revalidates every choice atomically. Shared
donor verification and extraction now use bounded, file-versioned caches, so selection
previews and Apply rerun the live dependency plan without repeatedly hashing the whole
ISO or extracting the same donor level; switching levels can also reuse disc verification.
The cached donor snapshot also retains its validated family inventory, warnings, and
external Moby-reference requirements, avoiding repeated donor-wide scans while target-
dependent protection and stale-plan checks still rerun.
Post-commit entity and selection reconciliation now uses stable-ID indexes instead of
quadratic full-list scans, keeping large imported scenes responsive without changing
event or history ordering. Planning likewise indexes retained reference candidates and
group/settings membership once, while family commits index target entities and assets;
large replacements no longer rescan the full project for every imported item.
Read-only projections of the active immutable state reuse its current fingerprint, and
multi-family Apply no longer serializes an unused final projection after preparation.
Repeated tfrag, HUD, and FX imports now classify every project-owned payload as reused
without restaging and re-hashing those blobs, while editor snapshots build import-origin
and entity lookup indexes in one pass.
The preview UI likewise indexes scene entities once for reference choices and only
formats the 25 visible detail rows per family, so large plans do not allocate or rescan
their complete item inventories during React renders.
Donor streamed tfrag owners retain matching target chunks when available and otherwise
relocate as complete native owners into the target primary terrain, allowing imports
between maps with different chunk inventories without synthesizing unsupported chunks.
Unsupported opaque content remains an
internal compatibility/inventory family and is no longer shown as an import option.
Shared content origins now expose base, project, or imported state plus donor level and batch ID. Imported scene rows
are labeled and searchable by origin, level, or batch; imported group headers are
labeled; Properties shows the full identity; Sound already reports source/batch; and
Asset Preview reports the selected asset's distinct project-use origins. Skybox Replace
now swaps the complete shell composition, attaches the donor payload, validates the
result before an atomic commit, and blocks protected target shells. Level-settings
Replace now patches the validated target-native record after automatically rebinding
selected donor cuboids or collecting explicit retained-target Map/Clear choices. The
combined plan no longer protects geometry referenced only by settings being replaced.
Directional-light Replace now atomically rebuilds the bounded whole array, preserves the
target-native header/trailing bytes, and blocks plans whose retained or imported TIE
selectors cannot address the donor lights. HUD Replace now attaches the donor header,
all five physical banks, and normalized preview textures as portable project content;
the existing HUD composer then stages every bank from that verified source without the
donor map or catalog. FX Replace now attaches the complete normalized donor texture bank,
preserves its ordered indexes, and uses the SDK whole-bank composer to grow or shrink the
target bank while relocating unrelated target asset-WAD payloads. The Imports panel now projects the append-only
batch ledger without copying its large stable-ID sets, filters by source/family/mode,
reports result totals and live entities/groups, and links Properties and Asset Preview
origins to one-click Scene filtering, selection, and entity focus. Each batch now offers
an authoritative dependency-safe removal preview. Add-mode content is removed only when
no retained entity, group, level setting, collision binding, tfrag owner, sound graph,
class allocation, or attached-asset dependency needs it. Replace-mode results remain
explicit survivors because v1 provenance does not retain displaced target snapshots.
Apply revalidates the target fingerprint, commits atomically, participates in undo/redo,
and leaves the append-only provenance record and recovery blobs intact.

Acceptance:

- Skybox, level settings, directional lights, HUD, FX, and occlusion expose Replace only and atomically
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

Progress (2026-10-21): a level 41 Hoven project successfully replaced selected static
and terrain content from level 05, rebuilt, and displayed the imported content in-game.
The same run verified that original map sounds remained intact and imported Moby sounds
played. A subsequent Forge build also verified donor skybox replacement in-game. This
is useful integration evidence but does not replace the specified level 40, portable-
package, batch-removal, interruption, or deterministic hash qualification. The
[M16 qualification record](../../qualification/M16-cross-map-import.md) now maps the
automated coverage, freezes the adapter capability payload SHA-256, records the
provisional development observations, and provides the retained hash, packaged runtime,
and interruption checklists needed to close the remaining rows. The preview also copies
a deterministic, path-free, self-checking qualification JSON record retaining the
host-generated plan/batch identity, source/target hashes, selected-family counts, and
explicit reference choices.

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

Status: ✅ Complete

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

Implementation: [`moby-simulation-lifecycle-v1.md`](../../features/moby-simulation-lifecycle-v1.md)
freezes a game/class registry, immutable project/entity/PVar/reference input, narrow
renderer asset/FX services, derived-object ownership, proxy/selection identity, failure
fallback, invalidation, project-switch, and context-loss behavior. The game-neutral
controller starts disabled with no allocation, isolates class failures, caps fixed-step
catch-up, rejects stale asynchronous creation, and exposes lifecycle/resource counters.
Focused mock tests cover state transitions, deterministic timing, pause/reset, failure
isolation, targeted recreation, project/context replacement, cleanup, and registry keys.

### M17-002 — Implement the simulation controller and controls

Status: ✅ Complete

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

Implementation: the game-neutral controller now runs inside the viewport animation
loop behind a compact keyboard-accessible Simulation popover with enable, pause/resume,
reset, live simulator/tick counts, and an explicit preview-only label. Enable state is
scoped to the open project and resets on project switch. Disabled mode creates and
ticks nothing; pause freezes derived rendering; reset clears deterministic time; stalls
drop bounded backlog. Copied Moby snapshots classify committed transform, PVar,
reference, asset, FX, and visibility edits for targeted invalidation. The projection
keeps simulation proxy suppression separate from collision preview visibility, while
context loss, toggle-off, failure, and teardown restore proxies and release selection
bindings/resources. Tests cover grouped 100-instance dispatch, capped stalls, snapshot
copy/diff, failure isolation, async cancellation, context/project replacement, FX
resolution, resource counters, and composed proxy visibility.

### M17-003 — Implement UYA ocean-plane simulation for class `0x0B37`

Status: 🚧 In progress — corrected renderer awaiting visual validation

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

Implementation: [`UYA ocean simulation v1`](../../features/uya-ocean-simulation-v1.md)
registers class `0x0B37` without a shared-scene game conditional. It reads the named
typed MobyDex fields, maps level-relative texture IDs into the UYA FX bank, resolves
current project FX Asset IDs, shares repeated texture
loads, and owns bounded camera-relative patch geometry, layered materials, and leases.
Eight-component waves, per-fragment shimmer, world-scrolling overlay UVs, per-fragment projected STQ underlay UVs,
PVar colors/alpha/blend/fog/height, native encoded-color framebuffer blending, and
stable-reference hide cuboids update from fixed
time and camera state. Invalid PVars, references, or FX identify the owner field and
retain a safe ordinary proxy when no layer remains. Golden field, wave/geometry bound,
camera/cuboid visibility, deterministic timing, invalidation, and disposal tests cover
the implementation. A reported visual mismatch exposed a missing UYA `0x64` level-FX
base, reversed native color-pass labels, linear WebGL blending, and an invalid RGBA16F
composer-to-RGBA8 framebuffer copy. The WebGL path now copies into a format-compatible
half-float texture without changing Forge's renderer, but representative Forge
validation is required before this task returns to complete.

### M17-004 — Preserve scene interaction and simulation performance

Status: ✅ Complete

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

Implementation: derived ocean meshes bind directly to the authoritative Moby Entity ID,
participate in the existing ordered raycast, and respect entity hidden/disabled/locked
state while the ordinary proxy is suppressed through a separate renderer overlay.
They do not enter entity counts, outline duplication, serialization, collision, history,
or bake. The fixed 6,720-triangle patch follows the camera, hidden/below-water frames
skip geometry updates, disabled preview does no class work, and the viewport's existing
FPS/draw/triangle telemetry includes its actual cost. Proxy/pick bindings use reference
counts and focused lifecycle tests return them to baseline across toggle, invalidation,
context loss, project switch, failure, and disposal.

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

Current evidence: existing-grid reconstruction has been integrated for UYA primary
terrain with resolved native group mappings. The [qualification record](../../qualification/M18-occlusion.json)
contains the current profile, native-mask comparisons, performance evidence and open
limitations. Exploratory reports are archived locally; this task's broader native
contract and hardware requirements remain the acceptance criteria.

Acceptance:

- A checked-in contract defines octant/grid coordinates, indexes, masks, TIE/tfrag/moby
  mappings, bit/capacity limits, ordering, alignment, empty/always-visible behavior,
  and exact source/native writer ownership.
- Synthetic fixtures prove source no-edit byte/semantic identity and changed mapping
  semantic identity; unsupported families/unknown fields are enumerated.
- Resolve negative mapping sentinels, repeated tfrag second words, primary/chunk/LOD
  links, shared bits across families, and auxiliary occlusion section ownership with
  UYA evidence before using decoded fields as unique entity IDs or free capacity.
- A vanilla reconstruction procedure fixes source cells and grouping, defines
  per-family/member missing/extra metrics and whole-map holdouts, and separates
  source similarity from independent visible-object correctness.
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
Depends on: M18-001; M18-002 for automatic coverage integration

Build a bounded SDK/reference implementation for small fixtures that classifies
occluders and computes conservative tie/tfrag/moby visibility for each sample/view.
First exercise the source-grid reconstruction procedure from M18-001, preserving
native group assignments so visibility and grouping errors remain distinguishable.

The SDK supplies a bounded CPU ray oracle used to calibrate the production raster
worker. Alpha, source depth ownership, nearby backface exits, ocean barriers and
nonblocking moby bounds have retained regressions. This does not prove continuous
cell coverage; the broader conservative reference requirements remain open.

Acceptance:

- Material classification distinguishes opaque occluders from transparent/cutout/
  translucent/non-depth-writing surfaces while still allowing those objects to be
  visible occludees.
- Frustum/FOV/orientation, bounds/geometry intersection, backface/double-sided state,
  feathering, numeric tolerances, and uncertainty-visible behavior are specified and
  tested.
- Finite camera samples and finite raster resolution do not count as proof of
  invisibility; thin/subpixel geometry, interior viewpoints, and moving moby bounds
  trigger verified refinement or a conservative visibility fallback.
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
Production generation requires a qualified GPU path; the CPU reference remains a
bounded verification/diagnostic path. Include persistent-scene batching, shared
sample reuse, and visibility reduction/readback in the backend comparison.

Selected implementation: a persistent WebGPU raster worker in a separate Electron
process, with spatial batch culling, shared camera positions, bounded submissions and
readback. Ray and dense-sampling prototypes are archived rather than shipped. The
[current qualification record](../../qualification/M18-occlusion.json) preserves the
measured results and limits. Existing-grid native publication, cancellation and
CPU/GPU calibration are covered; cross-vendor, Windows and weaker-GPU qualification
remain open.

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
Depends on: M18-002, M18-004

Implement the selected accelerated backend behind the frozen solver contract with
bounded work units, conservative canonicalization, and fingerprinted cache/invalidation.

Acceptance:

- Generation stays off renderer/Electron loops, reports candidate/visibility/verify/
  compose phases, and cancels at safe checkpoints without replacing good results.
- Work chunks bound GPU time, CPU/GPU memory, readback, and diagnostic volume to avoid
  driver watchdogs, repeated scene/resource construction, and synchronous per-sample
  readback. Compare against measured Forge v1 behavior; it already uses GPU compute
  and sample caching.
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

The Occlusion panel now provides sample qualification previews, octant IDs, pull-in
and pixel-edge settings, live viewport culling and a frozen-octant inspection mode.
Visibility is transient and respects manual Hidden state, selection and picking.
Octant changes update affected rendering groups without synchronizing unrelated
collision or rerendering the whole editor. Source/generated heatmaps and exact-view
comparisons currently remain local diagnostics; integrated difference/POV tooling
and the full accessibility/performance matrix below remain open.

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
