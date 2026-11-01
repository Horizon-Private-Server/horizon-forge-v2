# Forge binary bridge protocol v5

Protocol v5 appends optional directional-light colors and directions to each editor
entity snapshot. The renderer uses the records, their source slots, and entity
rotations to reconstruct tfrag vertex lighting instead of displaying the game's
uninitialized cached colors.

The Electron client and Forge.Host must use the same protocol version. A mismatch
is rejected during handshake. Framing and request lifecycle are unchanged.

The interoperability vectors in
[`tests/fixtures/bridge-v5.json`](../tests/fixtures/bridge-v5.json) cover v5 framing.
Earlier documents and fixtures remain historical compatibility records.

## Occlusion coverage operations

Opcodes 42 (`QueryOcclusionCoverage`) and 43 (`UpdateOcclusionCoverage`) are additive,
capability-gated editor operations. They leave existing snapshot/command payloads and
v5 framing unchanged. Editor snapshots advertise `editor.occlusion-coverage.query`
and `editor.occlusion-coverage.update`; clients check those capabilities before use.

Query takes an empty payload and returns the project fingerprint, semantic recipe
fingerprint, visibility fingerprint, and [coverage state](features/occlusion-coverage-project-v1.md), in that order. The visibility fingerprint excludes entity hidden/locked
flags and treats missing/default entity state identically. The full project fingerprint
still guards authoring updates and preview publication. Update
takes the expected project fingerprint followed by a coverage state. Fingerprints
are 64 lowercase hexadecimal characters encoded as normal bridge strings.

Coverage state is encoded in this order:

1. Schema version (`u32`, currently 1), Primary contribution (`bool`), source count (`u32`).
2. Each source's ID/name (strings), enabled (`bool`), role (`u32`: Include=1, Exclude=2),
   and pose: position XYZ, rotation XYZW, scale XYZ (ten `f32` values).
3. Source kind (`u32`): box=1 followed by XYZ dimensions; cylinder=2 followed by
   radius/height; graph=3 followed by XYZ dimensions and marker count (`u32`).
   Extended graph=4 carries XYZ dimensions, Sticky (`bool`), sticky distance (`f32`),
   then marker count (`u32`). It retains the same marker layout below.
   Box with graph=5 carries XYZ box dimensions; cylinder with graph=6 carries
   radius/height. Both then carry graph-disabled (`bool`), Sticky (`bool`), sticky
   distance (`f32`) and marker count (`u32`), followed by the same marker layout.
   Attached graphs do not carry a second set of region dimensions.
4. Each graph marker's ID/name (strings) and local XYZ position (`f32`).

Integers/floats use the existing little-endian bridge convention. Source and marker
counts are checked before allocation, with at most 1,024 sources, 4,096 markers per
graph and 16,384 total markers. Domain validation checks names, IDs, shape data,
ownership, dimensions, poses and bounds. Unknown versions/kinds/roles, noncanonical
fingerprints, malformed booleans, truncation and trailing bytes fail explicitly.
Sticky distance is finite and between 0 and 1,000,000 map units. Legacy graph=3
means Sticky off with an effective default distance of 12; settings use graph=4
when present. Existing graph=3 vectors remain byte-identical. The
[`occlusion-coverage-sticky-v1.json`](../tests/fixtures/occlusion-coverage-sticky-v1.json)
vector covers extended graph request/snapshot interoperability.
[`occlusion-coverage-volume-graphs-v1.json`](../tests/fixtures/occlusion-coverage-volume-graphs-v1.json)
covers shape-owned graphs and retained disabled seed settings. Legacy kinds 3/4
remain readable and byte-identical. Circle containment is checked in addition to
height/bounding-box bounds. Active graphs require Include; an inactive graph may
remain saved on an exclusion volume. Clients check
`editor.occlusion-coverage.graph-shapes` before authoring cylinder seed graphs.

Update returns two length-prefixed byte fields: the existing encoded editor snapshot
and the coverage query result. Both are captured under the same runtime gate as the
edit. Stale project fingerprints fail with `Conflict`; invalid recipes do not mutate
the project. Accepted edits use normal history and recovery. An unchanged edit emits
no project-change event. Cancellation is checked before publishing the copied recipe.

[`occlusion-coverage-v1.json`](../tests/fixtures/occlusion-coverage-v1.json) freezes
request/query-result interoperability vectors for all three shapes. Complete update
responses, query/edit/history/stale rejection and save/reopen are exercised through
the actual C# host by `tests/host.integration.test.mjs`.

### Coverage preview

Opcode 44 (`PreviewOcclusionCoverage`) is gated by
`editor.occlusion-coverage.preview`. Builds using an SDK with the version-one
preview API declaration enable it automatically; the clean pinned SDK does not
advertise it. Capability detection and explicit overrides are documented in the
[coverage feature contract](features/occlusion-coverage-project-v1.md#development-preview-jobs).

Request payload: expected project fingerprint (string) and source ISO path (string,
empty when unnecessary, at most 4,096 characters with no NUL). Electron supplies
the configured path; the renderer supplies only the project fingerprint. Imported
Primary and Primary-disabled recipes need no ISO when no graph is enabled. Enabled
graphs require the configured ISO and the host-captured catalog context. Requests reject trailing bytes,
noncanonical fingerprints and malformed/truncated strings.

Response fields, in order:

1. Project and recipe fingerprints (strings).
2. Algorithm version (`u32`, positive) and cell size (`f32`, positive finite).
3. Primary-grid and coordinate fingerprints (strings); disabled Primary uses the
   SHA-256 of empty bytes.
4. Primary-cell and actual candidate-test counts (`u32`).
5. Source count (`u32`), then each source ID (string) and matched-cell count (`u32`).
   All recipe sources retain their input order, including disabled sources with zero.
6. Effective cell count (`u32`), then unsigned XYZ coordinate triples (`u32`) in
   strictly increasing Z/Y/X order, without duplicates.

Algorithm 3 appends a graph extension after the effective coordinates. Shape-only
responses retain the exact version-one layout above. The extension contains:

1. Extension version (`u32`, currently 1), packed-scene SHA-256 (string), ray count
   (`u32`) and query-test count (`u32`).
2. Graph count (`u32`), followed by each graph source ID (string), accepted count
   and XYZ triples, then rejected count and XYZ triples (all counts/axes `u32`).

Each graph ID must uniquely reference a source-count entry whose count equals its
accepted count. Both lists use canonical Z/Y/X order and cannot overlap within a
graph. Combined inspection cells are bounded to 100,000 and cannot exceed actual
candidate tests. Graph count is 1–1,024, rays 1–2,000,000 and query tests at most
200,000,000. Algorithm 3 requires the extension; other algorithms reject it.
Accepted/rejected cells precede final union/exclusions. Scene identity describes the
exact captured packed input and does not replace project/recipe/coordinate identity.

Responses retain at most 1,024 source counts, 100,000 cells and 2,000,000 candidate
tests. The decoder checks caps before allocation, fingerprint/ID/profile validity,
count consistency, canonical coordinates, truncation and trailing bytes. Standard
v5 progress frames report scaled completion; standard cancellation frames cancel
the request at safe host/SDK checkpoints. Only one preview runs per editor runtime.
Stale input, an intervening edit (including undo to the same fingerprint), or
close/reopen rejects the result with `Conflict`. Successful preview changes no
project state/history, accepted coverage, masks or stage files.

[`occlusion-coverage-preview-v1.json`](../tests/fixtures/occlusion-coverage-preview-v1.json)
freezes request/response interoperability. Host lifecycle tests and the real-host
integration exercise stale/concurrent/cancelled jobs, Primary composition, progress
and the default/development capability boundary.

[`occlusion-coverage-preview-graph-v1.json`](../tests/fixtures/occlusion-coverage-preview-graph-v1.json)
freezes the graph extension. Host and Electron decoders reject malformed versions,
identities, ownership, overlap, ordering, excessive work/counts and truncation.


Development coverage builds advertise `editor.occlusion-coverage.build` in editor
snapshots when the configured SDK declares the required generation API. This does
not add an opcode or change the v5 binary layout. Existing occlusion worker commands
prepare and commit the authored path; visibility JSON adds optional paired
`coverageRecipeFingerprint` and `projectFingerprint` hashes for renderer invalidation.
The latter is the normalized visibility fingerprint, compared with the coverage
snapshot's `visibilityFingerprint`, so editor hide/show and lock changes preserve masks.

For missing or stale authored masks, the visibility worker succeeds and writes
`{"schemaVersion":1,"status":"needs-build"}` instead of a grid. Electron validates
and forwards this expected result; it does not reject IPC or retain a build-failure
entry. Malformed cache/data and worker failures still reject normally. Map build
and commit validation continue to reject stale input.

Occlusion worker scenes may include a `preparation` report with measured input
`gridBytes`, `treeBytes`, `maskSlots`, estimated `unsharedMaskBytes`, SDK-selected
`alwaysVisibleGroups`, and `cellLimit`/`sampleLimit` work budgets. Qualification IPC
forwards the report after validating integer/resource bounds and count/size consistency.
The raw worker scene hash binds this additive metadata. Completion summaries may
include `nativeGridBytes`, validated against the existing native-cache byte budget.
These additions do not change the v5 binary payloads or native serialization rules.
