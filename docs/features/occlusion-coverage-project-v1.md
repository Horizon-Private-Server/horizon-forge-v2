# Occlusion coverage project contract v1

Project schema 27 adds `occlusionCoverage` to project content. It is independent
of `occlusion`, which retains an imported Primary source payload and provenance.
Projects using the base level's original grid do not need an imported source
attachment. Older supported schemas migrate in memory to Primary-only coverage;
an explicit save writes the new schema through the existing journaled save path.

M18A-002 provides the backend and IPC foundation. The existing Occlusion dock now
exposes Primary contribution, box/cylinder/graph authoring and viewport gizmos.
Development builds can evaluate shape and current-scene graph coverage in a
background preview job, including accepted/rejected graph-cell inspection. SDK-capable
development builds also regenerate coverage and bake visibility through Build occlusion.
SDK release integration and real-map/runtime qualification remain open.

## Stored recipe

```json
{
  "occlusionCoverage": {
    "schemaVersion": 1,
    "primaryEnabled": true,
    "sources": [
      {
        "id": "00000000-0000-4000-8000-000000000001",
        "name": "Courtyard",
        "enabled": true,
        "role": "Include",
        "transform": {
          "position": { "x": 0, "y": 0, "z": 0 },
          "rotation": { "x": 0, "y": 0, "z": 0, "w": 1 },
          "scale": { "x": 1, "y": 1, "z": 1 }
        },
        "boxSize": { "x": 16, "y": 24, "z": 8 }
      }
    ]
  }
}
```

Every volume has a stable UUID, name, enabled contribution, Include/Exclude role,
and native-space pose. New volumes own exactly one boundary:

- `boxSize`: positive local X/Y/Z dimensions centered at the pose origin.
- `cylinder`: positive circular `radius` and `height`, with local native Z as its axis.

Either boundary may have optional `graph` settings: owned `markers`, optional
`sticky` (legacy default false), `stickyDistance` (legacy default 12), and
`disabled` (default false). Active seed filtering requires Include. Disabling it
retains seeds/Sticky settings and fills the owning shape; an inactive graph may
remain on an exclusion volume. Seeds must lie inside the actual boundary, including
the cylinder's circular radius. New graph attachments enable Sticky at distance 8.

Legacy graph-only volumes retain their positive `graph.size` as a box boundary.
They remain readable without rewriting project content; shape-owned graphs omit
that redundant size. The editor presents both as shapes with optional seed graphs.
Legacy and equivalent attached-box graphs share semantic coverage identity.

Transform scale is always `(1,1,1)`; resizing changes shape dimensions. Rotation
is a finite unit quaternion, with squared-norm tolerance `0.0001`. Positions and
dimensions are finite and bounded by the version-one authoring limit of 1,000,000;
this does not establish native representability or a candidate-work budget.

Version one allows at most 1,024 sources, 4,096 markers per graph, and 16,384 markers
in total. Names contain 1–256 nonblank characters. Source and marker IDs are
nonempty and unique across the recipe. A marker's local position must lie inside
its volume boundary. Empty source lists and graphs without markers are valid authoring
states; an enabled empty graph will require an actionable generation diagnostic.
The recipe contains no visualization visibility, selection, generated octants,
visibility masks, or gameplay entities.

## Identity and edits

`OcclusionCoverageRecipe.Fingerprint` hashes the version, Primary contribution,
and canonical enabled source geometry/roles. Names, IDs, source order, marker order,
disabled sources, and exact duplicate contributions do not change that hash.
The full project fingerprint still changes for persisted names and structure,
so those edits remain dirty/undoable even when effective geometry is unchanged.
Active Sticky and its effective distance participate in the recipe hash. The
default/explicit 12-unit distances are equivalent, and disabled Sticky settings
do not change existing recipe identities.
Source payload identity, graph-dependent scene identity, SDK/generator versions,
and qualification/visibility settings belong in the later generation job key;
the recipe hash alone cannot validate a generated result.

`ForgeProjectWorkspace.SetOcclusionCoverage` validates the whole bounded recipe
before replacement and copies caller-owned source/marker lists. Primary contribution
can be changed independently without deleting authored sources or source attachments.
The editor update entry point checks the expected project fingerprint,
joins normal undo/redo history, preserves game-entity selection, and schedules normal
recovery. A no-op creates no history/event entry. Stale, cancelled, and invalid edits
do not replace accepted state. Existing save/reopen, portability, and recovery paths
carry the recipe as normal project content.

## IPC and dock controls

The [v5 bridge additions](../bridge-protocol-v5.md#occlusion-coverage-operations)
provide capability-gated coverage queries and bounded whole-recipe updates.
Electron validates sender identity and the complete recipe before forwarding it;
the host validates again before mutation. The update result carries the editor
snapshot and coverage state from the same locked transaction, so renderer dirty,
history, and recipe state agree. Main-process handlers reject replies after a switch
to another project. The shared TypeScript declarations live in
`src/types/OcclusionCoverage.d.ts`.

The Occlusion dock queries current coverage after editor events, including undo/redo.
Start from scratch disables Primary while retaining authored sources and attached
source data. Restore Primary re-enables its contribution. Saved volume rows expose
their name, shape kind, Include/Exclude role and enabled contribution; graph rows
show marker counts. The shared editor tree contains one selectable Primary row and
one row per authored volume, with eye, power and focus actions. Arrow keys select
rows; double-click focuses. Primary remains one coarse object with no per-octant
children. Its power action changes contribution, while its eye action hides its
cubes, IDs, frozen outline and samples together without changing contribution. Authored-volume eyes affect helper visibility only.
Controls are disabled during an update and stale edits refresh the recipe with an
actionable error. The dock identifies the temporary build restriction below.

Add volume offers Box and Cylinder, placed 16 map units ahead of the viewport camera.
Use seed graph in Volume properties optionally adds seed filtering to either shape. When the
viewport is closed/unavailable, creation retains the selected-object/default fallback.
Defaults are an 8-unit box or a radius-4, height-8 cylinder.
Source rows select an editor for name, Include/Exclude role and shape dimensions.
Position and rotation are edited with the viewport tools; their numeric coordinate
fields are omitted from this dock. Apply changes commits one history edit;
Discard edits resets the draft. Duplication
deep-copies a source and assigns fresh source/owned-marker IDs. Delete remains
undoable. Source selection clears gameplay-entity selection; selecting a gameplay
entity clears source and marker selection.

## Viewport authoring

The dock and viewport share source selection and focus. Show coverage volumes and
each source's Show control affect visualization only; disabled contributions remain
visible/editable when shown. Selecting or focusing a source reveals its helper.
Include outlines are solid, exclusions dashed, and selection has a white bounds
frame plus explicit selected text in the dock. Shown helpers take picking priority;
hide them to select objects underneath. Primary retains one coarse dock row.

Authored sources use the existing Move/Rotate/Scale tools, local/world spaces and
snapping settings without creating gameplay entities. Scale uses the editor's
existing vertical drag gesture. Transient scale becomes explicit box dimensions or
cylinder radius/height; persisted pose scale remains one and radial handles preserve
a circular cylinder. A drag previews locally and releases as one whole-recipe edit
against its captured project fingerprint. Escape, unexpected pointer capture loss,
target changes and disabled editing restore the original helper. Invalid or stale
drags cannot commit partial state.

Visibility, selected source and preview display live for the editor session, survive
closing the dock, and reset on project change. Disabling Primary hides its loaded
octants, IDs, frozen outline and qualified samples, disables source-grid viewport
culling and clears the freeze setting. Display counts report zero active Primary
octants. Restoring Primary reuses retained data and display preferences. Changing
Primary contribution hides the previous coverage preview until explicitly shown
again or refreshed; stale preview data is retained for comparison. Helpers and generated cell previews
dispose their GPU resources when replaced or closed.

## Optional seed graphs

Create a Box or Cylinder and check Use seed graph. The toggle immediately applies
the current valid volume edits and reveals the + action and seed list below Volume
properties. Invalid property drafts must be corrected or discarded before toggling.
The graph uses that volume's transform and dimensions; it does not introduce a
separate shape. Add seeds and generate explicitly in Build. An enabled volume with active filtering
and no seeds disables generation/build with a notice naming the unfinished volume.
Turning filtering off keeps seeds, uses the ordinary shape path and permits zero seeds.
Volume shape can be changed in properties. Box-to-cylinder conversion starts with
half the larger horizontal dimension and expands only as needed to retain existing
seed positions; cylinder-to-box uses its enclosing diameters and height. Numeric
shrinking rejects dimensions that would strand seeds outside the volume. Seed
placement is clamped to the actual shape. Existing graph-only volumes appear as
boxes with Use seed graph already on; their names, poses, seeds and Sticky settings
remain intact until an explicit edit.

The selected graph shows owned seeds in a labeled, bordered list using the shared
editor tree, with Seed 1, Seed 2, … labels derived from current graph order, visible
selection, a bounded scroll area and pagination in groups of 32. A + icon at the
bottom of the list adds a seed; only the selected row shows a delete icon. Both
buttons have accessible labels and support keyboard activation. There is no separate
seed-details panel. Adding a seed
places it ahead of the camera, converted to graph-local coordinates and clamped to
the graph bounds; without a viewport it uses the graph center. The viewport Move
tool positions selected seeds, and double-clicking their rows focuses them.
Copy/paste and duplicate shortcuts retain normal history and fresh identities;
delete is undoable. Seeds must remain inside the volume. Cells intersecting the boundary are included, so
cubes can protrude slightly past a curved edge; wholly outside cells are not generated.
Empty graphs remain valid authoring states with an
explicit no-seeds notice. Existing per-graph/global marker limits apply before adding
or duplicating. Graph copies retain independent marker identities. Seed names are
not editable. List labels and delete labels use the same one-based index across
pages and renumber after deletion. Legacy persisted names
are retained for format compatibility but do not determine displayed labels; copies
and pastes use an indexed name rather than appending “copy.”

Add at least one seed, check Use when generating octants in Volume properties and Apply changes,
then use Generate octants in the Build tab. The tree's enable action also toggles
contribution directly. Generation reuses the existing whole-recipe preview job and
shows final effective coverage. Enabled empty graphs disable generation/build
actions with a notice naming the unfinished graphs. Unavailable preview support
and active work also disable generation; cancellation remains available above all
tabs. The Viewport tab can inspect the selected graph's accepted/rejected cells and
reports counts and staleness. Qualify samples and Build occlusion use the combined
enabled coverage and final exclusions; either can run without generating a preview.

The dock groups tasks into Volumes, Build and Viewport tabs using the existing
Mantine controls. Volumes owns the compact Add volume menu, Primary toggle, tree,
volume properties and seed list. Volume Apply and Discard actions share a compact
footer with an Actions menu for duplication/deletion; seed actions live in the list.
Build owns generation, qualification and baking. Sample pull-in and pixel edge
allowance are visible in the main container, followed directly by a full-width
Build occlusion button. Generation details remain collapsed. Viewport
owns visibility/freeze controls and all overlay toggles. Display help is collapsed.
Tab switches preserve uncommitted property drafts; progress and cancellation remain
above the tabs so they are available while editing or inspecting.

Generated octant overlays are available immediately after successful generation;
no visibility bake is required. The separate Enable visibility culling control
needs baked masks to hide scene objects. Missing-mask guidance describes culling
only, and an absent generation result directs users to Generate octants and Log
rather than suggesting that a full build is needed to view the cells. Unchanged
metadata refreshes do not cancel generation; actual input changes and project
switches still cancel captured work and preserve the last successful result.

The configured Copy/Paste/Duplicate/Delete shortcuts and Forge Edit menu operate on
the selected coverage volume or seed. Copied seeds retain local positions inside the
selected graph; pastes outside its bounds are rejected. Copies/duplicates get fresh
IDs, including owned seeds when duplicating a graph. Primary cannot be deleted or
duplicated. The coverage clipboard resets on project change, and edits use normal
project history/undo/redo. Text fields keep ordinary text editing shortcuts. Handled
global edit keys do not propagate into dock navigation.

Seeds can be selected/focused from the dock or viewport. Viewport picking prioritizes
seeds within eight display pixels before their volume. Selecting a seed activates
Move; Rotate/Scale detach the gizmo with an instruction to select the graph. The
shared tool previews seed translation locally, commits one recipe edit on release,
and restores it on cancellation. World/local axes and snapping use the graph pose.
Graph translation/rotation carries all seeds; resizing changes region dimensions
without scaling local seed positions and rejects sizes that leave seeds outside.
Deleting a selected seed clears seed selection while retaining its graph selection.

The SDK development graph evaluator below supplies provisional blocker/probe rules.
Forge maps indexed apparently-buried-seed diagnostics to the graph name, current
one-based seed list label and stable graph/seed IDs. Legacy seed names do not appear
in diagnostics. Current-scene previews and accepted/rejected inspection use the existing
schema-27 recipe; real-map qualification remains open.

## SDK shape preview foundation

The unreleased SDK `OcclusionCoverageBuilder.PreviewShapes` API accepts explicit
Primary coordinates and enabled box/cylinder poses/roles. Disabling Primary means supplying
an empty Primary list. The target supplies cell size and structural preview limits;
the evaluator retains touching inclusion cells and removes only cells clearly
inside exclusion shapes. Cylinders retain circular cross-sections with local Z as
their axis, including when rotated. It returns copied canonical coordinates, per-source counts,
bounded work counts and a versioned coordinate fingerprint without masks or native
publication. This coordinate fingerprint does not replace the stored recipe hash
or the later generation/publication key.

The [box](../qualification/M18A-occlusion-coverage.md#box-coverage-preview-development)
and [cylinder qualification records](../qualification/M18A-occlusion-coverage.md#cylinder-and-mixed-shape-preview-development)
fix the tolerance, rejection boundaries, budgets and test evidence. The original
box-only preview API and its version-one coordinate fingerprint remain compatible;
mixed shape evaluation uses version two. `ReadCoordinates` reads existing native
Primary cells through the neutral SDK boundary without inferring masks.
The unreleased `PreviewGraphs` API now accepts explicit scene triangles/alpha,
bounded regions and owned local seeds. It uses finite segment queries from the cell
center/eight inset corners, retains uncertain hits, and composes accepted graph cells
before exclusions. Per-graph accepted/rejected coordinates, bounded work counters,
progress/cancellation and indexed apparently-buried-seed errors establish the SDK
foundation. Its version-three coordinate identity leaves shape-only identities
unchanged. The [SDK graph record](../qualification/M18A-occlusion-coverage.md#sdk-graph-preview-development)
defines the provisional rules and test evidence. Forge calls it with captured
current project geometry and the exact packed-scene identity. Forge's SDK pin remains
unchanged until release integration.

The current-scene snapshot foundation now prepares a packed level from the captured
unsaved workspace in an isolated temporary project. Content metadata and verified
attached assets enter that project, and the existing bake/pack services use a read-only
catalog. Its scratch recipe uses source-mode coverage for geometry preparation;
original authoring/dirty state, saved files, staging and visibility caches stay
unchanged. The result carries the captured project fingerprint and SHA-256 of the
exact packed level. Temporary files are deleted on success, cancellation or failure.
Shared `Domain/ForgeProjectSnapshot` owns input copying, verification, limits and
temporary-directory cleanup. The UYA adapter supplies the scratch coverage state
and retains its bake/pack calls and warning policy.
Copies are bounded to 2 GiB, 50,000 entries and 16 content-directory levels, with the
existing 256 MiB per-file/attached-asset ceiling and link rejection. Invalid inputs
or unrelated unacknowledged bake warnings fail actionably rather than falling back
to base geometry. The background preview job uses this capture for every enabled
graph. The SDK's neutral `BuildCoverageGeometry` API extracts calibrated static
terrain/TIE/shrub blockers independently of native grid and source-group metadata.
It excludes target spheres/barriers and rejects unsupported streamed terrain chunks;
the strict visibility-bake extraction path retains its validation.

## Development preview jobs

Graph volume properties expose Sticky and Sticky distance using the normal Apply
changes / Discard edits flow. Apply the settings, then generate, qualify or build
explicitly; editing them does not start evaluation automatically. Sticky filters
candidate graph octants before seed-visibility queries. Both preview and native
generation use the same evaluator and current packed scene.
New seed graph attachments enable Sticky at distance 8. Existing saved graphs retain their settings
and the legacy missing-field defaults above. Shape dimensions and Sticky controls
remain visible alongside the normal volume actions. Numeric position/rotation and
seed coordinates are omitted; viewport tools handle placement.

The filter carries forward v1's fourteen world-axis/corner directions. For each
direction `d`, it casts from `center - 2*d` toward `center + normalize(d)*distance`.
A definite front-facing TIE/tfrag hit retains the candidate; surface edges and
finite endpoints count. The two-unit start offsets also retain surfaces crossing
the octant itself, including when distance is zero. The existing scene extraction,
blocker/material classification and raw PS2 alpha sampling are reused. Shrubs and
mobys are not Sticky surfaces. The filter never reads collision geometry.

Sticky is a surface-proximity heuristic, not a proof of player reachability. The
graph must still pass its seed-visibility test. Rejected cells remain inspectable;
ordinary include shapes can restore intended airborne or scripted camera space,
and exclusions still apply last. Sticky rays and geometry tests share the same
runtime budgets and cancellation checks as seed visibility. The existing ray
preflight estimates seed-visibility work; extra Sticky rays are bounded during
evaluation rather than pessimistically rejecting otherwise valid large graphs.

The configured SDK advertises supported preview APIs in
`src/RatchetPs2.Sdk/OcclusionCoveragePreview.props` using
`RatchetSdkOcclusionCoveragePreviewApiVersion=1`. Forge's shared SDK build properties
load this declaration and automatically include the preview adapter and tests.
With the updated configured checkout, `npm run dev` and `npm test` need no preview
flag. Restart/rebuild Forge after updating the SDK so the host advertises the
`editor.occlusion-coverage.preview` capability.

The clean pinned v0.10.0 SDK has no declaration, so it still builds with authoring
available and preview unavailable. `ForgeEnableOcclusionCoveragePreview=false`
explicitly disables preview; `true` remains available for compatible development
snapshots without the declaration. `RatchetSdkRoot` can override the configured
checkout. Preview support alone does not enable native regeneration/publication.
The additional generation capability below is required; the SDK release pin is unchanged.

Coverage enumeration is explicit for shapes and graphs. Creation, movement, seed
edits, contribution changes and undo/redo mark previous output stale without starting
a preview. Metadata refresh alone does not change preview color. Generate octants
evaluates the recipe; Qualify samples and Build occlusion evaluate through the existing
preparation path. Successful qualification hides the old coverage preview and uses
its freshly qualified cells for the normal octant/ID display, independently of the
sample-marker visibility checkbox. Cancelled/stale results cannot replace current
output, and failed/cancelled attempts are not automatically retried.

The SDK bounds candidate counts before evaluation and charges actual rays to the
shared two-million-ray budget. It no longer rejects the theoretical product of
all candidates, nine probes and all seeds before Sticky/early acceptance can run.
Each retained candidate tries its nearest seed first, then the remaining seeds
if needed. The acceptance rule and existing candidate, seed, geometry-work and
cancellation limits remain unchanged.

Coverage read, edit, preview and explicit cancellation failures use Forge's shared
`useLogReporter` and session Log, matching other editor operations. Operational
errors are not rendered as a separate alert at the top of the coverage dock.
An unsuccessful preview retains the last successful result and releases the
generation controls. Cancelled or superseded preview responses do not log a second
operation failure. SDK scene-query limits remain bounded; ordinary blocked/unblocked
segments stop at the first definite opaque hit, while apparently-buried-seed checks
retain nearest-hit traversal with the same geometry and alpha rules.
Query-budget failures report the configured limit, rays attempted and scene triangle
count. The limit measures traversal work across enabled graphs; it is not a seed
count or native-game limit. A few seeds in a large region can exhaust it.
The development query budget is 200 million tests, calibrated against the saved
Hoven3 three-seed graph (51,948 candidates and about 97.5 million tests). SDK and
both bridge boundaries share this bound; candidate, ray, geometry and cell limits
and progress/cancellation checkpoints are unchanged.

Preview captures an immutable workspace projection under the editor gate, then
runs its game adapter on a background task with a cancellation token. Imported
Primary comes from the verified attached source; otherwise the adapter extracts
the base level's grid from the configured source ISO. Shape-only previews with
Primary disabled need no ISO. Enabled graphs require the captured asset catalog
and configured source ISO even when Primary is disabled, because scene composition
needs both. The evaluator includes unsaved recipe and geometry edits, never saves
the project and never replaces coverage, visibility, bake caches or stage files.

Only one preview runs per editor runtime. Input capture and result acceptance check
the full project fingerprint, workspace instance and project version. An edit,
undo/redo returning to an earlier fingerprint, or close/reopen of the same project
rejects the old result. The editor gate remains free during evaluation. Bridge
cancellation reaches SDK checkpoints; base-ISO extraction currently checks
cancellation immediately before/after its existing synchronous SDK operation.

The dock shows source counts, effective cells, Primary cells and actual candidate
tests. It reports progress for validation/Primary reading/evaluation/completion,
offers cancellation and retains the previous successful result with an explicit
stale indicator after edits. Show octant preview displays the bounded result (at most
100,000 cells) through the same octant renderer used by Primary and frozen octants:
instanced transparent fills and twelve-edge cube outlines, with the configured
octant color and normal scene depth testing. Cells are not pickable. Stale
results change appearance and show an explicit viewport notice; they remain preview
data and do not become generated authority. Graph results also carry the exact
packed-scene hash and bounded ray/query counts. Preview cell display switches
between effective coverage and the selected graph's accepted or rejected cells.
Graph inspection precedes final union/exclusions: ordinary includes may rescue a
rejected cell and exclusions may remove an accepted cell. Switching back to a
shape-only result resets graph inspection. Scene identity describes the captured
input; separate watching of external ISO/catalog file changes is not implemented.
Protocol details and wire vectors are in the [v5 bridge contract](../bridge-protocol-v5.md#coverage-preview).

## Interim build behavior

Until matching generated coverage and visibility can be published, UYA preflight
reports `UYA_OCCLUSION_COVERAGE_NOT_GENERATED` whenever Primary is disabled or any
authored source is enabled. This blocks full/selective bakes and packed output,
preserving the previous stage. Primary-only coverage and disabled authored helpers
retain source-mode output. The corrective action is to enable Primary and disable
authored contributions; the editor never silently discards the recipe for a build.

## Verification

`OcclusionCoverageProjectTests` covers migration without unsolicited file writes,
semantic hashing, malformed inputs, caller-list isolation, portable attachments,
save/reopen, recovery, stale/cancelled edits, selection, and undo/redo. Game-specific
`UyaOcclusionCoverageBuildTests` verifies that ungenerated authored coverage blocks
selective baking and packing, disabled helpers preserve the golden packed result,
and rejected builds retain the previous stage manifest.

`OcclusionCoveragePreviewTests` covers unlocked background evaluation, captured
inputs, concurrent rejection, stale/ABA results, close/reopen and cancellation.
The opt-in `UyaOcclusionCoveragePreviewTests` checks imported Primary, unsaved mixed
sources, no-ISO operation, counts, progress, identity and unsupported graph filtering.
Shared C#/TypeScript preview vectors retain bounds/truncation/ordering checks;
real-host tests exercise capability gating, preview progress and stale requests.
Both default pinned and development-preview full suites passed on 2026-10-31.
Viewport regression tests cover coordinate conversion, circular sizing, local drag
previews, one commit per release, cancellation, stale callbacks, snapping settings,
picking and resource disposal. An isolated Electron pointer/keyboard walkthrough is
recorded in the [qualification record](../qualification/M18A-occlusion-coverage.md#viewport-authoring-development);
real-map, large-preview, display-scaling and broader accessibility checks remain open.
Shared C#/TypeScript payload vectors and malformed/truncated/count-bound fixtures
verify wire interoperability. Real-host integration covers querying, editing,
atomic replies, stale conflicts, normal history, saving and reopening.


## Building authored occlusion

SDK checkouts advertising `RatchetSdkOcclusionCoverageGenerationApiVersion=1` in the
same declaration enable generation automatically when previews are enabled. The host
advertises `editor.occlusion-coverage.build`. The clean v0.10.0 pin keeps its existing
source-only builder and authored-build diagnostic. An explicit
`ForgeEnableOcclusionCoverageGeneration=false` opts out of generation.

In the Occlusion dock, **Build occlusion** saves the scene, regenerates its cells from
enabled Primary/boxes/cylinders/graphs and final exclusions, qualifies sample cameras,
and runs the existing GPU visibility worker. It accepts coverage and visibility
together in one atomic cache replacement. There is no separately accepted unbaked
grid. **Qualify samples** uses the same generated cells without accepting output.
**Generate octants** remains a nonauthoritative preview and does not save or bake visibility.

Preparation composes the saved scene in the existing isolated snapshot workspace.
Graph evaluation uses those exact packed bytes without composing a second scene.
The SDK creates initial complete masks with bit 1023 set for all generated cells,
including cells with no source counterpart, and inserts/replaces the grid through
its existing asset/archive composition. Empty effective coverage fails with a prompt
to add/enlarge includes, restore Primary or reduce exclusions. Existing native-group
validation and streamed-terrain restrictions of the visibility builder remain in force.

The established schema-one bake cache adds optional `coverage` metadata containing
recipe/project hashes, algorithm version, canonical coordinate hash and bounded object
mappings from the composed scene. Source-mode caches omit it. The scene input hash
identifies the packed scene **before** generated-grid insertion; the original-grid hash
identifies the deterministic initial generated grid. Worker result/scene hashing still
binds qualified samples, pull-in and pixel-edge settings. Generated complete masks may
grow the native allocation; source-only bakes retain source-allocation compaction.

Commit validates worker output, recipe/project/algorithm identity, exact coordinates,
reserved-bit policy, resource budgets and the saved project again before atomic
replacement. Map preflight requires matching accepted metadata; final packing checks
the exact packed scene hash before using the SDK's arbitrary-coverage application.
Shared `OcclusionBakeStore` applies the same recipe/project/algorithm identity check
to cache reads and commit validation. The UYA adapter supplies the expected algorithm
version and retains native-grid coordinate and reserved-mask validation.
Stale, absent, malformed, cancelled and failed output cannot become an implicit Primary
fallback. The previous cache stays on disk until a successful replacement. Restoring
Primary-only ignores the authored cache and returns to the original source-mode path.
Visibility loading treats missing or stale authored masks as a `needs-build` result.
The dock and session log show one warning while they remain stale. Loaded masks are
checked locally as the scene changes; authoring edits, visibility toggles and completed
unrelated work do not repeat the export. Loading runs on project opening, successful
occlusion builds, a switch between Primary-only and authored masks, or explicit retry
after a load failure. Undo can restore still-loaded masks when their identity matches
again. Corrupt data and other load failures remain errors with a retry control.

The project hash excludes entity hidden/locked flags, normalizes implicit/default
entity state, and excludes coverage authoring data, which has its own semantic
recipe hash. Renaming volumes/seeds or editing inactive volumes preserves accepted
visibility. Effective coverage changes still invalidate the recipe. Other content
edits conservatively invalidate the project hash. Full project hashes still guard
authoring updates and commits; packing still validates exact scene bytes. Existing
development bakes using the previous project-hash calculation need one rebuild.

Authored visibility export returns its saved object mappings and optional
`coverageRecipeFingerprint` / `projectFingerprint` fields. Electron validates both;
the renderer compares the latter with the coverage snapshot's `visibilityFingerprint`
and ignores masks that do not match the current recipe and scene. Built
cells, labels, freeze and samples work with Primary disabled and reuse the established
octant renderer. Recipe changes stale qualified samples and reset freeze. Unmapped
objects remain visible. Expanded grids still need PCSX2/hardware qualification.

Sample qualification includes an optional preparation report: qualified cell/sample
counts against the existing work limits, SDK-measured input grid/tree bytes and mask
slots, unshared-mask bytes (one complete mask per cell), and SDK always-visible fallback
groups. The report describes the prepared input; masks can share slots after baking,
so it is not a final packed-size or runtime-memory guarantee. Build preparation emits
the same report before the GPU worker runs, and successful completion reports the
actual native grid byte count. Source-only and authored jobs share the existing
qualifier/export path. Older qualification/completion payloads remain accepted.
