# M18A occlusion coverage qualification

Date: 2026-10-31  
Status: ✅ Complete — maintainer sign-off (2026-11-06)

Milestone: [M18A coverage authoring](../planning/milestones/M18A-occlusion-coverage.md)  
Task: [M18A-001 native contract](../planning/tasks/M18A-tasks.md#m18a-001--freeze-the-native-coverage-contract)

## Maintainer sign-off

The maintainer marked M18A-001 and M18A-003 through M18A-008 complete on 2026-11-06.
This closes the task acceptance gates by maintainer sign-off. Earlier pending
rows and open-gate statements below are retained as historical checkpoints,
not current task status. No additional run metadata, measurements, hashes,
or capture references were supplied. Documented support limits still apply.
See the [planning sign-off record](../planning/README.md#maintainer-sign-off).

## Evidence scope

Inventory update (2026-11-06): Forge now pins SDK `v0.11.0`, which declares preview,
generation, and graph-shape API version 1. The clean pinned build enables the coverage
adapters through `build/RatchetSdk.props`; host regression tests exercise generation,
native publication, inspection, packing, and invalidation. SDK release/pin integration
is complete. The development sections below retain the implementation history at
their recorded revisions; references to unreleased APIs or a `v0.10.0` pin describe
that earlier state. No new packaged, PCSX2, hardware, allocation, or route sign-off
is inferred from the dependency update or passing synthetic tests.

On 2026-10-31 the user confirmed that octant qualification and building for a custom
cylinder works in-game. Record this as user-reported end-to-end evidence for that
map and workflow. The game build, emulator/hardware, cell count, allocation size and
routes were not specified. No broader native allocation, graph, outside-coverage,
empty-grid or release/platform qualification is inferred from this report.

The initial qualification consumes the pinned SDK `v0.10.0`, commit
`1c2f6a50fdccb357a513d2809ed90cd287524276`, through neutral SDK composition APIs.
Game-specific readers are used only in the UYA test fixture to inspect results.
No runtime binary layout implementation is added to Forge.

Retained checks live in
[`UyaOcclusionCoverageContractTests.cs`](../../tests/Forge.Host.ProtocolTests/Games/UYA/UyaOcclusionCoverageContractTests.cs)
and run with the C# bridge/domain suite. All input payloads are synthetic. Findings
below distinguish executable SDK behavior from game runtime semantics.

## Structural findings

| Area | Executable SDK evidence | Consequence for implementation |
| --- | --- | --- |
| Sparse coverage | A four-cell fixture contains a missing X cell, missing Y row, and missing Z slice. Reading and mask replacement preserve exactly its ordered coordinates. | Do not fill a rectangular envelope when serializing the final cell set. |
| Source-mode validation | Mask APIs reject added, removed, or substituted coordinates. Grid validation rejects a different set with the same cell count. | Add an explicit arbitrary-grid path; retain source-mode checks. |
| Mask aliasing | Cells sharing source slots can receive different complete 128-byte masks; reversing dictionary insertion order produces identical native bytes. | Source slot identity is not authored cell identity. Preserve canonical output ordering. |
| Source allocation | Distinct masks grow the four-cell fixture from 320 to 576 bytes. Source-mode compaction fits it back into 320 bytes without losing generated visibility or changing reserved bits. | The source-allocation ceiling is current rebuild policy; it is not established as a native format limit. |
| Asset relocation | The generic asset composer accepts the larger grid, relocates an adjacent heightmap/opaque-payload pointer, updates scene-view and asset sizes, and preserves prefix/adjacent bytes. Shrinking back restores every original fixture byte. | Lower-level asset composition already supports growth. Engine-memory qualification remains required. |
| Complete-level replacement | A synthetic level replaces its original four cells with twenty cells at X=30–49, grows the grid from 320 to 2,624 bytes, crosses a level-data sector boundary, and reads back the exact replacement. Palette, sound, adjacent opaque asset, and auxiliary occlusion bytes survive unchanged; repeated builds are byte-identical. The source-only application API still rejects changed coordinates. | Generic complete-level composition supports grid growth and changed coordinates. The later dedicated SDK application path below reuses this composition; engine qualification remains required. |
| Empty coverage | An eight-byte grid with masks offset 8 and zero Z rows is readable. Current source mask replacement and nonempty-source grid validation reject empty replacement. | Reader acceptance does not authorize empty publication; sample planning/cache also require at least one cell. |
| Coordinate starts | Unsigned 16-bit row starts at 65,535 with a two-entry X row decode cells at 65,535 and 65,536; source mask replacement retains them without wrapping. | Row-start width is not an engine world-coordinate limit. The supported authoring coordinate profile still needs runtime evidence. |
| Tree budget | A 20,000-cell source using aliased rows is readable, but source mask replacement fails with native pointer-capacity diagnostics when rebuilding unaliased rows. | Cell count alone cannot preflight sparse coverage or reserialization. Measure the actual tree layout. |
| Input ownership | Source bytes survive successful replacement, relocation, rejection, and cancellation unchanged. | Retain immutable input and atomic project publication contracts. |

The current SDK encodes Z/Y/X starts and counts as unsigned 16-bit values. Y/X table
pointers use unsigned 16-bit values in four-byte units; zero pointers denote absent
rows. X slots use `0xffff` for absent cells. Masks contain 128 bytes, with a signed
32-bit mask-table byte offset in the header. The writer aligns the mask table to
16 bytes and the final payload to 64 bytes. These are inspected SDK layout facts;
engine behavior remains subject to the runtime gates below.

Current limits from the SDK/host remain separate:

- The sample planner accepts 1–100,000 cells and at most 250,000 shared origins,
  checking coordinate magnitudes against 250,000 cells. That planner range does not
  prove native serialization or runtime support for the same coordinates.
- The current native writer has 65,535 usable mask indices, excluding the missing-
  cell sentinel. Relative-pointer capacity depends on tree layout, even with very
  few unique masks.
- The current host cache bounds native grids to 16 MiB and stores source-grid and
  packed-scene hashes. Coverage authoring will need an explicit effective-grid identity.

## Native lookup inspection (2026-10-31)

The connected DL Ghidra program provides named reference functions. Their lookup
and mask-selection rules were compared with UYA level 03 code extracted read-only
through the existing SDK from the configured clean NTSC-U ISO (`SCUS_973.53`).
The extracted boot ELF SHA-256 is
`200068cb4186715521cdc1c20bc6c5a88054d0da54aa336ffdb2022633351c7d`.
Addresses below refer to each game's own loaded code, not interchangeable offsets.
No game payloads or full disassembly are retained in the repository.

| Rule | UYA level 03 | DL reference | Implementation consequence |
| --- | --- | --- | --- |
| Sparse lookup | `0x003ec580` | `ParseOcclGrid`, `0x004c0530` | Unsigned 16-bit row starts/counts and four-byte-unit relative pointers; zero rows and `0xffff` cells return no mask. Endpoints are exclusive. The existing SDK layout matches. |
| Camera conversion | Selection routine at `0x003ec710` | `BuildOcclVisibility`, `0x004c06c0` | Each native axis is multiplied by 0.25 then converted to an integer. Four-unit cells match the existing preview. |
| Reserved bit | `0x003ed1bc`–`0x003ed1ec` | End of `BuildOcclVisibility` | Byte 127 is ORed with `0x80` after mask selection: bit 1023 is always visible. New generated masks should set it; source-preserving APIs continue retaining supplied source bits. |
| Outside coverage | Selection/fallback through `0x003ed1f4` | `BuildOcclVisibility` and `UpdateOcclusion`, `0x004c0aa8` | Mode-dependent fallback can union neighboring masks, retain the prior mask, select auxiliary masks, or use all-visible. A missing cell is not a universal all-visible guarantee. |

For the EE conversion instruction, the [PCSX2 interpreter implementation of
`CVT_W`](https://github.com/PCSX2/pcsx2/blob/master/pcsx2/FPU.cpp) truncates finite
in-range values toward zero. Thus negative fractional camera positions must not
be modeled by flooring into signed cells. The current authoring profile continues
rejecting negative coordinates; its broader structural range is not a gameplay
qualification. Scripted cameras and auxiliary overrides still require route tests.

## Runtime gates at the earlier checkpoint

1. Probe actual camera routes in PCSX2 at cell boundaries, sparse gaps and outside
   coverage, including transitions that retain prior masks. Static inspection fixes
   the lookup and reserved-bit rules; it does not demonstrate route completeness.
2. Keep structurally empty native publication unsupported. A reader accepting an
   empty tree does not establish safe mode-dependent fallback or builder behavior.
3. Verify engine allocation/loading and associated auxiliary occlusion sections for
   expanded coverage in PCSX2 and the parent M18 hardware qualification path. Successful
   archive encoding alone does not prove memory budgets or auxiliary independence.

M18A-001 remains in progress. Bit 1023's unconditional runtime visibility is now
established by static evidence; expanded allocations and gameplay remain unqualified.

## Source-free SDK development

M18A-004 adds `OcclusionComposer.CreateGrid(GameId, masks, token)` in the SDK. The
input is an explicit, nonempty coordinate-to-mask dictionary; no source grid or
reserved-bit policy is inferred. UYA creation and existing mask replacement share
one serializer. Creation canonicalizes Z/Y/X order, interns complete masks, measures
the unaliased tree before output allocation, validates native spans/pointers/slots,
and re-reads exact coordinates and complete masks. Existing source-only validation
and level application keep their coordinate restrictions.

`UyaOcclusionGridCreationTests` in the SDK retains an independently encoded golden
cell, source-free added/removed/moved cells, sparse gaps, explicit bit-1023 values,
mask sharing, input-order determinism, padding, row-end boundaries, invalid masks,
negative/overflowing spans, excessive cells/masks/pointers, cancellation, and
unsupported-game rejection. The API remains a low-level encoding capability;
authored grid publication is not enabled. The later runtime inspection above
establishes the new-cell reserved-bit policy without changing this explicit-mask API.

At this development checkpoint the change was unreleased, Forge pinned `v0.10.0`,
and compatibility testing used an explicit `RatchetSdkRoot` override. The 2026-11-06
inventory above records the subsequent release/pin integration; the native runtime
and allocation qualification gates remain open.

## Box coverage preview development

M18A-003 adds the unreleased SDK
`OcclusionCoverageBuilder.PreviewBoxes(GameId, primary, boxes, token)` and
`GetBoxPreviewProfile(GameId)` entry points. The UYA implementation stays in the
game library; neutral coordinate/box/profile/result contracts live in Core.
The SDK documents the complete contract in `docs/OCCLUSION_BOX_COVERAGE.md`.
Forge production code does not consume this API and its dependency pin is unchanged.

Algorithm version 1 uses four-unit native cell cubes, double-precision box poses,
15-axis separating-axis inclusion, and box-local slab containment for exclusion.
The fixed tolerance is `0.00001` world units. Face/edge/corner contact and projection
gaps within that tolerance retain inclusion cells. Exclusion requires more than
that tolerance of clearance for every cube corner; uncertain/tangent cells stay.
Exclusions take final precedence over the deduplicated union of Primary and include
boxes. Counts retain each input box's spatial contribution; exclusion counts refer
to the full inclusion union before removals, independent of source order.

The preview profile accepts coordinates 0–131,069 on each axis, up to 1,024 boxes,
100,000 input Primary entries, a 100,000-cell inclusion union before exclusions,
and 2,000,000 worst-case candidate tests. This is an individual-coordinate structural
ceiling from the writer, not an engine profile or assurance that combined spans or
tree pointers will fit. Finite dimensions are positive and at most 1,000,000; unit
quaternions use the stored recipe's squared-norm tolerance `0.0001` and normalize
in double precision. All boxes, including exclusions, undergo coordinate validation.

Candidate bounds use the tolerance-expanded box AABB and include boundary contacts.
Any candidate outside the profile fails without clipping. Consequently a box
touching world zero also touches a negative cell and is rejected. These stricter
evaluation limits do not prevent storing a broader authoring recipe. Future changes
to boundary rules require another algorithm version and retained fixtures.

Before enumeration, preflight sums Primary entries and include AABB candidates,
then adds exclusion tests against the largest possible inclusion union capped at
100,000. Oversized work fails before cell enumeration. Enumeration checks
cancellation at most every 256 candidate tests. Results own read-only coordinate
and count arrays in Z/Y/X order; inputs survive failures and cancellation.
An empty preview is valid, without authorizing empty native publication.
The coordinate fingerprint is SHA-256 over `uya-box-coverage-v1` plus a zero byte,
then canonical little-endian signed int32 X/Y/Z triples. Recipe, source, scene,
visibility and publication identities remain separate.

`UyaOcclusionBoxCoverageTests` checks rotated geometry against an independent
corner/edge/face reference and rotated exclusions against all-corner containment
over 80 seeded boxes. Fixtures retain thin boxes, tangencies, uncertainty,
disconnected cells, Primary/exclusion precedence, duplicate/order/quaternion-sign
equivalence, a golden fingerprint, malformed inputs, structural/source/cell/work
limits, cancellation during validation and enumeration, and unsupported games.
Two overlapping include boxes produce 100,000 unique cells from 200,000 tests in
approximately 65–110 ms in the local Release runs; this is an observation on the
development machine, not a cross-platform performance qualification.

Validation on 2026-10-30: the SDK build completed without warnings; its expanded
`--uya-occlusion` suite, Forge C# bridge/domain suite and full Forge `npm test`
pipeline passed against `/tmp/horizon-forge-m18a-sdk`. The tested patch was applied
to the sibling SDK checkout without releasing it or updating the Forge pin.
At that checkpoint cylinder coverage, graph filtering, host background jobs/progress,
interactive preview controls and native publication remained unimplemented.
Subsequent cylinder evidence follows. Runtime gates above remain open.

## Cylinder and mixed-shape preview development

The SDK development API now adds
`OcclusionCoverageBuilder.PreviewShapes(GameId, primary, shapes, token)` with
`OcclusionCoverageBox` and `OcclusionCoverageCylinder` contracts. The cylinder is
centered at its pose, has a circular positive radius and height, and uses local Z
as its axis. Finite dimensions/radius are bounded to 1,000,000; accepted near-unit
rotations normalize in double precision. Unknown shape kinds fail validation.
`GetPreviewProfile(GameId)` returns algorithm version 2, with the same combined
source/cell/work limits and structural coordinate range as the box-only preview.

Both shape kinds share preflight, canonical union, final exclusions, source counts,
cancellation and copied-result handling. The original box-only API retains its
version-one geometry, counts, work bounds and golden coordinate fingerprint.
Mixed evaluation fingerprints UTF-8 `uya-shape-coverage-v2` plus a zero byte and
canonical little-endian signed int32 coordinate triples; equal coordinates across
these algorithm versions intentionally have different identities.

Cylinder inclusion expands radius and half-height by `0.00001` world units, clips
the candidate cube to its two cap planes, and tests the radial projection's convex
hull against the circular disk. The clipped solid's vertices are retained cube
corners and original edge/cap intersections, bounded to 32 entries including
duplicates. The origin inside the projected polygon or a boundary point within the
expanded radius establishes intersection; point/segment projections also work.
Production geometry uses neither polygonal cylinder approximation nor an iterative
solver. Constant-size stack storage avoids per-candidate heap allocations.

Cylinder candidate bounds enclose the expanded radius/height before applying the
shared AABB tolerance. Unsupported candidates fail without clipping. Exclusion
requires all eight cube corners strictly inside the tolerance-shrunken radial and
height limits; nonpositive shrunken extents, tangency and uncertain containment keep
the cell. Convexity makes all-corner containment sufficient. Local-axis spins retain
the circular geometry instead of producing ellipses.

`UyaOcclusionCylinderCoverageTests` retains analytic circle/rectangle plus height
interval comparisons over 30 seeded axis-aligned cylinders, an independent SAT
reference between inscribed/circumscribed 64-sided prisms for 40 seeded rotated
cylinders, and independent corner containment for rotated exclusions. The reference
checks more than 28,000 inclusion and exclusion cases each, with fewer than 100
ambiguous prism-boundary cases. Other fixtures cover cap/rim/corner contact, clear
separation, thin crossings, subnormal dimensions, structural maximum coordinates,
mixed Primary/box/cylinder precedence, source order, quaternion sign/local-axis
spin, a golden fingerprint, malformed/unknown inputs, budgets and cancellation.
The existing box and native-grid regressions continue to pass.

The local Release overlap workload generated 39,420 cells from 156,000 candidate
tests in approximately 52 ms. This is a development-machine observation, not a
cross-platform performance qualification. SDK documentation is retained in
`docs/OCCLUSION_SHAPE_COVERAGE.md`; the focused test selector is
`--uya-occlusion-cylinder-coverage`, also included by `--uya-occlusion`.

Validation on 2026-10-30: the SDK build completed without code warnings/errors and
its full occlusion suite passed. The full Forge `npm test` pipeline passed against
`/tmp/horizon-forge-m18a-sdk`, and the tested patch was applied to the sibling SDK
checkout. NuGet vulnerability auditing was disabled for these local builds because
the sandbox could not reach its advisory feed; dependencies were not changed.
The SDK remains unreleased and Forge's pin unchanged. Host background jobs/progress,
interactive previews, graph filtering, native publication and the runtime gates
above remain open.

## Host and dock preview development

On 2026-10-31, M18A-003/005/007 added background preview jobs and dock authoring
controls. The normal build still consumes the clean pinned SDK without referencing
unreleased preview APIs. `ForgeEnableOcclusionCoveragePreview=true` explicitly
includes the development UYA adapter and its capability; `RatchetSdkRoot` must point
to the SDK development checkout. This gate does not enable native publication.
The [feature contract](../features/occlusion-coverage-project-v1.md#development-preview-jobs)
records the development launch/test command and SDK-release follow-up.

The editor captures a workspace projection and copied recipe under its gate, then
runs evaluation on a background task. The adapter validates the target/recipe,
rejects enabled graphs, reads verified imported Primary or extracts the base level's
grid from the configured source ISO, and consumes neutral SDK entry points for
coordinate reading and shape evaluation. Disabled Primary requires no ISO. The new
`OcclusionCoverageBuilder.ReadCoordinates` facade retains native reader bounds and
cancellation; SDK fixtures cover exact readback, malformed input, cancellation and
unsupported games. No native parser or shape intersection code is copied into Forge.

Each editor runtime permits one in-flight preview. The editor lock remains free
during evaluation; accepting a result checks the full project fingerprint, project
version and original workspace instance. This rejects an intervening edit even if
undo restores the same fingerprint, and rejects close/reopen of an unchanged project.
Cancellation reaches input/evaluation checkpoints and SDK enumeration. Base-ISO
extraction currently uses its existing synchronous SDK call, with cancellation
checked immediately before/after extraction. No partial result is accepted.

Preview responses carry project/recipe/Primary/coordinate identities, profile,
Primary/candidate counts, all source counts (disabled sources are zero) and up to
100,000 canonical effective coordinates. Progress reports validation, Primary read,
evaluation and completion. Capability-gated opcode 44 and the shared
[`occlusion-coverage-preview-v1.json`](../../tests/fixtures/occlusion-coverage-preview-v1.json)
vectors freeze the bounded wire contract. The renderer cannot supply filesystem
paths. Standard cancellation applies to pending/running requests, including project
close. Successful preview changes no dirty/history state, saved source authority,
coverage output, masks, bake caches or stage files.

The Occlusion dock now creates boxes/cylinders at the selected object's native
position, with an explicit default when nothing is selected. Coarse rows expose
selection, contribution, kind and role. Numeric editing supports name, position,
quaternion rotation, box dimensions, and circular radius/height; Apply commits one
history entry and Discard resets the draft. Duplication deep-copies owned markers
with new IDs and enforces recipe count limits. Deletion remains undoable. Development
preview builds add progress/cancel, per-source/effective counts and an explicit stale
indicator while retaining the prior successful preview. Viewport helper rendering,
cell overlays, focus/selection and drag gizmos/snapping were added in the viewport
follow-up below. Graph editing remains open.

Verification:

- `OcclusionCoveragePreviewTests` covers captured inputs, an unlocked editor gate,
  concurrent-job rejection, success without history/dirty changes, stale requests,
  edit/undo ABA rejection, cancellation and close/reopen of the same project.
- Opt-in `UyaOcclusionCoveragePreviewTests` composes imported Primary with unsaved
  boxes/cylinders, verifies coordinate/source identities, counts and progress,
  checks cancellation before ISO access and rejects enabled graphs. Synthetic
  imported coverage is used; no new real-ISO walkthrough is recorded.
- Shared C#/TypeScript payload tests reject malformed fingerprints/paths, excessive
  counts, invalid profiles, truncation, trailing bytes and duplicate/unordered cells.
  Source-factory/duplication checks retain dimensions, stable ownership and copied
  caller data. Existing real-host coverage tests now exercise preview capability
  gating, progress, actual SDK evaluation, no-authority mutation and stale requests.
- SDK occlusion tests, TypeScript typechecking and full Forge `npm test` passed
  with the development option enabled. A separate full Forge run passed against a
  clean archive of pinned SDK commit `1c2f6a50fdccb357a513d2809ed90cd287524276`,
  with preview support absent. The tested SDK reader addition was applied to the
  sibling checkout without changing the pin or creating a release.

The viewport follow-up below records a limited interactive walkthrough. Native
runtime/publication gates above remain open.

## Viewport authoring development

On 2026-10-31, M18A-005 added session-owned coverage selection, helper visibility,
focus and preview display shared by the dock and viewport. Box/cylinder helpers use
native-to-scene transforms, include/exclude line patterns, a selected bounds frame,
and picking without gameplay entities. Contribution and visualization remain
independent. Existing saved graph regions/markers render without enabling graph
authoring or filtering. Helper state survives closing the dock and resets on project
change.

The shared transform tool accepts coverage targets, retaining its mode, coordinate
space, snapping and camera-interaction foundation. Drag previews stay local. Release
submits one whole-recipe edit against the captured project fingerprint; invalid or
stale candidates cannot commit. Escape, unexpected capture loss, target changes and
disabled editing restore the original helper. A normal mouse-up's synchronous
capture release is distinguished from cancellation. Transient scale becomes explicit
dimensions with unit stored pose scale; cylinder radial handles retain a circular
section. The existing scale gesture uses vertical pointer movement.

Preview coordinates render in one instanced wireframe mesh, bounded by the existing
100,000-cell response limit. Cells are not pick targets. Stale previews retain their
explicit dock/viewport notice and change appearance. Replaced/removed helpers,
marker geometries and cell-instance resources are disposed.

Verification:

- `tests/occlusion-coverage-viewport.test.ts` covers coordinate axes and rotated
  dimensions, independent contribution/visibility, helper picking, circular sizing,
  invalid dimensions, local previews, single commits, stale callbacks, cancellation,
  snapping settings and resource disposal. The actual TransformTool runs against a
  canvas event stub to distinguish normal release from capture-loss/Escape/cancel.
  Existing viewport-picking and scene-projection checks also pass.
- A sandboxed/context-isolated Electron window under Xvfb/SwiftShader rendered the
  actual SceneViewport, dock controls and shared hook at 1280×900 against a synthetic
  in-memory backend. Focus selected and framed both sources. Keyboard shortcuts
  selected Move and Scale. A real pointer X drag moved a box from X=10 to approximately
  11.38249 with one commit and unchanged Y/Z. Escape during the next drag retained
  that pose and commit count. A radial scale drag changed the cylinder radius from
  4 to approximately 4.59479 with one additional commit, unchanged height 8 and unit
  stored pose scale. Hiding the selected helper retained its enabled contribution,
  project fingerprint and commit count. This fixture contains no game geometry, ISO
  or native publication.
- TypeScript typechecking and the full Forge `npm test` pipeline passed with the
  development preview option and SDK snapshot. No SDK dependency/pin changes were
  made in this viewport follow-up.

Real-map overlapping-volume picking, large-preview rendering/performance, full
snapping/rotation/camera combinations, display scaling and broader accessibility
qualification remain open. This evidence does not close M18A-005 or the runtime
publication gates.

## Graph authoring development

On 2026-10-31, M18A-006 added disabled graph-region creation and owned seed authoring
without changing the persisted schema or IPC. New regions have size `(32,32,32)` and
an empty seed list. The dock supports seed creation at the graph center, rename,
local numeric positioning, duplicate, focus and delete. Numeric drafts Apply in one
recipe edit or Discard; marker rows paginate in groups of 32. Per-graph/global limits
apply before adding or copying seeds. Empty graphs show an actionable authoring
notice; enabled graphs still receive the existing unsupported-filtering diagnostic.

The viewport uses an eight-pixel seed hit target before volume picking and renders
selected seeds with a bounds frame and explicit dock selection text. Selecting a
seed activates Move; Rotate/Scale explain that region transformations require graph
selection. Dragging converts through the owning graph's pose into a bounded local
position, previews without authority changes, commits once, and restores on cancel.
Graph translation/rotation carries local seeds; region resizing retains local seed
positions and rejects bounds that would exclude them. Marker position/color buffers
are reused during a drag and disposed on count/kind changes or project close.

The Forge v1 reference inspected for this work is
`Assets/Forge/Scripts/Occlusion/OcclusionGraph.cs` and `OcclusionNode.cs` in the supplied
Unity workspace. Its child-node interaction informs this authoring flow. Ownership
here is explicit in each graph's saved marker list; no global first-graph lookup is
used. Its Unity Physics raycasts are not copied into Forge or treated as a qualified
SDK filtering contract.

Verification:

- Viewport regressions cover translated/rotated parent coordinates, local seed drag
  readback, no cross-graph edits, single commits, cancellation, out-of-region rejection,
  graph movement/resize behavior, explicit ownership, constant pixel hit size at
  different depths, hidden/clipped seeds and Move-only tool attachment.
- Factory/recipe checks cover disabled empty graph defaults, independent copied seed
  data/IDs, graph duplication ownership and out-of-region rejection. Existing bridge,
  project history/recovery/save/reopen and graph-rejection fixtures remain green.
- The isolated sandboxed Electron fixture rendered the actual dock, shared hook and
  viewport. Add graph region and Add seed each committed once. Focus framed the seed
  and activated Move. A real pointer X drag changed its local X from 0 to approximately
  0.988955 with exactly one additional commit; its graph pose and region stayed
  unchanged. Duplicate seed copied its local position with a fresh ID; deleting the
  duplicate retained the original seed and graph, with one commit for each action.
  A direct viewport click selected the seed before its region without another
  history edit. The fixture uses a synthetic in-memory backend and no game geometry.
- `npm run typecheck`, scene-projection tests and the full `npm test` pipeline passed
  with the existing development preview option/SDK snapshot. No SDK source or pin
  changes were made for graph authoring.

M18A-006 remains in progress: SDK blocker/probe semantics, line-of-sight filtering,
buried-seed diagnostics, accepted/rejected-cell inspection and scene-dependent
invalidation remain open. Real-map, accessibility/display-scaling and maximum-marker
interaction/performance qualification also remain open. Native publication gates
are unchanged.

## SDK graph preview development

On 2026-10-31, M18A-006 added the unreleased neutral
`OcclusionCoverageBuilder.PreviewGraphs` and `GetGraphPreviewProfile` APIs. Explicit
native-space graph poses, box regions, owned local seeds and calibrated scene
triangles/alpha enter the SDK; no project paths, Unity dependencies or executable
node network enter the algorithm. UYA implementation and finite-segment acceleration
stay in `RatchetPs2.Games.UYA`; declarative input/result models stay in Core.
`docs/OCCLUSION_GRAPH_COVERAGE.md` in the SDK records the complete provisional rules.

Algorithm 3 uses the existing four-unit box/cell intersection, the cell center and
eight corners inset 0.001 world units. Any clear probe-to-owned-seed segment retains
the cell. Only triangle flag bit 0 blocks, from either side; target spheres/barrier
planes are excluded. Existing PS2 bilinear alpha/addressing is reused, requiring
alpha greater than `127 + 0.00001`. Definite barycentric interior hits block; edges,
vertices, near-parallel/coplanar hits and contacts within 0.001 world units of either
endpoint remain uncertain and retain coverage. These rules can intentionally retain
seam coverage and do not change visibility-bake or sample-qualification behavior.

Six fixed oblique nearest-exit queries diagnose a seed only when every definite
nearest opaque hit is a backface. The first apparently buried seed fails actionably
with one-based graph/seed indices. Coincident opposite windings are ambiguous and
cannot trigger that diagnostic based on triangle ordering. This is an oriented-mesh
heuristic, not a complete point-in-solid proof; missed buried seeds and false
positives require real-map qualification. Empty or non-finite/out-of-region seed
lists fail before evaluation rather than acting as ordinary include boxes.

Per-graph accepted and rejected coordinates are copied, read-only and canonical
Z/Y/X lists before final composition. Effective coverage unions Primary, accepted
graph cells and ordinary includes, then applies ordinary exclusions last. Primary
and include shapes can restore graph-rejected cells. Shape counts retain input
order, and the version-three SHA-256 coordinate fingerprint uses its own prefix.
Existing shape-only/box-only coordinates, counts and version-two/version-one
fingerprints remain unchanged. A coordinate fingerprint is not a scene identity.

The preflight bounds combined sources to 1,024, seeds to 4,096 per graph/16,384 total,
combined graph bounding-box candidates to 100,000, worst-case rays to 2,000,000 and
combined graph/shape candidate work to 2,000,000. Existing geometry/alpha and
100,000-cell inclusion-union limits remain. The BVH counts nodes and triangle tests,
rejecting work above 40,000,000 with a corrective action. It reorders copied indices,
never caller triangles. Progress reports preparation, each 256 candidates and
completion; cancellation reaches validation, BVH construction/sort/traversal,
enumeration and result acceptance. Failure/cancellation returns no partial result.

Verification:

- `UyaOcclusionGraphCoverageTests` retains wall-separated rooms and a doorway,
  additional seeds, independent analytical segment/plane references, rotated/local
  ownership and high coordinates, quaternion/source/seed/triangle ordering, include
  rescue/Primary/final-exclusion precedence and duplicate graph coverage.
- Alpha thresholds, nonblockers, front/back winding, finite endpoints, shared-edge
  uncertainty, apparently-buried seeds and coincident opposite-winding ambiguity have
  fixtures. Seed/source/coordinate/union/ray/query limits, progress, pre-cancelled and
  in-flight cancellation are exercised. The frozen coordinate identity is
  `9a1c04340679b1298d29fe30d0e0c5689f114fb0ab3ea246b7cd8220ccdf56f4`.
- A measured Release workload with 10,000 distant triangles retained 108 accepted and
  108 rejected cells, 1,082 rays and 26,380 query tests in approximately 50–55 ms on
  this development machine. The test asserts pruning and result correctness, not a
  cross-platform timing threshold.
- The SDK Release build completed with zero warnings/errors and the complete
  `--uya-occlusion` suite passed, retaining box/cylinder/native-grid/calibration
  regressions. The full Forge `npm test` pipeline passed against the source-only SDK
  snapshot with development previews enabled. The tested nine-file patch was then
  applied to the sibling SDK checkout after checking all destination baseline hashes.
  TypeScript typechecking also passed; no SDK release or dependency-pin update was
  made.

At this checkpoint, Forge still rejected enabled graphs: current-scene capture,
scene-dependent preview identity, indexed diagnostics and accepted/rejected
inspection needed host/IPC/dock integration (connected in the later record below). A base-ISO-only scene must not be substituted for
custom or unsaved geometry. The tested SDK API remains unreleased and the dependency
pin unchanged. Real-map blocker/probe/camera qualification and native publication
remain open; M18A-006 is not complete.

## Current-scene snapshot foundation

On 2026-10-31, the Forge UYA adapter added `UyaOcclusionSceneSnapshot.CaptureAsync`
as the input foundation for graph previews. It accepts a captured workspace, catalog
root and source archive, copies project content metadata and verified declared
attachments into an isolated temporary project, and serializes the captured unsaved
state there. Existing bake/pack services compose the current level in that workspace
with a read-only catalog and generated visibility application disabled. Only the
scratch recipe is Primary-only so geometry preparation can use source-mode preflight.
It never saves or changes the original project or disables its authored sources.

The result contains the packed level bytes, captured project fingerprint and SHA-256
of those exact bytes. The conservative scene identity includes unrelated packed
payloads; narrowing it is unnecessary before scene-dependent reuse exists. Copying
is bounded to 2 GiB/50,000 entries, 16 nested content-directory levels and the existing
256 MiB per-file/attached-asset limit. Directory traversal is streamed, links/device
files are rejected, attached blobs are integrity-checked, and file-length changes
while copying fail. Empty files are reproduced without reading them. Cancellation
reaches copying/baking/packing, and progress covers preparation through composition.
The temporary directory is deleted on success, failure and cancellation.

The custom-model fixture exposed an existing packing bug: an effective model override
could still be classified as an unchanged source-level asset, causing restoration of
the original model. `UyaStaticAssetPackService` now requires the effective and source
asset references to match before retaining the source model. This applies to normal
packing as well as temporary scene composition; unchanged source models retain their
existing path.

`UyaOcclusionSceneSnapshotTests` exercises a synthetic imported level with original
last-known-good staging. Native readback proves an unsaved TIE transform reaches the
packed level. A second edit changes scene identity, identical captures are
byte-deterministic, an unsaved attached model override reaches the native asset, and
unsaved deletion removes the native instance. Whole-file digests prove project,
staging and catalog files stay unchanged; reopening proves no implicit save, and
the original dirty/coverage state remains intact. Invalid source input, oversized
files, corrupt attachment integrity, callback failure, cancellation and Unix link
rejection retain original files and leave no snapshot directories behind.

Verification on 2026-10-31: the complete Forge `npm test` pipeline passed with the
development preview option and SDK snapshot, including the final scene-copy and
model-override regression checks. The complete C# bridge/domain suite also passed
against the clean pinned SDK with development previews disabled. TypeScript
typechecking, documentation links/anchors and `git diff --check` passed. No real ISO,
interactive UI walkthrough, SDK source change or dependency-pin update was needed
for this slice.

At this checkpoint, the current-scene helper was not yet connected to the editor's
preview executor or wire contract. Captured catalog context, SDK extraction/evaluation,
scene identity, indexed diagnostics and accepted/rejected inspection were connected
in the subsequent integration record below. No base-ISO-only approximation was used. Real-map performance/coverage and native
publication qualification remain open.

## Current-scene graph preview integration

On 2026-10-31, the development preview adapter connected captured catalog context,
current-scene composition and SDK graph evaluation. Shape-only version-two results
and the original wire vector remain unchanged; graph results use algorithm 3 and
an optional versioned wire extension with exact packed-scene hash, accepted/rejected
coordinates per authored graph and bounded ray/query counts. The default pinned
build still excludes the adapter and does not advertise preview capability.

`OcclusionSceneBuilder.BuildCoverageGeometry` extracts calibrated static blockers
without requiring native groups or a native grid. It retains terrain/TIE/shrub
material/alpha rules, omits nonblocking target spheres/barriers, and rejects nonempty
streamed terrain chunks. WAD, instance, geometry and graph budgets remain enforced.
The strict visibility-bake extractor keeps its original grid/group requirements.
Graph preview requires a configured source ISO and captured catalog even with
Primary disabled; missing inputs and empty graphs fail actionably. Buried-seed
indices map to authored graph/seed names and IDs. Geometry comes from the isolated
current-scene snapshot, including unsaved custom models, rather than base-only data.

The dock exposes Final effective coverage, Selected graph accepted cells and
Selected graph rejected cells through a labeled keyboard-selectable control. Graph
inspection precedes final union and exclusions; source counts and viewport text make
that distinction explicit. One instanced overlay shows the chosen set, is not
pickable and disposes resources on replacement. Edits retain the previous preview
with explicit stale text and appearance. Shape-only results reset graph inspection.

Evidence:

- `UyaOcclusionCoveragePreviewTests` composes synthetic project/catalog/ISO fixtures,
  previews an empty custom scene with Primary disabled, then sees an unsaved custom
  glTF model and changed packed-scene identity/query work. It verifies coverage
  extraction with native groups absent and with the native grid pointer removed,
  while strict bake extraction rejects the scene. Original saved bytes remain
  unchanged. A closed custom cube exercises authored-name/ID buried diagnostics;
  missing inputs, empty graphs and cancellation reject without partial authority.
- The shared graph wire vector is checked by both host and Electron codecs.
  Regression cases cover every-byte truncation, trailing bytes, invalid extension
  versions/algorithm pairing, ownership, fingerprint, overlap/order and work/count
  bounds before allocation.
- Viewport tests cover selected accepted/rejected cell coordinates, native/view axis
  conversion, stale recoloring, disposal on mode/source changes, missing selection
  and recipe immutability.
- An isolated Electron/Xvfb fixture rendered the actual hook, dock and viewport
  with simulated host replies. It exercised the preview button, keyboard selection
  of accepted cells, effective/rejected display, retained stale cells after an
  authored edit, refresh, and reset to effective display after a shape-only result.
  Screenshots confirmed cell placement, distinct fresh
  modes, explicit inspection text and orange stale cells with a warning. This is a
  UI check with synthetic replies, not a real-ISO or packaged-app walkthrough.
- The full Forge `npm test` pipeline, TypeScript typecheck and complete SDK
  `--uya-occlusion` suite passed with development preview enabled. The full C#
  bridge/domain suite also passed against the clean pinned SDK with preview disabled.
  The tested eight-file SDK integration patch was applied to the sibling SDK checkout
  after checking each destination's pre-edit hash; the dependency pin is unchanged.

These are synthetic development checks. Real-map blocker/probe behavior, external
source/catalog change watching, large-scene performance, broader accessibility and
native grid/mask publication remain unqualified or unimplemented. No regeneration
or production bake-publication gate was opened.

## Primary tree and display correction

On 2026-10-31, the Coverage dock adopted the shared editor tree. A selectable
Primary row represents original/imported octants; authored boxes, cylinders and
graphs each have their own row with eye, power and focus controls. Keyboard row
selection and double-click focus use the existing tree behavior. Primary selection
has no transform gizmo or generated entity. Rows fit a narrow dock with ellipsized
names and visible actions.

Start from scratch previously disabled the recipe contribution while the viewport
continued drawing the terrain package's original grid. Primary contribution now
gates that grid, IDs, frozen outline, qualified samples and viewport mask culling.
Disabled Primary reports zero active octants and source-grid display/bake controls
are inactive. Disabling also clears the freeze setting and hides any retained
coverage preview; restoring Primary retains source data and display preferences.
Authored helpers remain independently visible and editable.

`occlusion-visibility.test.ts` verifies disabling loaded/frozen masks, reloading old
mask data while Primary is disabled, restoring Primary and preserving source data.
The actual dock/viewport are checked in an isolated Electron fixture with synthetic
Primary cells, IDs, samples and authored shapes. It verifies Primary selection and
focus, ArrowDown navigation, zero visible IDs/disabled display controls after Start
from scratch, restored cells/IDs, and eye hiding without changing contribution.
Screenshots show that old cubes, IDs, samples and frozen outlines disappear. The
full `npm test` pipeline, `npm run typecheck` and `git diff --check` pass. These checks
do not enable native grid regeneration/publication or qualify real-map coverage.

## Automatic shape octant previews

On 2026-10-31, Forge connected normal shape editing to visible SDK cell generation.
The SDK now declares preview API version one in `OcclusionCoveragePreview.props`;
shared Forge build properties automatically enable the host adapter and matching
C# tests when that declaration is present. No environment flag is needed with the
updated configured SDK. Clean pinned SDKs omit preview support, and explicit
`ForgeEnableOcclusionCoveragePreview=false` still disables it. The SDK pin is
unchanged and no production native-publication gate was opened.

The authoring hook defaults to automatic shape previews after a 400 ms pause in
committed edits. It coalesces rapid edits, uses the existing background host job,
shows successful results immediately, and refreshes after undo/redo or clearing
sources. It permits one active renderer request and does not repeatedly retry a
failed/cancelled attempt. Manual Preview coverage retries; automatic updates can be
turned off. Enabled graphs use explicit evaluation. Display labels distinguish
retained Primary octants from the generated octant preview.

Verification uses an isolated Electron/Xvfb window with the actual hook, dock,
viewport, host process and SDK (no mocked geometry replies or proprietary inputs).
A synthetic empty project disables Primary and authors ordinary shapes. Assertions
verify automatic visible cells after Add box, two rapid edits coalescing into one
host request, transformed/resized box and cylinder coordinates, undo, manual mode,
manual retry, cancellation without retry loops, failure retaining stale cells,
explicit graph evaluation and clearing all sources to an empty preview. Screenshots
show the actual SDK-generated box cells in the viewport. The real-host bridge
regression retains moved-box, cylinder-height and empty-result coordinates.

MSBuild property checks verify automatic enablement, explicit opt-out and clean-pin
fallback. TypeScript typecheck and the full Forge test pipeline pass without the
preview environment flag against the capable SDK. A clean pinned-SDK C# run checks
backward compatibility. Synthetic editor cell generation does not qualify real-map
coverage, native coordinate/bit/allocation behavior or visibility publication.

## Shared octant display correction

On 2026-10-31, the generated-cell overlay was corrected to reuse the original
Primary octant renderer. The previous preview implementation used triangle
wireframes, a separate color and an overlay pass that drew through geometry.
`OcclusionOctantOverlay` now owns the existing instanced transparent fills and
batched twelve-edge outlines for Primary, frozen and generated octants. Generated
cells render in the main scene with the same opacity, configured color, tone
mapping and depth behavior. Authoring gizmos/helpers remain in the tool pass.
Stale and graph-inspection colors remain explicit diagnostics on the same geometry.

Viewport regressions verify both consumers use the shared renderer, exact matching
coordinates/material settings, twelve axis-aligned edges per cube without triangle
diagonals, normal depth testing, color changes without geometry rebuilds, no picking
and disposal of fill, instance and edge resources. The actual-host Electron fixture
also checks the corrected generated-cell appearance and the existing automatic
shape generation/cancellation/error behavior. Native generation/publication remains
outside this display correction.

## Reproduction

The [coverage project contract](../features/occlusion-coverage-project-v1.md) records
the M18A-002 schema-27 foundation. `OcclusionCoverageProjectTests` verifies Primary-only
migration without file rewrites, separate imported-source authority, semantic recipe
hashes, bounded/malformed data, copied caller lists, portable attached assets,
save/reopen/recovery, stale/cancelled edits, no-op events, selection and history.
`UyaOcclusionCoverageBuildTests` retains the golden source-mode packed result with
disabled helpers and proves that active authored coverage blocks selective bakes
and packing without changing the previous stage. This is backend evidence; the
shape creation and viewport gizmos were added in the subsequent development work
recorded above.

Run the normal consumer contract suite:

```sh
npm run test:bridge:cs
```

To put all build artifacts in a temporary directory while using the same pinned
SDK source and tests:

```sh
dotnet run --project tests/Forge.Host.ProtocolTests/Forge.Host.ProtocolTests.csproj \
  --configuration Release -p:BuildInParallel=false -m:1 \
  -p:UseArtifactsOutput=true -p:ArtifactsPath=/tmp/horizon-forge-m18a-artifacts
```

The standard `npm test` pipeline includes these tests. No proprietary ISO, WAD,
model, or texture payload is required for the synthetic checks.

Initial validation on 2026-10-30: the complete C# bridge/domain suite passed using the
temporary-artifact command above. The SDK checkout and pinned revision were unchanged.

Subsequent validation on 2026-10-30: complete-level growth/replacement checks pass
against the pinned SDK. The SDK source-free grid suite, existing `--uya-occlusion`
suite, Forge occlusion tests, and complete C# bridge/domain suite also pass against
the SDK development snapshot. The existing SDK `--uya-sdk-archive` suite fails at
the gameplay Moby-replacement readback assertion; the same assertion also fails in
the untouched checkout's Release test binary. That broader baseline failure remains
outside this occlusion change.

The full Forge `npm test` pipeline passed with
`RatchetSdkRoot=/tmp/horizon-forge-m18a-sdk`, including C# consumer compatibility,
desktop builds, host integration, worker lifecycle, and security checks. The first
sandboxed run stopped in updater tests because loopback listen was denied with
`EPERM`; the successful run allowed their local fixture server. The tested SDK patch
was then applied to the sibling SDK checkout without changing Forge's dependency pin.

Schema-27 backend validation on 2026-10-30: the complete C# consumer suite and full
Forge `npm test` pipeline passed against the same SDK development snapshot, including
the new recipe migration/history/recovery and authored-build-gating checks. Planning
links/anchors and `git diff --check` passed.

Coverage IPC validation on 2026-10-30: capability-gated query/update operations retain
the existing v5 snapshot layout. Shared C#/TypeScript fixtures cover all shape tags,
native-space poses and markers; invalid states/counts, truncation and trailing bytes
are rejected. The full Forge pipeline passed with a new real-host test proving
atomic coverage/editor replies, stale conflict handling, undo/redo and save/reopen.
Typechecking and focused C#/occlusion checks passed. The dock now exposes Primary
start/restore and saved-source contribution controls; an interactive visual/keyboard
walkthrough had not yet been recorded. Subsequent creation/gizmo development is
recorded above; native generation/publication remains open.

## Arbitrary coverage application development (2026-10-31)

The SDK now exposes `OcclusionComposer.ApplyCoverageToLevel(GameId, levelWad,
masks, token)`. It joins the existing source-free serializer to the existing asset
composer and archive builder. No second writer, archive implementation, or Forge
binary parser was added. The old `ApplyToLevel` API still rejects changed coordinates
and retains its allocation compaction. The new path accepts complete explicit masks,
checks exact packed grid readback and never writes the source level or project.

The existing asset composer now also handles a zero source occlusion pointer. It
inserts the new payload inside the loaded scene view, before trailing assets, and
relocates known pointers and the trailing sequence table. An invalid/nonpositive
scene-view boundary is rejected. Empty coverage remains unsupported.

SDK complete-level fixtures test growth across sectors, shrinkage, missing-grid
insertion before trailing assets and at asset end, sequence relocation, deterministic
output, exact coordinates/masks, palette/sound/auxiliary/opaque-byte preservation,
source-only rejection, malformed masks, invalid boundaries, cancellation and immutable
inputs. Archive packing regenerates its compressed-size metadata, so semantic payload
preservation is checked separately from whole-container byte identity.

This API is unreleased and not yet consumed by Forge's visibility-bake publication
path. M18A-007 must pass an accepted effective-grid identity to the existing sample
planner/GPU worker, key its cache accordingly, and atomically publish the result.
Expanded grids remain subject to the runtime gates above.

Read-only real-level composition checks used the configured clean ISO and existing
SDK extraction, unpacking, creation, application and grid reading APIs. All new masks
explicitly set bit 1023. Replacing coverage grew each native allocation; applying a
second replacement shrank each to 16 cells. Both packed outputs read back every
intended coordinate and complete mask. The checks did not save a level, patch either
ISO, or edit a Forge project.

| Source level | Source cells / native bytes | Replacement cells / native bytes | Source level-WAD SHA-256 |
| --- | --- | --- | --- |
| 03 | 5,721 / 177,408 | 1,642 / 213,632 | `2d2b93e9aa8e8dc4dadb911e6eb1710bdf2135c408cdd66eb07d73f9d1ec4dfd` |
| 41 | 12,125 / 657,664 | 5,394 / 701,568 | `86a508f1bc092fc08d389be6aaa479221f2d212074bcb9479d41719532f55db0` |

For each level, all 49 unrelated leaf payloads retained every source byte. Three
gameplay blocks acquired only zero alignment padding through the existing archive
builder; the remaining 46 were byte-identical. Container headers/pointers and
compression are regenerated by that builder. Source WAD hashes remained unchanged.
These artificial masks test composition and do not qualify gameplay visibility.

The SDK Release build, full `--uya-occlusion` suite and existing
`--uya-level-asset-composer` suite pass. The broader `--uya-sdk-archive` selector
fails its pre-existing nested gameplay replacement assertion in
`ValidateUyaLevelArchiveBuilder` (`Program.cs`, assertion text: “UYA SDK archive
replacements should survive final compression and reader re-entry”). An independent
copy with all five changed files restored to their pre-change bytes reproduces the
same failure. That failure is outside this occlusion change and remains unresolved.

Forge verification against the configured SDK checkout: full `npm test`,
`npm run typecheck`, and `git diff --check` pass. The SDK checkout also passes
`git diff --check`. The SDK remains unreleased and the Forge dependency pin is
unchanged. No PCSX2 or hardware execution was performed for these new grids.


## Authored coverage build integration (2026-10-31)

SDK-capable development builds now expose **Build occlusion** in the established
Occlusion dock with Primary disabled. This action combines regeneration and visibility
baking into one accepted result. **Qualify samples** prepares the same generated cells
without accepting a bake. Preview remains a separate nonauthoritative operation.

The generation adapter reuses `UyaOcclusionSceneSnapshot`, the existing SDK preview
adapter, `CreateGrid` / `ApplyCoverageToLevel`, `OcclusionSceneBuilder`, sample planner,
GPU worker and `OcclusionBakeStore`. Graph evaluation consumes the already composed
packed scene rather than creating another snapshot. All initial generated masks set
bit 1023, as established by native inspection. New visibility masks can grow beyond
that initial allocation; Primary-only rebuilds retain source-bound compaction.

The cache retains its existing schema and adds optional coverage metadata: recipe
hash, project hash, SDK algorithm version, canonical coordinate hash and bounded
object mappings from the composed scene. Source-only cache reads ignore authored
results. The input hash identifies exact packed bytes before inserting generated
coverage; final packing compares those bytes before applying the generated grid.
Commit checks the saved project twice, validates the worker/coordinate identities
and resource budgets, then uses the existing atomic replacement. Empty coverage is
rejected and previous output is retained. Map preflight rejects missing/stale authored
results. The project hash conservatively invalidates content edits except editor
hidden/locked flags, with implicit/default entity state normalized.

Visibility export uses accepted generated cells and composed-scene mappings rather
than original Primary masks. Electron validates the optional recipe/project identity
pair and the renderer accepts only the matching current result. Octant display and
IDs use the same established overlay; freeze and qualified samples work with Primary
disabled. Coverage edits stale samples and reset freeze. Closing or switching projects
invokes the existing build cancellation path in Electron before changing the editor
session. Unmapped editor objects remain visible. Existing SDK native-group validation
and streamed-terrain restrictions remain unchanged.

`RatchetSdkOcclusionCoverageGenerationApiVersion=1` advertises the generated-grid
application overload used by the cache path. Forge enables its generation adapter
and `editor.occlusion-coverage.build` capability only for compatible SDK checkouts
with preview support. The existing source-only SDK application API is unchanged.
The clean v0.10.0 pin still builds without the development adapters; no dependency
pin or release was changed.

Verification:

- `UyaOcclusionCoverageGenerationTests` prepares mixed box/cylinder coverage with
  Primary disabled, checks sample coordinates/reserved bits, accepts worker output
  that grows beyond the seed allocation, applies it to a complete level and verifies
  normal bake/pack identity. It checks generated visibility export, cylinder replacement,
  current-scene graph generation, empty coverage, cancelled/stale commits, project/recipe
  and exact packed-byte invalidation, retained prior cache and Primary restoration.
- Renderer checks cover authored masks with Primary disabled, recipe/scene identity
  rejection and coverage-dependent sample staleness. Electron boundary checks reject
  incomplete, malformed and nonstring identity pairs. Build lifecycle checks abort
  active jobs and verify the next job can run after cancellation.
- The existing Electron/WebGPU worker ran against an isolated synthetic fixture with
  two generated octants at `(10,10,10)` and `(20,20,20)` from a box and cylinder:
  18 qualified origins, approximately 693 ms worker wall time and 1.73 ms reported
  GPU time. The actual host commit accepted its result, and visibility readback
  contained exactly those cells and matching recipe/project identities. This is a
  small functional check, not a performance or hardware qualification.
- Full Forge `npm test`, typechecking and diff checks pass against the configured
  development SDK. The SDK occlusion suite passes with the new cached-grid overload.
  An isolated clean v0.10.0 build and full C# bridge/domain run also pass, preserving
  the source-only path and absence of the authored build capability.

The fixture exporter is available through the C# test runner's
`--write-occlusion-coverage-fixture <new-directory>` selector; the focused retained
suite uses `--occlusion-coverage-generation`. Generated test input contains no game
payloads. No user project, source ISO or development ISO was written by these checks.
SDK release/pin integration, broader UI/resource testing and PCSX2/hardware execution
of expanded grids remain open. The pre-existing broader SDK archive-test failure is
recorded in the preceding section and was not changed by this integration.


## Project-load visibility concurrency regression (2026-10-31)

Project loading could request visibility before coverage metadata arrived, then
request it again when metadata resolved. React development effect replay could also
overlap reads. The visibility handler shared the exclusive build flag, so a second
read failed with “A build is already running” even though no rebuild was requested.

Visibility now has a separate shared in-flight read keyed by project/editor state,
source and caller. Matching requests share its result. A real build cancels an older
background read; reads requested during a build wait for publication. Actual builds
remain exclusive. Project close/switch cancels active and queued reads, and failed
reads can be retried. The renderer waits for initial coverage metadata and defers
reads while editor work is busy; disposed requests cannot update its data/errors.

The retained lifecycle suite reproduces the original overlap failure before the
fix and now covers shared reads, failure/retry, build interruption and queued reads,
changed project state and cancellation on closure. Full `npm test`, typechecking
and `git diff --check` pass. An isolated real Electron window opened the synthetic
mixed-shape project and returned generated masks for all three simultaneous visibility
requests, with zero build-busy errors. The fixture's deliberately incomplete source
asset thumbnails still report their pre-existing decode errors; this check targets
project opening and visibility request coordination. No user project was used.

## Editor visibility invalidation regression (2026-10-31)

Scene-tree hide/show persisted entity state and changed the full project fingerprint.
The renderer then rejected masks loaded from the saved project, disabling authored
occlusion controls even though no baked geometry changed. After saving, the host also
rejected the bake's full project identity. An isolated Electron reproduction confirmed
different live and loaded fingerprints immediately after hiding an object.

Coverage snapshots now include a separate `visibilityFingerprint`, computed with
entity hidden/locked flags removed and absent/default state normalized. Accepted
coverage metadata and renderer validity use this identity. The full project fingerprint
still protects edits, previews and worker commits; final packing still verifies exact
composed bytes. Geometry disabling and other content changes remain invalidating.
A mismatched visibility response now reports the corrective build action instead of
silently leaving the dock disabled. Older bakes made with explicit entity state may
require one rebuild to adopt the normalized identity.

Retained tests cover implicit/explicit defaults, hide/lock, disabling and history
restoration, renderer identity checks, and accepted masks through save/reopen and
normal packing with hidden objects. Full `npm test`, `npm run typecheck` and
`git diff --check` pass. An isolated Electron check verified that scene-tree hiding,
the Solid collision viewport switch, occlusion off/on, and a subsequent save/read
kept generated masks available and the enable control usable. It used the existing
synthetic two-cell fixture and did not touch a user project or ISO. Its incomplete
thumbnail assets still produced their known unrelated preview error.

## Stale visibility warnings and export frequency (2026-10-31)

Missing or stale authored visibility is an expected worker result:
`{"schemaVersion":1,"status":"needs-build"}`. It no longer rejects the visibility
IPC request or appends a build-failure log. The renderer shows a warning in the dock
and uses the existing log snapshot mechanism to avoid repeated warning entries.
Malformed cache/data still fails normally, and map packing/commit acceptance remains
strict. No previous cache is deleted or rewritten by visibility inspection.

The visibility hook loads after coverage metadata arrives and retains the settled
result. Editing compares recipe/visibility identities locally; undo can restore the
loaded masks. Successful occlusion builds, project changes, switches between
Primary-only/authored mode and explicit retries request new masks. Enable/freeze
toggles, coverage preview and unrelated busy work do not reload settled results.
An interrupted initial request can resume, and obsolete project responses are ignored.

Retained hook checks cover repeated edits, busy work, undo, build publication, mode
switching, expected warnings, failures/retry, interrupted reads and project switching.
Host checks verify missing/stale inspection succeeds while strict build reads fail,
corruption remains an error, and the accepted cache survives inspection. Runtime
checks validate the expected status and reject malformed statuses. Full `npm test`,
typechecking and diff checks pass. The isolated real Electron check made several
consecutive scene edits, observed one warning with no occlusion IPC errors, then
saved and read the stale scene successfully as `needs-build`. It used only the
synthetic fixture; its known unrelated thumbnail error remained. No user project
or ISO was used.

## Qualification and build preparation report (2026-10-31)

The existing shared export/qualifier now attaches an optional `preparation` report
to worker scenes. The UYA adapter uses the established SDK grid reader to measure
input grid bytes, tree/header bytes and allocated mask slots; it reports the SDK's
fallback-group mask and the existing qualifier's cell/sample work limits. Unshared
mask bytes are an estimate of one complete 128-byte mask per qualified cell.
No second native serializer, shape enumerator or sample qualifier was introduced.

Electron checks finite integer/resource bounds, mask allocation sizes and consistency
with qualified counts. The dock displays the report with qualified samples. Old
scenes without metadata remain supported. Build preparation reports counts and sizes
before launching the GPU worker; completion reports the actual native grid byte count.
The report describes prepared input, not a proven runtime-memory budget. Final SDK
mask/tree capacity validation and atomic cache publication remain authoritative.

Verification:

- Retained host generation checks compare preparation data with the SDK's native
  readback and qualifier limits. Runtime checks reject malformed/missing fields,
  inconsistent counts/allocations, oversized summaries and invalid completion sizes;
  legacy payloads remain accepted.
- Full `npm test`, `npm run typecheck` and `git diff --check` pass.
- A fresh synthetic mixed box/cylinder job contained 2 cells, 18 shared sample
  positions, a 192-byte input grid, a 64-byte tree/header, one input mask slot and
  a 256-byte unshared-mask estimate. Electron read the actual host output, and a
  server-rendered Occlusion panel displayed these values and the 100,000-cell /
  250,000-sample work limits.
- The existing Electron/WebGPU worker baked that scene with the additive metadata
  (about 646 ms wall time and 3.08 ms reported GPU time). Host commit validated the
  result and reported a 192-byte final native grid. This small fixture establishes
  interoperability, not representative performance or platform qualification.

No user project, source ISO or development ISO was modified. Broader overlapping
volumes, graph routes, resource scaling and packaged-platform qualification remain
open; the user's custom-cylinder in-game report is recorded separately above.

## Camera placement, explicit evaluation and coverage shortcuts (2026-10-31)

New boxes/cylinders use a point 16 map units ahead of the current viewport camera;
larger graph regions use 48 units. The existing project/scene transforms convert the
point without changing camera state or updating React on every camera frame. Without
an open viewport, the existing selected-object/default fallback remains available.
New graph seeds use a camera-relative point in graph-local coordinates, clamped to
the region bounds; graph-center fallback remains available without a camera.

Automatic preview scheduling and its session toggle were removed. Edits mark previous
results stale, and Preview coverage / Qualify samples / Build occlusion explicitly
evaluate them. A coverage-metadata refresh alone no longer flashes stale color.
Successful sample qualification hides the previous preview and displays its actual
qualified coordinates through the established octant/ID overlay, independently of
the sample-marker checkbox. Qualified cells never become visibility/build authority.

The existing configured global edit actions now dispatch to the active coverage
selection before gameplay-entity editing. Copy/paste, duplicate and delete support
both source volumes and owned seeds, with fresh IDs, boundary/count validation,
normal project history and project-local clipboard lifetime. Seed copies retain local
coordinates and cannot paste outside the selected graph. Primary remains protected.
The Forge Edit menu reflects coverage selection/clipboard availability. Text inputs
retain normal editing, and handled edit keys stop before dock navigation. A real
walkthrough exposed Delete closing a focused dock tab; this is fixed and retained
as a shortcut propagation regression check.

Verification:

- Math/overlay checks cover camera direction and coordinate conversion, rotated
  graph seed coordinates and the existing shared gizmo path.
- Hook/action checks cover explicit-only evaluation, metadata refresh, stable stale
  state, clipboard IDs/positions, active coverage dispatch, boundary/count validation,
  failed/busy edits, project changes, custom keybindings and text-input behavior.
- Full `npm test`, `npm run typecheck` and `git diff --check` pass.
- An isolated real Electron window created a graph ahead of the camera and a seed
  within its bounds, retained the stale preview until explicit refresh, and exercised
  Ctrl+C/V, Ctrl+D, Delete and undo/redo. Delete also passed with the dock tab focused.
  The known unrelated synthetic thumbnail decode error remained. The fixture was
  restored afterwards; no user project or ISO was used.

Larger-map placement/interaction, display scaling, broader accessibility and in-game
graph-route qualification remain open.

## Graph seed list and generation workflow (2026-10-31)

Graph seeds now use the shared editor tree inside a labeled, bordered list with
numbered rows, explicit selection text, focus controls, keyboard navigation and
scrolling. Existing pagination remains for large graphs. Selected-seed properties
have their own fieldset, separate from the list.

A nearby generation fieldset exposes “Include this graph in coverage” and
“Generate graph octants”. Empty or disabled graphs cannot start generation. The
button uses the existing whole-recipe evaluator and displays the selected graph's
accepted cells only after success. Failed/cancelled previews preserve inspection
state. Help text explains enabled-source evaluation and the separate Qualify samples
/ Build occlusion steps required for the combined in-game output.

Verification:

- Full `npm test`, `npm run typecheck` and `git diff --check` pass. Hook regression
  checks cover preview success/failure return values without changing explicit-only
  evaluation behavior.
- A server-rendered graph with three seeds verified labeled rows, selected-seed
  properties, empty/disabled generation guards and the enabled-generation state.
- An isolated Electron window verified three seed rows, keyboard selection, graph
  inclusion and generation-button guards. A captured dock image was inspected for
  list framing, alignment and separation of generation/selected-seed controls.
- The placeholder ISO correctly failed graph generation at source-level lookup
  (“exceeds ISO stream length”); the UI retained its inspection state and reported
  the error. This fixture cannot establish a successful graph preview against real
  map geometry; existing host graph-preview tests and hook success checks cover
  the corresponding automated paths. The private project fixture was restored.

No user project or ISO was modified. In-game graph routes, larger seed lists and
broader display-scaling/accessibility qualification remain open.

## Graph operation logging and scene-query traversal (2026-10-31)

The coverage preview path had diverged from other editor operations: preview/read/
cancellation failures were only stored in a local dock alert, while edit failures
both logged and rendered the same alert. All four operation paths now use the
existing `useLogReporter('Occlusion')` and shared session Log. The duplicate local
error state, authoring-contract field and dock alert were removed. Expected
cancelled/superseded responses remain suppressed. A failed preview retains previous
successful output, releases its operation state and can be explicitly retried.

The reported work-limit message originates in the SDK's bounded scene-query
traversal. Ordinary blocked/unblocked tests unnecessarily searched for a nearest
hit. The same existing traversal now stops at the first definite opaque hit for
those tests. Buried-seed checks retain nearest-hit searches and coincident-winding
handling. No geometry/alpha policy, coordinate identity, public API or work limit
was changed; parsing and intersection remain in the SDK, with reporting in React.

Verification:

- A new 40,000-overlapping-triangle graph workload reproduced the same 40-million
  scene-query failure before the fix. It now completes with the same coordinate
  fingerprint and 108 accepted / 108 rejected cells, 1,082 rays and 15,745,268 query
  tests (about 363 ms in this run). A separate blocked-segment assertion checks
  early exit on a definite hit. These synthetic figures are not real-map benchmarks.
- The SDK `--uya-occlusion` suite passes, including graph rooms/doorways, alpha,
  winding, buried-seed ambiguity, finite endpoints, work/ray limits, cancellation,
  composition, golden identities and the existing bake/qualification regressions.
- Hook checks use the actual shared LogStore/reporter and cover read/edit/preview/
  cancellation failures, one entry across rerenders, preserved preview data,
  released controls and suppression of a superseded preview rejection.
- Full `npm test`, `npm run typecheck` and both repositories' `git diff --check`
  pass. An isolated Electron walkthrough confirmed a real failed preview appeared
  once as an Occlusion error in the session Log, without an operational dock alert;
  its Generate button remained usable. The private placeholder-ISO fixture was
  restored afterwards.

The user's exact map/graph was not rerun. Real workloads can still reach the retained
budgets, particularly on poorly separated geometry. No user project or ISO was
modified; broader real-map graph and runtime qualification remains open.

## Hoven3 saved-graph reproduction and budget calibration (2026-10-31)

The user confirmed `hoven3` as the affected project. Its saved enabled Graph 2 has
three seeds and dimensions about 306.55 × 292.04 × 32 map units. The exact bounding
range contains 51,948 candidates. An isolated diagnostic copied the manifest,
content and attached assets to `/tmp`, used the configured asset catalog read-only,
and captured the current saved scene through `UyaOcclusionSceneSnapshot`. The source
ISO was opened read-only. No user staging, bake cache, project or ISO was changed;
original manifest/content SHA-256 checks matched after the reproduction.

The captured scene contains 154,744 triangles and 190 textures. The unchanged
production query budget fails after the 18,944-candidate progress checkpoint in
about 1.06 s of graph evaluation. A diagnostic-only runtime profile override, before
attempting any production-limit change, allowed the same traversal to complete:

- 15,652 accepted and 36,296 rejected graph octants, from 51,948 candidates.
- 1,037,980 rays and 97,519,715 geometry-query tests, about 1.72 s of evaluation.
- Coordinate fingerprint `c97c9a212e23f08830f8ea41e9aeb8f048c29b07d1b767723a4931cd927c25ad`.

These timings exclude scene preparation and describe one development machine,
not a platform performance guarantee or an in-game graph-route qualification.
This SDK comparison evaluates graph contribution before shape union/exclusions.

The user authorized the shared budget change after reviewing this measurement.
The development query allowance is now 200 million actual geometry tests in the
SDK, C# bridge encoder and Electron decoder. The wire layout, algorithm version,
coordinate identity, candidate/ray/geometry/cell limits, cancellation checkpoints
and geometry/alpha rules are unchanged. No query-result cache or replacement
intersection algorithm was added.

A retained synthetic three-seed graph exceeds the old 40-million-test ceiling and
completes with identical coordinates under the calibrated budget. C# and TypeScript
wire tests accept the new boundary and reject 200,000,001 tests. The SDK still
forces the runtime work-limit failure and checks its actual limit, rays attempted
and scene triangle count, using invariant count formatting. Operation failures
continue through the shared session Log.

Full `npm test`, `npm run typecheck`, the SDK `--uya-occlusion` suite and both
repositories' `git diff --check` pass. The copied saved project is additionally
checked against the rebuilt SDK without a profile override and through the complete
Host preview / C# encoding / Electron decoding path. Results are recorded below.

The rebuilt SDK completed the copied saved graph under the actual 200-million
profile, with no diagnostic override: 15,652 accepted / 36,296 rejected cells,
1,037,980 rays and 97,519,715 tests, with the same coordinate fingerprint above
(about 1.83 s of SDK evaluation). The complete Host path recomposed the copied
project from the read-only source ISO/catalog and returned the same graph lists;
including the cylinder contribution, final effective coverage also contained
15,652 cells. Composition, extraction, evaluation and C# encoding took about
9.92 s in this run. Electron decoded the actual 811,716-byte result and retained
all counts above the old 40-million wire bound. The original project's manifest
and content hashes still matched the pre-reproduction values. In-game graph-route
and broader platform qualification remain open.

## Sticky graph coverage from TIE/tfrag surfaces (2026-10-31)

The user requested v1 Sticky / Sticky distance behavior and explicitly selected
TIE/tfrag geometry as its surface source. Graph source properties now expose Sticky
(off for existing graphs) and distance (12 map units by default), using normal Apply
source / Discard edits, whole-recipe history, save/reopen and explicit generation.
No collision data is read. The UI also reports the active setting beside generation.

The SDK extracts the current TIE/tfrag render-face subset from the existing
calibrated scene extraction, excluding shrubs/mobys and sharing the material/alpha
atlas. The existing BVH/query path applies v1's fourteen axis/corner probes, with
the two-unit starting offsets and finite distance, before checking seed visibility.
Front-facing opaque surface contacts include edges/endpoints; seed visibility and
buried-seed intersection behavior remain unchanged. Both query instances share the
existing ray/work budgets and cancellation checks. Rejected cells remain in graph
inspection; include shapes can restore cells and exclusions still apply last.
Preview and native preparation use the same current-scene evaluator.

Optional graph settings do not require a project rewrite on open. Disabled Sticky
retains the existing recipe identity; active distance participates in identity with
omitted/explicit 12 equivalent. Extended graph wire kind 4 carries the settings;
legacy graph kind 3 and its shared fixture remain byte-identical. Source updates
validate finite nonnegative distance through 1,000,000 at Electron, Host and SDK
boundaries. Shared C#/TS fixtures cover the extended wire representation.

Verification:

- Full `npm test`, `npm run typecheck`, the SDK `--uya-occlusion` suite and both
  repositories' `git diff --check` pass. SDK cases cover air-layer trimming,
  diagonal-only surfaces, v1 starting offsets, zero/default distance, inclusive
  endpoints, winding, alpha, nonblocking faces, composition, invalid input,
  shared budgets and cancellation. Existing graph golden identities remain valid.
- Host checks cover extended payloads and malformed values, save/reopen, semantic
  identity/defaults, current unsaved TIE extraction and actual Sticky rejection in
  the shared preview/preparation evaluator.
- A real isolated Electron window verified default-off / distance 12, editing and
  applying distance 8, normal undo/redo and saved project content. The captured
  controls were visually inspected. The unrelated synthetic thumbnail decode error
  remained; the private fixture was restored afterwards.
- The previously copied saved `hoven3` scene contained 154,744 render triangles,
  with 153,336 TIE/tfrag Sticky triangles. Sticky off retained the exact 15,652-cell
  coordinate fingerprint recorded above, 1,037,980 rays and 97,519,715 tests.
  Sticky distances 12 / 8 / 4 retained 10,198 / 8,225 / 5,616 graph cells,
  respectively (all strict subsets of the baseline). Counts include both surface
  and visibility queries: 891,268 / 857,211 / 819,049 rays and 57,459,407 /
  50,485,806 / 42,668,752 tests. SDK evaluation took about 1.21 / 1.13 / 0.98 s in
  this run, excluding scene preparation; these are not platform guarantees.
- The actual Host packed-scene evaluator retained 10,198 graph cells at distance
  12 and 10,308 final effective cells after the enabled cylinder contribution.
  This confirms the shared evaluator and source-composition behavior.

No user project, source ISO or development ISO was modified. Sticky is a proximity
heuristic rather than a walkability proof. In-game camera routes, thin/complex
surface cases and broader platform qualification remain open.

### New graph defaults and collapsed coordinates (2026-11-01)

New graphs now enable Sticky at distance 8. Existing saved graph values and
legacy missing-field defaults are preserved. Source transforms/dimensions and
selected-seed positions use the existing Mantine Accordion pattern, initially
collapsed under Coordinates; Sticky controls remain visible.

`npm run typecheck`, full `npm test` and `git diff --check` pass. An isolated
Electron window with a synthetic project verified new-graph defaults, initially
collapsed source and seed coordinates, keyboard expansion, applying both source
and seed coordinate edits, and saving. The synthetic fixture's absent native occlusion grid and
thumbnail data produced unrelated load errors. No user project was modified.

### Occlusion panel task grouping (2026-11-01)

The dock now separates Sources, Build and Viewport tasks. Compact Add volume and
source/seed Actions menus replace the repeated creation/duplicate/delete buttons.
Generation, qualification and baking share the Build tab, with settings and
diagnostics collapsed. Viewport groups visibility/freeze and every overlay toggle;
its display guide is collapsed. Source properties owns the enabled state and
Sticky controls. Graphs omit the noneditable coverage-role field. Source and seed
drafts survive tab switches. Progress/cancellation appear above the tabs.

Verification:

- Full `npm test`, `npm run typecheck`, desktop build and `git diff --check` pass.
- An isolated Electron editor verified Add volume, source/seed duplication and
  deletion, source/seed draft retention across tabs, and the named guidance and
  disabled build actions for enabled graphs without seeds.
- After positioning the synthetic box inside the supported grid, Generate octants
  produced 64 cells. The generated overlay toggle and keyboard tab navigation
  worked. Earlier fixture-origin coordinates outside the native range were
  rejected normally; user project data was not involved.
- All three tabs fit a 320-pixel panel without horizontal overflow at XS and medium
  interface scales. Actual compact button heights increased from 26 to 34 pixels
  with the theme. Captured panel screenshots were visually inspected.

Only the temporary synthetic project/profile was changed. No native generation
algorithm changed, and in-game behavior was not requalified for this UX pass.

### Volume terminology (2026-11-01)

Panel copy now uses Volumes, Volume properties, Volume actions and Apply changes.
The enabled checkbox reads Use when generating octants. Help and tab references
use the same terms. Internal contracts and behavior are unchanged.
`npm run typecheck`, desktop build and `git diff --check` pass; the full suite and
live editor checks were not repeated for this wording-only change.

### Indexed graph seeds (2026-11-01)

Seed rows, focus labels and the selected-seed heading now derive Seed 1, Seed 2,
… from current graph order. The editable name field and redundant ordinal column
are removed. Add/duplicate/paste use an indexed name rather than appending “copy.”
Legacy persisted names remain compatible and do not control displayed labels;
deletion closes numbering gaps without changing surviving seed IDs or positions.

Full `npm test`, `npm run typecheck` and `git diff --check` pass. The copy/paste hook
regression starts with a legacy “copy copy copy” name and checks indexed copies.
An isolated Electron editor with 34 legacy-named seeds verified labels and focus
actions across pages, indexed selected-seed headings, repeated duplication and
deletion, automatic renumbering, absent name input, stable surviving IDs, applying
coordinate edits and saving. Only the temporary synthetic project was modified;
in-game behavior was not requalified for this editor change.

### Generation before visibility baking and actual ray budgets (2026-11-01)

The SDK no longer rejects candidate × nine probes × seed count as a worst-case ray
estimate. Actual Sticky, diagnosis and line-of-sight rays retain the shared
2,000,000 cap; candidate, seed and 200,000,000 geometry-test limits are unchanged.
Retained cells try the nearest seed first, then every remaining seed if needed,
preserving the any-clear-probe/seed acceptance rule. The distance scan includes
cancellation checkpoints. Existing coordinate golden identities remain valid.

The editor also no longer cancels generation merely because metadata refreshes
with unchanged content. Actual scene changes and project switches still invalidate
captured work. The separate mask control is now Enable visibility culling; missing
mask guidance explicitly permits octant overlays before baking. A missing preview
points to Generate octants and Log rather than implying that viewing requires a
full build. Generation failures remain in the shared session log.

Verification:

- Full `npm test`, `npm run typecheck`, the SDK `--uya-occlusion` suite and both
  repositories' `git diff --check` pass. SDK regressions cover a 51,948-cell region
  with 16 seeds, 4,096 seeds in an open region, reduced two-room query work, actual
  runtime ray-budget rejection, existing golden coverage and cancellation.
- The hook regression first reproduced a discarded result during an unchanged
  metadata refresh, then passed with the fix. A content change still cancels the
  pending operation and preserves the last successful result as stale.
- A copy of the current saved `hoven3` contains a 51,948-candidate graph with 16
  seeds and Sticky distance 8. Removing the pessimistic preflight alone exposed a
  genuine actual-ray exhaustion. Trying the nearest seed first completes with
  13,553 accepted graph cells, 38,395 rejected cells and 13,731 effective cells
  after the enabled cylinder contribution. The shared Host evaluator measured
  1,956,909 rays and 171,760,728 geometry tests. Preparation plus evaluation took
  about 13.25 seconds in this run; this is not a platform guarantee. The ray count
  remains close to the actual cap.
- An isolated Electron editor generated a small synthetic graph and toggled its
  overlay without baking masks. Only visibility culling remained disabled. Missing
  result guidance and the distinct culling message were checked. Placeholder render
  objects were removed from that private fixture, following the existing Host graph
  integration test setup; no production rendering behavior changed.

The real catalog and source ISO were read only. All evaluation/preparation and
fixture writes stayed in temporary workspaces; no user project or development ISO
was modified. Broader graph workloads and in-game routes remain unqualified.

### Optional seed graphs on boxes and cylinders (2026-11-01)

Creation now offers Box and Cylinder. Each include volume has an optional Use seed
graph control; its existing shape supplies the boundary, transform and dimensions.
New attachments default to Sticky enabled at distance 8. Disabling filtering keeps
seeds and settings while restoring plain-shape generation. Existing standalone
graphs display as boxes with filtering enabled without being rewritten on open.
Shape changes preserve seed identities, and dimension edits reject seeds outside
the resulting volume. Cylinder seed placement/paste and candidate evaluation use
the circular boundary. Intersecting edge cells remain intentionally conservative.

The SDK reuses the existing cylinder/cell intersection implementation for graphs.
Host preview and native preparation consume that shared evaluator. New attached
graph wire kinds preserve legacy encoding, and a capability flag gates cylinder
graphs when using an older SDK. Semantic fingerprints preserve equivalent legacy
box graphs and ignore inactive graph metadata.

Verification:

- Full `npm test`, `npm run typecheck`, desktop build, SDK `--uya-occlusion` suite
  and both repositories' `git diff --check` pass. Tests cover shared binary fixtures,
  circular containment, rotated cylinder coverage, disabled filters, shape changes,
  history/save/reopen, legacy fingerprints and cylindrical native preparation.
- An isolated Electron editor verified the two creation options, legacy graph
  presentation, cylinder graph attachment, Sticky defaults, circular seed placement,
  conversion to a box and back, generating/viewing octants before visibility baking,
  and preserving seeds when filtering is disabled and reenabled.
- The copied saved `hoven3` graph still produces exactly 13,553 accepted graph
  cells, 38,395 rejected cells and 13,731 effective cells, using 1,956,909 rays and
  171,760,728 geometry tests. This run completed in approximately 13.37 seconds;
  the result and work counts match the preceding legacy evaluation exactly.

All project edits used temporary fixtures. The user project, catalog and source ISO
were not modified. In-game behavior was not requalified for this workflow change.

### Immediate graph attachment and seed controls (2026-11-01)

Use seed graph now applies the current valid volume edits immediately. The seed
list and Add seed appear as soon as the update succeeds. Previously the checkbox
changed only an unapplied draft, leaving seed controls hidden; a viewport move
replaced that draft and made filtering appear to switch off. Invalid drafts disable
the toggle until corrected or discarded. Failed updates retain the saved recipe
through the existing commit/error path.

The private editor regression reproduced the missing persisted graph before the
fix, then verified immediate attachment and seed creation without Apply changes,
a move through the real coverage transform session preserving the active graph and
seed identities, and filtering off/on preserving seeds. A viewport regression also
covers moving an attached cylinder graph after refreshing its transform target.
Full `npm test`, `npm run typecheck`, desktop build and `git diff --check` pass.
The panel screenshot was inspected. Only temporary fixture project data changed;
in-game behavior was not retested for this editor-only fix.

### Seed list and expanded Build tab (2026-11-01)

The seed list now ends with an accessible + icon button; only the selected seed
row shows a trash icon. The separate seed-details panel, seed coordinate form,
seed Actions menu and per-row focus icons are removed. Double-click still focuses
a seed, viewport Move positions it, and configured duplicate/copy/paste/delete
commands remain available. Volume position/rotation fields and their Coordinates
accordion are removed; shape dimensions remain directly editable in properties.

Sample pull-in and pixel edge allowance now appear openly in the Build tab. Build
occlusion is a full-width button immediately beneath those settings. Generation
and qualification remain above them; generation details remain collapsible.

Verification:

- Full `npm test`, `npm run typecheck`, desktop build and `git diff --check` pass.
- An isolated Electron editor verified immediate graph attachment, the icon-only
  add action at the bottom of the list, selected-row-only deletion, keyboard Enter
  activation of both icons, stable surviving seed identity, and absent volume
  coordinates and seed details.
- The open pull-in slider accepts keyboard changes, the pixel edge checkbox
  toggles, and Build occlusion spans the settings width directly below them.
- Volumes and Build fit a 320-pixel panel at XS and medium interface scales without
  horizontal overflow. Captured screenshots were visually inspected. The harness
  brought its private window forward so animation-dependent menus could render.

Only temporary fixture project/profile data changed. No coverage algorithm changed,
and this presentation pass did not repeat native baking or in-game qualification.

### Occlusion code cleanup and shared geometry work (2026-11-01)

Reviewed the authored-coverage path across renderer helpers, contracts, IPC codecs,
Host recipe/history, scene preparation, bake identity and SDK enumeration/queries.
The responsibility boundaries remain: editor interaction and display in React/Three,
privileged lifecycle in Electron, project orchestration in Host, and native formats,
scene extraction and coverage geometry behind the neutral SDK facade. Duplicate
validation at language/trust boundaries and versioned compatibility entry points
remain intentional; the graph and sample-qualification intersection policies differ
and were not merged.

Cleanup:

- Host adapts project shapes/graphs to SDK inputs in named private methods. Preview
  and accepted-bake validation use one algorithm-version selector. The previous
  validator mistook retained, disabled graph settings for an active filter; a new
  preparation/commit regression reproduced the resulting false stale-bake failure
  and passes with the shared selector.
- SDK coverage and visibility scene construction share geometry ordering and
  blocker policy. Plain shapes and graphs share prepared-shape candidate counting.
  Graph seed reachability is separated from candidate enumeration. Buried-seed
  directions are reused instead of allocating an array for each seed.
- When all coverage geometry is eligible for Sticky, it retains the original
  triangle array and both query modes share one spatial index. Different Sticky
  geometry still has its own index and shares the same ray/work budget. Regressions
  compare coverage hashes, ray counts and query tests across shared/separate indexes.
- Production volume creation now accepts only boxes/cylinders. Current authoring
  tests attach seed graphs through the supported helper and share a test-only volume
  fixture. Legacy read/encode and conversion fixtures remain. Removed redundant
  scale validation and obsolete form rotation normalization after numeric pose
  fields were removed. Grid-cell declarations now share one renderer contract.

Verification:

- Full `npm test`, `npm run typecheck`, desktop/Host builds, final Host protocol/domain
  suite, SDK `--uya-occlusion` suite and both repositories' `git diff --check` pass.
- A synthetic 2,048-face Sticky graph allocated 712,816 bytes before and 579,344
  bytes after cleanup per measured evaluation (about 18.7% less). All five runs
  retained the same fingerprint, 1,352 cells, 50,025 rays and 97,277 geometry tests.
  These are fixture measurements, not a general map performance guarantee.
- The copied `hoven3` still yields 13,731 effective cells, including 13,553 accepted
  and 38,395 rejected graph cells, with exactly 1,956,909 rays and 171,760,728 tests.
  Preparation/evaluation took approximately 13.32 seconds in this run. Its source
  ISO and catalog were read only, and its temporary project copy was not edited.

No algorithm version or fingerprint format changed. No user project was modified.
Interactive UI and in-game routes were not requalified for this internal cleanup.

### Stable panel layout during seed saves (2026-11-01)

The initial Loading coverage row now appears only before a recipe is available.
Routine recipe commits retain edit locking and expose a separate updating state so
the dock does not show generic job progress or hide the last completion message
during an edit. Real generation/build progress and cancellation remain unchanged.

A private Electron fixture reproduced a 19.1875-pixel vertical shift during a seed
move. After the fix, measured property, input and seed-list rectangles were identical
before, during and after the real gizmo commit, including the disabled state. Hook
regressions distinguish initial loading, pending edits and metadata refreshes, and
verify failed edits release the updating state. Full `npm test`, typecheck, desktop
build and `git diff --check` pass. Only temporary fixture data changed; in-game
behavior was not retested for this UI fix.

### Graph generation profiling and optimization (2026-11-01)

Profiled the saved 16-seed Hoven graph using the existing temporary project copy,
read-only source ISO/catalog and the production Host/SDK adapters. Scene preparation
was the largest cost: each bake layer reopened and decompressed the same project.
Bake preparation now retains its validated workspace for staging and texture
inventory, and pack reuses its own preflight workspace. Reuse lasts only for that
operation; final bake validation still reloads the project and rejects changed
content before accepting the staged manifest. Asset verification is retained.

SDK any-hit queries recheck the preceding blocking face before BVH traversal,
separately for Sticky and visibility. Each hint is a full finite-segment/alpha test
and counts against the budget. Misses traverse normally. Nearest-hit buried-seed
diagnosis and ambiguity handling do not use hints. BVH sorting caches partition
centroids in pooled scratch memory while preserving its deterministic tie breaker.

Development-machine measurements:

| Measurement | Before | After |
| --- | ---: | ---: |
| Scene preparation, profiled run | 8.19 s | 6.50 s |
| SDK graph evaluation, median of three runs | 2.82 s | 2.09 s |
| Complete Host generation, single run | 13.32 s | 11.30 s |
| Geometry query tests | 171,760,728 | 131,770,409 |
| Rays | 1,956,909 | 1,956,909 |

All six SDK runs retained 13,731 effective cells, including 13,553 accepted and
38,395 rejected graph cells. Coordinate fingerprint remained
`6f9dfb981f699be09bf838583b8311c807722b31ca4a4a59cbd604e5dbc0e5ae`;
the serialized graph-inspection hash remained
`e64ee19f13b45f95ca6777627ed401db37ae82f883587ab461ff9835ef6af60c`.
Composed scene WAD bytes before/after Host reuse matched exactly. SDK allocations
rose slightly from approximately 31.5 MB to 32.3 MB per evaluation; no persistent
scene cache was added. Timing varies with scene geometry and machine load.

Verification:

- SDK `--uya-occlusion` passes, including golden coverage, independent segment
  references, alpha, endpoints, winding, buried-seed ties, budgets and cancellation.
  Added checks for one-test repeated hits, a cached face beyond the new endpoint,
  and alpha resampling. The dense repeated-seed fixture now asserts bounded work
  rather than requiring the old unnecessary work count.
- Full `npm test`, `npm run typecheck`, desktop/Host builds and both repositories'
  `git diff --check` pass. A new workflow regression edits the synthetic project
  during staging, verifies fresh final validation rejects it and confirms the last
  accepted manifest survives. Existing cancellation and corrupt-asset checks pass.

No algorithm version, fingerprint format, coverage policy or work limit changed.
Only temporary fixture data changed. Interactive and in-game qualification were
not repeated for these internal performance changes.

### Precommit identity and seed-diagnostic fixes (2026-11-01)

The baked visibility scene hash now excludes coverage authoring state, which is
already validated through the semantic recipe hash. Volume/seed renames and edits
to disabled volumes remain persisted project changes without forcing a visibility
rebake. Effective coverage changes still invalidate the recipe, and scene edits
still invalidate the project hash. Existing development bakes need one rebuild for
the corrected hash calculation; their files remain intact until replacement.

Buried-seed diagnostics now use the current one-based list index and retain stable
graph/seed IDs. A fixture with a legacy `Seed 2 copy copy` name checks the error
identifies Seed 2, then Seed 1 after deleting the earlier seed.

Project regressions distinguish persisted authoring identity from scene/recipe
identity. Generation regressions retain accepted masks across harmless edits,
save/reopen, layer baking and map packing. Existing real coverage and scene edit
invalidation checks remain. Full `npm test` and `git diff --check` pass. No SDK pin
was changed. Interactive and in-game behavior were not retested.

### Shared snapshot infrastructure and bake identity checks (2026-11-01)

Extracted bounded project copying into `Domain/ForgeProjectSnapshot`, which owns
verified attachments, content copying, link/resource checks and temporary-directory
cleanup. Callers supply captured content; the shared service has no occlusion or
game policy. `UyaOcclusionSceneSnapshot` retains scratch coverage selection, native
bake/pack calls, warning acknowledgement and composed-scene identity.

`Domain/Occlusion/OcclusionBakeStore` now owns one recipe/project/algorithm matcher
used by cache reads and commit validation. The UYA adapter supplies the expected
algorithm version and retains native-coordinate, reserved-mask and packed-input
validation. No shared Domain code depends on the UYA adapter.

Shared regressions use DL project metadata to check copying unsaved state, preserving
caller coverage, verified assets, empty/additional content, skipped caches, failure
cleanup and original-file preservation. Identity regressions check matching and
mismatched recipe/project/algorithm values, absent authored metadata and Primary-only
mode. Existing UYA composition, cancellation, oversized/link/corrupt input and
generation/packing regressions remain connected and pass.

Full `npm test` (including desktop/Host builds) and `git diff --check` pass. A fresh
capture of the temporary Hoven project produced exactly the previous packed WAD:
SHA-256 `61cf489a7091fa31ac97af19ef168153bd815d5ab6d432d363260e5259f81129`.
Source ISO/catalog inputs were read only. No SDK code, SDK pin, persisted format,
fingerprint calculation or generation policy changed in this refactor. Interactive
and in-game qualification were not repeated.
